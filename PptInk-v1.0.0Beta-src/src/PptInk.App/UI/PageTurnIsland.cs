using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 角落翻页岛（v0.7.0 灵动岛体系；v0.9.0 单窗口架构降维为舞台元素）：
/// 上一页 / 页码 / 下一页 的独立迷你岛。
///  - 固定两份：右下角 + 左下角；不绑定拖动手势，也不保存可移动位置；
///  - 「点击即有」：‹ › 按下瞬间翻页（PressWire），微滑不丢点；
///  - 页码胶囊只显示页码与进度，不参与手势拖动；
///  - 翻页动作走编排器 AdvanceSlide（COM 直翻 + 兜底推算，与翻页笔同一条真相）；
///  - 退出放映不设在此处（低频高危操作，在主工具栏/副岛，防误触中断课堂）。
/// </summary>
public sealed class PageTurnIsland : StageElement
{
    public event Action? PrevRequested;
    public event Action? NextRequested;

    /// <summary>角标："R" 固定右下 / "L" 固定左下。</summary>
    public string Anchor { get; }

    private readonly AppServices _svc;
    private TextBlock? _pageText;
    private Rectangle? _pageFill;
    private Border? _chromeBody;
    private ScrollViewer? _chromeScroller;
    private bool _slotConfigured;
    private double _slotLeft;
    private double _slotRight;
    private double _dockBottom;

    public PageTurnIsland(AppServices svc, string anchor)
    {
        _svc = svc;
        Anchor = anchor;

        Content = BuildIsland();
        // 预创建阶段使用自然宽度定位；放映前编排器再分配精确槽宽，宽度不足时内容可横向触摸滚动。
        RestorePosition();
        Loaded += (_, _) => RestorePosition();
    }

    private readonly Border _pagePill = new();

    /// <summary>岛体：全胶囊面板（CornerRadius 32）[‹ 56] [页码胶囊 92] [› 56]。</summary>
    private ScrollViewer BuildIsland()
    {
        var prev = ToolbarFactory.BuildSimple(ToolbarFactory.Icon("IconPrev"), "上一页",
            () => PrevRequested?.Invoke());
        var next = ToolbarFactory.BuildSimple(ToolbarFactory.Icon("IconNext"), "下一页",
            () => NextRequested?.Invoke());

        // 页码胶囊：只显示数字 + 底部 3px 进度条，不承接拖动
        _pageText = new TextBlock
        {
            Text = "– / –",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _pageFill = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
        _pageFill.SetResourceReference(Rectangle.FillProperty, "AccentBrush");
        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Child = _pageFill, Margin = new Thickness(4, 0, 4, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");

        var pill = new Grid { Width = 92 };
        pill.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pill.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_pageText, 0);
        pill.Children.Add(_pageText);
        Grid.SetRow(track, 1);
        pill.Children.Add(track);
        // 纯显示容器：不注册鼠标/触摸/笔拖动处理器。
        _pagePill.Background = Brushes.Transparent;
        _pagePill.Child = pill;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(prev);
        row.Children.Add(_pagePill);
        row.Children.Add(next);

        _chromeBody = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(32),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
        };
        _chromeBody.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _chromeBody.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        _chromeBody.SetResourceReference(Border.EffectProperty, "ShadowChrome");
        _chromeScroller = new ScrollViewer
        {
            Content = _chromeBody,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            CanContentScroll = false,
            Focusable = false,
            ClipToBounds = true,
        };
        return _chromeScroller;
    }

    // ———————— 页码联动 ————————

    /// <summary>岛体屏幕矩形（新手引导高亮定位，DIP 版——擦除锚点等 DIP 链路用）。</summary>
    public Rect? GetIslandRect() => Content is FrameworkElement f ? StageElement.ScreenRectOf(f) : null;

    /// <summary>岛体屏幕物理像素矩形（v0.8.0 引导定位链路：混合 DPI 部署零错位传递）。</summary>
    public Rect? GetIslandPhysicalRect() => Content is FrameworkElement f ? StageElement.ScreenRectOfPhysical(f) : null;

