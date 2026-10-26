using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>
/// 悬浮主工具栏（v0.7.0 灵动岛体系核心成员；v0.9.0 单窗口架构降维为舞台元素）：
///  - 布局：AppConfig.ToolbarLayout + ToolbarCatalog 驱动；「翻页组」不再内联渲染——
///    由独立翻页岛（PageTurnIsland ×2：右下/左下）承载；「结束放映」恒定尾部元素；
///  - 可分割：Tools→Ops 边界是可拖拽「分割缝」——拉出即把 Ops 段拆成独立副岛（SegmentIsland），
///    副岛拖近/长按即合并回来；分割状态与位置均持久化；
///  - 点击即有：全部按钮 PreviewMouseLeftButtonDown 即执行（ToolbarFactory.PressWire）——
///    无需松手，手指微滑不丢点（大屏教学刚需）；
///  - 选中态「朱砂点亮」：AccentSoft 底 + 图标染强调色；
///  - 吸附：手柄拖动松手 → 顶/底停靠带 + 水平居中（SnapKit 弹簧动画）；
///  - 拖动跟手：手柄/胶囊维持 v0.6.0 三源定源纪律（实测稳定，不回归）；
///  - 长按手柄 600ms = 最小化收纳为屏幕胶囊。
/// v0.9.0 元素化：原 ToolbarWindow 窗口（xaml + code-behind）合一为纯代码构建的
/// StageElement —— Left/Top 以包装属性映射 ScreenLeft/ScreenTop（屏幕 DIP 语义零迁移），
/// 显隐走 Visibility，FindResource 走 App 级（构造期未挂视觉树）。
/// v0.9.4 由 1119 行单文件按职责拆分（纯搬运，零逻辑改动）：
///  - 本文件 = 主控：状态字段 / Left·Top 包装 / 事件 / 构造与双形态容器构建 / 引导定位查询；
///  - Toolbar.Layout.cs = 布局渲染与分割（RebuildLayout / SplitAtOps·MergeOpsBack / 按钮构建 / 选中高亮）；
///  - Toolbar.Drag.cs = 手柄拖动 / 停靠吸附 / 胶囊收纳 / 唤起收回动画；
///  - Toolbar.Palette.cs = 内嵌调色面板（最近色 / 三档粗细 / RGB 微调 / 笔样预览）。
/// </summary>
public sealed partial class Toolbar : StageElement
{
    private readonly AppServices _svc;
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private Point _holdOrigin;                 // 屏幕/舞台固定坐标，不随工具栏移动
    private Point _holdWindowOrigin;           // 按下瞬间工具栏位置，Move 用绝对增量计算
    private Border? _gripVisual;
    private bool _dragging;
    private int _gripSource;                 // 手柄拖动定源：0=空闲、1=鼠标、2=触摸、3=手写笔
    private TouchDevice? _gripTouchCapture;
    private StylusDevice? _gripStylusCapture;
    private bool _gripMouseCapture;
    private InkTool _currentTool = InkTool.Pen;
    /// <summary>v1.1.2：槽位放不下完整按钮时自动降级为纯图标紧凑模式。
    /// 原先超宽部分被 ScrollViewer 裁掉且触摸按下即执行、无法拖动滚动 ——
    /// 1024 宽一体机上「结束放映」「插件」会直接不可达。</summary>
    private bool _compact;

    // 双形态容器字段（原 xaml x:Name；构造器 BuildRoot 即赋值，null! 断言构造后恒可用）
    private Border _palettePanel = null!;
    private Border _panelRoot = null!;
    private StackPanel _rootRow = null!;
    private ScrollViewer _toolScroller = null!;
    private Border _capsuleRoot = null!;
    private double _dockLaneLeft;
    private double _dockLaneRight;
    private double _dockBottom;
    private bool _dockLaneConfigured;

    internal double DockLaneLeft => _dockLaneConfigured ? _dockLaneLeft : ShowScreen.Bounds.Left + 240;
    internal double DockLaneRight => _dockLaneConfigured ? _dockLaneRight : ShowScreen.Bounds.Right - 240;
    internal double DockBottom => _dockLaneConfigured ? _dockBottom : ShowScreen.Bounds.Bottom - (_svc.Config.RespectGestureZone ? 48 : 8);

