using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 分割副岛（v0.7.0 可分割工具栏；v0.9.0 单窗口架构降维为舞台元素）：从主工具栏拉出的
/// Ops 段（撤销/重做/AI/插件/结束放映）独立成岛，拖近主工具栏松手即合并回去。
///  - 交互与主工具栏完全同构：56px 按钮 + 点击即有（PressWire）+ 手柄拖动三源定源（DragKit）；
///  - 松手吸附与主工具栏同语言：顶/底停靠带 + 水平居中（SnapKit.ResolveToolbar）；
///  - 合并手势：拖动结束时岛中心与主工具栏矩形距离 &lt; 90px → MergeRequested（编排器执行
///    Toolbar.MergeOpsBack，主栏变宽中心保持，副岛吮吸收回）；
///  - 位置记忆：SegmentDockX/Y（屏幕 DIP 零迁移）；无记忆时出现在主栏右侧 12px（分裂连续感）。
/// </summary>
public sealed class SegmentIsland : StageElement
{
    /// <summary>拖近主工具栏松手：请求合并回去（编排器 → Toolbar.MergeOpsBack + 关闭本岛）。</summary>
    public event Action? MergeRequested;

    private readonly AppServices _svc;
    private readonly DragKit _drag;
    private readonly Func<Rect?> _mainBarRect;
    private double _slotLeft;
    private double _slotRight;
    private double _slotTop;
    private bool _slotConfigured;

    /// <summary>
    /// 构建副岛。items = 分割点之后的元素（ToolbarCatalog.Item）；onCommand(id) = 命令分发
    /// （undo/redo/ai/plugin/nav.exit）；mainBarRect = 主工具栏屏幕矩形（合并距离判定）。
    /// </summary>
    public SegmentIsland(AppServices svc, IReadOnlyList<ToolbarCatalog.Item> items,
                         Action<string> onCommand, Func<Rect?> mainBarRect)
    {
        _svc = svc;
        _mainBarRect = mainBarRect;

        Content = new ScrollViewer
        {
            Content = BuildIsland(items, onCommand),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            CanContentScroll = false,
            Focusable = false,
            ClipToBounds = true,
        };

        _drag = new DragKit(this);
        _drag.Wire(_grip);
        _drag.Pressed += () => AnimationKit.GlideCancel(this);
        _drag.Dragged += (_, _) => ClampToScreen();
        _drag.LongPressed += () => MergeRequested?.Invoke();   // 长按 = 合并回去的显式手势
        _drag.Ended += moved =>
        {
            if (!moved) return;
            if (NearMainBar()) { MergeRequested?.Invoke(); return; }
            SnapToDock();
        };
    }

    private readonly Button _grip = new();

    private Border BuildIsland(IReadOnlyList<ToolbarCatalog.Item> items, Action<string> onCommand)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // 手柄（拖动/合并入口；长按 = 合并回去的显式手势）
        _grip.Style = (Style)Application.Current.FindResource("ToolButtonStyle");
        _grip.ToolTip = "可左右移动，长按并回主工具栏";
        _grip.Content = ToolbarFactory.Icon("IconGrip");

        row.Children.Add(_grip);

        foreach (var item in items)
        {
            var b = ToolbarFactory.BuildSimple(ToolbarFactory.Icon(item.Icon), item.Tip,
                () => onCommand(item.Id));
            row.Children.Add(b);
        }

