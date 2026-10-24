using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Plugins;
using PptInk.UI.Theme;

namespace PptInk.UI;

// ============================================================================
// 插件面板族（v0.9.0 单窗口架构）：原 BuiltinPlugins.cs 内的独立 Topmost 窗口族
// （TimerWindow / SpotlightWindow / PickWindow / BoardWindow / QuickPluginPanel /
//  PluginPanelWindow）全部降维为舞台 Plugin 层元素。
//  - 面板族在 Plugin 层（Z 序高于墨迹层与 UI 层）：全屏遮罩（聚光灯）覆盖一切，
//    与旧独立 Topmost 窗口的观感等价，且不再有 Topmost 互压竞争；
//  - 拖动统一 DragKit 三源定源（旧 DragMove 有个别场合的源抖动）；
//  - 收放统一 ShowPanel/HidePanel（借窗纪律见 StageElement.ShowBorrowedAware）；
//  - 通用按钮视觉 PluginUiKit 随族迁入（原位于 BuiltinPlugins.cs）。
// ============================================================================

/// <summary>插件窗口族的主题化按钮：透明底 + 悬停亮起 + 按压加深 + 圆角 10，
/// 与主工具栏 ToolButtonStyle 同一视觉语言 —— WPF 默认按钮的浅灰方块在深色面板上极突兀。
/// 模板静态缓存（无状态可复用），实例只绑 Content/Click。
/// v0.9.8：文字与悬停/按压全部改用主题资源（原先硬编码暖白 + 白色 alpha，
/// 浅色主题下面板变浅底时这些按钮就成了白底白字看不见）。</summary>
internal static class PluginUiKit
{
    private static ControlTemplate? _tplNormal, _tplStretch;

    public static Button Button(string text, Action onClick, bool stretch = false)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Focusable = false,
            Margin = new Thickness(3, 2, 3, 2),
            MinWidth = 44,
        };
        // 文字跟随主题：深色主题暖白 / 浅色主题墨绿黑
        b.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        if (stretch) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.Template = Template(stretch);
        ToolbarFactory.PressWire(b, () => { AnimationKit.PressPulse(b); onClick(); });
        return b;
    }

    private static ControlTemplate Template(bool stretch)
    {
        if (stretch) return _tplStretch ??= Build(true);
        return _tplNormal ??= Build(false);
    }

    private static ControlTemplate Build(bool stretch)
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            stretch ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        // 悬停/按压用 DynamicResource：主题热切换时自动跟随，不再硬编码白色 alpha
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ItemHoverBrush"), "bd"));
        t.Triggers.Add(hover);
        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ItemSelectedBrush"), "bd"));
        t.Triggers.Add(press);
        return t;
    }
}

// ==================== 正倒计时 ====================

/// <summary>
/// 正倒计时面板（v0.9.0：原独立 TimerWindow 降维为舞台 Plugin 层元素）。
/// 已知差异（有意）：输入框键盘录入依赖窗口焦点 —— 舞台 NOACTIVATE 铁律下，
/// 软键盘以外的物理键盘录入受限（倒计时默认 300s 覆盖课堂主场景；正计时无需输入）。
/// </summary>
internal sealed class TimerElement : StageElement
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _seconds;
    private int _mode; // 0=正 1=倒
    private readonly TextBlock _display = new() { FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _input = new() { Text = "300", FontSize = 16, Width = 80, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DragKit _drag;

    public TimerElement()
    {
        Width = 220;   // 高度由内容撑开（原 SizeToContent.Height 等价）
        var panel = new StackPanel { Margin = new Thickness(0) };
        var border = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14), Child = panel };
        Content = border;
        // 面板底色/文字跟随主题（原硬编码深底 + 白色文字，浅色主题下与整体割裂）
        BindTheme(() => border.Background = ThemeManager.ContentBackground);
        _display.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_display);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        void Btn(string t, Action click) => row.Children.Add(PluginUiKit.Button(t, click));
        Btn("正", () => { _mode = 0; _seconds = 0; _timer.Start(); });
        Btn("倒", () => { if (int.TryParse(_input.Text, out var s)) _seconds = s; _mode = 1; _timer.Start(); });
        Btn("停", () => _timer.Stop());
        Btn("×", () => { _timer.Stop(); HidePanel(); });
        panel.Children.Add(_input);
        panel.Children.Add(row);

        // 放映屏感知：锚放映屏顶部居中（旧版「不设位置默认放主屏」的问题由屏幕坐标纪律解决）
        var show = ShowScreen.Bounds;
        ScreenLeft = show.Left + (show.Width - 220) / 2;
        ScreenTop = show.Top + 48;

        // 拖动（原 DragMove）：整面板冒泡模式 —— 内含按钮/文本框，不得隧道拦截
        //（否则按钮 Click 与文本框聚焦全失效）；空白区按下才拖动，与旧版逐点等价
        _drag = new DragKit(this);
        _drag.WireBubble(this);

        _timer.Tick += (_, _) =>
        {
            // v0.8.1「好用」：倒计时归零停表 + Toast 提醒（原先静停在 00:00，老师背对屏幕根本不知道时间到）
            if (_mode == 1 && _seconds > 0)
            {
                _seconds--;
                if (_seconds == 0) { _timer.Stop(); Toast.Show("时间到"); }
            }
            else if (_mode == 0) _seconds++;
            _display.Text = $"{_seconds / 60:D2}:{_seconds % 60:D2}";
        };
        _display.Text = "00:00";
    }

    public void ShowPanel()
    {
        var show = ShowScreen.Bounds;
        Measure(new Size(show.Width, show.Height));
        double h = Math.Max(ActualHeight, DesiredSize.Height);
        if (!double.IsFinite(h) || h < 1) h = 150;
        if (!ShowInExclusivePanelSlot(220, h))
            Toast.Show("面板放不下。关掉调试面板，或把显示缩放调小。");
    }
    public void HidePanel() => HideBorrowedAware();
}

