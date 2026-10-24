using System.IO;
using System.Text;
using System.Text.Json;
using PptInk.Ink;

namespace PptInk.Core;

/// <summary>
/// 采样记录器（v0.9.4 真机验收工具）：采样模式开启时以 JSONL 落盘四类数据，
/// 供 Windows 真机验收使用 —— 触控/穿透兼容矩阵、COM 故障恢复、真实课堂笔迹语料、性能采样。
///
/// 数据源复用现有诊断总线 <see cref="InputDiagnostics"/>（输入层已上报会话/设备/渲染/事件环），
/// 本类只做「定时快照 + 笔迹订阅 + 事件差分」的采集与落盘，不改动输入/渲染主链路。
/// 工程纪律与 AppLog / InputDiagnostics 同律：全程 try-catch，采样异常绝不外溢到主流程。
///
/// 输出：日志目录\sampling\session-&lt;时间戳&gt;.jsonl，一行一事件（{"t":ms,"ts":hh:mm:ss.fff,"type":..,"data":{..}}）。
/// 事件类型：env（启动环境快照）/ perf（性能：帧率·帧数·内存）/ session（输入会话边界）/
/// device · render（设备与渲染快照，变化时）/ stroke（一笔点序列语料）/ com（COM 故障恢复）/ control（启停）。
/// </summary>
public sealed class SamplingRecorder : IDisposable
{
    public static SamplingRecorder Instance { get; } = new();

    private readonly object _gate = new();
    private StreamWriter? _w;
    private Timer? _poll;
    private AppServices? _svc;
    private Action<StrokeData>? _strokeHandler;
    private bool _enabled;

    private long _lastFrame;
    private long _lastFrameTick;
    private string _lastSessionKey = "";
    private string _lastDevKey = "";
    private string _lastRenKey = "";
    private string _lastEventMsg = "";

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk", "logs", "sampling");

    private SamplingRecorder() { }

    public bool IsEnabled { get { lock (_gate) return _enabled; } }
    public string? SessionFile { get; private set; }

