using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>统一舞台窗口 · 墨迹层主控：状态字段 / 初始化接线 / 舞台激活停用 / 页面缓存与重合成 / 运行时状态快照。（v0.9.2 由 1224 行单文件按职责拆分，纯搬运，零逻辑改动）</summary>
public sealed partial class UnifiedStageWindow
{
    // ———————— 墨迹层状态（自 InkOverlayWindow 迁移） ————————

    private readonly RawInkPlugIn _plugIn = new();
    // 由 UI 线程更新、输入线程只读的界面命中区快照。底层全屏墨迹层的 Raw Stylus
    // 通道可能仍收到覆盖在 UI 上方的触点，因此必须在 Down 阶段明确屏蔽 UI 区域。
    private Rect[] _uiInputRects = Array.Empty<Rect>();
    private DispatcherTimer? _uiInputBoundsTimer;
    private RenderTargetBitmap? _pageCache;
    private Size _renderSize;
    private readonly Pen _guidePen = new(Brushes.White, 1.5) { DashStyle = DashStyles.Dash };
    private ScaleTransform? _cachedCanvasToDipTransform;
    private double _cachedCanvasTransformScaleX = double.NaN;
    private double _cachedCanvasTransformScaleY = double.NaN;

    /// <summary>输入源会话锁：0=空闲 1=鼠标(C) 2=StylusPlugIn(B) 3=WM_POINTER(A)
    /// 4=收笔临界区 5=舞台停用。原子占位——首个 down 独占到完整处理 up；以 4
    /// 阻止另一通道在上一笔提交完成前抢到新会话，以 5 阻止舞台收起窗口时继续接笔。</summary>
    private int _inputSession;
    // StylusPlugIn 在墨迹线程运行，而舞台生命周期在 UI 线程。该门闩把“确认会话仍有效
    // →创建首个笔划”与“停用舞台→强制收笔”串行化，避免停用先收笔、迟到 Down 后建出隐藏半笔。
    private readonly object _inputLifecycleGate = new();

    /// <summary>触摸所有权时限（v0.6.0）：通道 A 手掌抑制拒绝触摸后，该触摸随后
    /// 提升出的合成鼠标事件（通道 C）也必须拒绝 —— 否则被手掌判死的触摸会以
    /// 鼠标形态绕道复活，画出 unwanted 墨迹。时限而非标志：触摸 up 后标志无人
    /// 清理，时限 800ms（覆盖一次长接触）自然过期。</summary>
    private long _touchOwnedUntil;

    private bool _mouseInking;

    // 手掌接触守卫：由 WPF Touch 事件填充（接触面积），B/C 通道按设备 id / 短窗口查询。
    // 说明：StylusPlugIn 本身不提供接触面积；这是 B 通道在没有 WM_POINTER 元数据时的兜底。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _touchPalmById = new();
    private long _palmBlockUntilTick;

    /// <summary>输入状态牌刷新泵（250ms 轮询 Invalidate）：状态牌内容（穿透位/
    /// 会话）随轮询刷新；指针坐标由各输入回调缓存（Move 不主动重绘 —— 高频
    /// InvalidateVisual 会拖累渲染管线，轮询粒度足够判断「在不在层上」）。</summary>
    private DispatcherTimer? _badgeTimer;
    private bool _pointerOnLayer;
    private double _lastPtrX, _lastPtrY;
    private long _lastPtrTick;

    /// <summary>状态牌档位名（SetInputMode 可变 —— 单窗口下切档不重建窗口）。</summary>
    private string ModeLabel => _inputMode switch
    {
        "super" => "超级兼容",
        "compat" => "鼠标兼容",
        _ => "触摸优先",
    };

