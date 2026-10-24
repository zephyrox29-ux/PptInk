using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PptInk.UI;

/// <summary>
/// 三源定源拖动状态机（v0.6.0 拖动纪律的公共抽取，v0.7.0 灵动岛家族共用）：
///  - Down：首个到达源定源（1=真鼠标 2=触摸/笔），其余源事件整轮忽略（防连发抖动）；
///  - Move：只认定源；累计位移 &gt; 8px 判拖动；窗口位置 = 按下时窗口位置 + 指针增量；
///  - Up：终结会话（moved = 是否真拖过），未拖 &amp; 按压 &lt; 450ms = Tap。
///  触摸/笔事件 Handled 阻断提升链（合成鼠标不再产生，单源零抖动）——
///  「拖动卡卡的不跟手」的根因修复纪律，任何新窗口不得绕过此件自写拖动。
///  附加：LongPress（600ms 未拖触发一次）、EndSession（宿主主动终结，如分割缝拉出即拆）。
/// </summary>
public sealed class DragKit
{
    private readonly IInputElement _positionBase;   // 非 StageElement 宿主坐标基准
    private readonly StageElement? _stageHost;      // 动态取 Stage，避免移动元素自身坐标系反向变化
    private readonly Func<Point> _getPos;           // 宿主位置读取（屏幕 DIP）
    private readonly Action<Point> _setPos;         // 宿主位置写入
    private int _source;                // 0=未按 1=真鼠标 2=触摸/笔
    private Point _origin = new();      // 按下时指针在宿主内位置
    private Point _downWinPos = new();  // 按下时宿主左上角（屏幕 DIP）
    private long _downTick;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _longPressed;
    private bool _preview = true;
    private long _activeTick;
    private FrameworkElement? _wiredElement;
    private int _inputKind;             // 1=mouse, 2=touch, 3=stylus；区分提升事件与真实 Up
    private TouchDevice? _touchCapture;
    private StylusDevice? _stylusCapture;
    private bool _mouseCapture;

    // v0.9.6 全局拖动独占：同一时刻只允许一个元素在拖。
    private static DragKit? _activeOwner;

    /// <summary>窗口宿主（阶段D 之前的存量窗口）。</summary>
    public DragKit(Window host)
        : this(host, () => new Point(host.Left, host.Top), p => { host.Left = p.X; host.Top = p.Y; })
    {
    }

    /// <summary>舞台元素宿主（v0.9.0 单窗口架构：ScreenLeft/ScreenTop 与旧 Window.Left/Top
    /// 同语义，拖动数学零迁移；元素挂 Canvas 的换算由 DP 回调承担）。</summary>
    public DragKit(StageElement host)
        : this(host, () => new Point(host.ScreenLeft, host.ScreenTop),
              p => { host.ScreenLeft = p.X; host.ScreenTop = p.Y; }, host)
    {
    }

    private DragKit(IInputElement positionBase, Func<Point> getPos, Action<Point> setPos, StageElement? stageHost = null)
    {
        _positionBase = positionBase;
        _stageHost = stageHost;
        _getPos = getPos;
        _setPos = setPos;
    }

    private IInputElement CoordinateBase => _stageHost?.Stage ?? _positionBase;

    /// <summary>当前是否处于拖动态（位移已超阈值）。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>按下（会话开始；宿主可在此 GlideCancel 清位置动画残留）。</summary>
    public event Action? Pressed;

    /// <summary>拖动超阈值，首次进入拖动态。</summary>
    public event Action? DragStarted;

    /// <summary>拖动中（参数=宿主窗口新 Left/Top；宿主可钳制屏内）。</summary>
    public event Action<double, double>? Dragged;

    /// <summary>松手（moved=是否真拖动过）。</summary>
    public event Action<bool>? Ended;

    /// <summary>轻点：未拖动且按压 &lt; 450ms。</summary>
    public event Action? Tapped;

    /// <summary>长按 600ms（未拖动时触发一次；间隔内拖动会取消）。</summary>
    public event Action? LongPressed;

    static DragKit() { }   // 静态构造占位（保持与库内其他件一致的初始化语义）

    /// <summary>挂到专用拖动手柄元素：三套 preview 事件直订（隧道段抢先于任何按钮逻辑）。
    /// 纪律：手柄子树内不得含可交互控件（按钮/滑块/文本输入）—— 隧道段会 Handled 掉
    /// 它们的输入；含控件的「整面板可拖」场景用 <see cref="WireBubble"/>。</summary>
    public void Wire(FrameworkElement handle) => WireCore(handle, preview: true);