    /// <summary>启动采集（幂等：已开启则直接返回）。</summary>
    public void Start(AppServices svc)
    {
        lock (_gate)
        {
            if (_enabled) return;
            try
            {
                Directory.CreateDirectory(Dir);
                SessionFile = Path.Combine(Dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                _w = new StreamWriter(SessionFile, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                _svc = svc;
                _enabled = true;
                _lastFrame = InputDiagnostics.FrameCount;
                _lastFrameTick = Environment.TickCount64;
                _strokeHandler = OnStroke;
                svc.InkEngine.StrokeCommitted += _strokeHandler;
                WriteEnv(svc);
                Write("control", new { action = "start", file = SessionFile });
                _poll = new Timer(_ => Poll(), null, 500, 500);
                svc.Log.Info($"[采样] 已开启，输出 {SessionFile}");
            }
            catch (Exception ex)
            {
                _enabled = false;
                try { _svc?.Log.Warn($"[采样] 开启失败：{ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        }
    }

    /// <summary>停止采集并刷盘（幂等：未开启则直接返回）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _enabled = false;
            try
            {
                _poll?.Dispose(); _poll = null;
                if (_svc is { } s && _strokeHandler is not null) s.InkEngine.StrokeCommitted -= _strokeHandler;
                _strokeHandler = null;
                Write("control", new { action = "stop", file = SessionFile });
                _w?.Flush(); _w?.Dispose(); _w = null;
                _svc?.Log.Info("[采样] 已关闭，数据已保存");
            }
            catch { }
        }
    }

    public void Dispose() => Stop();

    /// <summary>COM 故障恢复事件埋点（UiOrchestrator 连接/重连/看门狗回收时调用）。</summary>
    public void RecordCom(string kind, string detail) => Write("com", new { kind, detail });

    /// <summary>自由事件埋点（切档 / 坐标重映射 / 握手等）。</summary>
    public void RecordEvent(string kind, string detail) => Write("event", new { kind, detail });

    // ———————— 采集 ————————

    private void WriteEnv(AppServices svc)
    {
        Write("env", new
        {
            os = Environment.OSVersion.VersionString,
            machine = Environment.MachineName,
            clr = Environment.Version.ToString(),
            cpus = Environment.ProcessorCount,
            wholeWindowHit = svc.Config.WholeWindowHit,
            deviceProfileId = svc.Config.DeviceProfileId ?? "",
            theme = svc.Config.Theme,
            accent = svc.Config.AccentColor,
            deviceSummary = InputDiagnostics.Device.Summary,
        });
    }

    private void Poll()
    {
        if (!IsEnabled) return;
        try
        {
            var svc = _svc;
            if (svc is null) return;

            // ① 性能采样：帧率（帧计数差分）/ 帧数 / 托管内存
            long fc = InputDiagnostics.FrameCount;
            long now = Environment.TickCount64;
            double secs = (now - _lastFrameTick) / 1000.0;
            double fps = secs > 0.05 ? (fc - _lastFrame) / secs : 0;
            _lastFrame = fc; _lastFrameTick = now;

            var sess = InputDiagnostics.SessionSnapshot();
            Write("perf", new
            {
                fps = Math.Round(fps, 1),
                frames = fc,
                gcMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                channel = sess?.Channel ?? "",
                sessionPoints = sess?.Points ?? 0,
            });

            // ② 触控/穿透兼容矩阵：输入会话边界（通道 / 接触矩形 / 判定 / 压感分层）
            string sessKey = sess is null ? "" : $"{sess.Channel}|{sess.PointerId}|{sess.StartTick}";
            if (sessKey != _lastSessionKey)
            {
                _lastSessionKey = sessKey;
                if (sess is not null)
                    Write("session", new
                    {
                        channel = sess.Channel,
                        pointerType = sess.PointerType,
                        pointerId = sess.PointerId,
                        hasContact = sess.HasContact,
                        cx = sess.ContactX, cy = sess.ContactY, cw = sess.ContactW, ch = sess.ContactH,
                        areaPx2 = sess.AreaPx2,
                        verdict = sess.Verdict,
                        accepted = sess.Accepted,
                        pressureRaw = sess.PressureRaw,
                        pressureNorm = sess.PressureNorm,
                        pressureActive = sess.PressureActive,
                        pressureTier = sess.PressureTier,
                    });
            }

            // ③ 设备快照（变化时）
            var dev = InputDiagnostics.Device;
            string devKey = $"{dev.Summary}|{dev.ProfileId}|{dev.CalibratedPxPerMm:F2}|{dev.IsLargeTouchPanel}";
            if (devKey != _lastDevKey)
            {
                _lastDevKey = devKey;
                Write("device", new
                {
                    dev.Summary,
                    dev.IsLargeTouchPanel,
                    dev.CalibratedPxPerMm,
                    dev.ProfilePxPerMm,
                    dev.ProfileId,
                    dev.CalibrationSource,
                });
            }

            // ④ 渲染快照（变化时）：缓存位图口径 / 窗口缩放 / 放映屏缩放（不一致=坐标链路有陈旧值）
            var ren = InputDiagnostics.Render;
            string renKey = $"{ren.CacheW}x{ren.CacheH}|{ren.WindowScale:F3}|{ren.ShowScreenScale:F3}";
            if (renKey != _lastRenKey)
            {
                _lastRenKey = renKey;
                Write("render", new
                {
                    ren.CacheW, ren.CacheH,
                    ren.RenderDipW, ren.RenderDipH,
                    ren.WindowScale, ren.ShowScreenScale,
                    ren.BitmapNote,
                });
            }

            // ⑤ 诊断事件环增量（会话占用/释放、自愈、校准、档案切换 …）
            var evs = InputDiagnostics.EventsSnapshot();
            if (evs.Length > 0)
            {
                var last = evs[evs.Length - 1];
                string msg = last.Time + " " + last.Msg;
                if (msg != _lastEventMsg) { _lastEventMsg = msg; Write("event", new { kind = "diag", detail = msg }); }
            }
        }
        catch { }
    }

    /// <summary>③ 真实课堂笔迹语料：每笔提交时记录点序列（坐标 / 压感 / QPC 时间戳）。</summary>
    private void OnStroke(StrokeData s)
    {
        if (!IsEnabled) return;
        try
        {
            var pts = s.Points;
            int n = pts.Count;
            var arr = new double[n * 4];
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                int j = i * 4;
                arr[j] = Math.Round(p.X, 2);
                arr[j + 1] = Math.Round(p.Y, 2);
                arr[j + 2] = Math.Round(p.Pressure, 3);
                arr[j + 3] = p.TimestampTicks;
            }
            Write("stroke", new
            {
                id = s.Id,
                color = s.Pen.ColorHex,
                mm = s.Pen.ThicknessMm,
                highlighter = s.Pen.Highlighter,
                n,
                points = arr,
            });
        }
        catch { }
    }

    private void Write(string type, object data)
    {
        lock (_gate)
        {
            if (!_enabled || _w is null) return;
            try
            {
                _w.WriteLine(JsonSerializer.Serialize(new
                {
                    t = Environment.TickCount64,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    type,
                    data,
                }));
            }
            catch { }
        }
    }
}