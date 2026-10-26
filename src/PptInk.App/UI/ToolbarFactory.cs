using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PptInk.UI;

/// <summary>
/// 工具栏按钮公共工厂（v0.7.0 灵动岛体系）：主工具栏 / 副岛 / 翻页岛共用一份构建与交互真相。
///  - PressWire「点击即有」：PreviewMouseLeftButtonDown 即执行并 Handled——
///    无需松手（大屏手指按压面积大，轻滑即超出 Click 判定半径导致「点了没反应」；
///    触摸经提升链到达鼠标事件，v0.6.0 实测工具栏可点可拖证明该链路活）。
///    Handled 阻断 ButtonBase 进入 pressed 状态 → Click 永不触发，零双发。
///  - SetToolActive「朱砂点亮」：选中工具 = 朱砂柔光底 + 图标染强调色（品牌识别度）；
///    未选中 = 透明底 + 暖白图标。主题热切换时宿主重放（OnThemeApplied 路径）。
/// </summary>
public static class ToolbarFactory
{
    /// <summary>
    /// 触摸优先的按下即执行。鼠标、触摸、WPF Stylus 都进入同一按压会话：
    /// 首个 Down 执行业务，其余同一接触的提升事件只被消费，不重复执行；任一 Up 立即释放。
    /// 不使用常规时间去抖——快速连续的两次独立触摸必须各自生效。
    /// </summary>
    public static void PressWire(Button b, Action onPressed) => PressWire((UIElement)b, onPressed);