    /// <summary>可用槽宽（屏幕 DIP）。尚未分配槽位时按屏幕宽度保守估算，
    /// 供 RebuildLayout 判断是否需要紧凑降级。</summary>
    private double AvailableLaneWidth =>
        _dockLaneConfigured ? Math.Max(1, _dockLaneRight - _dockLaneLeft)
                            : Math.Max(1, ShowScreen.Bounds.Width - 480);

    /// <summary>Left/Top 包装（屏幕 DIP，与旧 Window.Left/Top 同语义）——原 1032 行窗口
    /// 逻辑的坐标读写零迁移；动画走 ScreenLeftProperty/ScreenTopProperty（包装是 CLR 属性）。</summary>
    public double Left
    {
        get => ScreenLeft;
        set => ScreenLeft = value;
    }

    public double Top
    {
        get => ScreenTop;
        set => ScreenTop = value;
    }

    public event Action<InkTool>? ToolSelected;
    public event Action? EraseRequested;          // 再次点按"擦除"（已在擦除模式）：弹出三模式浮岛
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? ExitShowRequested;
    public event Action? ShapeAssistRequested;
    public event Action? Minimized;
    public event Action? GeometryChanged;

    internal void NotifyGeometryChanged() => GeometryChanged?.Invoke();

    public Toolbar(AppServices svc)
    {
        _svc = svc;
        _paletteSaveDebounce.Tick += (_, _) => FlushPaletteConfigSave();

        // 原 xaml 窗口级视觉属性（元素级等价物）
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextRenderingMode(this, System.Windows.Media.TextRenderingMode.ClearType);
        Stylus.SetIsFlicksEnabled(this, false);   // 禁轻滑手势（与禁长按右击配套，基类已设 PressAndHold）

        Content = BuildRoot();
        RefreshAiBadge();   // 持久化开启 AI 时，启动即点亮（开关状态与工具栏视觉一致性）
        RenderTransform = _slide;

        // 手柄（拖动 + 长按收纳为屏幕胶囊）
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (!_dragging && _gripSource != 0)
            {
                _gripSource = 0;
                ReleaseGestureCapture(_gripVisual, ref _gripTouchCapture, ref _gripStylusCapture, ref _gripMouseCapture);
                CollapseToCapsule();
            }
        };

