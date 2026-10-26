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
///
/// v1.1.2 环境医生（<see cref="EnsureUsableAsync"/>）：不再"装完 NumPy 就返回成功"。
/// 逐环节自检 Python → NumPy → 识别模块 → 桥握手 → 合成识别；任一环失败按环节修复
/// （修正 _pth / 重装 NumPy 1.x / 重建运行时并回滚）后**全量复检**，直到端到端可用。
/// 与运行时 RestartProcess 使用同一解释器优先级（内置优先）与 UTF-8 启动环境。
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

    

    /// <summary>指定解释器能否成功 `import numpy`。</summary>
    private static async Task<bool> HasNumpyAsync(string pythonExe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe, "-c \"import numpy as _n; _n.zeros(2).sum(); print(_n.__version__)\"")
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
            var psi = new ProcessStartInfo(pythonExe, "-m pip install --no-warn-script-location \"numpy>=1.24,<2.0\"")
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

            // PyPI JSON：优先固定 1.26.4（cp312 win_amd64 有轮子，避免 numpy 2.x API 变化）；
            // 固定版本不可达时回退最新版，仍按 cp312 win_amd64 挑选 + sha256 校验。
            string json;
            try { json = await http.GetStringAsync("https://pypi.org/pypi/numpy/1.26.4/json", ct); }
            catch { json = await http.GetStringAsync(PyPiNumpyJson, ct); }
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

            // 配置 _pth：启用 site-packages（嵌入式默认关闭）。不写死 python312._pth，
            // 按解压出的实际文件名修正，避免版本/文件名变化导致 site-packages 不生效。
            TryRepairPth(pyDir);
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

    // ———————— v1.1.2：逐环节自检 + 修复循环（AI 环境医生） ————————

    /// <summary>
    /// 全面自检并修复，直到「端到端可用」：Python 可运行 → NumPy 可用 → 识别模块可导入 →
    /// 桥握手 → 合成圆形识别。任何一环失败都会触发对应修复并**重新全量复检**，
    /// 而不是"装完 NumPy 就返回成功、随后握手继续报错"。
    /// 修复手段：修正 _pth → 重装 NumPy（固定 1.x）→ 重建嵌入式运行时（失败回滚旧目录）。
    /// 每轮候选解释器与运行时 RestartProcess 保持一致（内置 python.exe 优先，其次 PATH 的 python）。
    /// </summary>
    public static async Task<Result> EnsureUsableAsync(string bridgeDir, IProgress<string>? progress,
        Func<string, Task<bool>>? confirmNetwork, CancellationToken ct)
    {
        var log = new List<string>();
        string? lastError = null;
        var repairs = new HashSet<string>(StringComparer.Ordinal);
        bool? granted = null;
        async Task<bool> EnsureNetworkAllowed(string what)
        {
            if (granted is bool g) return g;
            if (confirmNetwork is null) { granted = false; return false; }
            granted = await confirmNetwork(what);
            return granted.Value;
        }

        var script = Path.Combine(bridgeDir, "ink_bridge.py");
        var bundled = Path.Combine(bridgeDir, "python", "python.exe");

        for (int round = 1; round <= 10 && !ct.IsCancellationRequested; round++)
        {
            progress?.Report($"AI 环境自检（第 {round} 轮）……");

            if (!File.Exists(script))
                return new Result(false, "找不到桥接脚本：" + script + "（请重新生成包含 bridge 目录的完整便携包）");

            // 与运行时严格一致：内置 python.exe 存在就只用它（RestartProcess 也优先它），否则用 PATH 的 python。
            // 不能"内置坏了却退到系统 python"——那样自检能过、运行时仍用坏内置 → 必然握手失败。
            var candidates = new List<string>();
            if (File.Exists(bundled)) candidates.Add(bundled);
            else candidates.Add("python");

            string? usable = null;
            foreach (var exe in candidates)
            {
                if (!await HasPythonAsync(exe, ct)) { log.Add($"{exe}：无法运行"); continue; }
                if (!await HasNumpyAsync(exe, ct)) { log.Add($"{exe}：NumPy 不可用"); continue; }
                if (!await ImportOkAsync(exe, "pptink_recog", bridgeDir, ct)) { log.Add($"{exe}：识别模块导入失败"); continue; }
                usable = exe;
                break;
            }

            if (usable is not null)
            {
                progress?.Report("依赖齐全，正在验证桥握手与识别……");
                var probe = await BridgeProbe.RunAsync(bridgeDir, script, usable, ct);
                foreach (var s in probe.Stages) log.Add($"  · {s.Name}：{(s.Ok ? "通过" : "失败")} {s.Detail}");
                if (probe.Ok)
                {
                    var detail = log.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, log.TakeLast(12));
                    return new Result(true, "AI 环境可用。" + detail);
                }
                lastError = probe.Message;
            }
            else
            {
                lastError = "没有找到「Python + NumPy + 识别模块」齐备的解释器";
            }

            bool repaired = await RepairAsync(bridgeDir, bundled, repairs, progress, EnsureNetworkAllowed, ct, log);
            if (!repaired) break;
        }

        return new Result(false,
            "AI 环境仍不可用（已尝试自动修复）。" + Environment.NewLine +
            string.Join(Environment.NewLine, log.TakeLast(20)) + Environment.NewLine +
            "最后错误：" + lastError);
    }

    /// <summary>按当前状态选择并执行一种修复；返回是否真的做了动作（否则停止循环）。</summary>
    private static async Task<bool> RepairAsync(string bridgeDir, string bundled, HashSet<string> repairs,
        IProgress<string>? progress, Func<string, Task<bool>> ensureNetwork, CancellationToken ct, List<string> log)
    {
        string pyDir = Path.GetDirectoryName(bundled) ?? Path.Combine(bridgeDir, "python");

        if (!File.Exists(bundled))
        {
            if (!repairs.Add("provision")) return false;
            if (!await ensureNetwork($"AI 需要联网下载嵌入式 Python 3.12（约 11MB）与 NumPy（约 16MB），安装到 {bridgeDir}\\python（不影响系统环境）。"))
                return false;
            progress?.Report("下载并配置嵌入式 Python 运行时……");
            bool ok = await ProvisionBundledAsync(bridgeDir, progress, ct) && await HasNumpyAsync(bundled, ct);
            log.Add(ok ? "已下载并配置嵌入式 Python + NumPy" : "嵌入式运行时下载/配置失败");
            return ok;
        }

        // 内置运行时存在：修正 _pth → 重装 NumPy → 重建运行时（各尝试一次，避免死循环）
        if (repairs.Add("pth") && TryRepairPth(pyDir))
        {
            log.Add("已修正嵌入式运行时 _pth（启用 site-packages / import site）");
            return true;
        }
        if (repairs.Add("numpy"))
        {
            if (!await ensureNetwork($"AI 需要联网重新安装 NumPy（来自 PyPI，约 16MB），安装到 {bridgeDir}\\python。"))
                return false;
            progress?.Report("重新安装 NumPy……");
            bool ok = await TryInstallNumpyAsync(bundled, bridgeDir, ct, () => ensureNetwork("重新安装 NumPy"))
                      && await HasNumpyAsync(bundled, ct);
            log.Add(ok ? "已重新安装 NumPy" : "NumPy 重装失败");
            if (ok) return true;
        }
        if (repairs.Add("reprovision"))
        {
            if (!await ensureNetwork($"AI 运行时可能已损坏，需要联网重新下载嵌入式 Python（约 11MB）与 NumPy（约 16MB）。安装位置：{bridgeDir}\\python。"))
                return false;
            progress?.Report("重建嵌入式运行时……");
            bool ok = await TryReprovisionAsync(bridgeDir, progress, ct);
            log.Add(ok ? "已重建嵌入式 Python 运行时" : "重建运行时失败（已回滚旧目录）");
            return ok;
        }
        return false;
    }

    /// <summary>解释器能否导入指定模块（工作目录=bridge 目录，保证能找到本地识别模块）。</summary>
    private static async Task<bool> ImportOkAsync(string pythonExe, string module, string workDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe, $"-c \"import {module}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };
            BridgeProbe.ApplyPythonEnv(psi);
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
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch { return false; }
    }

    /// <summary>确保嵌入式 _pth 启用 site-packages（原实现写死 python312._pth）。返回是否改动。</summary>
    private static bool TryRepairPth(string pyDir)
    {
        try
        {
            if (!Directory.Exists(pyDir)) return false;
            var pth = Directory.GetFiles(pyDir, "python*._pth").FirstOrDefault()
                      ?? Path.Combine(pyDir, "python312._pth");
            var zipName = Path.GetFileNameWithoutExtension(pth) + ".zip";
            var wanted = new[] { zipName, ".", "Lib\\site-packages", "import site" };

            var lines = File.Exists(pth)
                ? File.ReadAllLines(pth).Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            bool changed = false;
            foreach (var w in wanted)
                if (!lines.Contains(w, StringComparer.OrdinalIgnoreCase)) { lines.Add(w); changed = true; }
            if (!changed) return false;

            File.WriteAllText(pth, string.Join("\r\n", lines) + "\r\n");
            return true;
        }
        catch { return false; }
    }

    /// <summary>重建嵌入式运行时：旧目录改名备份 → 重新下载配置 → 失败回滚。</summary>
    private static async Task<bool> TryReprovisionAsync(string bridgeDir, IProgress<string>? progress, CancellationToken ct)
    {
        string pyDir = Path.Combine(bridgeDir, "python");
        string backup = pyDir + ".broken-" + Guid.NewGuid().ToString("N");
        bool moved = false;
        try
        {
            if (Directory.Exists(pyDir))
            {
                Directory.Move(pyDir, backup);
                moved = true;
            }
            bool ok = await ProvisionBundledAsync(bridgeDir, progress, ct)
                      && await HasNumpyAsync(Path.Combine(pyDir, "python.exe"), ct);
            if (ok)
            {
                if (moved) { try { Directory.Delete(backup, true); } catch { } }
                return true;
            }
            try { if (Directory.Exists(pyDir)) Directory.Delete(pyDir, true); } catch { }
            if (moved && Directory.Exists(backup))
            {
                try { Directory.Move(backup, pyDir); } catch { }
            }
            return false;
        }
        catch
        {
            try { if (moved && Directory.Exists(backup) && !Directory.Exists(pyDir)) Directory.Move(backup, pyDir); } catch { }
            return false;
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
