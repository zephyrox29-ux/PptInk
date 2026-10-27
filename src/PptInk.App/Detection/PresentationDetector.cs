namespace PptInk.Detection;

/// <summary>
/// 双通道仲裁器：COM 主通道（精确页码、零轮询）+ WinEvent 兜底（覆盖 COM 失败场景），
/// 互为兜底、事件去重。策略：
///  - Start 事件先到先得，后到的重复 Start 丢弃（COM 与 WinEvent 都会报）；
///  - 翻页事件优先采信 COM（读 CurrentShowPosition）；COM 不健康时采信兜底计数；
///  - End 事件双通道任一触发即结束；开场 300ms 内的 End 视为双通道错序抖动，忽略（防闪隐）。
/// </summary>
public sealed class PresentationDetector : IDisposable
{
    private readonly ComWatcher _com = new();
    private readonly WinEventWatcher _winEvent = new();
    private readonly object _gate = new();
    private bool _showActive;
    private string _activeSource = "";
    private DateTime _lastEventAt = DateTime.MinValue;
    private DateTime _showStartedAt;
    private Timer? _rebindTimer;

    public bool ComHealthy => _com.IsHealthy;
    public bool WinEventHealthy => _winEvent.IsHealthy;

    /// <summary>最近一次 COM 页码查询是否成功（由编排器轮询回写）。
    /// COM 事件通道失效但查询可用时仍以 COM 为准；连查询都失败才让 WinEvent 的
    /// 方向推算法接管 —— 否则「COM 健康」会把唯一可用的兜底也静默掉。</summary>
    public bool ComPageReadOk { get; set; } = true;

    public event EventHandler<ShowStartedEvent>? PresentationStarted;
    public event EventHandler<SlideChangedEvent>? SlideChanged;
    public event EventHandler? PresentationEnded;
    public event EventHandler? ForeignOverlayDetected;

    public void Start()
    {
        _com.Started += OnComStarted;
        _com.SlideChanged += OnComSlideChanged;
        _com.Ended += OnComEnded;
        _com.Unbound += (_, _) =>
        {
            // COM 断开后让 WinEvent 的翻页计数重新有机会接管。
            lock (_gate) { if (!_com.IsHealthy) _activeSource = ""; }
        };

        _winEvent.Started += OnWinStarted;
        _winEvent.SlideChanged += OnWinSlideChanged;
        _winEvent.Ended += OnWinEnded;
        _winEvent.ForeignOverlay += (_, _) => ForeignOverlayDetected?.Invoke(this, EventArgs.Empty);

        _com.Start();
        _winEvent.Start();

        // PPT 进程中途启动场景：每 5s 轻量尝试 COM 重绑（仅当未绑定时，
        // GetActiveObject 开销微秒级，可视为零成本，不违反零轮询目标）
        _rebindTimer = new Timer(_ =>
        {
            _com.ProbeHealth();
            if (!_com.IsHealthy) _com.TryBind();
        }, null, 5_000, 5_000);
    }

    private void OnComStarted(object? sender, ShowStartedEvent e)
    {
        lock (_gate)
        {
            if (_showActive && _activeSource == e.Source) return; // COM 自身重复
            _showActive = true;
            _activeSource = e.Source; // COM 优先接管（"COM:PowerPoint.Application" 等）
            _lastEventAt = DateTime.UtcNow;
            _showStartedAt = DateTime.UtcNow;
        }
        PresentationStarted?.Invoke(this, e);
    }

    private void OnWinStarted(object? sender, ShowStartedEvent e)
    {
        lock (_gate)
        {
            if (_showActive) return; // COM 已报：兜底通道沉默
            _showActive = true;
            _activeSource = e.Source;
            _lastEventAt = DateTime.UtcNow;
            _showStartedAt = DateTime.UtcNow;
        }
        PresentationStarted?.Invoke(this, e);
    }

    private void OnComSlideChanged(object? sender, SlideChangedEvent e)
    {
        lock (_gate)
        {
            // 审查修复：_activeSource 实为 "COM:PowerPoint.Application"/"COM:KWPP.Application"
            //（ComWatcher.Name），原判定 `!= "COM"` 恒真 → COM 翻页事件被整体丢弃 →
            // InkEngine.SwitchPage 不被调用 → 墨迹不随页切换（翻页后串页）。
            if (!_showActive || !_activeSource.StartsWith("COM", StringComparison.Ordinal)) return;
            _lastEventAt = DateTime.UtcNow;
        }
        SlideChanged?.Invoke(this, e);
    }

    private void OnWinSlideChanged(object? sender, SlideChangedEvent e)
    {
lock (_gate)
            {
                // COM 健康且页码查询可用时以 COM 为准；COM 已断开或查不到页码时必须放行
                // WinEvent 的兜底计数，否则 _activeSource 里残留的 "COM:..." 会永久静默兜底翻页。
                if (!_showActive || (_com.IsHealthy && ComPageReadOk && _activeSource.StartsWith("COM", StringComparison.Ordinal))) return;
                _lastEventAt = DateTime.UtcNow;
            }
        SlideChanged?.Invoke(this, e);
    }

    private void OnComEnded(object? sender, EventArgs e) => RaiseEnd("COM");

    private void OnWinEnded(object? sender, EventArgs e) => RaiseEnd("WinEvent");

    private void RaiseEnd(string source)
    {
        lock (_gate)
        {
            if (!_showActive) return;
            // 开场即 End（距 Start < 300ms）：双通道对同一放映的启停信号错序抖动，忽略之，
            // 否则 UI 会瞬间闪隐再唤起。真实退映时长必然远超 300ms。
            if ((DateTime.UtcNow - _showStartedAt).TotalMilliseconds < 300) return;
            _showActive = false;
            _activeSource = "";
        }
        PresentationEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>强制复位放映去重状态（不触发 PresentationEnded —— 调用方自行完成 UI 收回）。
    /// 供看门狗等绕过本检测器直接回收的路径调用。审查修复：开场 300ms 内的 End 被吞噬后
    /// _showActive 滞留 true，而看门狗直接调 StateMachine.OnPresentationEnd 不经本类，
    /// 此后重启放映的 Start 被 OnWinStarted/OnComStarted 的去重静默丢弃（COM 源跨次放映
    /// 同名，兜底通道无条件沉默），整场放映不再唤起批注 UI。误复位无害：放映实际仍在时，
    /// 下一拍 Started/翻页/补报事件会重新建立状态。</summary>
    public void ForceReset()
    {
        lock (_gate)
        {
            _showActive = false;
            _activeSource = "";
        }
    }

    /// <summary>兜底模式下的手动翻页推算入口（翻页栏按钮 → PageNumberTracker → 此处）。</summary>
    public void NotifyManualAdvance(bool forward) => _winEvent.NotifyManualAdvance(forward);

    public void Dispose()
    {
        _rebindTimer?.Dispose();
        _com.Dispose();
        _winEvent.Dispose();
    }
}
