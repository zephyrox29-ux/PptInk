using System.Runtime.InteropServices;
using System.Text;

namespace PptInk.Core;

/// <summary>应用运行阶段。S2 为"避让隐藏"：希沃白板/EasiNote 前台时主动让位。</summary>
public enum AppPhase
{
    /// <summary>S0 后台静默：无窗口，托盘可隐藏，CPU≈0，内存&lt;30MB。</summary>
    S0Background,

    /// <summary>S1 UI 唤起：悬浮工具栏 + 浮岛 + 墨迹层激活，书写就绪。</summary>
    S1Active,

    /// <summary>S2 隐让：检测到希沃白板/EasiNote/全屏视频等前台覆盖，UI 收回但放映会话保持。</summary>
    S2Yield,
}

/// <summary>
/// 状态机：S0↔S1↔S2 流转 + 迟滞去抖（&gt;=500ms）。
/// 风险应对：全屏视频或白板弹窗误判导致 UI 频繁闪隐 → 隐藏判定加迟滞去抖，且支持手动锁定 UI 显示。
/// 线程：COM 线程 / WinEvent 线程 / 线程池 Timer / UI 线程均可驱动流转。
/// 并发纪律（审查修复）：所有公共入口一律"锁内改状态、锁外触发事件"。
/// 原实现在 lock(_gate) 内触发 PhaseChanged/SlideIndexChanged —— 订阅方同步 Dispatcher.Invoke 等 UI 线程，
/// 一旦存在任何"持 _gate 且等待 COM/WinEvent 线程"的路径即构成环死锁；且处理器耗时全部计入临界区。
/// </summary>
public sealed class AppStateMachine
{
    private readonly object _gate = new();
    private readonly int _hysteresisMs;
    private DateTime _lastVisibleAt = DateTime.MinValue;
    private Timer? _yieldRecoverTimer;
    private ShowSession? _session;

    public AppPhase Phase { get; private set; } = AppPhase.S0Background;

    /// <summary>当前放映会话（S1/S2 期间非空）。</summary>
    public ShowSession? Session => _session;

    /// <summary>UI 手动锁定显示（教师不希望任何场景自动隐藏时开启）。</summary>
    public bool UiLocked { get; set; }

    public event EventHandler<AppPhase>? PhaseChanged;
    public event EventHandler<int>? SlideIndexChanged;

    public AppStateMachine(int hysteresisMs = 500)
    {
        _hysteresisMs = Math.Max(500, hysteresisMs);
    }

    /// <summary>放映开始（来自 COM 主通道或 WinEvent 兜底通道，先到先得，幂等）。</summary>
    public void OnPresentationStart(Detection.ShowStartedEvent ev)
    {
        AppPhase? phase = null;
        int? slide = null;
        lock (_gate)
        {
            if (Phase == AppPhase.S1Active)
            {
                slide = ApplySlideIndex(ev.SlideIndex);
            }
            else
            {
                _session = new ShowSession(ev.Source, ev.TotalSlides, ev.SlideIndex);
                phase = TransitionTo(AppPhase.S1Active);
                slide = ApplySlideIndex(ev.SlideIndex);
                // v1.1.1 取证日志：放映开始（来源通道 + 起始页）—— 无放映环境排障第一现场
                AppLog.Instance.Info($"放映开始：来源 {ev.Source} 第 {ev.SlideIndex}/{ev.TotalSlides} 页");
            }
        }
        Fire(phase, slide);
    }

    /// <summary>翻页（COM 读 CurrentShowPosition；兜底通道用事件计数推算）。</summary>
    public void OnSlideChanged(Detection.SlideChangedEvent ev)
    {
        int? slide = null;
        lock (_gate)
        {
            _session?.UpdatePosition(ev.SlideIndex, ev.TotalSlides);
            if (Phase == AppPhase.S1Active) slide = ApplySlideIndex(ev.SlideIndex);
        }
        if (slide is { } idx)
        {
            AppLog.Instance.Info($"翻页：第 {idx}/{ev.TotalSlides} 页（来源 {ev.Source}）");
            SlideIndexChanged?.Invoke(this, idx);
        }
    }

    /// <summary>放映结束：UI 收回，回到 S0。墨迹缓存保留至导出或会话清理策略。</summary>
    public void OnPresentationEnd()
    {
        AppPhase? phase = null;
        lock (_gate)
        {
            _yieldRecoverTimer?.Dispose();
            _yieldRecoverTimer = null;
            _session = null;
            if (Phase != AppPhase.S0Background)
            {
                phase = TransitionTo(AppPhase.S0Background);
                AppLog.Instance.Info("放映结束：收回 UI，回 S0");   // v1.1.1 取证日志
            }
        }
        if (phase is { } p) PhaseChanged?.Invoke(this, p);
    }

