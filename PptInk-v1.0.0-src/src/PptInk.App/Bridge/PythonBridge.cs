using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PptInk.Ink;

namespace PptInk.Bridge;

/// <summary>
/// C# ↔ Python 桥（AI1.0 形状识别）：
///  - 传输：子进程 stdio 行式 JSON（WriteLine/ReadLine 一一对应）。
///    审查修复：原实现 C# 用 NamedPipe 的文本行、Python 用 multiprocessing.connection 的
///    pickle 二进制帧 —— 两端字节协议根本不兼容（发的 JSON 被当 pickle 解析报错、
///    回的 pickle 帧被当文本读取），AI 形状规整从未真正工作。改 stdio 后：
///    无命名管道依赖、无第三方库、进程退出即断开、握手即时。
///  - 懒加载：S0 绝不启动 Python；首次启用 AI（设置开关）才拉起并常驻；
///  - 崩溃自愈：子进程退出后下个请求自动重启（最多 3 次/分钟，防死循环拉起）。
/// </summary>
public sealed class PythonBridge : IDisposable
{
    private const string BridgeId = "pptink-bridge-v1";

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _restartsThisMinute;
    private DateTime _minuteMark = DateTime.UtcNow;
    private volatile bool _disposed;
    private readonly object _gate = new();
    /// <summary>读写串行闸：并发识别请求排队，JSON 行不交错。</summary>
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public bool IsRunning => _process is { HasExited: false } && _stdin is not null && _stdout is not null;