// ==================== 焦点聚光灯遮罩 ====================

/// <summary>
/// 焦点聚光灯（v0.9.0：原独立 SpotlightWindow 降维为舞台 Plugin 层全屏元素 + 修复）。
/// v0.9.0 修复：原版点暗区 Close() 销毁窗口，之后任何 Show()/Hide() 均非法异常 ——
/// 「关掉聚光灯再激活」在旧实现是死路；元素版统一 HidePanel 幂等收放。
/// </summary>
internal sealed class SpotlightElement : StageElement
{
    private readonly DrawingGroup _maskGroup = new();
    private readonly double _w;
    private readonly double _h;
    private const double Radius = 280;
    private Point _center;

    public SpotlightElement()
    {
        // 放映屏感知：直接显式几何（原「先锚定目标屏再最大化」的元素等价）
        var b = ShowScreen.Bounds;
        ScreenLeft = b.Left;
        ScreenTop = b.Top;
        Width = b.Width;
        Height = b.Height;
        _w = Math.Max(800, b.Width);
        _h = Math.Max(600, b.Height);
        _center = new Point(_w / 2, _h / 2);
        Content = BuildSpotlight();
    }

    public void ShowPanel()
    {
        if (StageUiLayout.EnterModal(this)) ShowBorrowedAware();
        else Toast.Show("先关掉全屏界面再开聚光灯。");
    }
    public void HidePanel()
    {
        HideBorrowedAware();
        StageUiLayout.ExitModal(this);
    }

