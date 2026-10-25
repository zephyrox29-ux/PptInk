using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using PptInk.Core;

namespace PptInk.Bridge;

/// <summary>
/// AI 运行环境自动配置（v0.9.9；v1.1.1 加固）。
///
/// 目标：用户开启 AI 识别后，无论机器环境如何，程序自行把必需项补齐，直到识别真的可用。
/// 检测 → 修复 → 复检，逐级降级：
///   1) 内置嵌入式运行时（便携包自带）+ NumPy → 直接用；
///   2) 内置 Python 在、NumPy 缺 → 为它装 NumPy（pip；嵌入式无 pip 则下载 wheel 解压）；
///   3) 系统 Python 已带 NumPy → 只读使用；**绝不对系统 Python 执行 pip 安装**
///      （v1.1.1：改动机器全局环境必须用户自己动手，工具不做）；
///   4) 全无 → 征得用户同意后从 python.org 下载嵌入式运行时 + 从 PyPI 取 NumPy wheel。
/// v1.1.1 安全加固：任何联网步骤前必须经确认回调授权；嵌入式运行时固定 URL + 内置
/// SHA256/字节数双重校验；NumPy wheel 按 PyPI 元数据 digests.sha256 校验；临时文件
/// 唯一命名。全程后台线程执行，不冻结 UI；任一步骤失败都返回可读的原因，不静默吞掉。
/// </summary>
internal static class AiProvisioner
{
    // 嵌入式运行时来源（与便携包打包口径一致：CPython 3.12 embeddable win-amd64）
    private const string EmbedPythonUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip";
    // 官方发布包固定指纹（python.org 3.12.10 embed-amd64，2026-10 校验）——供应链完整性：
    // 固定 URL 若被劫持/损坏，这里拒绝安装而不是把任意内容解压进程序目录
    private const string EmbedPythonSha256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3";
    private const long EmbedPythonBytes = 11_133_606;
    private const string PyPiNumpyJson = "https://pypi.org/pypi/numpy/json";
    private const int TimeoutSeconds = 90;

    // HTTP UA 跟程序集版本（v1.1.0 统一 —— 原先硬编码 "PptInk/0.9.9"，与实际版本漂移，
    // 排障时无法从服务端日志判断用户跑的哪个版本）
    private static readonly string UserAgent =
        "PptInk/" + (typeof(AiProvisioner).Assembly.GetName().Version?.ToString(3) ?? "1.1.1");

    public sealed record Result(bool Ok, string Message);

