using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PptInk.Bridge;

/// <summary>
/// AI 运行环境自动配置（v0.9.9）。
///
/// 目标：用户开启 AI 识别后，无论机器环境如何，程序自行把必需项补齐，直到识别真的可用。
/// 检测 → 修复 → 复检，逐级降级：
///   1) 内置嵌入式运行时（便携包自带）+ NumPy → 直接用；
///   2) 内置 Python 在、NumPy 缺 → 为它装 NumPy（pip；嵌入式无 pip 则下载 wheel 解压）；
///   3) 无内置 → 探测系统 Python（python/python3/py），有 NumPy 用，缺则装；
///   4) 全无 → 从 python.org 下载嵌入式运行时，再从 PyPI 取 NumPy wheel 解压到 site-packages。
/// 全程后台线程执行，不冻结 UI；任一步骤失败都返回可读的原因，不静默吞掉。
/// </summary>
internal static class AiProvisioner
{
    // 嵌入式运行时来源（与便携包打包口径一致：CPython 3.12 embeddable win-amd64）
    private const string EmbedPythonUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip";
    private const string PyPiNumpyJson = "https://pypi.org/pypi/numpy/json";
    private const int TimeoutSeconds = 90;

    public sealed record Result(bool Ok, string Message);

    /// <summary>确保 AI 环境可用（检测 + 自动补齐 + 复检）。</summary>
    public static async Task<Result> EnsureAsync(string bridgeDir, IProgress<string>? progress, CancellationToken ct)
    {
        string bundled = Path.Combine(bridgeDir, "python", "python.exe");
        try
        {
            // 1) 内置运行时 + NumPy 齐备 —— 便携包默认路径
            if (File.Exists(bundled))
            {
                if (await HasNumpyAsync(bundled, ct))
                    return new Result(true, "内置 Python 运行时可用");
                progress?.Report("内置 Python 缺少 NumPy，正在安装……");
                if (await TryInstallNumpyAsync(bundled, bridgeDir, ct) && await HasNumpyAsync(bundled, ct))
                    return new Result(true, "已为内置 Python 安装 NumPy");
            }

            // 2) 系统 Python（开发模式 / 未打便携包）
            foreach (var sys in new[] { "python", "python3", "py" })
            {
                if (!await HasPythonAsync(sys, ct)) continue;
                if (await HasNumpyAsync(sys, ct))
                    return new Result(true, $"使用系统 Python（{sys}，已含 NumPy）");
                progress?.Report($"系统 Python（{sys}）缺少 NumPy，正在安装……");
                if (await TryInstallNumpyAsync(sys, bridgeDir, ct) && await HasNumpyAsync(sys, ct))
                    return new Result(true, $"已为系统 Python（{sys}）安装 NumPy");
            }

            // 3) 全无 —— 下载嵌入式运行时 + NumPy
            progress?.Report("未检测到可用 Python，正在下载嵌入式运行时……");
            if (await ProvisionBundledAsync(bridgeDir, progress, ct) && await HasNumpyAsync(bundled, ct))
                return new Result(true, "已下载并配置嵌入式 Python 运行时");

            return new Result(false, "无法自动配置 AI 环境（网络不可用或权限不足）。请手动安装 Python 3.10+ 与 NumPy。");
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
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
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
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>为指定解释器安装 NumPy：先试 pip，嵌入式无 pip 则下载 wheel 解压到 site-packages。</summary>
    private static async Task<bool> TryInstallNumpyAsync(string pythonExe, string bridgeDir, CancellationToken ct)
    {
        // pip 路径（完整 CPython / 系统 Python 通常可用）
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
                await p.WaitForExitAsync(cts.Token);
                if (p.ExitCode == 0) return true;
            }
        }
        catch { /* 嵌入式 Python 无 pip → 走 wheel 解压 */ }

        // wheel 解压路径（嵌入式运行时：无 pip，直接把 wheel 展开到 site-packages）
        return await DownloadNumpyWheelAsync(pythonExe, bridgeDir, ct);
    }

    /// <summary>从 PyPI 取匹配当前解释器版本的 NumPy wheel，解压到其 site-packages。</summary>
    private static async Task<bool> DownloadNumpyWheelAsync(string pythonExe, string bridgeDir, CancellationToken ct)
    {
        try
        {
            // 推断 site-packages 目录：内置运行时 = bridge/python/Lib/site-packages；
            // 系统 Python 交给 pip 处理，这里只处理嵌入式形态。
            string pyDir = Path.GetDirectoryName(pythonExe) ?? bridgeDir;
            string sitePackages = Path.Combine(pyDir, "Lib", "site-packages");
            if (!File.Exists(Path.Combine(pyDir, "python.exe")) && !Directory.Exists(Path.Combine(pyDir, "Lib")))
                return false;   // 非嵌入式目录形态，wheel 解压不适用
            Directory.CreateDirectory(sitePackages);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PptInk/0.9.9");

            // PyPI JSON：挑一个 cp312 win_amd64 的 wheel
            string json = await http.GetStringAsync(PyPiNumpyJson, ct);
            using var doc = JsonDocument.Parse(json);
            string? wheelUrl = null;
            if (doc.RootElement.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in urls.EnumerateArray())
                {
                    string? fn = u.TryGetProperty("filename", out var f) ? f.GetString() : null;
                    string? url = u.TryGetProperty("url", out var l) ? l.GetString() : null;
                    if (fn is not null && url is not null &&
                        fn.Contains("cp312") && fn.Contains("win_amd64") && fn.EndsWith(".whl"))
                    {
                        wheelUrl = url;
                        break;
                    }
                }
            }
            if (wheelUrl is null) return false;

            string tmpWheel = Path.Combine(Path.GetTempPath(), "pptink-numpy.whl");
            await using (var fs = File.Create(tmpWheel))
                await (await http.GetStreamAsync(wheelUrl, ct)).CopyToAsync(fs, ct);

            // wheel 本质是 zip：直接展开到 site-packages
            ZipFile.ExtractToDirectory(tmpWheel, sitePackages, overwriteFiles: true);
            try { File.Delete(tmpWheel); } catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>下载嵌入式 Python 运行时到 bridge/python，配置 _pth 启用 site-packages，并装 NumPy。</summary>
    private static async Task<bool> ProvisionBundledAsync(string bridgeDir, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            string pyDir = Path.Combine(bridgeDir, "python");
            Directory.CreateDirectory(pyDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PptInk/0.9.9");

            string tmpZip = Path.Combine(Path.GetTempPath(), "pptink-python-embed.zip");
            progress?.Report("下载嵌入式 Python 运行时……");
            await using (var fs = File.Create(tmpZip))
                await (await http.GetStreamAsync(EmbedPythonUrl, ct)).CopyToAsync(fs, ct);

            progress?.Report("解压运行时……");
            ZipFile.ExtractToDirectory(tmpZip, pyDir, overwriteFiles: true);
            try { File.Delete(tmpZip); } catch { }

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
    }
}