        // 胶囊交互（v0.2.0）：点按展开 / 拖动 + 松手吸附最近边缘。
        _capsuleRoot.PreviewMouseLeftButtonDown += OnCapsuleDown;
        _capsuleRoot.PreviewMouseLeftButtonUp += OnCapsuleUp;
        _capsuleRoot.PreviewMouseMove += OnCapsuleMove;
        _capsuleRoot.PreviewStylusDown += OnCapsuleDown;
        _capsuleRoot.PreviewStylusUp += OnCapsuleUp;
        _capsuleRoot.PreviewStylusMove += OnCapsuleMove;
        _capsuleRoot.PreviewTouchDown += OnCapsuleDown;
        _capsuleRoot.PreviewTouchUp += OnCapsuleUp;
        _capsuleRoot.PreviewTouchMove += OnCapsuleMove;
        _capsuleRoot.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnCapsuleMouseCaptureLost), true);
        _capsuleRoot.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(OnCapsuleTouchCaptureLost), true);
        _capsuleRoot.AddHandler(UIElement.LostStylusCaptureEvent, new StylusEventHandler(OnCapsuleStylusCaptureLost), true);

        ApplyScale();
        RebuildLayout();
        SelectTool(InkTool.Pen);

        RestoreDockPosition();
    }

    // ———————— 缩放（v0.9.6：工具栏大小设置） ————————

    /// <summary>应用工具栏缩放（LayoutTransform.ScaleTransform）。</summary>
    private void ApplyScale()
    {
        double s = Math.Clamp(_svc.Config.ToolbarScale, 0.7, 1.6);
        LayoutTransform = Math.Abs(s - 1.0) < 0.01 ? null : new System.Windows.Media.ScaleTransform(s, s);
    }

    /// <summary>当前缩放系数（ApplyScale 同口径；无变换 = 1）。</summary>
    internal double ScaleFactor =>
        LayoutTransform is System.Windows.Media.ScaleTransform st ? st.ScaleX : 1.0;

    // v1.1.1 尺寸口径统一：实测（0.7/1.6 档验证）LayoutTransform 下 ActualWidth/RenderSize
    // 是变换前尺寸，DesiredSize 才是父坐标（视觉）尺寸。停靠钳制、胶囊中心、屏幕钳制
    // 全部改用视觉口径 —— 原混用让 160% 档视觉越出中央槽、70% 档底边悬空。
    // DesiredSize 需要至少一次布局才有效；未布局（构造早期）按 ActualWidth×scale 推算。

    /// <summary>视觉（缩放后）宽度，屏幕坐标系。</summary>
    internal double VisualWidth
    {
        get
        {
            double d = DesiredSize.Width;
            return double.IsFinite(d) && d > 0 ? d : Math.Max(1, ActualWidth * ScaleFactor);
        }
    }

    /// <summary>视觉（缩放后）高度，屏幕坐标系（画笔面板展开时含面板行）。</summary>
    internal double VisualHeight
    {
        get
        {
            double d = DesiredSize.Height;
            return double.IsFinite(d) && d > 0 ? d : Math.Max(1, ActualHeight * ScaleFactor);
        }
    }

    /// <summary>设置中心改完缩放后刷新（重建并重新定位，避免缩放后出屏）。</summary>
    public void RefreshScale()
    {
        if (IsPaletteOpen) HidePalette(immediate: true);
        ApplyScale();
        RebuildLayout();
        UpdateLayout();
        // Scale changes retain the same unique bottom-center slot; they cannot lift the bar over another panel.
        ClampToScreen();
        NotifyGeometryChanged();
    }

    /// <summary>
    /// 双形态容器构建（原 ToolbarWindow.xaml 纯代码化）：3 行 Grid ——
    /// Row0 内嵌调色面板（默认收起）/ Row1 主面板 ↔ 胶囊（叠放互斥）/ Row2 下弹调色面板位。
    /// </summary>
    private Grid BuildRoot()
    {
        // 内嵌调色面板（默认收起；弹出方向由代码按屏幕空间决定，见 ShowPalette）
        _palettePanel = new Border
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Width = 296,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6),
        };
        _palettePanel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _palettePanel.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        Grid.SetRow(_palettePanel, 0);

        // 主面板：按钮行宿主
        _rootRow = new StackPanel { Orientation = Orientation.Horizontal };
        _toolScroller = new ScrollViewer
        {
            Content = _rootRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            CanContentScroll = false,
            Focusable = false,
            ClipToBounds = true,
        };
        _panelRoot = new Border
        {
            Child = _toolScroller,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(6, 4, 6, 4),
        };
        _panelRoot.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _panelRoot.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        _panelRoot.SetResourceReference(Border.EffectProperty, "ShadowChrome");

        // 收纳胶囊（长按手柄后）：48px 圆球，可拖动、松手吸附最近屏幕边缘、点按展开
        _capsuleRoot = new Border
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Width = 48,
            Height = 48,
            Cursor = Cursors.Hand,
            ToolTip = "点一下展开。可在底部中央左右移动，不会挡翻页按钮",
            Child = new Path
            {
                Style = (Style)Application.Current.FindResource("IconFill"),
                Data = (Geometry)Application.Current.FindResource("IconPen"),
                Width = 20,
                Height = 20,
                IsHitTestVisible = false,
            },
        };
        _capsuleRoot.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _capsuleRoot.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        _capsuleRoot.SetResourceReference(Border.EffectProperty, "ShadowChrome");

        var mid = new Grid();
        mid.Children.Add(_panelRoot);
        mid.Children.Add(_capsuleRoot);
        Grid.SetRow(mid, 1);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_palettePanel);
        root.Children.Add(mid);
        return root;
    }

    // ———————— 新手引导定位（屏幕 DIP 矩形） ————————

    /// <summary>指定工具的屏幕矩形（引导高亮定位）。</summary>
    public Rect? GetToolScreenRect(InkTool tool)
    {
        var id = tool switch
        {
            InkTool.TouchThrough => "touch",
            InkTool.Pen => "pen",
            InkTool.Highlighter => "highlighter",
            InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe => "erase",
            _ => null,
        };
        return id is not null && _buttons.TryGetValue(id, out var b) ? StageElement.ScreenRectOf(b) : null;
    }

    /// <summary>擦除按钮矩形（擦除模式浮岛弹出锚点）。</summary>
    public Rect? GetEraseButtonRect() => _buttons.TryGetValue("erase", out var b) ? StageElement.ScreenRectOf(b) : null;

    /// <summary>插件按钮矩形（快捷小组件面板锚点）。</summary>
    public Rect? GetPluginButtonRect() => _buttons.TryGetValue("plugin", out var b) ? StageElement.ScreenRectOf(b) : null;

    /// <summary>撤销/重做联合区。</summary>
    public Rect? GetUndoRedoRect() => UnionRect("undo", "redo");

    /// <summary>AI/插件联合区。</summary>
    public Rect? GetAiPluginRect() => UnionRect("ai", "plugin");

    /// <summary>整个工具面板矩形（this = 双形态容器，与旧窗口 Content 矩形同语义——
    /// 胶囊态返回 52px 球矩形，副岛合并距离判定口径不变）。</summary>
    public Rect? GetPanelRect() => StageElement.ScreenRectOf(this);

    private Rect? UnionRect(params string[] ids)
    {
        Rect? acc = null;
        foreach (var id in ids)
        {
            if (_buttons.TryGetValue(id, out var b) && StageElement.ScreenRectOf(b) is { } r)
                acc = acc is { } a ? Rect.Union(a, r) : r;
        }
        return acc;
    }

    // —— 物理像素族（v0.8.0 引导定位链路专用，见 StageElement.ScreenRectOfPhysical 注释） ——

    /// <summary>指定工具的屏幕物理像素矩形（引导高亮）。</summary>
    public Rect? GetToolPhysicalRect(InkTool tool)
    {
        var id = tool switch
        {
            InkTool.TouchThrough => "touch",
            InkTool.Pen => "pen",
            InkTool.Highlighter => "highlighter",
            InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe => "erase",
            _ => null,
        };
        return id is not null && _buttons.TryGetValue(id, out var b) ? StageElement.ScreenRectOfPhysical(b) : null;
    }

    /// <summary>拖动柄矩形（物理像素）。引导「长按收纳 / 拖动移动」一步的挖空定位在此 ——
    /// 手势目标就是这根柄，挖在笔上会误导用户。</summary>
    public Rect? GetGripPhysicalRect() =>
        _gripVisual is { } g ? StageElement.ScreenRectOfPhysical(g) : null;

    /// <summary>撤销/重做联合区（物理像素）。</summary>
    public Rect? GetUndoRedoPhysicalRect() => UnionPhysicalRect("undo", "redo");

    /// <summary>AI/插件联合区（物理像素）。</summary>
    public Rect? GetAiPluginPhysicalRect() => UnionPhysicalRect("ai", "plugin");

    /// <summary>单个按钮的屏幕物理像素矩形（引导按功能逐一高亮用）。</summary>
    public Rect? GetButtonPhysicalRect(string id) =>
        _buttons.TryGetValue(id, out var b) ? StageElement.ScreenRectOfPhysical(b) : null;

    private Rect? UnionPhysicalRect(params string[] ids)
    {
        Rect? acc = null;
        foreach (var id in ids)
        {
            if (_buttons.TryGetValue(id, out var b) && StageElement.ScreenRectOfPhysical(b) is { } r)
                acc = acc is { } a ? Rect.Union(a, r) : r;
        }
        return acc;
    }
}