    /// <summary>确保 AI 环境可用（检测 + 自动补齐 + 复检）。
    /// confirmNetwork：联网前授权回调（描述将弹给用户的下载说明），返回 false = 用户拒绝。
    /// 不提供回调 = 一律拒绝联网（调用方必须显式授权，不存在静默下载路径）。</summary>
    public static async Task<Result> EnsureAsync(string bridgeDir, IProgress<string>? progress, CancellationToken ct,
        Func<string, Task<bool>>? confirmNetwork = null)
    {
        string bundled = Path.Combine(bridgeDir, "python", "python.exe");
        // 一次配置会话只询问一次：嵌入式运行时与 NumPy 两步下载合并授权
        bool? granted = null;
        async Task<bool> EnsureNetworkAllowed(string what)
        {
            if (granted is bool g) return g;
            if (confirmNetwork is null) { granted = false; return false; }
            granted = await confirmNetwork(what);
            return granted.Value;
        }

        try
        {
            // 1) 内置运行时 + NumPy 齐备 —— 便携包默认路径
            if (File.Exists(bundled))
            {
                if (await HasNumpyAsync(bundled, ct))
                    return new Result(true, "内置 Python 运行时可用");
                progress?.Report("内置 Python 缺少 NumPy，正在安装……");
                if (await TryInstallNumpyAsync(bundled, bridgeDir, ct,
                        () => EnsureNetworkAllowed($"AI 功能需要联网下载 NumPy（来自 PyPI，约 16MB），安装到程序目录：{bridgeDir}\\python。")) &&
                    await HasNumpyAsync(bundled, ct))
                    return new Result(true, "已为内置 Python 安装 NumPy");
            }

            // 2) 系统 Python：只读使用（已有 NumPy 才用）。v1.1.1：不再对系统 Python 执行
            //    pip install —— 修改用户机器的全局环境必须用户显式自己动手，工具不做。
            foreach (var sys in new[] { "python", "python3", "py" })
            {
                if (!await HasPythonAsync(sys, ct)) continue;
                if (await HasNumpyAsync(sys, ct))
                    return new Result(true, $"使用系统 Python（{sys}，已含 NumPy）");
            }

            // 3) 全无 —— 征得同意后下载嵌入式运行时 + NumPy（只写进 bridge\python，不动系统）
            if (!await EnsureNetworkAllowed(
                    $"AI 功能需要联网下载：\n" +
                    $"· Python 3.12 嵌入式运行时（python.org，约 11MB）\n" +
                    $"· NumPy 计算库（PyPI，约 16MB）\n" +
                    $"安装位置：{bridgeDir}\\python（不影响系统环境）。\n下载内容将校验 SHA256 指纹。"))
                return new Result(false, "已取消下载。可手动安装 Python 3.10+ 与 NumPy 后重试，AI 功能暂不启用（不影响书写批注）。");

            progress?.Report("未检测到可用 Python，正在下载嵌入式运行时……");
            if (await ProvisionBundledAsync(bridgeDir, progress, ct) && await HasNumpyAsync(bundled, ct))
                return new Result(true, "已下载并配置嵌入式 Python 运行时");

            return new Result(false, "无法自动配置 AI 环境（网络不可用、下载校验失败或权限不足）。请手动安装 Python 3.10+ 与 NumPy。");
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "AI 环境配置已取消。");
        }
        catch (Exception ex)
        {
            return new Result(false, $"AI 环境配置失败：{ex.Message}");
        }
    }

    /// <summary>指定解释器能否成功 `import numpy`。</summary>
    private static async Task<bool> HasNumpyAsync(string pythonExe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe, "-c \"import numpy\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode == 0;
            }
            catch
            {
                // 超时/取消不会终止子进程（Process.Dispose 只释放句柄）：不补杀会留下孤儿
                // python/pip 继续在后台跑。按原语义返回 false。
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch { return false; }
    }

    /// <summary>指定解释器是否存在（`--version` 能跑通）。</summary>
    private static async Task<bool> HasPythonAsync(string pythonExe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode == 0;
            }
            catch
            {
                // 同 HasNumpyAsync：取消等待后必须补杀子进程，不留孤儿。
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch { return false; }
    }

    /// <summary>为嵌入式运行时安装 NumPy：先试 pip（若可用），无 pip 则下载 wheel 解压到
    /// site-packages。只写 bridge\python 内部目录，永不触碰系统解释器。</summary>
    private static async Task<bool> TryInstallNumpyAsync(string pythonExe, string bridgeDir, CancellationToken ct,
        Func<Task<bool>> ensureNetwork)
    {
        if (!await ensureNetwork()) return false;

        // pip 路径（完整嵌入式发行版若带 pip）
        try
        {
            var psi = new ProcessStartInfo(pythonExe, "-m pip install --no-warn-script-location numpy")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                    if (p.ExitCode == 0) return true;
                }
                catch
                {
                    // 超时/取消不杀子进程 → pip 孤儿继续向 site-packages 写入，与随后
                    // wheel 解压（overwriteFiles:true）并发写同一目录，可能装出损坏的
                    // NumPy。补杀后再按原语义走 wheel 兜底。
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { /* 无 pip → 走 wheel 解压 */ }

        // wheel 解压路径（嵌入式运行时：无 pip，直接把 wheel 展开到 site-packages）
        return await DownloadNumpyWheelAsync(pythonExe, bridgeDir, ct);
    }

    /// <summary>从 PyPI 取匹配当前解释器版本的 NumPy wheel，按 PyPI 元数据 sha256 校验后
    /// 解压到其 site-packages（v1.1.1：原先不校验任何指纹，下载损坏/被篡改直接落盘）。</summary>
    private static async Task<bool> DownloadNumpyWheelAsync(string pythonExe, string bridgeDir, CancellationToken ct)
    {
        string? tmpWheel = null;
        try
        {
            // 推断 site-packages 目录：只处理嵌入式形态（bridge/python/Lib/site-packages）
            string pyDir = Path.GetDirectoryName(pythonExe) ?? bridgeDir;
            string sitePackages = Path.Combine(pyDir, "Lib", "site-packages");
            if (!File.Exists(Path.Combine(pyDir, "python.exe")) && !Directory.Exists(Path.Combine(pyDir, "Lib")))
                return false;   // 非嵌入式目录形态，wheel 解压不适用
            Directory.CreateDirectory(sitePackages);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            // PyPI JSON：挑一个 cp312 win_amd64 的 wheel + 官方 digest
            string json = await http.GetStringAsync(PyPiNumpyJson, ct);
            string? wheelUrl = null;
            string? wheelSha256 = null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in urls.EnumerateArray())
                    {
                        string? fn = u.TryGetProperty("filename", out var f) ? f.GetString() : null;
                        string? url = u.TryGetProperty("url", out var l) ? l.GetString() : null;
                        string? digest = null;
                        if (u.TryGetProperty("digests", out var d) && d.TryGetProperty("sha256", out var s))
                            digest = s.GetString();
                        if (fn is not null && url is not null && digest is not null &&
                            fn.Contains("cp312") && fn.Contains("win_amd64") && fn.EndsWith(".whl"))
                        {
                            wheelUrl = url;
                            wheelSha256 = digest.ToLowerInvariant();
                            break;
                        }
                    }
                }
            }
            if (wheelUrl is null || wheelSha256 is null) return false;

            // 唯一临时名（v1.1.1：固定名并发跑两次校验/删除互踩）+ 校验后解压
            tmpWheel = Path.Combine(Path.GetTempPath(), "pptink-numpy-" + Path.GetRandomFileName() + ".whl");
            await using (var fs = File.Create(tmpWheel))
                await (await http.GetStreamAsync(wheelUrl, ct)).CopyToAsync(fs, ct);
            string actual = await Sha256FileAsync(tmpWheel, ct);
            if (!string.Equals(actual, wheelSha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Instance.Warn("NumPy wheel SHA256 校验失败，已丢弃下载文件");
                return false;
            }

            // wheel 本质是 zip：直接展开到 site-packages（哈希已验证完整性）
            ZipFile.ExtractToDirectory(tmpWheel, sitePackages, overwriteFiles: true);
            return true;
        }
        catch { return false; }
        finally
        {
            if (tmpWheel is not null) { try { File.Delete(tmpWheel); } catch { } }
        }
    }

    /// <summary>下载嵌入式 Python 运行时到 bridge/python：固定 URL + 内置 SHA256/字节数
    /// 双重校验（v1.1.1），配置 _pth 启用 site-packages，并装 NumPy。</summary>
    private static async Task<bool> ProvisionBundledAsync(string bridgeDir, IProgress<string>? progress, CancellationToken ct)
    {
        string? tmpZip = null;
        try
        {
            string pyDir = Path.Combine(bridgeDir, "python");
            Directory.CreateDirectory(pyDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            tmpZip = Path.Combine(Path.GetTempPath(), "pptink-python-embed-" + Path.GetRandomFileName() + ".zip");
            progress?.Report("下载嵌入式 Python 运行时……");
            await using (var fs = File.Create(tmpZip))
                await (await http.GetStreamAsync(EmbedPythonUrl, ct)).CopyToAsync(fs, ct);

            var fi = new FileInfo(tmpZip);
            if (fi.Length != EmbedPythonBytes)
            {
                AppLog.Instance.Warn($"嵌入式运行时字节数不符（{fi.Length} != {EmbedPythonBytes}），已丢弃");
                return false;
            }
            string sha = await Sha256FileAsync(tmpZip, ct);
            if (!string.Equals(sha, EmbedPythonSha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Instance.Warn("嵌入式运行时 SHA256 校验失败，已丢弃下载文件");
                return false;
            }

            progress?.Report("解压运行时……");
            ZipFile.ExtractToDirectory(tmpZip, pyDir, overwriteFiles: true);

            // 配置 _pth：启用 site-packages（嵌入式默认关闭）
            string pth = Path.Combine(pyDir, "python312._pth");
            File.WriteAllText(pth, "python312.zip\r\n.\r\nLib\\site-packages\r\nimport site\r\n");
            Directory.CreateDirectory(Path.Combine(pyDir, "Lib", "site-packages"));

            progress?.Report("下载并安装 NumPy……");
            string pythonExe = Path.Combine(pyDir, "python.exe");
            bool ok = await DownloadNumpyWheelAsync(pythonExe, bridgeDir, ct);
            return ok && await HasNumpyAsync(pythonExe, ct);
        }
        catch { return false; }
        finally
        {
            if (tmpZip is not null) { try { File.Delete(tmpZip); } catch { } }
        }
    }

    /// <summary>文件 SHA256（小写十六进制）。</summary>
    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