        // 结束放映：Ops 段尾部恒定元素（低频高危，不进翻页岛防误触）；
        // v0.8.0：图标染警示琥珀（与主栏同款，高危一眼可辨）
        var exit = ToolbarFactory.BuildSimple(ToolbarFactory.Icon("IconStop"), "结束放映",
            () => onCommand("nav.exit"));
        if (exit.Content is System.Windows.Shapes.Path ep)
            ep.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "AccentAmberBrush");
        row.Children.Add(exit);

        var panel = new Border
        {
            Child = row,
            // 嵌套公式：内按钮圆角 14 + 垂直 Padding 6 = 20（圆角体系见 Tokens.xaml 注释）
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
        };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        panel.SetResourceReference(Border.EffectProperty, "ShadowChrome");
        return panel;
    }

    /// <summary>初始定位：有记忆用记忆；否则主栏右侧 12px（分裂连续感）；再兜底屏幕右下。
    /// v0.8.0 保险：PlaceInitial 在可见之前调用（ActualWidth=0，钳制无效），
    /// 元素 Loaded（挂舞台布局完成）后按真实尺寸二次钳制——记忆位贴屏缘也不出屏。</summary>
    public void PlaceInitial(double mainRight, double mainTop)
    {
        var wa = ShowScreen.Bounds;
        ScreenLeft = Math.Clamp(mainRight, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Math.Max(ActualWidth, 1) - 8));
        ScreenTop = mainTop;
        Loaded += (_, _) => ClampToScreen();
    }

    /// <summary>分配中央栏上方的专属副栏行；副栏可以横向调位，但永远不能离开该行。</summary>
    internal void SetChromeSlot(double laneLeft, double laneRight, double top)
    {
        var wa = ShowScreen.Bounds;
        _slotLeft = Math.Clamp(laneLeft, wa.Left + 8, wa.Right - 8);
        _slotRight = Math.Clamp(laneRight, _slotLeft + 1, wa.Right - 8);
        _slotTop = Math.Clamp(top, wa.Top + 8, wa.Bottom - 48);
        _slotConfigured = true;
        double slotWidth = Math.Max(96, _slotRight - _slotLeft);
        MaxWidth = slotWidth;
        if (Content is FrameworkElement content) content.MaxWidth = slotWidth;
        UpdateLayout();
        double w = Math.Max(ActualWidth, DesiredSize.Width);
        double maxLeft = Math.Max(_slotLeft, _slotRight - w);
        ScreenLeft = Math.Clamp(ScreenLeft, _slotLeft, maxLeft);
        ScreenTop = _slotTop;
    }

    internal Point ConstrainToChromeSlot(Point requested, double width, double height)
    {
        if (!_slotConfigured)
        {
            var wa = ShowScreen.Bounds;
            return new Point(
                Math.Clamp(requested.X, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - width - 8)),
                Math.Clamp(requested.Y, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - height - 8)));
        }
        double maxLeft = Math.Max(_slotLeft, _slotRight - width);
        return new Point(Math.Clamp(requested.X, _slotLeft, maxLeft), _slotTop);
    }

    private bool NearMainBar()
    {
        // 两岛处于不同的物理行，拖动无法造成相交。为保留快速合并手势，
        // 松手时只有副栏中心对准主栏中心的窄窗口才合并；长按手柄仍可显式合并。
        if (_mainBarRect() is not { } r) return false;
        double cx = ScreenLeft + ActualWidth / 2;
        double mx = r.Left + r.Width / 2;
        return Math.Abs(cx - mx) < 32;
    }

    private void SnapToDock()
    {
        ClampToScreen();
        _svc.Config.SegmentDockX = ScreenLeft;
        _svc.Config.SegmentDockY = ScreenTop;
        _svc.Config.Save();
    }

    private void ClampToScreen()
    {
        var p = ConstrainToChromeSlot(new Point(ScreenLeft, ScreenTop),
            Math.Max(ActualWidth, DesiredSize.Width), Math.Max(ActualHeight, DesiredSize.Height));
        ScreenLeft = p.X;
        ScreenTop = p.Y;
    }

    /// <summary>放映屏切换后的重对位（编排器 RelocateAllToScreen 联动：钳制回放映屏内）。</summary>
    public void RelocateIntoScreen() => ClampToScreen();

    // ———————— 唤起 / 收回 ————————

    /// <summary>唤起：内容吮吸放出（PopOpen，原窗口版语义）。</summary>
    public override void ShowAnimated()
    {
        if (Visibility != Visibility.Visible)
        {
            Opacity = 0;
            Visibility = Visibility.Visible;
        }
        if (Content is UIElement panel) AnimationKit.PopOpen(panel);
        else BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>收回：纯淡出（HideDuration/EaseHide，原窗口版语义——与翻页岛同家族动画语言）。</summary>
    public override void HideAnimated()
    {
        if (Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, (Duration)Application.Current.FindResource("HideDuration"))
        { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseHide") };
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
    }
}