    private UIElement BuildSpotlight()
    {
        // OpacityMask 方案：暗罩的透明掩码 = 白底黑圆 → 圆内透明（挖洞）
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x08, 0x0A, 0x10)),
        };
        RebuildMask();
        overlay.OpacityMask = new DrawingBrush(_maskGroup) { Stretch = Stretch.None };

        // 可拖动透明圆（描边提示）
        var hole = new Ellipse
        {
            Width = Radius * 2, Height = Radius * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll,
        };
        Canvas.SetLeft(hole, _center.X - Radius);
        Canvas.SetTop(hole, _center.Y - Radius);

        var canvas = new Canvas();
        canvas.Children.Add(overlay);
        canvas.Children.Add(hole);

        // 暗区点击/触摸即退出；洞区的 Preview 输入先标记 Handled，因此不冒泡到退出逻辑。
        canvas.MouseLeftButtonDown += (_, e) => { if (!e.Handled) HidePanel(); };
        canvas.TouchDown += (_, e) => { if (!e.Handled) HidePanel(); };
        canvas.StylusDown += (_, e) => { if (!e.Handled) HidePanel(); };

        bool dragging = false;
        int inputKind = 0; // 1=鼠标、2=触摸、3=笔
        TouchDevice? touchCapture = null;
        StylusDevice? stylusCapture = null;
        bool mouseCapture = false;
        Stylus.SetIsPressAndHoldEnabled(hole, false);
        Stylus.SetIsFlicksEnabled(hole, false);

        int Kind(InputEventArgs e) => e switch
        {
            MouseEventArgs => 1,
            TouchEventArgs => 2,
            StylusEventArgs => 3,
            _ => 0,
        };

        Point PointOf(InputEventArgs e) => e switch
        {
            MouseEventArgs m => m.GetPosition(canvas),
            TouchEventArgs t => t.GetTouchPoint(canvas).Position,
            StylusEventArgs st => st.GetStylusPoints(canvas) is { Count: > 0 } pts ? pts[0].ToPoint() : _center,
            _ => _center,
        };

        void ReleaseCapture()
        {
            var touch = touchCapture;
            var stylus = stylusCapture;
            bool mouse = mouseCapture;
            touchCapture = null; stylusCapture = null; mouseCapture = false;
            try { if (touch is not null) touch.Capture(null); } catch { }
            try { if (stylus is not null) stylus.Capture(null); } catch { }
            try { if (mouse && ReferenceEquals(Mouse.Captured, hole)) Mouse.Capture(null); } catch { }
        }

        void CancelDrag()
        {
            dragging = false;
            inputKind = 0;
            ReleaseCapture();
        }

        void Down(object? sender, InputEventArgs e)
        {
            e.Handled = true;
            int kind = Kind(e);
            if (dragging)
            {
                if (kind != inputKind) return; // 同一接触的提升事件
                CancelDrag(); // 新的真实 Down 可自愈前一轮丢失的 Up
            }
            dragging = true;
            inputKind = kind;
            try
            {
                switch (e)
                {
                    case TouchEventArgs t:
                        if (t.TouchDevice.Capture(hole, CaptureMode.Element)) touchCapture = t.TouchDevice;
                        break;
                    case StylusEventArgs st:
                        if (st.StylusDevice.Capture(hole, CaptureMode.Element)) stylusCapture = st.StylusDevice;
                        break;
                    case MouseButtonEventArgs:
                        mouseCapture = Mouse.Capture(hole, CaptureMode.Element);
                        break;
                }
            }
            catch { }
        }

        void Move(object? sender, InputEventArgs e)
        {
            e.Handled = true;
            if (!dragging || Kind(e) != inputKind) return;
            if (inputKind == 1 && e is MouseEventArgs m && m.LeftButton != MouseButtonState.Pressed) return;
            var p = PointOf(e);
            _center = p;
            Canvas.SetLeft(hole, p.X - Radius);
            Canvas.SetTop(hole, p.Y - Radius);
            RebuildMask(); // 描边圆与遮罩洞同步移动
        }

        void Up(object? sender, InputEventArgs e)
        {
            e.Handled = true;
            if (!dragging || Kind(e) != inputKind) return;
            CancelDrag();
        }

        hole.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
        hole.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up), true);
        hole.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        hole.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(Down), true);
        hole.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(Up), true);
        hole.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(Move), true);
        hole.AddHandler(UIElement.PreviewStylusDownEvent, new StylusDownEventHandler(Down), true);
        hole.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler(Up), true);
        hole.AddHandler(UIElement.PreviewStylusMoveEvent, new StylusEventHandler(Move), true);
        hole.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler((_, _) => { if (mouseCapture) CancelDrag(); }), true);
        hole.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>((_, e) => { if (ReferenceEquals(touchCapture, e.TouchDevice)) CancelDrag(); }), true);
        hole.AddHandler(UIElement.LostStylusCaptureEvent, new StylusEventHandler((_, e) => { if (ReferenceEquals(stylusCapture, e.StylusDevice)) CancelDrag(); }), true);
        return canvas;
    }

    /// <summary>重建挖洞遮罩：白底 + 黑圆（DrawingBrush 的黑色区域令暗罩透明）。</summary>
    private void RebuildMask()
    {
        using var dc = _maskGroup.Open();
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _w, _h));
        dc.DrawEllipse(Brushes.Black, null, _center, Radius, Radius);
    }
}

// ==================== 随机点名 ====================

