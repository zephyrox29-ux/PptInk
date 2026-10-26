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

    /// <summary>单次请求最多发送的点数。超长笔画（4096+ 点）整段序列化 / 传输 / 解析会明显
    /// 抬高首笔延迟，而识别引擎自身会按 4px 重采样 —— 等距抽稀到该上限不损失几何特征，
    /// 只把 IPC 成本压回毫秒级。</summary>
    private const int MaxSentPoints = 1200;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _restartsThisMinute;
    private DateTime _minuteMark = DateTime.UtcNow;
    /// <summary>首次拉起不占崩溃限流额度（冷启动失败不等于崩溃风暴）。</summary>
    private bool _startedOnce;
    private volatile bool _disposed;
    private readonly object _gate = new();
    /// <summary>读写串行闸：并发识别请求排队，JSON 行不交错。</summary>
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    /// <summary>最近一次"桥不可用"的可读原因（UI 直接展示，替代笼统的“未就绪”）。
    /// 成功握手后清空。</summary>
    public string? LastUnavailableReason { get; private set; }

    public bool IsRunning => _process is { HasExited: false } && _stdin is not null && _stdout is not null;

    /// <summary>依赖缺失（NumPy / 识别模块）专用异常：这类失败不计入崩溃限流，
    /// 否则用户装好依赖后还被“暂停拉起”卡一分钟。</summary>
    private sealed class BridgeUnavailableException : Exception
    {
        public BridgeUnavailableException(string message) : base(message) { }
    }

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
            var portable = Path.Combine(exeDir, "bridge");
            if (File.Exists(Path.Combine(portable, "ink_bridge.py"))) return portable;

            // 开发布局（v1.1.0 修复）：从 exe 所在目录逐级向上找 python\ink_bridge.py。
            // 原先写死 "..\..\..\python"（注释声称到仓库根）—— 实际 exe 落在
            // bin\{cfg}\net8.0-windows\，到仓库根要 4~5 级，dotnet run 下 AI 自检永远
            // "找不到桥接脚本"。逐级探测不依赖目录层级，加 TFM 子目录后也不会再漏。
            for (var dir = new DirectoryInfo(exeDir); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "python", "ink_bridge.py")))
                    return Path.Combine(dir.FullName, "python");

            // 全部未命中：回退便携布局（让 CheckHealthAsync 给出"找不到桥接脚本"的明确路径）
            return portable;
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
                catch (Exception ex)
                {
                    LastUnavailableReason = ex is BridgeUnavailableException or FileNotFoundException or TimeoutException
                        ? ex.Message
                        : $"AI 桥拉起失败：{ex.Message}";
                    Core.AppLog.Instance.Error(ex, "AI 桥拉起失败（限流或启动超时）");
                }
            }
        });
    }

    private void RestartProcess()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonBridge));
        // 限流：一分钟内最多 3 次（桥崩溃风暴保护）。首次拉起不计数。
        if (DateTime.UtcNow - _minuteMark > TimeSpan.FromMinutes(1)) { _restartsThisMinute = 0; _minuteMark = DateTime.UtcNow; }
        if (_startedOnce && ++_restartsThisMinute > 3) throw new InvalidOperationException("Python 推理进程反复崩溃，已暂停拉起（1 分钟内最多重试 3 次；请先运行「快速检测 AI 功能」）");
        _startedOnce = true;

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

        var psi = new ProcessStartInfo
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
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        BridgeProbe.ApplyPythonEnv(psi);   // v1.1.2：与自检同口径，强制 UTF-8 / 无缓冲
        _process = new Process { StartInfo = psi };
        try
        {
            _process.Start();
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;
            _stdout = _process.StandardOutput;
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Core.AppLog.Instance.Warn($"[AI桥] {e.Data}"); };
            _process.BeginErrorReadLine();

            // 握手：子进程就绪即回 ok（numpy import 可能耗时数秒 → 带超时，避免无限等待）
            // v0.9.7：15s → 35s。嵌入式 Python 冷启动 + NumPy import 在低配大屏上实测
            // 可达 18~22s（尤其首次运行、杀毒软件扫描时），原 15s 超时把正常环境误判为
            // "握手超时"，这是用户反馈"AI bridge 未检测到"的最常见根因。
            _stdin.WriteLine(JsonSerializer.Serialize(new { cmd = "hello", client = BridgeId, version = 3 }));
            var hello = ReadLineOrDispose("AI 桥握手", 35_000);
            if (hello is null)
                throw new IOException("AI 桥握手无响应");

            // v1.1.1：结构化校验取代 Contains("ok") —— 子进程向 stdout 打印的任何含 "ok"
            // 的告警/告错文本（如 "broken pipe" 之类）都会被旧判据当成握手成功。
            // v1.1.2：同时读取依赖可用性，把"缺 NumPy"从"桥返回空"里区分出来。
            bool handshakeOk = false;
            string? dependencyProblem = null;
            try
            {
                using var doc = JsonDocument.Parse(hello);
                var root = doc.RootElement;
                handshakeOk = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                if (handshakeOk)
                    dependencyProblem = DescribeDependencyProblem(root);
            }
            catch (JsonException) { handshakeOk = false; }
            if (!handshakeOk)
                throw new IOException("AI 桥握手协议异常");
            if (dependencyProblem is not null)
                throw new BridgeUnavailableException(dependencyProblem);

            LastUnavailableReason = null;
            WarmUp();
        }
        catch (Exception ex)
        {
            // 依赖缺失是可修复的环境问题，不该让用户装完 NumPy 还被崩溃限流挡在门外。
            if (ex is BridgeUnavailableException) _restartsThisMinute = Math.Max(0, _restartsThisMinute - 1);
            // Startup failures (including a timed-out handshake) must not leave an apparently
            // running but unusable child that blocks all future retry attempts.
            Cleanup();
            throw;
        }
    }

    /// <summary>把桥握手里的依赖可用性翻译成可执行的中文指引。</summary>
    private static string? DescribeDependencyProblem(JsonElement hello)
    {
        bool numpyOk = !hello.TryGetProperty("numpy", out var numpyEl) || numpyEl.ValueKind != JsonValueKind.False;
        bool recognizerOk = !hello.TryGetProperty("recognizer", out var recEl) || recEl.ValueKind != JsonValueKind.False;
        if (numpyOk && recognizerOk) return null;
        string detail = hello.TryGetProperty("numpy_error", out var ne) && ne.ValueKind == JsonValueKind.String
            ? ne.GetString() ?? "" : "";
        if (!numpyOk)
            return "AI 桥缺少 NumPy：当前 Python 环境没有安装 numpy。请到 设置 → AI 功能 → 快速检测 AI 功能，让工具自动补齐依赖。"
                 + (string.IsNullOrWhiteSpace(detail) ? "" : $"（{detail}）");
        return "AI 桥的识别模块加载失败（pptink_recog 无法导入）。请在 设置 → AI 功能 → 快速检测 AI 功能 中一键修复。";
    }

    /// <summary>预热：让 NumPy 的首次调用开销在课堂开始前付掉。命令行已完整读回，
    /// 不会与后续请求错位；失败只记日志，不改变桥可用状态。</summary>
    private void WarmUp()
    {
        try
        {
            _stdin!.WriteLine("{\"cmd\":\"warmup\"}");
            var line = ReadLineOrDispose("AI 桥预热", 8_000);
            if (!string.IsNullOrWhiteSpace(line)) using (JsonDocument.Parse(line)) { }
        }
        catch (Exception ex) when (ex is not TimeoutException and not ObjectDisposedException)
        {
            Core.AppLog.Instance.Warn($"AI 桥预热未完成（不影响识别）：{ex.GetType().Name}");
        }
    }

    /// <summary>读取一行协议响应（带超时），随时可被 Dispose 置位的 _disposed 打断。
    /// 教训：此前持锁同步 Wait(25s) 不可打断 —— 退出路径的 Dispose 在 _gate 上排队等完整
    /// 冷启动，UI 线程最长卡 25s。分片轮询让 Dispose 置位后握手立即放弃、锁即刻释放。</summary>
    private string? ReadLineOrDispose(string stage, int timeoutMs)
    {
        var readTask = _stdout!.ReadLineAsync();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!readTask.IsCompleted)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PythonBridge));
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"{stage}超时（{timeoutMs / 1000} 秒）：Python 或 NumPy 未就绪。请运行设置中心「快速检测 AI 功能」查看具体原因。");
            readTask.Wait(100);
        }
        return readTask.Result;
    }

    /// <summary>
    /// 独立健康检查：不改变 AI 开关、不接管正式常驻进程，启动短命桥进程并执行一次合成圆形识别。
    /// 这样用户即使暂时关闭 AI，也可验证 Python/NumPy/桥协议/识别算法能否完整工作。
    /// confirmNetwork（v1.1.1）：联网下载/装包前的授权回调（拒绝则不联网），见 AiProvisioner。
    /// </summary>
    public async Task<string> CheckHealthAsync(CancellationToken cancellationToken = default, IProgress<string>? progress = null,
        Func<string, Task<bool>>? confirmNetwork = null)
    {
        var dir = BridgeDir;
        var script = Path.Combine(dir, "ink_bridge.py");
        if (!File.Exists(script))
            return $"AI 检测失败：找不到桥接脚本。{Environment.NewLine}预期位置：{script}{Environment.NewLine}请重新生成包含 bridge 目录的完整便携包。";

        progress?.Report("开始逐环节自检与自动修复……");
        // v1.1.2：环境医生逐环节自检（Python → NumPy → 识别模块 → 桥握手 → 合成识别），
        // 任一环失败自动修复并全量复检，直到可用或穷尽修复手段（不再"装完库就报错")。
        var result = await AiProvisioner.EnsureUsableAsync(dir, progress, confirmNetwork, cancellationToken);
        return result.Message;
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
            // 超长笔画等距抽稀：识别引擎自身按 4px 重采样，抽到 1200 点不损失形状特征，
            // 却能把 JSON 序列化 + IPC + 解析的成本从几十毫秒压回毫秒级（首笔更跟手）。
            IReadOnlyList<InkPoint> source = target.Points;
            InkPoint[]? sampled = null;
            if (source.Count > MaxSentPoints)
            {
                sampled = new InkPoint[MaxSentPoints];
                double step = (source.Count - 1) / (double)(MaxSentPoints - 1);
                for (int i = 0; i < MaxSentPoints; i++) sampled[i] = source[(int)Math.Round(i * step)];
                source = sampled;
            }
            // 协议 v2：options 与识别引擎（pptink_recog）参数一一对应；灵敏度 k 同时调制
            // 全部容差（宽松 → 容差放大 → 手绘更随意也能规整）。引擎侧仍兼容旧字段。
            var req = new
            {
                cmd = "recognize",
                points = source.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) }).ToArray(),
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
            if (!resp.Value.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                // ok=false 有两种语义：①正常的"这不像图形"；②桥自身出错。后者必须让
                // 用户看到具体原因（缺 NumPy / 算法异常），而不是"未识别出规整图形"。
                string? reason = resp.Value.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString() : null;
                if (reason is "numpy_missing" or "recognizer_unavailable" or "recognize_failed" or "request_failed")
                {
                    string detail = resp.Value.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
                        ? detailEl.GetString() ?? "" : "";
                    LastUnavailableReason = reason switch
                    {
                        "numpy_missing" => "AI 桥缺少 NumPy：请到 设置 → AI 功能 → 快速检测 AI 功能 自动补齐依赖。"
                                           + (string.IsNullOrWhiteSpace(detail) ? "" : $"（{detail}）"),
                        "recognizer_unavailable" => "AI 识别模块加载失败：请在 设置 → AI 功能 → 快速检测 AI 功能 中一键修复。",
                        "recognize_failed" => "AI 识别算法异常（本次已跳过，不影响书写）：" + detail,
                        _ => "AI 桥内部请求失败，已自动恢复。",
                    };
                    return (null, false);
                }
                return (null, true);
            }

            // 桥是本地进程边界，仍按不可信响应处理：缺字段/类型错/越界数组一律
            // 当“未识别”，不能让 JsonException 把整个 AI 请求标成桥不可用。
            if (!resp.Value.TryGetProperty("shape", out var shapeEl) ||
                !resp.Value.TryGetProperty("confidence", out var confEl) ||
                !resp.Value.TryGetProperty("points", out var pointsEl) ||
                pointsEl.ValueKind != System.Text.Json.JsonValueKind.Array ||
                !confEl.TryGetDouble(out double confidence))
                return (null, true);
            var shape = shapeEl.GetString();
            var pointValues = pointsEl.EnumerateArray()
                .Take(StrokeData.MaxPoints)
                .Select(a => a.ValueKind == System.Text.Json.JsonValueKind.Array && a.GetArrayLength() >= 2
                    ? (X: a[0].GetDouble(), Y: a[1].GetDouble())
                    : (X: double.NaN, Y: double.NaN))
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                .ToArray();
            if (pointValues.Length < 2 || double.IsNaN(confidence) || double.IsInfinity(confidence))
                return (null, true);

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
            LastUnavailableReason = $"AI 桥请求异常：{ex.Message}";
            return (null, false);
        }
    }

    /// <summary>请求-响应（行式 JSON）。桥不可达时自动重启一次。
    /// 串行化：自动气泡与手动触发并发时同一时刻仅一个请求在流上读写，JSON 行不交错；
    /// 重连放池线程（await 续体可能回到 UI 线程，同步 RestartProcess 会长时间阻塞界面）。</summary>
    private async Task<JsonElement?> SendAsync(string line)
    {
        if (!await _ioGate.WaitAsync(TimeSpan.FromSeconds(8)))
        {
            LastUnavailableReason = "AI 请求排队超时：上一笔识别仍未返回，Python 进程可能已卡死。";
            return null;   // 排队上限：桥卡死时不无限堆积
        }
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
                    LastUnavailableReason = ex is TimeoutException
                        ? "AI 识别超时：Python 进程无响应，已尝试自动重启。"
                        : $"AI 桥通信失败：{ex.Message}";
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
        // 先置位再抢锁（volatile 写）：池线程若正持 _gate 做冷启动握手，其分片等待观察到
        // _disposed 即刻放弃 → 锁快速释放，本方法不再排队等最长 25s 的握手。
        // 配套：AppServices.Shutdown 已把会话墨迹快照排在本释放之前，双保险防强杀丢板书。
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
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
