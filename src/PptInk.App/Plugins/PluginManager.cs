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
///    file/network/clipboard 权限需显式声明并经用户确认；
///  - 插件包支持可选签名校验（未签名包首次启用弹确认）；
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
            ZipFile.ExtractToDirectory(zipPath, staging);

            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("缺少 manifest.json");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            if (manifest is null || manifest.Magic is < 1 or > 3)
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

            var entryPath = Path.GetFullPath(Path.Combine(staging,
                manifest.Entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            var rootPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
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
        // 权限确认：首次启用含敏感权限的插件时弹一次确认
        if (plugin.Manifest.Permissions.Any(p => p is "file" or "network" or "clipboard") &&
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
