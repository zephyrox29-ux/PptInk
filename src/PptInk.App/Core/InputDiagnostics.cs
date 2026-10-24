namespace PptInk.Core;

/// <summary>
/// 输入诊断数据总线（v0.4.0 调试模式）。
///
/// 职责：各输入层（WM_POINTER / StylusPlugIn / 鼠标 / 手掌抑制 / 渲染）把关键决策
/// 数据上报到此处，<see cref="PptInk.UI.DebugHudElement"/> 按 100ms 轮询快照显示 ——
/// 「识别到的接触面积、接触点、绘制情况等各层策略」一目了然。
///
/// 工程纪律（诊断永不伤害主流程）：
///  - 所有写方法 try-catch 包裹 + 单锁快照写：诊断异常绝不外溢到输入/渲染链路；
///  - 调试模式关闭时上报调用仍在（单 lock + 十几个字段写，开销纳秒级），
///    HUD 不轮询即零显示成本 —— 避免「调试开关改变代码路径」引入的行为差异；
///  - 无事件订阅（HUD 主动轮询），无跨线程封送需求。
/// </summary>
public static class InputDiagnostics
{
    private static readonly object _gate = new();

    // ———————— 快照数据（读写均在 _gate 内） ————————

    /// <summary>当前/最近一次输入会话（三通道互斥，同一时刻只有一个持有者）。</summary>
    public sealed class SessionInfo
    {
        public string Channel = "";              // A:WM_POINTER / B:StylusPlugIn / C:Mouse
        public int PointerType;                  // 0=未知(B/C) 1=PT_PEN 2=PT_TOUCH
        public uint PointerId;
        public long StartTick;
        public long LastUpdateTick;
        public int Points;                       // 会话累计报点数
        public int HistoryMax;                   // 单条 UPDATE 最大历史深度

        // 接触元数据（屏幕物理 px；驱动不提供时 HasContact=false）
        public bool HasContact;
        public double ContactX, ContactY, ContactW, ContactH;
        public double AreaPx2;

        // 判定与压感
        public string Verdict = "";              // 接受 / 拒绝：手掌 …（PalmRejection 理由，展示用）
        public bool Accepted = true;             // v0.4.1 结构化判定结果（诊断颜色判断用，不再解析字符串）
        public uint PressureRaw;                 // 驱动原始压感（0~1024；0=未提供）
        public double PressureNorm = 0.5;        // 实际生效的归一压感
        public bool PressureActive;              // 探测认定设备有真实压感
        public string PressureTier = "L0";       // L0=纯坐标保底 / L1=有面积 / L2=有压感
    }

    /// <summary>触摸数字化仪探测结论（TouchDigitizer 镜像）。</summary>
    public sealed class DeviceInfo
    {
        public string Summary = "";
        public bool IsLargeTouchPanel;
        public double CalibratedPxPerMm;          // <=0 = 未校准
        public double ProfilePxPerMm;
        public string ProfileId = "";
        public string CalibrationSource = "";
    }

    /// <summary>渲染与显示层快照。</summary>
    public sealed class RenderInfo
    {
        public long FrameCount;                   // 累计渲染帧（FPS 由 HUD 差分计算）
        public int CacheW, CacheH;                // 页面缓存位图像素尺寸
        public double RenderDipW, RenderDipH;     // 渲染目标 DIP 尺寸
        public double WindowScale = 1.0;          // 窗口 TransformToDevice.M11
        public double ShowScreenScale = 1.0;      // ShowScreen.DpiScale（对照：两者不一致=坐标链路有陈旧值）
        public string BitmapNote = "";            // 位图口径说明
    }

    public static SessionInfo? Session { get; private set; }
    public static DeviceInfo Device { get; } = new();
    public static RenderInfo Render { get; } = new();

    private static readonly Queue<(string Time, string Msg)> _events = new();

    // ———————— 会话上报（输入层调用） ————————

    /// <summary>会话开始（down）：通道占位成功后调用。</summary>
    public static void BeginSession(string channel, int pointerType, uint pointerId, bool hasContact,
        double cx, double cy, double cw, double ch, string verdict, bool accepted,
        uint pressureRaw, double pressureNorm, bool pressureActive)
    {
        try
        {
            lock (_gate)
            {
                Session = new SessionInfo
                {
                    Channel = channel,
                    PointerType = pointerType,
                    PointerId = pointerId,
                    StartTick = Environment.TickCount64,
                    LastUpdateTick = Environment.TickCount64,
                    HasContact = hasContact,
                    ContactX = cx, ContactY = cy, ContactW = cw, ContactH = ch,
                    AreaPx2 = cw > 0 ? cw * ch : 0,
                    Verdict = verdict,
                    Accepted = accepted,
                    PressureRaw = pressureRaw,
                    PressureNorm = pressureNorm,
                    PressureActive = pressureActive,
                };
            }
        }
        catch { }
    }