    /// <summary>挂到「整面板可拖」宿主（冒泡段、不 Handled）：面板内含按钮/滑块/文本框时
    /// 用此模式 —— 控件自身消费的按下（已 Handled）不会触发拖动，空白区按下拖动面板；
    /// 与旧版 Window.DragMove 的「整窗冒泡拖动」语义逐点等价（不吞控件输入，按钮照常可点）。</summary>
    public void WireBubble(FrameworkElement host) => WireCore(host, preview: false);

    private void WireCore(FrameworkElement el, bool preview)
    {
        _wiredElement = el;
        _preview = preview;
        el.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnMouseCaptureLost), true);
        el.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(OnTouchCaptureLost), true);
        el.AddHandler(UIElement.LostStylusCaptureEvent, new StylusEventHandler(OnStylusCaptureLost), true);
        if (preview)
        {
            el.PreviewMouseLeftButtonDown += OnDown;
            el.PreviewMouseLeftButtonUp += OnUp;
            el.PreviewMouseMove += OnMove;
            el.PreviewStylusDown += OnDown;
            el.PreviewStylusMove += OnMove;
            el.PreviewStylusUp += OnUp;
            el.PreviewTouchDown += OnDown;
            el.PreviewTouchMove += OnMove;
            el.PreviewTouchUp += OnUp;
        }
        else
        {
            el.MouseLeftButtonDown += OnDown;
            el.MouseLeftButtonUp += OnUp;
            el.MouseMove += OnMove;
            el.StylusDown += OnDown;
            el.StylusMove += OnMove;
            el.StylusUp += OnUp;
            el.TouchDown += OnDown;
            el.TouchMove += OnMove;
            el.TouchUp += OnUp;
        }
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_source != 0 && !IsDragging && !_longPressed)
            {
                _longPressed = true;
                LongPressed?.Invoke();
            }
        };
    }

    /// <summary>宿主主动终结会话（拖动转交 / 状态突变时用；后续 move/up 静默忽略）。</summary>
    public void EndSession()
    {
        // 所有权/状态先清空，随后释放 Capture；LostCapture 同步回调不得重入。
        _source = 0;
        _inputKind = 0;
        IsDragging = false;
        _hold.Stop();
        if (_activeOwner == this) _activeOwner = null;
        ReleaseCapture();
    }

    private void OnDown(object? sender, InputEventArgs e)
    {
        if (_source != 0)
        {
            // 同一接触的跨源提升消息（通常 Touch/Stylus → Mouse）只消费不重入；
            // 同类的新 Down 代表下一次真实接触，可自愈上一轮未送达的 Up。
            if (_inputKind != InputKind(e)) { if (_preview) e.Handled = true; return; }
            CancelSession();
        }
        if (_activeOwner is { } owner && owner != this && owner._source != 0)
        {
            if (Environment.TickCount64 - owner._activeTick < 3000) return;
            owner.CancelSession(); // 过期会话不可长期独占拖动权
        }
        _activeOwner = this;
        _activeTick = Environment.TickCount64;
        _source = SourceOf(e);
        _inputKind = InputKind(e);
        _origin = PositionOf(e);
        _downWinPos = _getPos();
        _downTick = Environment.TickCount64;
        IsDragging = false;
        _longPressed = false;
        Capture(e);
        _hold.Start();
        Pressed?.Invoke();
        // 隧道（手柄）模式：Handled 阻断提升链与下层元素；冒泡（整面板）模式不阻断 ——
        // 控件的输入已由控件自己消费，空白区事件才到达本件
        if (_preview) e.Handled = true;
    }

    private void OnMove(object? sender, InputEventArgs e)
    {
        if (_source == 0 || SourceOf(e) != _source || InputKind(e) != _inputKind) return;
        bool pressed = _source == 1
            ? e is MouseEventArgs m && m.LeftButton == MouseButtonState.Pressed
            : true;   // 触摸/笔的 Move 只在接触时发生
        if (!pressed) return;
        _activeTick = Environment.TickCount64;

        var p = PositionOf(e);
        if (!IsDragging && Math.Abs(p.X - _origin.X) + Math.Abs(p.Y - _origin.Y) > 8)
        {
            IsDragging = true;
            _hold.Stop();
            DragStarted?.Invoke();
        }
        if (IsDragging)
        {
            var requested = new Point(_downWinPos.X + p.X - _origin.X, _downWinPos.Y + p.Y - _origin.Y);
            // 单窗口舞台元素遵循布局槽位约束。普通面板不能拖进底部 chrome，
            // 副栏只能沿自己的横向槽移动；这在输入阶段就禁止重叠，而非松手后检测。
            if (_stageHost is not null) requested = StageUiLayout.ConstrainDrag(_stageHost, requested);
            _setPos(requested);
            var pos = _getPos();
            Dragged?.Invoke(pos.X, pos.Y);
        }
    }

    private void OnUp(object? sender, InputEventArgs e)
    {
        if (_source == 0 || SourceOf(e) != _source || InputKind(e) != _inputKind)
        { if (_preview) e.Handled = true; return; }
        _hold.Stop();
        bool wasDragging = IsDragging;
        long held = Environment.TickCount64 - _downTick;
        _source = 0;
        _inputKind = 0;
        IsDragging = false;
        if (_activeOwner == this) _activeOwner = null;
        ReleaseCapture();
        if (!wasDragging && held < 450 && !_longPressed)
            Tapped?.Invoke();
        Ended?.Invoke(wasDragging);
        if (_preview) e.Handled = true;
    }

    private void Capture(InputEventArgs e)
    {
        if (_wiredElement is null) return;
        try
        {
            switch (e)
            {
                case TouchEventArgs t:
                    if (t.TouchDevice.Capture(_wiredElement, CaptureMode.Element)) _touchCapture = t.TouchDevice;
                    break;
                case StylusEventArgs st:
                    if (st.StylusDevice.Capture(_wiredElement, CaptureMode.Element)) _stylusCapture = st.StylusDevice;
                    break;
                case MouseButtonEventArgs:
                    _mouseCapture = Mouse.Capture(_wiredElement, CaptureMode.Element);
                    break;
            }
        }
        catch { /* Capture 不可用时仍由路由 Up / 新 Down 自愈 */ }
    }

    private void ReleaseCapture()
    {
        var touch = _touchCapture;
        var stylus = _stylusCapture;
        bool mouse = _mouseCapture;
        _touchCapture = null;
        _stylusCapture = null;
        _mouseCapture = false;
        try { if (touch is not null) touch.Capture(null); } catch { }
        try { if (stylus is not null) stylus.Capture(null); } catch { }
        try { if (mouse && ReferenceEquals(Mouse.Captured, _wiredElement)) Mouse.Capture(null); } catch { }
    }

    private void CancelSession()
    {
        _source = 0;
        _inputKind = 0;
        IsDragging = false;
        _hold.Stop();
        if (_activeOwner == this) _activeOwner = null;
        ReleaseCapture();
    }

    private void OnMouseCaptureLost(object? sender, MouseEventArgs e)
    {
        if (_mouseCapture && _source == 1) CancelSession();
    }

    private void OnTouchCaptureLost(object? sender, TouchEventArgs e)
    {
        if (ReferenceEquals(_touchCapture, e.TouchDevice) && _source == 2) CancelSession();
    }

    private void OnStylusCaptureLost(object? sender, StylusEventArgs e)
    {
        if (ReferenceEquals(_stylusCapture, e.StylusDevice) && _source == 2) CancelSession();
    }

    private static int InputKind(InputEventArgs e) => e switch
    {
        MouseEventArgs => 1,
        TouchEventArgs => 2,
        StylusEventArgs => 3,
        _ => 0,
    };

    /// <summary>输入源归类：1=真鼠标直达 2=触摸/笔（Touch 与 Stylus 同源同坐标，归一类）。</summary>
    private static int SourceOf(InputEventArgs e) => e switch
    {
        MouseButtonEventArgs => 1,
        TouchEventArgs or StylusEventArgs => 2,
        _ => 1,
    };

    /// <summary>跨源取指针在宿主内的位置（三套事件统一坐标基准 = 宿主元素/窗口）。</summary>
    private Point PositionOf(InputEventArgs e) => e switch
    {
        MouseButtonEventArgs m => m.GetPosition(CoordinateBase),
        MouseEventArgs m => m.GetPosition(CoordinateBase),
        TouchEventArgs t => t.GetTouchPoint(CoordinateBase).Position,
        StylusEventArgs s => s.GetStylusPoints(CoordinateBase) is { Count: > 0 } pts ? pts[0].ToPoint() : new Point(),
        _ => new Point(),
    };
}