    /// <summary>墨迹层输入/渲染接线（构造器调用一次）。订阅全部落在 _inkLayer 而
    /// 非 Window：命中 UI 层元素的输入不进墨迹链路（单窗口分层命中语义）。</summary>
    private void InitializeInk()
    {
        // StylusPlugIn 挂墨迹层画布：报点路由以「命中 _inkLayer」为前提 ——
        // Background 必须非 null（铁律：null 的元素不参与 hit-test，StylusPlugIn
        /// 收不到输入 —— v0.1.0 笔记本实测回打的核心 bug），由 ApplySurface 恒定保证。
        _inkLayer.Attach(_plugIn);

        // 禁系统长按右击（press-and-hold）+ 轻滑手势：与全部 StageElement 同纪律。
        // 不设的后果：在墨迹层上按住不动 → 系统手势识别接管并吞掉后续鼠标消息 →
        // 通道 C 收不到 up、笔迹悬挂/长按不落墨（「长按也会留下墨点」的基本要求）。
        Stylus.SetIsPressAndHoldEnabled(_inkLayer, false);
        Stylus.SetIsFlicksEnabled(_inkLayer, false);

        // 通道 B（WPF 墨迹线程报点，绕过 UI 消息泵，第一笔零延迟）
        _plugIn.SuppressInkAtPoint = IsInkStartBlockedByUi;
        _plugIn.SuppressInkForDevice = IsPalmSuppressedDevice;
        _plugIn.PointDown += OnRawDown;
        _plugIn.PointMove += OnRawMove;
        _plugIn.PointUp += OnRawUp;

        // 通道 C（鼠标兜底/触摸提升链）—— 订阅在 _inkLayer：点击工具栏按钮的
        // 鼠标命中 UI 元素、不路由到墨迹层，杜绝「点按钮画出墨迹」（原版靠
        /// 工具栏独立窗口的 Z 序隔离实现，单窗口由视觉树命中顺序承担）。
        _inkLayer.MouseLeftButtonDown += OnMouseInkDown;
        _inkLayer.MouseMove += OnMouseInkMove;
        _inkLayer.MouseLeftButtonUp += OnMouseInkUp;

        // WPF 的 StylusPlugIn 只能拿到压力/坐标，没有接触面积。用同一触点提升出的
        // TouchPoint.Size 做手掌面积判定，并按 TouchDevice.Id 缓存；若 StylusPlugIn 的
        // 设备 id 与 Touch id 在驱动上不一致，则用 120ms 的短窗口兜底（宁可拒绝一次
        // 真实起笔，也不让整个手掌落墨；真实手指重触即可恢复）。
        _inkLayer.PreviewTouchDown += OnTouchContactDown;
        _inkLayer.PreviewTouchMove += OnTouchContactMove;
        _inkLayer.PreviewTouchUp += OnTouchContactUp;

        // 状态牌指针缓存（只记坐标不重绘，250ms 轮询统一刷新）
        _inkLayer.MouseMove += (_, e) => { var p = e.GetPosition(_inkLayer); NotePointer(p.X, p.Y); };
        _inkLayer.MouseEnter += (_, _) => _pointerOnLayer = true;
        _inkLayer.MouseLeave += (_, _) => _pointerOnLayer = false;
        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _badgeTimer.Tick += (_, _) => { if (_inkLayer.IsVisible) Invalidate(); };
        Closed += (_, _) =>
        {
            _badgeTimer?.Stop();
            _uiInputBoundsTimer?.Stop();
        };   // 定时器持窗口引用，停表放行 GC

        // 快照只枚举 UI/插件/引导/HUD 视觉子树；刷新频率 60Hz，输入线程无锁读取，
        // 不在每个触点上 Dispatcher.Invoke，避免为了拦截误画反过来增加触摸延迟。
        _uiInputBoundsTimer = new DispatcherTimer(DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(16) };
        _uiInputBoundsTimer.Tick += (_, _) => RefreshUiInputBounds();
    }