/// <summary>随机点名（v0.9.0：原独立 PickWindow 降维为舞台 Plugin 层元素）。</summary>
internal sealed class PickElement : StageElement
{
    private readonly TextBlock _name = new() { FontSize = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    // v0.8.1「能用可用」：默认占位名单仅兜底——「名单」按钮可从剪贴板导入多行名单
    private string[] _roster = Enumerable.Range(1, 45).Select(i => $"同学 {i:00}").ToArray();
    private readonly DispatcherTimer _roll = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DragKit _drag;

    public PickElement()
    {
        Width = 520;
        Height = 260;
        // CenterScreen 锚主屏 → 手动居中于放映屏工作区
        var show = ShowScreen.Bounds;
        ScreenLeft = show.Left + (show.Width - 520) / 2;
        ScreenTop = show.Top + (show.Height - 260) / 2;
        var shell = new Border
        {
            CornerRadius = new CornerRadius(24),
            Child = new StackPanel { Children = { _name, MakeButtons() } },
        };
        Content = shell;
        // 面板底色/文字跟随主题（原硬编码深底 + 白色文字）
        BindTheme(() => shell.Background = ThemeManager.ContentBackground);
        _name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        // 拖动：整面板冒泡模式（内含按钮，隧道拦截会让「开始/停/名单/×」全部失效）
        _drag = new DragKit(this);
        _drag.WireBubble(this);
        var rnd = Random.Shared;
        _roll.Tick += (_, _) => _name.Text = _roster[rnd.Next(_roster.Length)];
    }

    public void ShowPanel()
    {
        var show = ShowScreen.Bounds;
        Measure(new Size(show.Width, show.Height));
        double w = Math.Max(520, Math.Max(ActualWidth, DesiredSize.Width));
        double h = Math.Max(260, Math.Max(ActualHeight, DesiredSize.Height));
        if (!ShowInExclusivePanelSlot(w, h))
            Toast.Show("面板放不下。关掉调试面板，或把显示缩放调小。");
    }
    public void HidePanel() => HideBorrowedAware();

    private UIElement MakeButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        void Btn(string t, Action a) => row.Children.Add(PluginUiKit.Button(t, a));
        Btn("开始", () => _roll.Start());
        Btn("停！", () => _roll.Stop());
        Btn("名单", ImportRoster);
        Btn("×", () => { _roll.Stop(); HidePanel(); });
        return row;
    }

    /// <summary>从剪贴板导入名单（每行一个名字；随机点名真正可用的前置条件）。</summary>
    private void ImportRoster()
    {
        try
        {
            if (!Clipboard.ContainsText()) { _name.Text = "剪贴板无名单"; return; }
            var names = Clipboard.GetText()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            if (names.Length == 0) { _name.Text = "剪贴板无名单"; return; }
            _roster = names;
            _name.Text = $"已导入 {names.Length} 人";
        }
        catch { _name.Text = "读取剪贴板失败"; }
    }
}

// ==================== 板中板（画中画白板） ====================

/// <summary>
/// 板中板（v0.9.0：原独立 BoardWindow 降维为舞台 Plugin 层元素）。
/// 输入隔离说明：板中板位于 Plugin 层，自身不透明像素参与命中 → 触摸/笔由 WPF 输入
/// 路由直投 InkCanvas；舞台墨迹层的 StylusPlugIn（通道 B）与合成鼠标（通道 C）订阅
/// 挂在 _inkLayer，命中链不含墨迹层时天然收不到 → 与旧独立窗口逐像素等价，无双发。
/// </summary>
internal sealed class BoardElement : StageElement
{
    public BoardElement()
    {
        Width = 640;
        Height = 420;
        var show = ShowScreen.Bounds;
        ScreenLeft = show.Right - 660;
        ScreenTop = show.Top + 80;

        var dock = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        // 工具条底色跟随主题（原硬编码深色 → 浅色主题下按钮文字变深后不可见）
        BindTheme(() => bar.Background = ThemeManager.ContentBackground);
        // 板中板直接复用 WPF 原生 InkCanvas（本元素独立，不影响主墨迹层）
        var ink = new InkCanvas
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFA, 0xFA, 0xF7)),
            DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
            {
                Color = Color.FromRgb(0xE8, 0x44, 0x2E),   // 朱红批注（品牌默认色板首选，原 Indigo 与色板脱节）
                Width = 3.4, Height = 3.4, FitToCurve = true,
            },
        };
        void Btn(string t, Action a) => bar.Children.Add(PluginUiKit.Button(t, a));
        Btn("擦除", () => ink.EditingMode = System.Windows.Controls.InkCanvasEditingMode.EraseByStroke);
        Btn("书写", () => ink.EditingMode = System.Windows.Controls.InkCanvasEditingMode.Ink);
        Btn("清空", () => ink.Strokes.Clear());
        Btn("×", () => HidePanel());
        var grip = new Border { Cursor = Cursors.SizeAll, Child = bar };
        grip.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
        DockPanel.SetDock(grip, Dock.Top);
        dock.Children.Add(grip);
        dock.Children.Add(ink);
        var shell = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = dock,
        };
        shell.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        Content = shell;

        // 拖动手柄 = 顶部栏区（原 grip.MouseLeftButtonDown → DragMove）：grip 子树内是
        // 工具按钮条 —— 必须冒泡模式，否则「擦除/书写/清空/×」被隧道拦截全部失效
        var drag = new DragKit(this);
        drag.WireBubble(grip);
    }

    public void ShowPanel()
    {
        var show = ShowScreen.Bounds;
        Measure(new Size(show.Width, show.Height));
        double w = Math.Max(640, Math.Max(ActualWidth, DesiredSize.Width));
        double h = Math.Max(420, Math.Max(ActualHeight, DesiredSize.Height));
        if (!ShowInExclusivePanelSlot(w, h))
            Toast.Show("面板放不下。关掉调试面板，或把显示缩放调小。");
    }
    public void HidePanel() => HideBorrowedAware();
}

