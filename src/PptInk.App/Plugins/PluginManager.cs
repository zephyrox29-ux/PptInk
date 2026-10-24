using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using PptInk.Bridge;
using PptInk.Builtin;
using PptInk.Ink;
using PptInk.UI;

namespace PptInk.Plugins;

/// <summary>
/// 插件管理器：
///  - 插件包为 zip（图标 + manifest.json + 脚本）；
///  - 脚本运行于 MoonSharp Lua 沙箱：仅能访问 manifest 声明的 API（白名单注入），
///    file/clipboard 权限需显式声明并经用户确认（network 权限暂无对应能力，声明了也不生效）；
///  - 本版本不验签：Signature 字段只记日志警告，插件等同本机代码，只装可信来源的包；
///  - 内置首批插件以 C# 直接实现，走同一 Host API（M4 交付插件生态 v1）。
/// </summary>
public sealed class PluginManager
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        // Plugin manifests use standard lower-camel-case JSON. System.Text.Json's generic
        // defaults are case-sensitive, which otherwise makes valid packages fail to install.
        PropertyNameCaseInsensitive = true,
    };

    private readonly InkEngine _ink;
    private readonly PythonBridge _ai;
    private readonly Dictionary<string, IPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _pluginRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk", "plugins");
    private QuickPluginElement? _quickPanel;   // v0.9.0 舞台 Plugin 层元素

    public IReadOnlyCollection<IPlugin> Loaded => _plugins.Values;

    /// <summary>插件安装根目录（设置中心"打开插件目录"）。</summary>
    public string PluginRoot => _pluginRoot;

    private readonly HashSet<string> _activeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>插件是否允许在工具栏快捷面板中使用；设置页开关仅改变可用性，不自动弹出组件。</summary>
    public bool IsEnabled(IPlugin plugin) => !_disabledIds.Contains(plugin.Manifest.Id);

    /// <summary>兼容运行态诊断查询。</summary>
    public bool IsActive(IPlugin plugin) => _activeIds.Contains(plugin.Manifest.Id) && plugin is not ScriptPlugin { IsFaulted: true };

    public PluginManager(InkEngine ink, PythonBridge ai)
    {
        _ink = ink;
        _ai = ai;
        foreach (var id in Core.AppConfig.Current.DisabledPluginIds)
            _disabledIds.Add(id);
        RegisterBuiltins();
        ScanInstalledPlugins();
    }

    /// <summary>启动扫描已安装插件（v1.1.0 修复）：InstallFromZip 只把插件注册进当前进程，
    /// 重启后磁盘上的包无人问津 —— 用户装了插件，关程序再开就"消失"，插件生态只活在安装后的
    /// 这一次运行里。逐目录走与安装一致的 manifest 校验后注册；单个坏包只跳过自身，绝不阻塞启动。</summary>
    private void ScanInstalledPlugins()
    {
        try
        {
            if (!Directory.Exists(_pluginRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_pluginRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".staging-", StringComparison.Ordinal) ||
                    name.Contains(".backup-", StringComparison.Ordinal))
                    continue;
                try
                {
                    var manifestPath = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifestPath)) continue;
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
                    if (manifest is null) { Core.AppLog.Instance.Warn($"跳过无效插件目录（manifest 反序列化为空）：{name}"); continue; }
                    ValidateManifest(manifest, dir);
                    if (_plugins.TryGetValue(manifest.Id, out var prior))
                    {
                        if (prior is not ScriptPlugin) { Core.AppLog.Instance.Warn($"跳过与内置插件同 ID 的已装包：{manifest.Id}"); continue; }
                        continue;   // 同 ID 脚本插件已注册（安装于本进程内）：保留更新后的注册
                    }
                    Register(new ScriptPlugin(manifest, dir));
                }
                catch (Exception ex)
                {
                    Core.AppLog.Instance.Warn($"启动加载插件失败（已跳过）：{name} — {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Instance.Error(ex, "插件启动扫描失败");
        }
    }

    private void RegisterBuiltins()
    {
        Register(new Builtin.TimerPlugin());
        Register(new Builtin.SpotlightPlugin());
        Register(new Builtin.SnapshotPlugin());
        Register(new Builtin.RandomPickPlugin());
        Register(new Builtin.BoardInBoardPlugin());
    }

    private void Register(IPlugin plugin) => _plugins[plugin.Manifest.Id] = plugin;

    /// <summary>从 zip 安装插件包：解压 → manifest 校验（魔数/签名）→ 注册（不激活）。</summary>
    public bool InstallFromZip(string zipPath, out string error)
    {
        error = "";
        string? staging = null;
        string? backup = null;
        string? finalDir = null;
        bool installed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                throw new FileNotFoundException("插件包不存在", zipPath);

            // Inspect ZIP metadata before extraction: bound file count/expanded size and reject
            // traversal/rooted names even on runtimes that already defend ExtractToDirectory.
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count is < 1 or > 128)
                    throw new InvalidDataException("插件包文件数不合法（最多 128 个）");
                long expandedBytes = 0;
                foreach (var item in archive.Entries)
                {
                    var name = item.FullName.Replace('\\', '/');
                    if (name.StartsWith('/') || Path.IsPathRooted(item.FullName) ||
                        name.Split('/').Any(part => part == ".."))
                        throw new InvalidDataException("插件包包含越界路径");
                    expandedBytes = checked(expandedBytes + item.Length);
                    if (expandedBytes > 20L * 1024 * 1024)
                        throw new InvalidDataException("插件包解压体积超过 20 MB 限制");
                }
            }

            Directory.CreateDirectory(_pluginRoot);
            staging = Path.Combine(_pluginRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // v1.1.0：流式解压并按实际写入字节计数。原先的预检查用的是 zip 中央目录里的
            // 声明长度（ZipArchiveEntry.Length），恶意包可以把声明写小、deflate 流实际展开很大，
            // 20 MB 限制形同虚设（磁盘 DoS）。预检查保留（快速失败），真限制在这里兜底。
            const long maxBytes = 20L * 1024 * 1024;
            long written = 0;
            var buffer = new byte[81_920];
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('\\', '/')));
                    var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                    if (!dest.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("插件包包含越界路径");
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                    {
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written += read;
                            if (written > maxBytes) throw new InvalidDataException("插件包实际解压体积超过 20 MB 限制");
                            dst.Write(buffer, 0, read);
                        }
                    }
                }
            }

            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("缺少 manifest.json");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions)
                ?? throw new InvalidDataException("manifest 反序列化为空");
            ValidateManifest(manifest, staging);

            // Do not replace an active plugin or shadow a built-in plugin ID.
            if (_plugins.TryGetValue(manifest.Id, out var prior))
            {
                if (_activeIds.Contains(manifest.Id))
                    throw new InvalidOperationException("请先停用同 ID 插件，再更新它");
                if (prior is not ScriptPlugin)
                    throw new InvalidOperationException("插件 ID 与内置插件冲突");
            }

            if (!string.IsNullOrEmpty(manifest.Signature))
                Core.AppLog.Instance.Warn("插件包携带 Signature 字段：真实验签（RSA/Ed25519）未实现，本版本忽略该字段，不构成完整性保证");

            // Validate completely before touching an existing installation. Swap directories so
            // a broken/invalid update cannot delete the last working version.
            finalDir = Path.Combine(_pluginRoot, manifest.Id);
            if (Directory.Exists(finalDir))
            {
                backup = finalDir + ".backup-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalDir, backup);
            }
            try
            {
                Directory.Move(staging, finalDir);
                staging = null;
                installed = true;
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(finalDir))
                {
                    Directory.Move(backup, finalDir);
                    backup = null;
                }
                throw;
            }

            Register(new ScriptPlugin(manifest, finalDir));
            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); backup = null; }
                catch (Exception cleanupEx) { Core.AppLog.Instance.Warn($"旧插件备份清理失败：{cleanupEx.Message}"); }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (installed && finalDir is not null && backup is not null && Directory.Exists(backup))
            {
                // If registration/cleanup failed after the swap, favor restoring the known-good
                // prior directory when possible.
                try
                {
                    if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
                    Directory.Move(backup, finalDir);
                    backup = null;
                }
                catch (Exception restoreEx) { Core.AppLog.Instance.Error(restoreEx, "插件更新回滚失败"); }
            }
            return false;
        }
        finally
        {
            if (staging is not null)
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            if (backup is not null && Directory.Exists(backup) && (finalDir is null || !Directory.Exists(finalDir)))
                try { Directory.Move(backup, finalDir!); } catch { }
        }
    }

    /// <summary>manifest 全量校验（安装与启动扫描共用同一套规则，杜绝"装得进、重启认不出"）。</summary>
    private static void ValidateManifest(PluginManifest manifest, string baseDir)
    {
        if (manifest.Magic is < 1 or > 3)
            throw new InvalidDataException("manifest 魔数非法（须为 1/2/3）");
        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            !Regex.IsMatch(manifest.Id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("插件 ID 只能包含字母、数字、点、下划线和连字符");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120)
            throw new InvalidDataException("插件名称为空或过长");
        manifest.Name = manifest.Name.Trim();
        if (manifest.Author?.Length > 200 || manifest.Description?.Length > 1000 || manifest.Version?.Length > 64)
            throw new InvalidDataException("插件作者、描述或版本字段过长");
        if (string.IsNullOrWhiteSpace(manifest.Entry) || manifest.Entry.Length > 240 || Path.IsPathRooted(manifest.Entry))
            throw new InvalidDataException("入口脚本路径非法");

        var entryPath = Path.GetFullPath(Path.Combine(baseDir,
            manifest.Entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;
        if (!entryPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPath))
            throw new InvalidDataException("入口脚本缺失或越过插件目录");
        if (new FileInfo(entryPath).Length > 256 * 1024)
            throw new InvalidDataException("插件脚本过大（>256KB）");

        manifest.Permissions ??= Array.Empty<string>();
        var knownPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file", "network", "clipboard" };
        if (manifest.Permissions.Any(p => string.IsNullOrWhiteSpace(p) || !knownPermissions.Contains(p)))
            throw new InvalidDataException("manifest 声明了未知权限");
        manifest.Permissions = manifest.Permissions.Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        NormalizeManifest(manifest);
    }

    /// <summary>Normalize layout metadata from an untrusted plugin manifest before WPF sees it.</summary>
    private static void NormalizeManifest(PluginManifest manifest)
    {
        if (manifest.Panel is not { } panel) return;
        panel.Dock = string.IsNullOrWhiteSpace(panel.Dock) ? "float" : panel.Dock.Trim().ToLowerInvariant();
        if (panel.Dock is not ("left" or "right" or "bottom" or "float"))
            throw new InvalidDataException("插件面板 Dock 只支持 left/right/bottom/float");
        panel.Width = double.IsFinite(panel.Width) ? Math.Clamp(panel.Width, 160, 600) : 240;
        panel.Height = double.IsFinite(panel.Height) ? Math.Clamp(panel.Height, 100, 500) : 180;
        panel.Alpha = double.IsFinite(panel.Alpha) ? Math.Clamp(panel.Alpha, 0.3, 1.0) : 0.88;
        panel.Elements ??= Array.Empty<PanelElement>();
        if (panel.Elements.Length > 64)
            throw new InvalidDataException("插件面板元素过多（最多 64 个）");

        foreach (var element in panel.Elements)
        {
            if (element is null) throw new InvalidDataException("插件面板包含空元素");
            if (string.IsNullOrWhiteSpace(element.Type)) throw new InvalidDataException("插件面板元素缺少 Type");
            element.Type = element.Type.Trim().ToLowerInvariant();
            if (element.Type is not ("text" or "slider" or "button"))
                throw new InvalidDataException($"不支持的插件面板元素类型：{element.Type}");
            if (element.Id?.Length > 80 || element.Text?.Length > 500)
                throw new InvalidDataException("插件面板元素 ID 或文本过长");
            if (!Regex.IsMatch(element.OnEvent ?? "", @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant))
            {
                if (element.Type is "button" or "slider")
                    throw new InvalidDataException("插件按钮/滑条 OnEvent 必须是 Lua 函数标识符");
                element.OnEvent = "noop";
            }

            if (element.Type == "slider")
            {
                var min = double.IsFinite(element.Min ?? 0) ? Math.Clamp(element.Min ?? 0, -100_000, 100_000) : 0;
                var max = double.IsFinite(element.Max ?? 100) ? Math.Clamp(element.Max ?? 100, -100_000, 100_000) : 100;
                if (max <= min) max = min + 1;
                var value = element.Value is { } v && double.IsFinite(v) ? v : min;
                element.Min = min;
                element.Max = max;
                element.Value = Math.Clamp(value, min, max);
            }
        }
    }

    /// <summary>工具栏"插件"按钮：弹出启用插件的快捷选择面板。</summary>
    public void ToggleQuickPanel(Rect? anchor = null)
    {
        if (_quickPanel is { IsVisible: true })
        {
            _quickPanel.Dismiss();
            _quickPanel = null;
            return;
        }
        // Other floating tools share one physical slot and may have hidden the old quick panel.
        // Dispose that stale element before creating a replacement (including its theme subscription).
        _quickPanel?.Dismiss();
        _quickPanel = StageHost.Mount(new QuickPluginElement(this, anchor));
    }

    /// <summary>设置页只改变可用状态，不会主动打开插件面板。</summary>
    public void EnablePlugin(IPlugin plugin)
    {
        if (_disabledIds.Remove(plugin.Manifest.Id)) SaveDisabledPlugins();
    }

    public void DisablePlugin(IPlugin plugin)
    {
        if (_disabledIds.Add(plugin.Manifest.Id)) SaveDisabledPlugins();
        if (_activeIds.Contains(plugin.Manifest.Id))
        {
            try { Deactivate(plugin); }
            catch (Exception ex) { Core.AppLog.Instance.Error(ex, $"停用插件失败：{plugin.Manifest.Id}"); }
        }
        if (_quickPanel is { IsVisible: true }) _quickPanel.Dismiss();
    }

    private void SaveDisabledPlugins()
    {
        Core.AppConfig.Current.DisabledPluginIds = _disabledIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        Core.AppConfig.Current.Save();
    }

    public void Activate(IPlugin plugin)
    {
        if (!IsEnabled(plugin))
        {
            Toast.Show($"插件「{plugin.Manifest.Name}」已禁用，请到设置中的插件页启用");
            return;
        }
        var api = new HostApiBridge(_ink, _ai, plugin.Manifest);
        // 权限确认：首次启用含敏感权限的插件时弹一次确认。
        // v1.1.0：network 不再参与弹窗 —— 沙箱没有注入任何网络 API，声明了也不生效，
        // 为它弹"授权"对话框是误导（授权心理学：用户以为拒了就挡住了什么）。
        if (plugin.Manifest.Permissions.Any(p => p is "file" or "clipboard") &&
            !IsTrusted(plugin.Manifest.Id))
        {
            var msg = $"插件「{plugin.Manifest.Name}」申请权限：\n{string.Join("、", plugin.Manifest.Permissions)}\n\n仅本次放映允许？";
            if (MessageBox.Show(msg, "插件权限确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            Trust(plugin.Manifest.Id);
        }
        try
        {
            plugin.Activate(api);
            _activeIds.Add(plugin.Manifest.Id);
        }
        catch (Exception ex)
        {
            try { plugin.Deactivate(); } catch { }
            _activeIds.Remove(plugin.Manifest.Id);
            Core.AppLog.Instance.Error(ex, $"插件启动失败：{plugin.Manifest.Id}");
            Toast.Show($"插件启动失败：{plugin.Manifest.Name}");
        }
    }

    public void Deactivate(IPlugin plugin)
    {
        plugin.Deactivate();
        _activeIds.Remove(plugin.Manifest.Id);
    }

    public void UnloadAll()
    {
        foreach (var p in _plugins.Values) p.Deactivate();
        _activeIds.Clear();
    }

    private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);
    private bool IsTrusted(string id) => _trusted.Contains(id);
    private void Trust(string id) => _trusted.Add(id);
}