    /// <summary>Python 桥根目录：按候选路径顺序探测 ink_bridge.py 实际位置。
    /// v0.9.7 修复：原先只查 &lt;exe&gt;\bridge\ 单一路径，便携包缺 bridge 目录时静默回退
    /// exeDir（那里没有脚本 → "找不到桥接脚本"），开发仓库（python\ink_bridge.py）也
    /// 同样探测不到。现按以下顺序探测，第一个含 ink_bridge.py 的目录胜出：
    ///   1) &lt;exe&gt;\bridge\           便携包标准布局（含嵌入式 python\ 子目录）
    ///   2) &lt;exe&gt;\python\           便携包变体 / 开发直运（dotnet run）
    ///   3) &lt;exe&gt;\..\python\        开发构建（bin\Debug\net8.0\ → 仓库 python\）
    ///   4) &lt;exe&gt;\                  扁平布局（脚本与 exe 同级）
    /// </summary>
    private static string BridgeDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(exeDir, "bridge"),
                Path.Combine(exeDir, "python"),
                Path.GetFullPath(Path.Combine(exeDir, "..", "python")),
                Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "python")),  // bin\{cfg}\net8.0 → 仓库根
                exeDir,
            };
            foreach (var dir in candidates)
                if (File.Exists(Path.Combine(dir, "ink_bridge.py")))
                    return dir;
            // 全部未命中：回退首选布局（让 CheckHealthAsync 给出"找不到桥接脚本"的明确路径）
            return candidates[0];
        }
    }

    /// <summary>懒拉起子进程（异步，UI 安全；S0 不调用则永不驻留内存）。
    /// 进程冷启动 + numpy import 最长可达 ~15s，全部在池线程执行、异常仅记日志：
    /// 设置开关回调与识别请求都不会冻结界面。</summary>
    public Task EnsureStartedAsync()
    {
        // Check the power switch both before queuing and again under the process lock. An
        // in-flight recognition must not revive Python after the user turns AI off.
        if (_disposed || !Core.AppConfig.Current.AiEnabled || IsRunning) return Task.CompletedTask;
        return Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !Core.AppConfig.Current.AiEnabled || IsRunning) return;
                try { RestartProcess(); }
                catch (Exception ex) { Core.AppLog.Instance.Error(ex, "AI 桥拉起失败（限流或启动超时）"); }
            }
        });
    }

    private void RestartProcess()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonBridge));
        // 限流：一分钟内最多 3 次（桥崩溃风暴保护）
        if (DateTime.UtcNow - _minuteMark > TimeSpan.FromMinutes(1)) { _restartsThisMinute = 0; _minuteMark = DateTime.UtcNow; }
        if (++_restartsThisMinute > 3) throw new InvalidOperationException("Python 推理进程反复崩溃，已暂停拉起");

        Cleanup();

        var script = Path.Combine(BridgeDir, "ink_bridge.py");
        var bundledPython = Path.Combine(BridgeDir, "python", "python.exe"); // 嵌入式运行时
        // v0.9.7：嵌入式运行时缺失不再静默回退系统 Python —— 系统 Python 可能未装 NumPy，
        // 此时握手能过、recognize 会报 "numpy import error" 但 C# 侧只看到 "桥返回空"，
        // 用户收到的是"AI 桥未检测到"而非真正的依赖缺失原因。明确区分两类环境。
        bool hasBundled = File.Exists(bundledPython);
        var pythonExe = hasBundled ? bundledPython : "python";
        if (!File.Exists(script)) throw new FileNotFoundException($"AI 桥脚本缺失：{script}");
        if (!hasBundled)
            Core.AppLog.Instance.Warn("AI 桥使用系统 Python（bridge\\python\\python.exe 不存在）；若系统 Python 缺 NumPy，识别会失败。请运行设置中心「快速检测 AI 功能」验证依赖。");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"-u \"{script}\"",   // -u：禁用 stdout 缓冲，协议行即时到达
                WorkingDirectory = BridgeDir,
                CreateNoWindow = true,           // 静默：无黑框
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };
        try
        {
            _process.Start();
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;
            _stdout = _process.StandardOutput;
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Core.AppLog.Instance.Warn($"[AI桥] {e.Data}"); };
            _process.BeginErrorReadLine();

            // 握手：子进程就绪即回 ok（numpy import 可能耗时数秒 → 带超时，避免无限等待）
            // v0.9.7：15s → 25s。嵌入式 Python 冷启动 + NumPy import 在低配大屏上实测
            // 可达 18~22s（尤其首次运行、杀毒软件扫描时），原 15s 超时把正常环境误判为
            // "握手超时"，这是用户反馈"AI bridge 未检测到"的最常见根因。
            _stdin.WriteLine(JsonSerializer.Serialize(new { cmd = "hello", client = BridgeId }));
            var readTask = _stdout.ReadLineAsync();
            if (!readTask.Wait(TimeSpan.FromSeconds(25)))
                throw new TimeoutException("AI 桥握手超时（25 秒）：Python 或 NumPy 未就绪。请运行设置中心「快速检测 AI 功能」查看具体原因。");
            var hello = readTask.Result;
            if (hello is null || !hello.Contains("ok"))
                throw new IOException("AI 桥握手协议异常");
        }
        catch
        {
            // Startup failures (including a timed-out handshake) must not leave an apparently
            // running but unusable child that blocks all future retry attempts.
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// 独立健康检查：不改变 AI 开关、不接管正式常驻进程，启动短命桥进程并执行一次合成圆形识别。
    /// 这样用户即使暂时关闭 AI，也可验证 Python/NumPy/桥协议/识别算法能否完整工作。
    /// </summary>
    public async Task<string> CheckHealthAsync(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        var dir = BridgeDir;
        var script = Path.Combine(dir, "ink_bridge.py");
        if (!File.Exists(script))
            return $"AI 检测失败：找不到桥接脚本。{Environment.NewLine}预期位置：{script}{Environment.NewLine}请重新生成包含 bridge 目录的完整便携包。";

        progress?.Report("正在检测运行环境……");
        // v1.0.0：先检测一次；失败则自动配置运行环境（补 NumPy / 下载嵌入式 Python），再复检一次。
        var first = await ProbeAsync(dir, script, cancellationToken);
        if (first.Ok) return first.Message;

        progress?.Report("环境不完整，自动配置中……");
        var provision = await AiProvisioner.EnsureAsync(dir, progress, cancellationToken);
        if (!provision.Ok)
            return first.Message + Environment.NewLine + Environment.NewLine +
                   "自动配置未能修复：" + provision.Message;

        progress?.Report("配置完成，复检中……");
        var second = await ProbeAsync(dir, script, cancellationToken);
        return second.Ok
            ? second.Message + Environment.NewLine + Environment.NewLine +
              "（已自动配置运行环境：" + provision.Message + "）"
            : first.Message + Environment.NewLine + Environment.NewLine +
              "自动配置后复检仍未通过：" + second.Message;
    }

    /// <summary>单次探测：启动短命桥进程，跑一次 hello 握手 + 合成圆识别。(Ok, Message)。</summary>
    private async Task<(bool Ok, string Message)> ProbeAsync(string dir, string script, CancellationToken cancellationToken)
    {
        var bundledPython = Path.Combine(dir, "python", "python.exe");
        var pythonExe = File.Exists(bundledPython) ? bundledPython : "python";
        var stderr = new System.Text.StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"-u \"{script}\"",
                WorkingDirectory = dir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return (false, "AI 检测失败：无法启动 Python 进程。");
            process.BeginErrorReadLine();
            process.StandardInput.AutoFlush = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // v0.9.7：18s → 28s（与 RestartProcess 25s 握手对齐 + 识别预留），
            // 嵌入式运行时冷启动在病毒扫描/机械硬盘环境下可超过原 18s 阈值。
            timeout.CancelAfter(TimeSpan.FromSeconds(28));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { cmd = "hello", client = BridgeId }));
            var helloLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(helloLine))
                throw new IOException("桥接进程没有返回握手响应。");

            bool cv2Available = false;
            using (var helloDoc = JsonDocument.Parse(helloLine))
            {
                var hello = helloDoc.RootElement;
                if (!hello.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                    !hello.TryGetProperty("server", out var server) || server.GetString() != "pptink-bridge")
                    throw new IOException("AI 桥握手协议不匹配。");
                cv2Available = hello.TryGetProperty("cv2", out var cv2) && cv2.ValueKind == JsonValueKind.True;
            }

            var points = Enumerable.Range(0, 48)
                .Select(i => new[]
                {
                    Math.Round(100 + 80 * Math.Cos(2 * Math.PI * i / 48), 1),
                    Math.Round(100 + 80 * Math.Sin(2 * Math.PI * i / 48), 1),
                }).ToArray();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { cmd = "recognize", points }));
            var recognitionLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(recognitionLine))
                throw new IOException("桥接进程没有返回识别结果。");

            string recognizedShape;
            double confidence;
            using (var recDoc = JsonDocument.Parse(recognitionLine))
            {
                var rec = recDoc.RootElement;
                if (!rec.TryGetProperty("ok", out var recOk) || recOk.ValueKind != JsonValueKind.True ||
                    !rec.TryGetProperty("shape", out var shape) || shape.GetString() != "circle")
                    throw new IOException("桥接已启动，但合成圆形识别未通过；请检查 NumPy 与识别脚本版本。响应：" + recognitionLine);
                recognizedShape = shape.GetString() ?? "未知";
                confidence = rec.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var n) ? n : 0;
            }

            return (true, string.Join(Environment.NewLine, new[]
            {
                "AI 功能检测通过。",
                "Python 桥：正常握手",
                "NumPy 与识别算法：正常",
                $"合成圆形识别：{recognizedShape}，置信度 {confidence:P1}",
                $"运行时：{(File.Exists(bundledPython) ? "应用内嵌 Python" : "系统 PATH 中的 Python")}",
                $"可选 OpenCV：{(cv2Available ? "可用" : "未启用（基础识别仍可运行）")}",
            }));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "AI 检测超时：Python/NumPy 启动或识别超过 28 秒。请检查运行时目录、依赖和安全软件拦截（可将 bridge\\ 目录加入杀毒软件白名单）。");
        }
        catch (Exception ex)
        {
            string diagnostic;
            lock (stderr) diagnostic = stderr.ToString().Trim();
            return (false, "AI 检测失败：" + ex.Message + (string.IsNullOrWhiteSpace(diagnostic)
                ? ""
                : Environment.NewLine + "Python 输出：" + Environment.NewLine + diagnostic));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>识别结果：形状名 + 先验置信度 + 规整点序列（替换笔迹用）。</summary>
    public sealed record RecognizedShape(string Shape, double Confidence, List<InkPoint> Points);

    /// <summary>
    /// 识别一笔手绘（纯 IO，不含 UI 决策）：编排器决定呈现方式——
    /// 自动校正即刻替换（快）/ 气泡确认（ShapeAssistBubble）。
    /// 灵敏度系数调制全部容差：宽松 = 手绘随意也规整。
    /// 返回 (shape, bridgeOk)：bridgeOk=false 表示桥不可用（区别于"不是形状"）。
    /// </summary>
    public async Task<(RecognizedShape? Shape, bool BridgeOk)> RecognizeAsync(StrokeData target)
    {
        try
        {
            await EnsureStartedAsync();
            if (!IsRunning) return (null, false);                        // 拉起失败（限流/超时），桥不可达
            double k = Math.Clamp(Core.AppConfig.Current.RecognitionSensitivity, 0.75, 1.3);
            // 协议 v2：options 与识别引擎（pptink_recog）参数一一对应；灵敏度 k 同时调制
            // 全部容差（宽松 → 容差放大 → 手绘更随意也能规整）。引擎侧仍兼容旧字段。
            var req = new
            {
                cmd = "recognize",
                points = target.Points.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) }).ToArray(),
                options = new
                {
                    closure_tol = 0.18 * k,        // 闭合判定容差
                    line_dev_ratio = 0.022 * k,    // 直线性容差（略弯也认直线）
                    corner_turn_min = 52.0 / k,    // 角点转向阈值（宽松 → 更钝的转角也算角）
                    tolerances = new               // 各形状残差尺度（统一乘 k）
                    {
                        circle = 0.045 * k, ellipse = 0.055 * k, triangle = 0.055 * k,
                        quad = 0.070 * k, polygon = 0.060 * k, arrow = 0.060 * k,
                        check = 0.055 * k, cross = 0.055 * k, wave = 0.075 * k,
                    },
                },
            };
            var resp = await SendAsync(JsonSerializer.Serialize(req));
            if (resp is null) return (null, false);                      // 桥不可达（含重启重试后失败）
            if (!resp.Value.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return (null, true);

            var shape = resp.Value.GetProperty("shape").GetString();
            var confidence = resp.Value.GetProperty("confidence").GetDouble();
            var pointValues = resp.Value.GetProperty("points").EnumerateArray()
                .Select(a => (X: a[0].GetDouble(), Y: a[1].GetDouble()))
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                .ToArray();
            if (pointValues.Length < 2) return (null, true);

            // InkPoint timestamps use InkClock's QPC-millisecond domain. Reconstructed points
            // are geometric samples, so distribute the original stroke's time interval evenly;
            // stamping all points with TickCount64 (a different clock) made tapered width and
            // dropout filtering depend on processing speed instead of the original gesture.
            long startTick = target.Points.Count > 0 ? target.Points[0].TimestampTicks : InkClock.NowMs();
            long endTick = target.Points.Count > 1 ? target.Points[^1].TimestampTicks : startTick + pointValues.Length - 1;
            if (endTick <= startTick) endTick = startTick + pointValues.Length - 1;
            var points = pointValues.Select((p, i) => new InkPoint(
                p.X, p.Y, 0.6,
                startTick + (long)((endTick - startTick) * (i / (double)(pointValues.Length - 1)))))
                .ToList();
            return (new RecognizedShape(shape ?? "shape", confidence, points), true);
        }
        catch (Exception ex)
        {
            Core.AppLog.Instance.Error(ex, "AI 桥请求异常");
            return (null, false);
        }
    }

    /// <summary>请求-响应（行式 JSON）。桥不可达时自动重启一次。
    /// 串行化：自动气泡与手动触发并发时同一时刻仅一个请求在流上读写，JSON 行不交错；
    /// 重连放池线程（await 续体可能回到 UI 线程，同步 RestartProcess 会长时间阻塞界面）。</summary>
    private async Task<JsonElement?> SendAsync(string line)
    {
        if (!await _ioGate.WaitAsync(TimeSpan.FromSeconds(8))) return null;   // 排队上限：桥卡死时不无限堆积
        try
        {
            if (_disposed || !Core.AppConfig.Current.AiEnabled) return null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!IsRunning) await EnsureStartedAsync();
                    if (_stdin is null || _stdout is null) throw new IOException("AI 桥不可用");
                    _stdin.WriteLine(line);
                    // A crashed/hung recognizer must not keep the semaphore (and future AI work)
                    // blocked forever. On timeout the normal recovery path kills/restarts the child.
                    using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var respLine = await _stdout.ReadLineAsync(responseTimeout.Token);
                    if (string.IsNullOrEmpty(respLine)) throw new IOException("桥返回空");
                    return JsonSerializer.Deserialize<JsonElement>(respLine);
                }
                catch (Exception ex)
                {
                    Core.AppLog.Instance.Error(ex, $"AI 桥通信失败（第 {attempt + 1} 次）");
                    // AI Power 已关闭：绝不再重连（否则"关闭即释放进程"会被在途请求复活）。
                    if (_disposed || !Core.AppConfig.Current.AiEnabled) return null;
                    await Task.Run(() =>
                    {
                        lock (_gate)
                        {
                            if (_disposed || !Core.AppConfig.Current.AiEnabled) return; // 关闭/退出后绝不复活子进程
                            try { RestartProcess(); }
                            catch (Exception rex) { Core.AppLog.Instance.Error(rex, "AI 桥重连失败"); }
                        }
                    });
                }
            }
            return null;
        }
        finally { _ioGate.Release(); }
    }

    private void Cleanup()
    {
        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        _stdin = null;
        _stdout = null;
        _process = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }
    }

    /// <summary>
    /// 释放本地推理子进程（AI Power 关闭时调用）：进程退出、内存归还；桥对象保留，
    /// 再次 EnsureStartedAsync 即可重新拉起。幂等、线程安全。正在进行的识别请求会因
    /// 管道关闭而按「桥不可用」返回（RecognizeAsync 全防御，不会抛给调用方）。
    /// </summary>
    public Task StopAsync()
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                if (_process is null) return;
                Cleanup();
                Core.AppLog.Instance.Info("AI 桥已停止（AI Power 关闭，释放子进程）");
            }
        });
    }
}