// ==================== 插件快捷选择面板 ====================

/// <summary>
/// 工具栏「插件」按钮弹出的选择面板（v0.9.0：原独立 QuickPluginPanel 窗口降维为舞台元素）。
/// 原窗口「失活自动关闭」在元素语境无焦点语义；等价收口 = 点击插件项即关（原有）+
/// 工具栏按钮 Toggle（原有）。注：大屏触摸本就不产生窗口失活，旧路径在触摸主导场景
/// 从未生效，故无能力损失。
/// </summary>
internal sealed class QuickPluginElement : StageElement
{
    public QuickPluginElement(PluginManager manager, Rect? anchor = null)
    {
        var show = ShowScreen.Bounds;
        var list = new StackPanel();
        foreach (var p in manager.Loaded.Where(manager.IsEnabled))
        {
            var item = PluginUiKit.Button(p.Manifest.Name, () => { manager.Activate(p); Dismiss(); }, stretch: true);
            item.Margin = new Thickness(4);
            list.Children.Add(item);
        }
        if (list.Children.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "没有启用的组件。请先在设置 → 插件中启用。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            list.Children.Add(empty);
        }

        var heading = new TextBlock
        {
            Text = "组件",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 2, 5, 7),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            CanContentScroll = false,
            Focusable = false,
            MaxHeight = Math.Max(120, show.Height * 0.52),
        };
        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(scroll);
        var shell = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        BindTheme(() =>
        {
            shell.Background = ThemeManager.ContentBackground;
            if (Application.Current?.Resources["PanelBorderBrush"] is Brush border)
                shell.BorderBrush = border;
        });
        Content = shell;

        // 快捷组件面板由工具栏按钮唤起，因此贴着触发按钮浮出，不再固定堵在右下角。
        double availableWidth = Math.Max(120, show.Width - 16);
        Width = Math.Min(300, availableWidth);
        Measure(new Size(Width, Math.Max(160, show.Height - 32)));
        double panelWidth = Math.Min(Math.Max(DesiredSize.Width, Math.Min(220, availableWidth)), availableWidth);
        double panelHeight = Math.Min(Math.Max(DesiredSize.Height, 80), Math.Max(120, show.Height * 0.52));
        Width = panelWidth;
        Height = panelHeight;
        var slotAnchor = anchor ?? new Rect(show.Left + show.Width / 2, show.Top + show.Height / 2, 1, 1);
        if (ShowNearAnchor(slotAnchor, panelWidth, panelHeight, 14)) ShowPanel();
        else Toast.Show("面板放不下。关掉调试面板，或把显示缩放调小。");
    }

    public void ShowPanel() => ShowBorrowedAware();
    public void HidePanel() => HideBorrowedAware();

    /// <summary>收口（点选插件后 / 再点工具栏按钮）：卸载 + 退订（原窗口 Close 的元素等价；
    /// 下次 Toggle 全新列表，与旧版 IsLoaded 判据语义一致）。</summary>
    public void Dismiss() => Close();
}

// ==================== Lua PANEL 插件宿主面板 ====================