    /// <summary>翻页联动：更新页码与进度条（数字上浮入场，进度条弹簧推进）。</summary>
    public void UpdatePageBadge(int current, int total)
    {
        if (_pageText is null) return;
        string text = total > 0 ? $"{current} / {total}" : $"{current}";
        if (_pageText.Text != text)
        {
            _pageText.Text = text;
            AnimationKit.RiseIn(_pageText, 8, 260);   // 新页码上浮浮现
        }
        double frac = total > 0 ? Math.Clamp((double)current / total, 0, 1) : 0;
        if (_pageFill is not null)
        {
            // 满宽 84 = 胶囊 92 − track 左右 Margin 4×2（100% 时进度条恰好填满轨道）
            var a = new DoubleAnimation(_pageFill.Width, 84 * frac, AnimationKit.Fast)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _pageFill.BeginAnimation(Rectangle.WidthProperty, a);
        }
    }

    // ———————— 停靠 / 吸附 / 记忆 ————————

    /// <summary>固定停靠到右下/左下角。元素未布局时先 Measure 内容取真实尺寸。</summary>
    internal double PreferredWidth
    {
        get
        {
            if (_chromeBody is null) return 220;
            _chromeBody.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(1, _chromeBody.DesiredSize.Width);
        }
    }

    /// <summary>编排器分配底角独占槽。岛体宽度严格等于槽宽，内容超出时只在本岛内部横向滚动。</summary>
    internal void SetChromeSlot(double left, double right, double dockBottom)
    {
        var wa = ShowScreen.Bounds;
        _slotLeft = Math.Clamp(left, wa.Left, wa.Right);
        _slotRight = Math.Clamp(right, _slotLeft, wa.Right);
        _dockBottom = Math.Clamp(dockBottom, wa.Top, wa.Bottom);
        _slotConfigured = true;
        double slotWidth = Math.Max(0, _slotRight - _slotLeft);
        Width = slotWidth;
        MaxWidth = slotWidth;
        if (_chromeScroller is not null) _chromeScroller.Width = slotWidth;
        UpdateLayout();
        double h = ActualHeight;
        if (h <= 1 && Content is FrameworkElement f)
        {
            f.Measure(new Size(Math.Max(0, slotWidth), double.PositiveInfinity));
            h = f.DesiredSize.Height;
        }
        if (!double.IsFinite(h) || h < 1) h = 64;
        ScreenLeft = _slotLeft;
        ScreenTop = Math.Clamp(_dockBottom - h, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
    }

    public void RestorePosition()
    {
        if (_slotConfigured)
        {
            SetChromeSlot(_slotLeft, _slotRight, _dockBottom);
            return;
        }
        var wa = ShowScreen.Bounds;
        double w = Math.Max(ActualWidth, Content is FrameworkElement f ? MeasuredWidth(f) : 220);
        double h = Math.Max(ActualHeight, Content is FrameworkElement f2 ? MeasuredHeight(f2) : 64);
        double bottom = wa.Bottom - h - (AppConfig.Current.RespectGestureZone ? 64 : 16);
        ScreenLeft = Anchor == "R" ? wa.Right - w - 16 : wa.Left + 16;
        ScreenTop = bottom;

        static double MeasuredWidth(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(el.DesiredSize.Width, 1);
        }
        static double MeasuredHeight(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(el.DesiredSize.Height, 1);
        }
    }

    // ———————— 唤起 / 收回（与主工具栏同款动画语言） ————————

    /// <summary>收回：纯淡出（HideDuration/EaseHide，原窗口版语义；基类 PopClose 是
    /// 弹出型浮岛语言 —— 翻页岛/副岛家族用淡出，保持原视觉不变）。</summary>
    public override void HideAnimated()
    {
        if (Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, (Duration)Application.Current.FindResource("HideDuration"))
        { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseHide") };
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
    }
}