    /// <summary>非 Button 触摸目标也使用同一输入语义（颜色圆点、粗细档位等）。</summary>
    public static void PressWire(UIElement element, Action onPressed)
    {
        bool pressed = false;
        int primaryInputKind = 0; // 1=mouse, 2=touch, 3=stylus
        TouchDevice? touchCapture = null;
        StylusDevice? stylusCapture = null;
        bool mouseCapture = false;

        void ReleaseCapture()
        {
            // 先清引用再释放；Capture(null) 会同步触发 Lost*Capture，避免事件重入递归。
            var touch = touchCapture;
            var stylus = stylusCapture;
            bool mouse = mouseCapture;
            touchCapture = null;
            stylusCapture = null;
            mouseCapture = false;
            try { if (touch is not null) touch.Capture(null); } catch { }
            try { if (stylus is not null) stylus.Capture(null); } catch { }
            try { if (mouse && ReferenceEquals(Mouse.Captured, element)) Mouse.Capture(null); } catch { }
        }

        void ResetSession()
        {
            pressed = false;
            primaryInputKind = 0;
            ReleaseCapture();
        }

        void Down(InputEventArgs e)
        {
            e.Handled = true;
            // 只合并同一接触的跨源提升事件（Touch/Stylus ↔ Mouse）。不能用固定时间窗
            // 去抖：真实的快速第二次点击必须执行；也不能让一个丢失 Up 的旧会话锁死按钮。
            if (pressed && primaryInputKind != InputKind(e)) return; // 同一物理接触的跨源提升事件
            if (pressed) ResetSession(); // 不是同一接触的 Down：旧 Up 丢失时立即自愈
            pressed = true;
            primaryInputKind = InputKind(e);
            try
            {
                switch (e)
                {
                    case TouchEventArgs t:
                        if (t.TouchDevice.Capture(element, CaptureMode.Element)) touchCapture = t.TouchDevice;
                        break;
                    case StylusEventArgs st:
                        if (st.StylusDevice.Capture(element, CaptureMode.Element)) stylusCapture = st.StylusDevice;
                        break;
                    case MouseButtonEventArgs:
                        mouseCapture = Mouse.Capture(element, CaptureMode.Element);
                        break;
                }
            }
            catch { /* capture 被系统拒绝时仍保留 Up 路由与下一次 Down 自愈 */ }
            onPressed();
        }

        int InputKind(InputEventArgs e) => e switch
        {
            MouseButtonEventArgs => 1,
            TouchEventArgs => 2,
            StylusEventArgs => 3,
            _ => 0,
        };

        void Up(InputEventArgs e)
        {
            e.Handled = true;
            if (primaryInputKind != InputKind(e)) return; // 只由按下该接触的设备释放会话
            ResetSession();
        }

        element.Focusable = false;
        Stylus.SetIsPressAndHoldEnabled(element, false);
        Stylus.SetIsFlicksEnabled(element, false);

        // handledEventsToo 保证即便控件模板先标记事件，也能释放这次真实接触。
        element.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => Down(e)), true);
        element.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => Up(e)), true);
        element.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, e) => Down(e)), true);
        element.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, e) => Up(e)), true);
        element.AddHandler(UIElement.PreviewStylusDownEvent, new StylusDownEventHandler((_, e) => Down(e)), true);
        element.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler((_, e) => Up(e)), true);
        element.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler((_, _) => { if (mouseCapture) ResetSession(); }), true);
        element.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>((_, e) => { if (ReferenceEquals(touchCapture, e.TouchDevice)) ResetSession(); }), true);
        element.AddHandler(UIElement.LostStylusCaptureEvent, new StylusEventHandler((_, e) => { if (ReferenceEquals(stylusCapture, e.StylusDevice)) ResetSession(); }), true);
    }

    /// <summary>
    /// 通用图标按钮（撤销/重做/AI/插件/结束放映/翻页…）：App 级 ToolButtonStyle + 点击即有。
    /// icon 为已构建的 Path（IconFill 样式 + 几何由调用方 FindResource）。
    /// 按压脉冲在此统一做（一份真相）——调用方 lambda 只写业务动作，无需捕获按钮自身。
    /// </summary>
    public static Button BuildSimple(Path icon, string tip, Action onPressed, string? label = null, bool compact = false)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButtonStyle"),
            ToolTip = tip,
            Content = MakeButtonContent(icon, label, compact),
            Focusable = false,
            IsTabStop = false,
        };
        ApplyLabelSize(b, compact);
        PressWire(b, () => { AnimationKit.PressPulse(b); onPressed(); });
        return b;
    }

    /// <summary>图标 Path 构建（App 级 IconFill 样式 + 几何键）。v1.1.2：按 AppConfig.ToolbarIconTheme
    /// 解析 Material Symbols 主题几何；主题缺失/解析失败回退 Icons.xaml 经典矢量。</summary>
    public static Path Icon(string geometryKey)
    {
        Geometry data;
        var theme = Core.AppConfig.Current.ToolbarIconTheme;
        if (theme != "classic" && Theme.IconThemes.TryGet(theme, geometryKey, out var themed))
            data = themed;
        else
            data = (Geometry)Application.Current.FindResource(geometryKey);
        return new Path
        {
            Style = (Style)Application.Current.FindResource("IconFill"),
            Data = data,
        };
    }

    /// <summary>图标下文字开关（默认开）。</summary>
    public static bool LabelsOn => Core.AppConfig.Current.ToolbarShowLabels;

    /// <summary>按钮内容：开启标签时 = 图标 + 下方中文短名；否则纯图标。
    /// compact（v1.1.2 槽位拥挤降级）强制纯图标，把按钮从 60×62 压回 48×48，
    /// 让 1024 宽一体机上 9 个按钮 + 手柄 + 结束放映仍能全部落在中央槽内。</summary>
    public static FrameworkElement MakeButtonContent(Path icon, string? label, bool compact = false)
    {
        if (compact || !LabelsOn || string.IsNullOrWhiteSpace(label)) return icon;
        icon.Width = 20;
        icon.Height = 20;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var text = new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 44,
            IsHitTestVisible = false,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(icon);
        stack.Children.Add(text);
        return stack;
    }

    /// <summary>带标签时按钮加宽加高，给图标下文字留纵向空间；compact 时固定 48×48。</summary>
    public static void ApplyLabelSize(Button b, bool compact = false)
    {
        if (compact)
        {
            b.Width = 48;
            b.Height = 48;
            return;
        }
        if (!LabelsOn) return;
        b.Width = 60;
        b.Height = 62;
    }

    /// <summary>取按钮内容里的图标 Path（内容可能是 Path，也可能是图标+文字的 StackPanel）。</summary>
    public static Path? IconPathOf(object? content) => content switch
    {
        Path p => p,
        Panel panel => panel.Children.OfType<Path>().FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// 朱砂点亮：on = AccentSoft 底 + 图标染强调色；off = 透明底 + 暖白图标。
    /// （Background 本地值覆盖模板默认；图标 Path.Fill 本地值覆盖 IconFill 样式——
    /// 两者都在宿主 RefreshSelection/OnThemeApplied 时全量重算，主题切换不残留。）
    /// </summary>
    public static void SetToolActive(Button b, bool on)
    {
        if (Application.Current?.Resources is not { } res) return;
        b.Background = on ? res["AccentSoftBrush"] as Brush ?? Brushes.Transparent : Brushes.Transparent;
        if (IconPathOf(b.Content) is { } p)
            p.Fill = on ? res["AccentBrush"] as Brush ?? Brushes.White : res["TextPrimaryBrush"] as Brush ?? Brushes.White;
    }
}