    /// <summary>会话中更新（move）：报点计数 + 接触矩形跟随 + 压感实况。</summary>
    public static void UpdateSession(int pointsDelta, bool hasContact,
        double cx, double cy, double cw, double ch, uint pressureRaw, double pressureNorm, bool pressureActive)
    {
        try
        {
            lock (_gate)
            {
                if (Session is not { } s) return;
                s.Points += Math.Max(0, pointsDelta);
                s.LastUpdateTick = Environment.TickCount64;
                s.HasContact = hasContact;
                if (hasContact)
                {
                    s.ContactX = cx; s.ContactY = cy; s.ContactW = cw; s.ContactH = ch;
                    s.AreaPx2 = cw * ch;
                }
                s.PressureRaw = pressureRaw;
                s.PressureNorm = pressureNorm;
                s.PressureActive = pressureActive;
            }
        }
        catch { }
    }

    /// <summary>手掌拒绝的指针跟踪（拒绝后仍显示其接触轨迹，红框可视化）。</summary>
    public static void UpdateRejected(bool hasContact, double cx, double cy, double cw, double ch, string verdict, bool accepted)
    {
        try
        {
            lock (_gate)
            {
                if (Session is not { } s) return;
                s.HasContact = hasContact;
                if (hasContact)
                {
                    s.ContactX = cx; s.ContactY = cy; s.ContactW = cw; s.ContactH = ch;
                    s.AreaPx2 = cw * ch;
                }
                s.Verdict = verdict;
                s.Accepted = accepted;
                s.LastUpdateTick = Environment.TickCount64;
            }
        }
        catch { }
    }

    /// <summary>历史深度记录（单条 UPDATE 的 historyCount）。</summary>
    public static void NoteHistory(int historyCount)
    {
        try
        {
            lock (_gate)
            {
                if (Session is { } s && historyCount > s.HistoryMax) s.HistoryMax = historyCount;
            }
        }
        catch { }
    }

    /// <summary>会话结束（up / 层收回）。</summary>
    public static void EndSession()
    {
        try { lock (_gate) { if (Session is { } s) s.Verdict += " ·会话结束"; } }
        catch { }
    }

    // ———————— 设备/渲染上报 ————————

    public static void SetDevice(string summary, bool largePanel, double calibratedPxPerMm,
        double profilePxPerMm, string profileId, string calibrationSource)
    {
        try
        {
            lock (_gate)
            {
                Device.Summary = summary;
                Device.IsLargeTouchPanel = largePanel;
                Device.CalibratedPxPerMm = calibratedPxPerMm;
                Device.ProfilePxPerMm = profilePxPerMm;
                Device.ProfileId = profileId;
                Device.CalibrationSource = calibrationSource;
            }
        }
        catch { }
    }

    public static void SetRender(int cacheW, int cacheH, double dipW, double dipH, double windowScale, double showScreenScale)
    {
        try
        {
            lock (_gate)
            {
                Render.CacheW = cacheW;
                Render.CacheH = cacheH;
                Render.RenderDipW = dipW;
                Render.RenderDipH = dipH;
                Render.WindowScale = windowScale;
                Render.ShowScreenScale = showScreenScale;
            }
        }
        catch { }
    }

    /// <summary>渲染帧计数（OnRender 调用，Interlocked 免锁）。</summary>
    public static void OnFrame()
    {
        try { Interlocked.Increment(ref _frameCount); }
        catch { }
    }

    private static long _frameCount;

    public static long FrameCount
    {
        get { try { return Interlocked.Read(ref _frameCount); } catch { return 0; } }
    }

    /// <summary>事件环（最近 8 条：会话占用/释放、自愈、校准、档案切换…）。</summary>
    public static void Note(string msg)
    {
        try
        {
            lock (_gate)
            {
                _events.Enqueue((DateTime.Now.ToString("HH:mm:ss"), msg));
                while (_events.Count > 8) _events.Dequeue();
            }
        }
        catch { }
    }

    /// <summary>事件环快照（HUD 拷贝显示）。</summary>
    public static (string Time, string Msg)[] EventsSnapshot()
    {
        try { lock (_gate) return _events.ToArray(); }
        catch { return Array.Empty<(string, string)>(); }
    }

    /// <summary>会话快照拷贝（v0.4.1 修复：原返回活引用 —— HUD 逐字段读取期间输入线
    /// 程并发写入会读到撕裂状态（如 HasContact=true 而 ContactW=0）。现锁内深拷贝全
    /// 字段，HUD 拿到的是一致性时刻快照，读取线程安全）。</summary>
    public static SessionInfo? SessionSnapshot()
    {
        try
        {
            lock (_gate)
            {
                if (Session is not { } s) return null;
                return new SessionInfo
                {
                    Channel = s.Channel,
                    PointerType = s.PointerType,
                    PointerId = s.PointerId,
                    StartTick = s.StartTick,
                    LastUpdateTick = s.LastUpdateTick,
                    Points = s.Points,
                    HistoryMax = s.HistoryMax,
                    HasContact = s.HasContact,
                    ContactX = s.ContactX, ContactY = s.ContactY,
                    ContactW = s.ContactW, ContactH = s.ContactH,
                    AreaPx2 = s.AreaPx2,
                    Verdict = s.Verdict,
                    Accepted = s.Accepted,
                    PressureRaw = s.PressureRaw,
                    PressureNorm = s.PressureNorm,
                    PressureActive = s.PressureActive,
                    PressureTier = s.PressureTier,
                };
            }
        }
        catch { return null; }
    }
}