    /// <summary>UI 线程更新所有非墨迹层的实际可命中区域，使用 InkLayer 本地 DIP 坐标。</summary>
    internal void RefreshUiInputBounds()
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)RefreshUiInputBounds); }
            catch (InvalidOperationException) { }
            return;
        }

        var rects = new List<Rect>(64);
        foreach (var layer in new[] { _uiLayer, _pluginLayer, _guideLayer, _hudLayer })
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(layer); i++)
                CaptureUiInputRects(VisualTreeHelper.GetChild(layer, i), rects);
        }
        Volatile.Write(ref _uiInputRects, rects.ToArray());
    }

    private void CaptureUiInputRects(DependencyObject node, List<Rect> rects)
    {
        if (node is UIElement ui && !ui.IsVisible) return;
        if (node is FrameworkElement fe && fe.IsHitTestVisible && fe.RenderSize.Width > 0 && fe.RenderSize.Height > 0)
        {
            try
            {
                // TransformBounds 同时考虑 RenderTransform/LayoutTransform；只转换左上角再套 RenderSize
                // 会漏掉被工具栏定位变换移到宿主之外的画笔面板。
                var r = fe.TransformToVisual(_inkLayer).TransformBounds(new Rect(new Point(0, 0), fe.RenderSize));
                // 轻微放大，避免边界像素因 DPI/圆角舍入而漏进墨迹层。
                r.Inflate(1.5, 1.5);
                if (double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Width) && double.IsFinite(r.Height))
                    rects.Add(r);
            }
            catch { /* 控件正在重建/脱离视觉树时跳过本帧，下帧快照会收敛 */ }
        }

        int children;
        try { children = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }
        for (int i = 0; i < children; i++)
        {
            try { CaptureUiInputRects(VisualTreeHelper.GetChild(node, i), rects); }
            catch { }
        }
    }

    /// <summary>供 StylusPlugIn/鼠标/WM_POINTER Down 使用；只读快照，不访问 VisualTree。</summary>
    private bool IsInkStartBlockedByUi(double x, double y)
    {
        var point = new Point(x, y);
        var snapshot = Volatile.Read(ref _uiInputRects);
        for (int i = snapshot.Length - 1; i >= 0; i--)
            if (snapshot[i].Contains(point)) return true;
        return false;
    }

    /// <summary>舞台激活（ActivateStage 调用）：状态牌泵启动 + 会话诊断复位
    ///（每会话首笔诊断重新待命、指针 id 清零防复用误触发）。</summary>
    private void InkOnStageActivated()
    {
        Interlocked.Exchange(ref _inputSession, 0); // 新舞台会话解除停用哨兵
        _badgeTimer?.Start();
        RefreshUiInputBounds();
        _uiInputBoundsTimer?.Start();
        _sessionFirstFeed = false;
        _pointerSessionId = 0;
        _rejectedPointerId = 0;
    }

    /// <summary>舞台停用（DeactivateStage 调用，ForceLiftStroke 之前）：通道 C
    /// 捕获释放 + 会话锁/指针状态/触摸所有权清零。不释放 capture 的后果：
    /// Mouse.Capture 残留 → 全局鼠标事件被吞（比穿透更严重的输入劫持）。</summary>
    private void InkOnStageDeactivating()
    {
        if (_mouseInking)
        {
            _mouseInking = false;
            Mouse.Capture(null);
        }
        // 保持非零哨兵到下一次 ActivateStage；即使 Hide 期间仍有迟到的输入消息，
        // 也不能在 ForceLiftStroke 与真正隐藏之间重新占用引擎。
        Interlocked.Exchange(ref _inputSession, 5);
        _pointerSessionId = 0;    // 通道 A 会话状态清零（防指针 id 复用误触发）
        _rejectedPointerId = 0;   // 拒绝跟踪指针同步清零
        _touchOwnedUntil = 0;     // 触摸所有权时限清零（新会话不受旧判定牵连）
        _sessionFirstFeed = false;
        _badgeTimer?.Stop();
        _uiInputBoundsTimer?.Stop();
        Volatile.Write(ref _uiInputRects, Array.Empty<Rect>());
        InputDiagnostics.EndSession();
    }

    /// <summary>触摸/笔报点同步进状态牌坐标缓存（任何通道的活跃输入都算
    /// 「指针在层上」—— 用户排查要看的正是「程序收没收到我的触摸」）。
    /// 墨迹线程调用：double 在 x64 上原子写，无撕裂风险。</summary>
    private void NotePointer(double x, double y)
    {
        _lastPtrX = x; _lastPtrY = y;
        _lastPtrTick = Environment.TickCount64;
        _pointerOnLayer = true;
    }

    private InkTool _lastComposedTool = InkTool.Pen;
    private int _composedPage = -1;
    private int _composedStrokeCount = -1;
    private int _composedHistoryDepth = -1;
    /// <summary>强制重合成标志（SetInputMode 置位：画布组合变化后缓存口径需刷新 ——
    /// 原切档=销毁重建窗口、缓存全新，单窗口下以显式失效表达）。</summary>
    private bool _forceRecompose;
    private string? _composedProfileId;
    private double _composedProfilePxPerMm = double.NaN;
    private double _composedViewportScaleX = double.NaN;
    private double _composedViewportScaleY = double.NaN;

    /// <summary>输入状态牌（v0.8.2 可观测性）：墨迹层工作时左上角实时亮出
    /// 「层是否在收输入 / 指针在不在层上 / 触摸会话报点」—— 现场排查
    /// 「写不出来」的第一现场。三行内容：
    ///  1) 层态：穿透实况（整窗环境读 Win32 穿透位；逐像素环境读底刷期望
    ///     —— 穿透位恒无、alpha 才是事实执行面）+ 当前输入档位；
    ///  2) 指针：Move/报点缓存坐标（1.5s 内有输入视为在层上）；
    ///  3) 会话：InputDiagnostics 快照（通道/报点数/进行中与否）。
    /// 250ms 轮询刷新（_badgeTimer）；DebugMode 开启时避让到右上角
    ///（诊断 HUD 恒占据左上 24,24）。全程 try-catch：状态牌异常绝不影响墨迹渲染。</summary>
    private void EnsurePageCache()
    {
        if (_renderSize.Width <= 0 || _renderSize.Height <= 0)
        {
            // 预热期画布尚未布局：放映屏口径兜底（窗口几何由 RelocateToScreen 负责）。
            var b = Core.ShowScreen.Bounds;
            _renderSize = new Size(Math.Max(1, b.Width), Math.Max(1, b.Height));
        }

        // The document has one canonical coordinate space (3840×2160 logical pixels) regardless
        // of whether the projector is 1080p, 4K, or running with Windows DPI scaling. Rendering a
        // bitmap at _renderSize×DPI while strokes use document-space coordinates was the source of
        // pen-up blur/position changes. Keep cache pixels 1:1 with the vector document, then scale
        // the completed image to the current WPF viewport in RenderInk.
        int w = _svc.InkDocument.ScreenWidthPx;
        int h = _svc.InkDocument.ScreenHeightPx;
        if (_pageCache is null || _pageCache.PixelWidth != w || _pageCache.PixelHeight != h)
        {
            _pageCache = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            double modelToDip = _renderSize.Width > 0 ? _renderSize.Width / w : 1.0;
            InputDiagnostics.SetRender(w, h, _renderSize.Width, _renderSize.Height, modelToDip, Core.ShowScreen.DpiScale);
        }
    }

    /// <summary>窗口本地 DIP → 固定 3840×2160 墨迹模型坐标；所有输入通道必须经过同一映射。</summary>
    private Point ToCanvasPoint(Point dipPoint)
    {
        double vw = _renderSize.Width > 0 ? _renderSize.Width : Core.ShowScreen.Bounds.Width;
        double vh = _renderSize.Height > 0 ? _renderSize.Height : Core.ShowScreen.Bounds.Height;
        if (!double.IsFinite(vw) || vw <= 0) vw = _svc.InkDocument.ScreenWidthPx;
        if (!double.IsFinite(vh) || vh <= 0) vh = _svc.InkDocument.ScreenHeightPx;
        return new Point(
            dipPoint.X * _svc.InkDocument.ScreenWidthPx / vw,
            dipPoint.Y * _svc.InkDocument.ScreenHeightPx / vh);
    }

    /// <summary>固定 3840×2160 墨迹模型坐标 → 窗口本地 DIP（ToCanvasPoint 的逆映射；
    /// AI 气泡锚点等 UI 元素定位用）。1080p 视口下因子 ≈0.5，原先把模型坐标直接当
    /// 屏幕 DIP 用，气泡恒跑右侧（v1.1.1 修复）。</summary>
    internal Point CanvasPointToDip(Point canvasPoint) => new(
        canvasPoint.X * CanvasToDipScaleX,
        canvasPoint.Y * CanvasToDipScaleY);

    private InkPoint ToCanvasPoint(InkPoint dipPoint)
    {
        var p = ToCanvasPoint(new Point(dipPoint.X, dipPoint.Y));
        return new InkPoint(p.X, p.Y, dipPoint.Pressure, dipPoint.TimestampTicks);
    }

    private ScaleTransform CanvasToDipTransform()
    {
        double sx = (_renderSize.Width > 0 ? _renderSize.Width : _svc.InkDocument.ScreenWidthPx) /
            _svc.InkDocument.ScreenWidthPx;
        double sy = (_renderSize.Height > 0 ? _renderSize.Height : _svc.InkDocument.ScreenHeightPx) /
            _svc.InkDocument.ScreenHeightPx;
        if (_cachedCanvasToDipTransform is null || sx != _cachedCanvasTransformScaleX || sy != _cachedCanvasTransformScaleY)
        {
            var transform = new ScaleTransform(sx, sy);
            transform.Freeze();
            _cachedCanvasToDipTransform = transform;
            _cachedCanvasTransformScaleX = sx;
            _cachedCanvasTransformScaleY = sy;
        }
        return _cachedCanvasToDipTransform;
    }

    private double CanvasToDipScaleX =>
        (_renderSize.Width > 0 ? _renderSize.Width : _svc.InkDocument.ScreenWidthPx) / _svc.InkDocument.ScreenWidthPx;
    private double CanvasToDipScaleY =>
        (_renderSize.Height > 0 ? _renderSize.Height : _svc.InkDocument.ScreenHeightPx) / _svc.InkDocument.ScreenHeightPx;

    /// <summary>笔刷：粗细物理毫米 → px（机型档案精确映射）；荧光笔半透明。
    /// 视觉下限 2.5 DIP：档案失配防"亚像素笔迹"（见 DrawStroke 注释）。</summary>
    private Pen MakeWpfPen(PenInfo pen, InkEngine engine)
    {
        // Keep the visible minimum expressed in DIPs while the stroke itself lives in model units.
        double visibleFloor = 2.5 / Math.Max(0.01, CanvasToDipScaleX);
        double px = Math.Max(visibleFloor, engine.Profile.MmToPx(pen.ThicknessMm));
        var brush = GetStrokeBrush(Color.FromArgb(pen.Alpha, pen.Color.R, pen.Color.G, pen.Color.B));
        return GetStrokePen(brush, Math.Max(1.0, px));
    }

    // ———————— 调试状态快照（v0.6.0：HUD「墨迹层窗口」区数据源） ————————

    /// <summary>墨迹层运行时状态快照（多行文本，HUD 100ms 轮询显示）。
    /// 现场「舞台是否真实出现 / 穿透位实况 / 被谁压住 / 几何对位 / 画布与命中环境」
    /// 一屏尽览 —— 不再隔空猜测，问题环境的窗口状态直接可见。</summary>
    public string DebugStateText()
    {
        try
        {
            if (_source is not { } src)
                return "  HWND 未创建（窗口从未显示 · HookHwnd 未执行）";
            var sb = new StringBuilder(256);
            int ex = GetWindowLongW(src.Handle, GWL_EXSTYLE);
            bool pass = (ex & WS_EX_TRANSPARENT) != 0;
            bool wantPass = _svc.InkEngine.Tool == InkTool.TouchThrough;
            sb.Append($"  HWND 0x{src.Handle:X} · Win32可见={YN(IsWindowVisible(src.Handle))} · " +
                      $"WPF可见={YN(IsVisible)} · 激活={YN(_active)} · 画布={YN(_inkLayer.IsVisible)}\n");
            sb.Append($"  EXSTYLE 0x{ex:X8} · 穿透位={(pass ? "挂" : "无")}{(pass != wantPass && _wholeWindowHit ? " ⚠与期望不符" : "")} · " +
                      $"期望={(wantPass ? "穿透" : "接收")} · 守卫={(_guard is null ? "缺位!" : "就位")} · " +
                      $"命中环境={(_wholeWindowHit ? "整窗" : "逐像素")}\n");
            var b = Core.ShowScreen.Bounds;
            sb.Append($"  窗口 ({Left:F0},{Top:F0}) {Width:F0}×{Height:F0} · 放映屏 ({b.Left:F0},{b.Top:F0}) " +
                      $"{b.Width:F0}×{b.Height:F0} · 工具 {_svc.InkEngine.Tool}\n");
            sb.Append($"  Z序上方: {ZAboveClasses()}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"  状态读取异常: {ex.Message}"; }
    }

    private static string YN(bool b) => b ? "是" : "否";

    /// <summary>Z 序上方可见窗口类名链（前 6 个）：舞台被谁压住现场可见。</summary>
    private string ZAboveClasses()
    {
        try
        {
            if (_source is not { } src) return "（未挂钩）";
            var sb = new StringBuilder(128);
            var cur = GetWindow(src.Handle, GW_HWNDPREV);
            int scanned = 0, shown = 0;
            var name = new StringBuilder(64);
            while (cur != IntPtr.Zero && scanned++ < 16 && shown < 6)
            {
                name.Clear();
                if (GetClassNameW(cur, name, 64) > 0 && IsWindowVisible(cur))
                {
                    if (shown > 0) sb.Append(" / ");
                    sb.Append(name.ToString());
                    shown++;
                }
                cur = GetWindow(cur, GW_HWNDPREV);
            }
            return shown == 0 ? "（无 —— 本窗口即最高）" : sb.ToString();
        }
        catch { return "（读取失败）"; }
    }

    // ———————— 嵌套宿主与输入插件 ————————

    /// <summary>墨迹层画布宿主：承载 StylusPlugIn（protected 集合访问）+ 自绘渲染管线
    ///（OnRender 委托回宿主窗口 —— 渲染随画布显隐：super 档穿透=画布隐藏=墨迹让位，
    /// 与原版「窗口隐藏」等价）。Background 由宿主 ApplySurface 管理（铁律：非 null，
    /// 否则元素不参与 hit-test、StylusPlugIn 收不到输入）。</summary>
    private sealed class InkCanvasHost : Canvas
    {
        private readonly UnifiedStageWindow _owner;
        public InkCanvasHost(UnifiedStageWindow owner) => _owner = owner;

        /// <summary>挂载 StylusPlugIn（WPF 墨迹线程低延迟报点通道）。</summary>
        public void Attach(StylusPlugIn plugIn) => StylusPlugIns.Add(plugIn);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);   // Panel 基类先画 Background（底刷/深色画布），墨迹叠于其上
            _owner.RenderInk(dc);
        }
    }

    #region RawInkPlugIn：WPF 墨迹线程（StylusPlugIn）

    /// <summary>
    /// 运行于 WPF 专用墨迹线程：报点不经过 UI 线程消息泵（Dispatcher 队列），
    /// 这是"第一笔零延迟 + 120Hz 跟手"的关键。StylusPlugIn 是 WPF 低延迟墨迹的官方机制。
    /// v0.3.1：纯转发 —— 「笔在场」检测/报点时刻戳等状态全部移除（笔优先策略
    /// 已随主动笔支持一起删除；防双发由窗口层会话锁统一承担）。
    /// </summary>
    private sealed class RawInkPlugIn : StylusPlugIn
    {
        public event Action<InkPoint>? PointDown;
        public event Action<InkPoint>? PointMove;
        public event Action<InkPoint>? PointUp;

        // v0.9.6 多指连线修复：以「首指设备 id + 陈旧超时 + 瞬跳过滤」三重栅栏，
        // 保证同一时刻只有一指在画（第二指的点不会混入当前笔画）。
        private int _activeId;
        private long _activeTick;
        private double _lastX = double.NaN, _lastY = double.NaN;
        private bool _ignoreActive;
        public Func<double, double, bool>? SuppressInkAtPoint { get; set; }
        /// <summary>按设备 id 拒绝被触摸面积判定为手掌的起笔；无映射时返回 false。</summary>
        public Func<int, bool>? SuppressInkForDevice { get; set; }

        /// <summary>复位栅栏（舞台停用/会话被外部终结时调用）。</summary>
        public void ResetActive() { _activeId = 0; _ignoreActive = false; _lastX = _lastY = double.NaN; }

        protected override void OnStylusDown(RawStylusInput rawStylusInput)
        {
            base.OnStylusDown(rawStylusInput);
            long now = Environment.TickCount64;
            // 现持槽者为被屏蔽触点（_ignoreActive：命中 UI 不产生墨迹）时直接让位 ——
            // 否则手掌搭在工具栏/翻页岛上时，真正书写手指的 Down 会被保鲜期无限期吞掉。
            if (_activeId != 0 && now - _activeTick < 2000 && !_ignoreActive) return;
            var points = rawStylusInput.GetStylusPoints();
            if (points.Count == 0) return;
            _activeId = rawStylusInput.StylusDeviceId;
            _activeTick = now;
            _lastX = _lastY = double.NaN;
            // 原始 StylusPlugIn 位于全屏底层，不能只依赖 WPF 的 routed-event Handled。
            // 若 Down 命中工具栏/翻页栏/弹层/手掌，本指触点整轮忽略到 Up，绝不落墨。
            _ignoreActive = SuppressInkAtPoint?.Invoke(points[0].X, points[0].Y) == true
                            || SuppressInkForDevice?.Invoke(rawStylusInput.StylusDeviceId) == true;
            if (_ignoreActive) return;
            long ts = InkClock.NowMs();
            // Down 批的后续点不能丢：首点创建会话，其余点按 Move 喂入（共享会话锁）。
            PointDown?.Invoke(new InkPoint(points[0].X, points[0].Y, PressureOf(points[0]), Stamp(ts, 0, points.Count)));
            for (int i = 1; i < points.Count; i++)
                PointMove?.Invoke(new InkPoint(points[i].X, points[i].Y, PressureOf(points[i]), Stamp(ts, i, points.Count)));
        }

        protected override void OnStylusMove(RawStylusInput rawStylusInput)
        {
            base.OnStylusMove(rawStylusInput);
            if (_activeId == 0) return;
            if (rawStylusInput.StylusDeviceId != _activeId) return;
            if (_ignoreActive) return;   // 被屏蔽触点不产生墨迹，也不得刷新保鲜期 —— 否则 Move 持续击穿 2 秒陈旧超时，会话槽被无限期占用
            long now = Environment.TickCount64;
            long ts = InkClock.NowMs();
            var pts = rawStylusInput.GetStylusPoints();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (!double.IsNaN(_lastX) && Math.Abs(p.X - _lastX) + Math.Abs(p.Y - _lastY) > 600 && now - _activeTick < 25) continue;
                _lastX = p.X; _lastY = p.Y; _activeTick = now;
                PointMove?.Invoke(new InkPoint(p.X, p.Y, PressureOf(p), Stamp(ts, i, pts.Count)));
            }
        }

        protected override void OnStylusUp(RawStylusInput rawStylusInput)
        {
            base.OnStylusUp(rawStylusInput);
            if (_activeId == 0) return;
            if (rawStylusInput.StylusDeviceId != _activeId) return;
            if (_ignoreActive)
            {
                _activeId = 0;
                _ignoreActive = false;
                _lastX = _lastY = double.NaN;
                return;
            }
            long ts = InkClock.NowMs();
            var pts = rawStylusInput.GetStylusPoints();
            // Up 批多点：前序点改为 Move，最后一点才提交 Up；旧实现只提交首点，
            // 笔画尾段会被截断、收锋位置也提前。
            for (int i = 0; i + 1 < pts.Count; i++)
                PointMove?.Invoke(new InkPoint(pts[i].X, pts[i].Y, PressureOf(pts[i]), Stamp(ts, i, pts.Count)));
            var last = pts.Count > 0 ? pts[^1] : default;
            if (pts.Count > 0)
                PointUp?.Invoke(new InkPoint(last.X, last.Y, PressureOf(last), Stamp(ts, pts.Count - 1, pts.Count)));
            _activeId = 0;
            _lastX = _lastY = double.NaN;
        }

        /// <summary>同一批报点的人工时间分布：WPF StylusPoint 不提供时间戳，按 4ms
        /// （约 250Hz）递增，至少避免批内全部为同一时刻导致速度虚高。最后一个点
        /// 用 now，保证与后续事件时间单调。</summary>
        private static long Stamp(long nowMs, int index, int count)
            => nowMs - (long)Math.Max(0, count - 1 - index) * 4;


        /// <summary>压感钳制（手写最优：手指/被动手写笔无真实压感，此值恒近 0.5 中性，
        /// 笔锋粗细由速度链路模拟；此处仅防异常值）。</summary>
        private static double PressureOf(StylusPoint p) =>
            double.IsNaN(p.PressureFactor) || p.PressureFactor <= 0 ? 0.5 : Math.Min(1.0, p.PressureFactor);
    }

    #endregion

    #region Win32（通道 A 补充；窗口通用 P/Invoke 见壳文件）

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);   // 输入保底：穿透位现场摘除

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    // 真实 Win32 布局（x64）：POINTER_INFO 不含接触矩形 —— rcContact 属 POINTER_TOUCH_INFO。
    // 原实现把 RECT 追加在 POINTER_INFO 末尾并调 GetPointerInfo，该字段永不被填充（读到栈垃圾），
    // 手掌判定等于用随机矩形（误杀正常书写 / 完全失效）。改为 GetPointerTouchInfo 取真实 rcContact。
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public int ptPixelLocationX, ptPixelLocationY, ptHimetricLocationX, ptHimetricLocationY;
        public int ptPixelRawX, ptPixelRawY, ptHimetricRawX, ptHimetricRawY;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;      // 原实现遗漏：缺它则后继字段全部错位（x64 对齐）
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO PointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    // —— 通道 A 查询：通用/历史/触摸/笔 ——
    [DllImport("user32.dll")]
    private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);

    [DllImport("user32.dll")]
    private static extern bool GetPointerInfoHistory(uint pointerId, ref uint historyCount,
        [Out] POINTER_INFO[] historyInfo);

    [DllImport("user32.dll")]
    private static extern bool GetPointerTouchInfo(uint pointerId, out POINTER_TOUCH_INFO info);

    // v0.4.0 从下往上兼容：笔压感查询（POINTER_PEN_INFO.pressure 0~1024）。
    // 注意语义纪律：这里只取压感做<b>锦上添花的宽度调制</b>（L2 升级链路），
    // 绝不恢复任何「笔在场/笔优先」判定（v0.3.1 铲除的历史教训，见类头注释）。
    [DllImport("user32.dll")]
    private static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO PointerInfo;
        public uint penFlags, penMask;
        public uint pressure;        // 0~1024（无压感笔恒 0 或恒 1）
        public uint rotation;
        public int tiltX, tiltY;
    }

    #endregion
}