/// <summary>
/// Lua PANEL 类插件（magic=1）的宿主面板元素（v0.9.0：原 PluginPanelWindow 降维）。
/// 原实现（v0.8.1 审查修复）按声明渲染元素并路由回调，让 PANEL 插件真正可用：
///  - text   → TextBlock（按 Id 注册，供 updatePanelElement 更新）
///  - slider → Slider（用户拖动 → 脚本 onEvent(数值)）
///  - button → Button（点击 → 脚本 onEvent()）
/// 风格与 Timer/Pick 同族（深色圆角卡片、可拖动、不抢焦点）。
/// </summary>
internal sealed class PluginPanelElement : StageElement
{
    private readonly Dictionary<string, TextBlock> _texts = new();
    private readonly Action _onClosedByUser;
    private readonly DragKit _drag;
    private bool _armed;   // Loaded 前 slider 程序化赋初值不触发脚本回调

    public PluginPanelElement(string title, PanelDecl decl, Action<string, double?> onEvent, Action onClosedByUser)
    {
        _onClosedByUser = onClosedByUser;
        Width = decl.Width;   // 高度由内容撑开（原 SizeToContent.Height 等价）
        var show = ShowScreen.Bounds;
        ScreenLeft = show.Right - decl.Width - 24;
        ScreenTop = show.Bottom - decl.Height - 90;

        var panel = new StackPanel();
        var border = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = panel,
        };
        Content = border;
        // 面板底色跟随主题（原硬编码深底 + 白色文字，浅色主题下白底白字）
        BindTheme(() => border.Background = ThemeManager.ContentBackgroundWithAlpha(decl.Alpha));

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        header.Children.Add(titleBlock);
        var close = PluginUiKit.Button("×", Dismiss);
        close.Margin = new Thickness(8, 0, 0, 0);
        close.MinWidth = 32;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(close);
        panel.Children.Add(header);

        foreach (var el in decl.Elements)
        {
            switch (el.Type)
            {
                case "text":
                    var tb = new TextBlock
                    {
                        Text = el.Text ?? "",
                        FontSize = 40,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 6),
                    };
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    if (!string.IsNullOrEmpty(el.Id)) _texts[el.Id] = tb;
                    panel.Children.Add(tb);
                    break;
                case "slider":
                    var sl = new Slider
                    {
                        Minimum = el.Min ?? 0,
                        Maximum = el.Max ?? 100,
                        Value = el.Value ?? 50,
                        Margin = new Thickness(6, 4, 6, 8),
                    };
                    // v0.9.8：插件面板滑块同样上触摸友好样式 + 直驱
                    if (Application.Current.TryFindResource("TouchSliderStyle") is Style ts) sl.Style = ts;
                    TouchSliderKit.Attach(sl);
                    sl.ValueChanged += (_, e) => { if (_armed) onEvent(el.OnEvent, e.NewValue); };
                    panel.Children.Add(sl);
                    break;
                case "button":
                    var b = PluginUiKit.Button(el.Text ?? el.OnEvent, () => onEvent(el.OnEvent, null), stretch: true);
                    b.Margin = new Thickness(4, 2, 4, 8);
                    panel.Children.Add(b);
                    break;
            }
        }

        // 拖动：整面板冒泡模式（内含按钮 + Slider，隧道拦截会让两者全失效）
        _drag = new DragKit(this);
        _drag.WireBubble(this);
        Loaded += (_, _) => _armed = true;
    }

    public void ShowPanel()
    {
        var show = ShowScreen.Bounds;
        Measure(new Size(show.Width, show.Height));
        double w = Math.Max(Width, Math.Max(ActualWidth, DesiredSize.Width));
        double h = Math.Max(ActualHeight, DesiredSize.Height);
        if (!double.IsFinite(h) || h < 1) h = 120;
        if (!ShowInExclusivePanelSlot(w, h))
            Toast.Show("面板放不下。关掉调试面板，或把显示缩放调小。");
    }
    public void HidePanel() => HideBorrowedAware();

    /// <summary>用户/宿主关闭（原窗口 Closed 事件等价：卸载 + 通知宿主收口脚本生命周期）。</summary>
    public void Dismiss()
    {
        Close();
        _onClosedByUser();
    }

    /// <summary>脚本 updatePanelElement(id, value) 落点：任意线程安全（Dispatcher 封送）。</summary>
    public void UpdateText(string id, string value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateText(id, value)); return; }
        if (_texts.TryGetValue(id, out var tb)) tb.Text = value;
    }
}