    /// <summary>
    /// 检测到外来覆盖（希沃白板、EasiNote、全屏视频前台）→ S2 隐藏。
    /// 迟滞：距上次可见 &lt; hysteresisMs 的抖动不直接丢弃而是推迟判定（审查修复：
    /// 原实现整体 return —— 进入 S1 后迟滞窗口内的首次 ForeignOverlay 被整抛且
    /// 事件纯驱动不补发，覆盖常驻时永不隐藏）；推迟到期复查前台，覆盖已消失则不隐藏
    /// （防闪隐语义保留：瞬时抖动仍不会触发隐藏）。
    /// </summary>
    public void OnForeignOverlay()
    {
        AppPhase? phase = null;
        lock (_gate)
        {
            if (Phase != AppPhase.S1Active || UiLocked) return;
            if ((DateTime.UtcNow - _lastVisibleAt).TotalMilliseconds < _hysteresisMs)
            {
                ScheduleYieldProbe();   // 推迟判定：迟滞期满复核前台，仍命中才进 S2
                return;
            }

            phase = EnterYield();
        }
        if (phase is { } p) PhaseChanged?.Invoke(this, p);
    }

    /// <summary>锁内：进入 S2 并排一次恢复探测（单次定时）。</summary>
    private AppPhase? EnterYield()
    {
        var phase = TransitionTo(AppPhase.S2Yield);
        ScheduleYieldProbe();
        return phase;
    }

    /// <summary>锁内：重置单次探测定时器。到期动作随当前相位而定：
    ///  - S2（恢复探测）：复核前台，外来覆盖<strong>仍在前台则顺延再探</strong>，已消失才回 S1。
    ///    审查修复：原实现固定 500ms 后无条件回 S1 —— 希沃白板仍在前台时，置顶全屏墨迹层
    ///    （UnifiedStageWindow Topmost + 书写拦截底刷）弹回白板上方，教师后续书写被本进程
    ///    截胡；且希沃自家窗口间每次切换都会再触发一轮 S2→500ms→S1 闪隐循环。
    ///  - S1（迟滞推迟判定）：复核前台，覆盖仍在前台才进 S2（见 OnForeignOverlay）。
    /// </summary>
    private void ScheduleYieldProbe()
    {
        _yieldRecoverTimer?.Dispose();
        Timer? probe = null;
        probe = new Timer(_ =>
        {
            AppPhase? phase = null;
            lock (_gate)
            {
                if (Phase == AppPhase.S2Yield && _session != null && !UiLocked)
                {
                    if (ForeignOverlayStillInForeground())
                    {
                        // 覆盖仍在前台：顺延一个迟滞周期后再探，绝不带着覆盖弹回 S1。
                        // 仅当本定时器仍是被排程的那个才顺延（期间可能已被替换/重排）；
                        // probe 在回调首次点火前必已完成赋值，判空只为安抚可空分析。
                        if (ReferenceEquals(_yieldRecoverTimer, probe))
                            probe?.Change(_hysteresisMs, Timeout.Infinite);
                        return;
                    }
                    phase = TransitionTo(AppPhase.S1Active);
                }
                else if (Phase == AppPhase.S1Active && !UiLocked && ForeignOverlayStillInForeground())
                {
                    phase = EnterYield();
                }
            }
            if (phase is { } p) PhaseChanged?.Invoke(this, p);
        }, null, _hysteresisMs, Timeout.Infinite);
        _yieldRecoverTimer = probe;
    }

    // 恢复/推迟判定所需的前台复核（线程池线程可调，纯 Win32 查询）：
    // 类名判定复用 WinEventWatcher 的前缀表，两处必须同源，避免前缀漂移
    private static bool ForeignOverlayStillInForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(256);
        return GetClassNameW(fg, sb, sb.Capacity) > 0 &&
               Detection.WinEventWatcher.IsForeignOverlay(sb.ToString());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder lpClassName, int maxCount);

    /// <summary>锁外依次触发快照事件（先相位后页码，与历史顺序一致）。</summary>
    private void Fire(AppPhase? phase, int? slide)
    {
        if (phase is { } p) PhaseChanged?.Invoke(this, p);
        if (slide is { } idx) SlideIndexChanged?.Invoke(this, idx);
    }

    /// <summary>锁内更新页码；返回新页码（未变化返回 null），事件由调用方锁外触发。</summary>
    private int? ApplySlideIndex(int slideIndex)
    {
        if (_session is { } s && s.CurrentSlide != slideIndex)
        {
            s.CurrentSlide = slideIndex;
            return slideIndex;
        }
        return null;
    }

    /// <summary>锁内变更相位；返回新相位（未变化返回 null），事件由调用方锁外触发。</summary>
    private AppPhase? TransitionTo(AppPhase next)
    {
        if (Phase == next) return null;
        if (next == AppPhase.S1Active) _lastVisibleAt = DateTime.UtcNow;
        Phase = next;
        return next;
    }
}

/// <summary>一次放映会话：来源通道、页码、总页数。</summary>
public sealed class ShowSession
{
    public ShowSession(string source, int totalSlides, int slideIndex)
    {
        Source = source; TotalSlides = totalSlides; CurrentSlide = slideIndex;
        StartedAt = DateTime.Now;
    }

    public string Source { get; }        // "COM:PowerPoint" | "COM:WPS" | "WinEvent:screenClass"
    public DateTime StartedAt { get; }
    public int TotalSlides { get; private set; }
    public int CurrentSlide { get; internal set; }

    public void UpdatePosition(int slideIndex, int totalSlides)
    {
        TotalSlides = totalSlides > 0 ? totalSlides : TotalSlides;
        CurrentSlide = slideIndex;
    }
}
