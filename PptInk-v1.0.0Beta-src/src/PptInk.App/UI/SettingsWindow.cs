using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using PptInk.Builtin;
using PptInk.Core;
using PptInk.Plugins;
using PptInk.Seewo;
using PptInk.UI.Theme;

// 注意：不 using System.IO（与 System.Windows.Shapes 的 Path 二义，Shapes.Path 用于图标），
// 文件系统 API 一律全限定（System.IO.Path / System.IO.Directory）。

namespace PptInk.UI;

/// <summary>
/// 现代设置中心（托盘「设置」打开，放映中置顶不被 PPT 遮挡）：
///  - 四分区：外观（主题/强调色/材质）· 工具栏（排序+显隐）· 插件（启停/安装/目录）· 行为（翻页笔/触控/机型/数据）；
///  - 分区切换走 AnimationKit.BlurSwitch 轻量转场（即时换内容 + 短淡入 / 位移）；
///  - 一切选择即时生效并持久化（无"保存"按钮 —— 课堂工具不该有提交仪式）；
///  - 主题/材质热切换：控件一律 SetResourceReference（DynamicResource 代码等价），
///    键被 ThemeManager 覆盖即全局刷新；窗口注册亚克力（Acrylic 时 DWM 模糊透出）；
///  - 大屏清晰度：UseLayoutRounding + ClearType + 全矢量图标；触控目标 ≥40px。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppServices _svc;
    private readonly Border _root;
    private readonly ContentControl _pageHost = new();
    private readonly ScrollViewer _pageScroller = new();
    private readonly Dictionary<string, NavItem> _nav = new();
    private readonly double _windowWidth;
    private readonly double _windowHeight;
    private readonly DispatcherTimer _configSaveDebounce;
    private string _page = "appearance";
    private int _pageRefreshToken;

    private sealed record NavItem(Button Btn, Rectangle Bar, TextBlock Txt);

    public SettingsWindow(AppServices svc)
    {
        _svc = svc;
        // 滑杆拖动期间只更新内存与实时 UI；延迟合并磁盘写入，避免触摸连续报点被同步 I/O 拖慢。
        _configSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _configSaveDebounce.Tick += (_, _) =>
        {
            _configSaveDebounce.Stop();
            _svc.Config.Save();
        };
        Closed += (_, _) =>
        {
            _configSaveDebounce.Stop();
            _svc.Config.Save(); // 关闭设置窗时保证最后一次滑杆值落盘
        };
        var sb = Core.ShowScreen.Bounds;
        // 按放映屏工作区自适应，避免低分辨率/高 DPI 时设置窗越界；大屏上留足设置项的阅读宽度。
        // Always fit the target display's usable area. The old minimum-first formula could
        // produce a 520×480 window larger than a small/remote-desktop display.
        double availableWidth = Math.Max(1, sb.Width - 24);
        double availableHeight = Math.Max(1, sb.Height - 24);
        _windowWidth = Math.Clamp(sb.Width * 0.78,
            Math.Min(520, availableWidth), Math.Min(1040, availableWidth));
        _windowHeight = Math.Clamp(sb.Height * 0.82,
            Math.Min(480, availableHeight), Math.Min(860, availableHeight));

        Title = "融绘 · 设置";
        Width = _windowWidth; Height = _windowHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;                     // S1 放映中打开不被全屏 PPT 盖住
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        // 审查修复（v0.1.3「圆角出现方形棱」）：窗口级 UseLayoutRounding 会把 1px 描边与
        // 圆角弧线取整到物理像素 —— 非 100% 缩放下圆角四角产生明显硬直角棱。
        // 窗口级布局圆整移除（SnapsToDevicePixels 保留位图对齐）。
        SnapsToDevicePixels = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        Stylus.SetIsPressAndHoldEnabled(this, false);

        Opacity = 0;
        // 淡入必须动画 Window 自身的 Opacity：动 _root 时 Window.Opacity 恒 0，
        // 最终透明度 = 两者相乘 → 窗口永远不可见（v0.1.0 "设置只有任务栏图标"的根因）
        // 定位：居中于放映屏（审查修复：CenterScreen 居主屏，双屏部署下设置窗
        // 弹在笔记本屏上、大屏前老师以为没反应）。
        Left = sb.Left + (sb.Width - _windowWidth) / 2;
        Top = sb.Top + (sb.Height - _windowHeight) / 2;
        Loaded += (_, _) => AnimationKit.FadeIn(this, 180);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        ThemeManager.Register(this);        // 材质系统（Acrylic 时 DWM 亚克力）
        Closed += (_, _) => ThemeManager.Unregister(this);

        _root = BuildShell();
        Content = _root;
        Navigate("appearance", initial: true);
    }

    /// <summary>主题热切换钩子（编排器接线）。控件全走 DynamicResource 引用，此处无需手动刷新。</summary>
    public void OnThemeApplied() { }

    /// <summary>应用版本号（v0.4.1 修复：从程序集读取，自动跟随 csproj，永不失同步。
    /// 原硬编码 "v1.2" 与实际版本 0.4.0 脱节 —— 用户反馈问题时无法准确标识版本，
    /// 售后定位困难。程序集缺失时回退 0.4.0）。</summary>
    private static string AppVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "v0.4.0";

    // ———————— 骨架：标题栏 + 左导航 + 内容区 ————————

    private Border BuildShell()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(BuildTitleBar());

        var body = new Grid();
        // Navigation yields space to the setting content on narrow displays instead of
        // keeping a desktop-only fixed-width sidebar.
        double navWidth = Math.Clamp(_windowWidth * 0.20, 112, 192);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(navWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);

        body.Children.Add(BuildNavColumn());

        var divider = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Right };
        divider.SetResourceReference(Rectangle.FillProperty, "PanelBorderBrush");
        Grid.SetColumn(divider, 0);
        body.Children.Add(divider);

        _pageHost.Margin = new Thickness(0, 0, 8, 0);
        _pageHost.VerticalAlignment = VerticalAlignment.Stretch;
        _pageScroller.Content = _pageHost;
        _pageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _pageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _pageScroller.PanningMode = PanningMode.VerticalOnly;
        _pageScroller.Padding = new Thickness(0, 0, 4, 0);
        Grid.SetColumn(_pageScroller, 1);
        body.Children.Add(_pageScroller);
        grid.Children.Add(body);

        var root = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            // 审查修复（v0.1.3「圆角有方棱」）：外圈柔和投影柔化圆角边缘
            //（layered window 内 DropShadow 有效），Margin 给阴影留绘制空间防裁剪。
            Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.45,
                Color = Color.FromRgb(0x00, 0x00, 0x00),
            },
        };
        root.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        root.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        return root;
    }

    private Border BuildTitleBar()
    {
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Ellipse.FillProperty, "AccentBrush");
        var title = new TextBlock
        {
            Text = "融绘 · 设置",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(dot);
        left.Children.Add(title);

        var closeIcon = new Path
        {
            Data = (Geometry)FindResource("IconClose"),
            Stretch = Stretch.Uniform,
            Width = 12, Height = 12,
            IsHitTestVisible = false,
        };
        closeIcon.SetResourceReference(Path.FillProperty, "TextSecondaryBrush");
        var close = new Button
        {
            Content = closeIcon,
            Width = 48, Height = 44,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = PlainButtonTemplate(7),
            ToolTip = "关闭 (Esc)",
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolbarFactory.PressWire(close, () => { AnimationKit.PressPulse(close); Close(); });

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        var border = new Border { Child = bar, CornerRadius = new CornerRadius(14, 14, 0, 0) };
        // v0.9.6：统一拖动 —— 标题栏走 DragKit（三源定源：触摸/笔/鼠标一致，松手即停）；
        // 冒泡段不吞 close 按钮点击。
        new DragKit(this).WireBubble(border);
        return border;
    }

    private DockPanel BuildNavColumn()
    {
        var dock = new DockPanel { Margin = new Thickness(10, 10, 10, 12) };

        // 版本号沉底（v0.4.1：读程序集版本，自动跟随 csproj）
        var ver = new TextBlock
        {
            Text = $"融绘 {AppVersion} · Build by Xianfish",
            FontSize = 10.5,
            Margin = new Thickness(17, 0, 0, 0),
        };
        ver.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
        DockPanel.SetDock(ver, Dock.Bottom);
        dock.Children.Add(ver);

        var col = new StackPanel();
        col.Children.Add(NavButton("appearance", "外观"));
        col.Children.Add(NavButton("toolbar", "工具栏"));
        col.Children.Add(NavButton("brush", "画笔"));
        col.Children.Add(NavButton("plugins", "插件"));
        col.Children.Add(NavButton("behavior", "行为与触控"));
        col.Children.Add(NavButton("advanced", "高级"));
        col.Children.Add(NavButton("about", "关于"));
        dock.Children.Add(col);
        return dock;
    }

    private Button NavButton(string key, string label)
    {
        var bar = new Rectangle { Width = 3, Height = 18, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        bar.SetResourceReference(Rectangle.FillProperty, "AccentBrush");

        var txt = new TextBlock { Text = label, FontSize = 13.5, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        txt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var grid = new Grid();
        grid.Children.Add(txt);
        grid.Children.Add(bar);

        var b = new Button
        {
            Content = grid,
            Height = 46,
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = PlainButtonTemplate(9),
        };
        ToolbarFactory.PressWire(b, () => { AnimationKit.PressPulse(b); Navigate(key); });
        _nav[key] = new NavItem(b, bar, txt);
        return b;
    }

    private void UpdateNavHighlight()
    {
        foreach (var (key, item) in _nav)
        {
            bool on = key == _page;
            item.Bar.Opacity = on ? 1 : 0;
            if (on)
            {
                item.Btn.SetResourceReference(Button.BackgroundProperty, "ItemSelectedBrush");
                item.Txt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                item.Txt.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                item.Btn.Background = Brushes.Transparent;
                item.Txt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                item.Txt.FontWeight = FontWeights.Normal;
            }
        }
    }

    // ———————— 页面路由（模糊转场） ————————

    private void Navigate(string key, bool initial = false)
    {
        if (!initial && _page == key) return;   // 重复点击当前页不转场
        _page = key;
        int navigationToken = ++_pageRefreshToken; // cancel stale scroll restores from the old page
        UpdateNavHighlight();
        if (initial)
        {
            _pageHost.Content = BuildPage(key);
            AnimationKit.FadeIn(_pageHost, 200);
            _pageScroller.ScrollToTop();
        }
        else
        {
            AnimationKit.BlurSwitch(_pageHost, () => _pageHost.Content = BuildPage(key));
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (IsVisible && navigationToken == _pageRefreshToken)
                    _pageScroller.ScrollToTop();
            }));
        }
    }

    /// <summary>
    /// 当前页局部状态重建。保留滚动位置并立即显示选中态；不对整页做淡入，避免每次
    /// 点选颜色/主题时闪白、滚回顶部和短暂延迟，导航页切换才使用独立转场。
    /// </summary>
    private void RefreshCurrentPage()
    {
        double offset = _pageScroller.VerticalOffset;
        int refreshToken = ++_pageRefreshToken;
        _pageHost.BeginAnimation(UIElement.OpacityProperty, null);
        _pageHost.Opacity = 1;
        _pageHost.Content = BuildPage(_page);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // A previous Loaded callback must not restore the scroll offset into a newer page.
            if (!IsVisible || refreshToken != _pageRefreshToken) return;
            _pageScroller.ScrollToVerticalOffset(offset);
        }));
    }

    /// <summary>滑杆交互的落盘合并：UI 和运行时设置即时更新，磁盘写入在短暂停手后执行。</summary>
    private void QueueConfigSave()
    {
        _configSaveDebounce.Stop();
        _configSaveDebounce.Start();
    }

    private FrameworkElement BuildPage(string key) => key switch
    {
        "toolbar" => BuildToolbarPage(),
        "brush" => BuildBrushPage(),
        "plugins" => BuildPluginsPage(),
        "behavior" => BuildBehaviorPage(),
        "advanced" => BuildAdvancedPage(),
        "about" => BuildAboutPage(),
        _ => BuildAppearancePage(),
    };

    // ══════════════════ 外观 ══════════════════

    private FrameworkElement BuildAppearancePage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("外观"));
        sp.Children.Add(PageSubtitle("改完全平台立即生效。"));

        // —— 主题 ——
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        themeRow.Children.Add(ThemeCard("深色", AppTheme.Dark, "暗环境舒适，默认"));
        themeRow.Children.Add(ThemeCard("浅色", AppTheme.Light, "明亮教室"));
        themeRow.Children.Add(ThemeCard("跟随系统", AppTheme.Auto, "随 Windows 明暗"));
        sp.Children.Add(Card("主题", themeRow));

        // —— 强调色 ——
        var wrap = new WrapPanel();
        foreach (var (name, hex) in ThemeManager.AccentPresets)
            wrap.Children.Add(AccentSwatch(name, hex));
        sp.Children.Add(Card("强调色", wrap));

        // —— 材质 ——
        var mWrap = new WrapPanel();
        foreach (var m in new[] { PanelMaterial.Solid, PanelMaterial.Translucent, PanelMaterial.Acrylic, PanelMaterial.Minimal })
            mWrap.Children.Add(MaterialCard(m));
        sp.Children.Add(Card("面板材质", mWrap));

        return sp;
    }

    private Border ThemeCard(string label, AppTheme theme, string tip)
    {
        Border MiniPanel(bool light)
        {
            // 预览色对齐 v0.8.0 岱青真实令牌：底色 Solid-Light #F5F7F6 / Solid-Dark #16201E，
            // 墨条对齐 TextPrimary（Light #1A211F / Dark #EEF2F0）——预览所见即主题所得
            var b = new Border { Width = 46, Height = 26, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(light ? Color.FromRgb(0xF5, 0xF7, 0xF6) : Color.FromRgb(0x16, 0x20, 0x1E)) };
            var sp = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };
            var ink = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x21, 0x1F) : Color.FromRgb(0xEE, 0xF2, 0xF0));
            sp.Children.Add(new Rectangle { Width = 24, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = ink });
            sp.Children.Add(new Rectangle { Width = 32, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = ink, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.55 });
            b.Child = sp;
            return b;
        }

        FrameworkElement preview = theme switch
        {
            AppTheme.Auto => new StackPanel { Orientation = Orientation.Horizontal, Children = { MiniPanel(false), MiniPanel(true) } },
            AppTheme.Light => MiniPanel(true),
            _ => MiniPanel(false),
        };

        var name = new TextBlock { Text = label, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var sub = new TextBlock { Text = tip, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
        body.Children.Add(preview);
        body.Children.Add(name);
        body.Children.Add(sub);

        var card = new Border { Child = body, Width = 148, Height = 116, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        bool on = ThemeManager.Theme == theme;
        if (on) card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else card.BorderBrush = Brushes.Transparent;

        ToolbarFactory.PressWire(card, () =>
        {
            AnimationKit.PressPulse(card);
            ThemeManager.SetTheme(theme);
            RefreshCurrentPage();
        });
        return card;
    }

    private Border AccentSwatch(string name, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        bool selected = ThemeManager.Accent == color;

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = selected ? 1 : 0 };

        var ball = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(color),
            Child = dot,
        };

        var swatch = new Border { Child = ball, Padding = new Thickness(5), CornerRadius = new CornerRadius(21), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = name };
        if (selected) swatch.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        ToolbarFactory.PressWire(swatch, () =>
        {
            AnimationKit.PressPulse(swatch);
            ThemeManager.SetAccent(color);
            RefreshCurrentPage();
        });
        return swatch;
    }

    private Border MaterialCard(PanelMaterial m)
    {
        // 32×26 材质示意：底层彩条透出程度 = 直觉的透明度
        FrameworkElement Glyph()
        {
            var accent = new SolidColorBrush(Color.FromArgb(0xB0, ThemeManager.Accent.R, ThemeManager.Accent.G, ThemeManager.Accent.B));
            var under = new StackPanel { Margin = new Thickness(3, 4, 3, 4), Opacity = 0.9 };
            under.Children.Add(new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = accent });
            under.Children.Add(new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = accent, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.6 });

            // v0.8.0 岱青化：色相统一 #16201E 系（对齐 ThemeManager 真实面板色）。
            // alpha 保留示意梯度 0x59/0xCC/0x88——真实 Acrylic(0xCC)/Translucent(0xD2) 均 ~80% 不透明，
            // 静态示意无法表达「系统模糊 vs 纯透明」之别，故以彩条透出程度做直觉区分；
            // Acrylic 描边对齐 ThemeManager 真实值 0x30。
            return m switch
            {
                PanelMaterial.Solid => new Border { Width = 32, Height = 26, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x1E)) },
                PanelMaterial.Acrylic => new Border { Width = 32, Height = 26, CornerRadius = new CornerRadius(6), Child = under, Background = new SolidColorBrush(Color.FromArgb(0x59, 0x16, 0x20, 0x1E)), BorderThickness = new Thickness(0.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) },
                PanelMaterial.Minimal => new Border { Width = 32, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x25, 0x23)) },
                _ => new Border { Width = 32, Height = 26, CornerRadius = new CornerRadius(6), Child = under, Background = new SolidColorBrush(Color.FromArgb(0x88, 0x16, 0x20, 0x1E)) },
            };
        }

        var (title, desc) = m switch
        {
            PanelMaterial.Solid => ("实体色", "最高对比 · 弱光投影环境"),
            PanelMaterial.Acrylic => ("亚克力模糊", "系统级实时模糊 · Win10 1803+"),
            PanelMaterial.Minimal => ("极简融合", "无描边无阴影 · 面板融入画面"),
            _ => ("半透明", "幻灯片透出 · 默认推荐"),
        };

        var nameT = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        nameT.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var descT = new TextBlock { Text = desc, FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) };
        descT.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        textCol.Children.Add(nameT);
        textCol.Children.Add(descT);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new ContentControl { Content = Glyph(), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(textCol);

        var card = new Border { Child = row, Padding = new Thickness(12, 10, 16, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 10, 10), Width = 246, Cursor = Cursors.Hand };
        if (ThemeManager.Material == m)
        {
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            card.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        }
        else
        {
            card.BorderBrush = Brushes.Transparent;
        }

        ToolbarFactory.PressWire(card, () =>
        {
            AnimationKit.PressPulse(card);
            ThemeManager.SetMaterial(m);
            RefreshCurrentPage();
        });
        return card;
    }

    // ══════════════════ 工具栏 ══════════════════

    private FrameworkElement BuildToolbarPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("工具栏"));
        sp.Children.Add(PageSubtitle("调图标顺序和显隐。笔始终可见。"));

        var list = new StackPanel();
        var layout = _svc.Config.ToolbarLayout;

        for (int i = 0; i < layout.Count; i++)
        {
            int index = i;
            var cfg = layout[i];
            var item = ToolbarCatalog.Find(cfg.Id);
            if (item is null) continue;

            var icon = new Path { Data = (Geometry)FindResource(item.Icon), Stretch = Stretch.Uniform, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Path.FillProperty, "TextPrimaryBrush");

            var nameT = new TextBlock { Text = item.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
            nameT.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var descT = new TextBlock { Text = item.Tip, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
            descT.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(nameT);
            textCol.Children.Add(descT);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(icon);
            left.Children.Add(new Border { Width = 12 });
            left.Children.Add(textCol);
            if (item.LockVisible)
            {
                var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
                var bt = new TextBlock { Text = "保底", FontSize = 10 };
                bt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                badge.Child = bt;
                left.Children.Add(badge);
            }

            Button MoveBtn(string iconKey, int delta, string tip)
            {
                var p = new Path { Data = (Geometry)FindResource(iconKey), Stretch = Stretch.Uniform, Width = 13, Height = 13, IsHitTestVisible = false };
                p.SetResourceReference(Path.FillProperty, "TextSecondaryBrush");
                var b = new Button
                {
                    Content = p, Width = 44, Height = 44,
                    Cursor = Cursors.Hand, Focusable = false,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Template = PlainButtonTemplate(8), ToolTip = tip,
                };
                ToolbarFactory.PressWire(b, () =>
                {
                    AnimationKit.PressPulse(b);
                    int j = index + delta;
                    if (j < 0 || j >= layout.Count) return;
                    (layout[index], layout[j]) = (layout[j], layout[index]);
                    _svc.Config.Save();
                    _svc.UiOrchestrator.RefreshToolbarLayout();
                    RefreshCurrentPage();
                });
                return b;
            }

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (item.LockVisible)
            {
                var t = new TextBlock { Text = "始终启用", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
                right.Children.Add(t);
            }
            else if (cfg.Id == "nav")
            {
                // 左右翻页岛是固定界面的一部分，不再由开关隐藏或进入拖动模式。
                var fixedLabel = new TextBlock
                {
                    Text = "左右固定",
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                };
                fixedLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
                right.Children.Add(fixedLabel);
            }
            else
            {
                var sw = new Switch(cfg.Visible);
                sw.Toggled += v =>
                {
                    cfg.Visible = v;
                    _svc.Config.Save();
                    _svc.UiOrchestrator.RefreshToolbarLayout();
                };
                right.Children.Add(sw);
            }

            var moves = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            if (cfg.Id != "nav")
            {
                moves.Children.Add(MoveBtn("IconUp", -1, "上移"));
                moves.Children.Add(MoveBtn("IconDown", +1, "下移"));
            }

            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(left);
            Grid.SetColumn(moves, 1);
            row.Children.Add(moves);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            list.Children.Add(row);
            if (index < layout.Count - 1)
                list.Children.Add(RowDivider());
        }
        sp.Children.Add(Card("元素布局", list));

        var visibilityList = new StackPanel();
        visibilityList.Children.Add(SwitchRow("工具栏闲置淡化",
            "闲置一会儿自动变淡，再碰一下就恢复。",
            _svc.Config.ToolbarIdleFadeEnabled,
            enabled =>
            {
                _svc.Config.ToolbarIdleFadeEnabled = enabled;
                _svc.Config.Save();
                _svc.UiOrchestrator.RefreshIdleFade();   // v0.9.9：工具栏 + 左右翻页岛一起刷新
            }));
        visibilityList.Children.Add(RowDivider());

        var idleDelayLabel = new TextBlock
        {
            Text = $"{_svc.Config.ToolbarIdleFadeSeconds:F1} 秒", Width = 58,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        };
        idleDelayLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var idleDelaySlider = new Slider
        {
            Minimum = 0.5, Maximum = 15.0, Width = 176,
            IsSnapToTickEnabled = true, TickFrequency = 0.5, SmallChange = 0.5, LargeChange = 1.0,
            Value = Math.Clamp(_svc.Config.ToolbarIdleFadeSeconds, 0.5, 15.0),
            ToolTip = "0.5–15 秒；仅在启用闲置淡化时生效",
        };
        ApplyTouchSliderStyle(idleDelaySlider);
        idleDelaySlider.ValueChanged += (_, _) =>
        {
            _svc.Config.ToolbarIdleFadeSeconds = Math.Round(idleDelaySlider.Value * 2.0) / 2.0;
            idleDelayLabel.Text = $"{_svc.Config.ToolbarIdleFadeSeconds:F1} 秒";
            QueueConfigSave();
            _svc.UiOrchestrator.RefreshIdleFade();   // v0.9.9：工具栏 + 左右翻页岛一起刷新
        };
        var idleDelayControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        idleDelayControl.Children.Add(idleDelayLabel);
        idleDelayControl.Children.Add(idleDelaySlider);
        visibilityList.Children.Add(SettingsRow("淡化等待时间", "多久没操作开始变淡。想常驻就调长。", idleDelayControl));
        visibilityList.Children.Add(RowDivider());

        var idleOpacityLabel = new TextBlock
        {
            Text = $"{_svc.Config.ToolbarIdleOpacity:P0}", Width = 58,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        };
        idleOpacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var idleOpacitySlider = new Slider
        {
            Minimum = 0.2, Maximum = 1.0, Width = 176,
            IsSnapToTickEnabled = true, TickFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.1,
            Value = Math.Clamp(_svc.Config.ToolbarIdleOpacity, 0.2, 1.0),
            ToolTip = "20% 较淡，100% 完全不透明",
        };
        ApplyTouchSliderStyle(idleOpacitySlider);
        idleOpacitySlider.ValueChanged += (_, _) =>
        {
            _svc.Config.ToolbarIdleOpacity = Math.Round(idleOpacitySlider.Value * 20.0) / 20.0;
            idleOpacityLabel.Text = $"{_svc.Config.ToolbarIdleOpacity:P0}";
            QueueConfigSave();
            _svc.UiOrchestrator.RefreshIdleFade();   // v0.9.9：工具栏 + 左右翻页岛一起刷新
        };
        var idleOpacityControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        idleOpacityControl.Children.Add(idleOpacityLabel);
        idleOpacityControl.Children.Add(idleOpacitySlider);
        visibilityList.Children.Add(SettingsRow("淡化后不透明度", "调高更清晰。想一直可见就关掉上面的开关。", idleOpacityControl));
        sp.Children.Add(Card("可见性与触摸", visibilityList));

        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 24, 24) };
        resetRow.Children.Add(GhostButton("恢复默认布局", "IconReset", () =>
        {
            _svc.Config.ToolbarLayout = AppConfig.CreateDefaultToolbarLayout();
            // v0.7.0：默认布局 = 翻页岛两份全开（nav 项开关与岛实例一体收敛）
            _svc.Config.PageIslandRightEnabled = true;
            _svc.Config.PageIslandLeftEnabled = true;
            // v0.9.7：同时清除分割标记 —— 副岛回主栏（撤销/重做/AI/插件回到主工具栏）
            _svc.Config.ToolbarSplitAfterId = null;
            _svc.Config.Save();
            _svc.UiOrchestrator.RefreshToolbarLayout();
            _svc.UiOrchestrator.RefreshPageIslands();
            RefreshCurrentPage();
            Toast.Show("已恢复默认布局。");
        }));
        // v0.9.7：当前处于分割态时提供「合并副岛回主栏」快捷入口
        //（用户找不到副岛时的自助恢复路径，不再只能靠拖回）。
        if (_svc.Config.ToolbarSplitAfterId is not null)
        {
            resetRow.Children.Add(new Border { Width = 10 });
            resetRow.Children.Add(GhostButton("合并副岛回主栏", "IconCheck", () =>
            {
                _svc.Config.ToolbarSplitAfterId = null;
                _svc.Config.Save();
                _svc.UiOrchestrator.RefreshToolbarLayout();
                RefreshCurrentPage();
                Toast.Show("副岛已并回主工具栏。");
            }));
        }
        sp.Children.Add(resetRow);

        return sp;
    }

    // ══════════════════ 画笔 ══════════════════

    private FrameworkElement BuildBrushPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("画笔"));
        sp.Children.Add(PageSubtitle("改了立即生效，也同步到当前笔。"));

        var cfg = _svc.Config;
        var penColors = new WrapPanel();
        var penPresets = new (string Name, string Hex)[]
        {
            ("朱红", "#E8442E"), ("明黄", "#F5D000"), ("天蓝", "#3D9BE8"),
            ("青绿", "#34B37A"), ("白", "#F2F3F5"), ("紫", "#A78BFA"),
            ("橙", "#F08A24"), ("墨黑", "#232323"),
        };
        foreach (var (name, hex) in penPresets)
        {
            var colorHex = hex;
            penColors.Children.Add(BrushColorSwatch(name, colorHex,
                cfg.DefaultColor.Equals(colorHex, StringComparison.OrdinalIgnoreCase),
                () =>
                {
                    cfg.DefaultColor = colorHex;
                    _svc.InkEngine.Pen = _svc.InkEngine.Pen with { ColorHex = colorHex };
                    _svc.UiOrchestrator.Toolbar?.SyncPaletteToCurrentPen();
                    cfg.Save();
                    Toast.Show($"默认画笔颜色：{name}");
                    RefreshCurrentPage();
                }));
        }
        sp.Children.Add(Card("默认画笔颜色", penColors));
        sp.Children.Add(Card("自定义画笔颜色", CustomHexColorRow(cfg.DefaultColor, "画笔", hex =>
        {
            cfg.DefaultColor = hex;
            _svc.InkEngine.Pen = _svc.InkEngine.Pen with { ColorHex = hex };
            _svc.UiOrchestrator.Toolbar?.SyncPaletteToCurrentPen();
            cfg.Save();
            Toast.Show($"默认画笔颜色已更新为 {hex}");
        })));

        var thickness = new Slider
        {
            Minimum = 0.6, Maximum = 6.0, Width = 216,
            IsSnapToTickEnabled = true,
            SmallChange = 0.1, LargeChange = 0.5,
            Value = Math.Clamp(cfg.DefaultThicknessMm, 0.6, 6.0),
            TickFrequency = 0.1, ToolTip = "0.6–6.0 mm",
        };
        ApplyTouchSliderStyle(thickness);
        var thicknessValue = new TextBlock
        {
            Text = $"{cfg.DefaultThicknessMm:F1} mm", Width = 62,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0),
        };
        thicknessValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        thickness.ValueChanged += (_, _) =>
        {
            cfg.DefaultThicknessMm = Math.Round(thickness.Value * 10) / 10.0;
            thicknessValue.Text = $"{cfg.DefaultThicknessMm:F1} mm";
            _svc.InkEngine.Pen = _svc.InkEngine.Pen with { ThicknessMm = cfg.DefaultThicknessMm };
            _svc.UiOrchestrator.Toolbar?.SyncPaletteToCurrentPen();
            QueueConfigSave();
        };
        var thicknessControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        thicknessControl.Children.Add(thicknessValue);
        thicknessControl.Children.Add(WrapSliderWithSteppers(thickness, 0.2));
        sp.Children.Add(Card("默认笔触", SettingsRow("默认画笔粗细", "按毫米存，换屏幕粗细不变。", thicknessControl)));

        var highlighterColors = new WrapPanel();
        var highlightPresets = new (string Name, string Hex)[]
        {
            ("荧光黄", "#F5D000"), ("薄荷绿", "#8ED081"), ("冰蓝", "#6DC8F2"),
            ("柔粉", "#FF8FB3"), ("杏橙", "#F6A96B"),
        };
        foreach (var (name, hex) in highlightPresets)
        {
            var colorHex = hex;
            highlighterColors.Children.Add(BrushColorSwatch(name, colorHex,
                cfg.HighlightColor.Equals(colorHex, StringComparison.OrdinalIgnoreCase),
                () =>
                {
                    cfg.HighlightColor = colorHex;
                    cfg.Save();
                    Toast.Show($"荧光笔颜色：{name}");
                    RefreshCurrentPage();
                }));
        }
        sp.Children.Add(Card("荧光笔颜色", highlighterColors));
        sp.Children.Add(Card("自定义荧光笔颜色", CustomHexColorRow(cfg.HighlightColor, "荧光笔", hex =>
        {
            cfg.HighlightColor = hex;
            cfg.Save();
            Toast.Show($"荧光笔颜色已更新为 {hex}");
        })));

        var note = new TextBlock
        {
            Text = "说明：这里修改的是默认画笔参数。工具栏调色面板仍可对当前笔进行临时颜色和粗细调整；AI 识别等待时间在「行为与触控」中设置。",
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 16, 18),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        sp.Children.Add(note);

        var brushActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 24, 18) };
        brushActions.Children.Add(GhostButton("恢复默认画笔", "IconReset", () =>
        {
            cfg.DefaultColor = "#E8442E";
            cfg.DefaultThicknessMm = 2.2;
            cfg.HighlightColor = "#F5D000";
            _svc.InkEngine.Pen = _svc.InkEngine.Pen with { ColorHex = cfg.DefaultColor, ThicknessMm = cfg.DefaultThicknessMm };
            _svc.UiOrchestrator.Toolbar?.SyncPaletteToCurrentPen();
            cfg.Save();
            Toast.Show("已恢复默认颜色与粗细。");
            RefreshCurrentPage();
        }));
        sp.Children.Add(brushActions);
        return sp;
    }

    private FrameworkElement CustomHexColorRow(string initial, string label, Action<string> apply)
    {
        var input = new TextBox
        {
            Text = initial, Width = 132, Height = 44, Padding = new Thickness(10, 0, 10, 0),
            FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 7, ToolTip = "格式：#RRGGBB",
            BorderThickness = new Thickness(1),
        };
        input.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        input.SetResourceReference(Control.BorderBrushProperty, "PanelBorderBrush");
        var sample = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        try { sample.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(initial)); }
        catch { sample.Background = Brushes.Transparent; }
        input.TextChanged += (_, _) =>
        {
            var candidate = input.Text.Trim();
            if (candidate.Length == 7 && candidate[0] == '#' && candidate.Skip(1).All(Uri.IsHexDigit))
                sample.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(candidate));
        };
        var action = ActionButton("应用颜色", "IconCheck", () =>
        {
            var candidate = input.Text.Trim();
            if (candidate.Length != 7 || candidate[0] != '#' || !candidate.Skip(1).All(Uri.IsHexDigit))
            {
                Toast.Show($"{label}颜色格式应为 #RRGGBB");
                return;
            }
            apply(candidate.ToUpperInvariant());
            RefreshCurrentPage();
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(input);
        row.Children.Add(sample);
        row.Children.Add(action);
        return row;
    }

    private Border BrushColorSwatch(string name, string hex, bool selected, Action pick)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var preview = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5),
        };
        var label = new TextBlock
        {
            Text = name, FontSize = 10.5, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(preview);
        content.Children.Add(label);
        var swatch = new Border
        {
            Width = 68, Height = 66, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2),
            Child = content, Cursor = Cursors.Hand, ToolTip = $"{name} {hex}",
        };
        if (selected)
        {
            swatch.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            swatch.SetResourceReference(Border.BackgroundProperty, "ItemSelectedBrush");
        }
        else
        {
            swatch.BorderBrush = Brushes.Transparent;
            swatch.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
        }
        ToolbarFactory.PressWire(swatch, () => { AnimationKit.PressPulse(swatch); pick(); });
        return swatch;
    }

    private static Border SettingsRow(string title, string description, FrameworkElement control)
    {
        var titleText = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var descriptionText = new TextBlock
        {
            Text = description, FontSize = 11, Margin = new Thickness(0, 2, 10, 0),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
        };
        descriptionText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(titleText);
        labels.Children.Add(descriptionText);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var row = new Border { Child = grid, Padding = new Thickness(10, 8, 6, 8), CornerRadius = new CornerRadius(8) };
        row.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
        return row;
    }

    /// <summary>v0.9.7：给 Slider 应用触摸友好样式（TouchSliderStyle：32px Thumb / 6px 轨道 /
    /// 44px 高 / 点轨即跳）。v0.9.8 追加触摸直驱（TouchSliderKit），设置页滑块统一入口。</summary>
    private void ApplyTouchSliderStyle(Slider slider)
    {
        slider.Style = (Style)FindResource("TouchSliderStyle");
        TouchSliderKit.Attach(slider);   // 按下即定位、移动即跟随、松手即停（绕开内建 Thumb 状态机）
    }

    /// <summary>v0.9.7：滑块 +/- 微调按钮（大屏远距/手指操作：点滑块难以精确步进，
    /// 按钮是可靠备选；按住按钮可连续步进）。返回包装好的行容器（含原滑块）。</summary>
    private FrameworkElement WrapSliderWithSteppers(Slider slider, double step)
    {
        Button StepBtn(string label, double delta)
        {
            var tb = new TextBlock
            {
                Text = label, FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var b = new Button
            {
                Content = tb, Width = 40, Height = 40, Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand, Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(1),
                Template = PlainButtonTemplate(10),
                ToolTip = delta > 0 ? "增大一档" : "减小一档",
            };
            b.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
            // 连按：短按单步，长按自动重复（WPF Button 原生 IsCancel=false；用 PressWire 保持轻量）
            ToolbarFactory.PressWire(b, () =>
            {
                AnimationKit.PressPulse(b);
                slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            });
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(StepBtn("−", -step));
        row.Children.Add(slider);
        row.Children.Add(StepBtn("+", step));
        return row;
    }

    // ══════════════════ 插件 ══════════════════

    private FrameworkElement BuildPluginsPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("插件"));
        sp.Children.Add(PageSubtitle("这里只管开关。放映时点工具栏【插件】按钮打开面板。脚本插件跑在 Lua 沙箱里，权限要确认后才给。"));

        var list = new StackPanel();
        var plugins = _svc.Plugins.Loaded.OrderBy(p => p.Manifest.Name).ToArray();

        if (plugins.Length == 0)
        {
            var empty = new TextBlock { Text = "暂无插件", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 4) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            list.Children.Add(empty);
        }

        foreach (var plugin in plugins)
        {
            var m = plugin.Manifest;

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var nameT = new TextBlock { Text = m.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
            nameT.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            nameRow.Children.Add(nameT);
            nameRow.Children.Add(new Border { Width = 8 });
            nameRow.Children.Add(Badge(m.Kind switch
            {
                PluginKind.Panel => "面板",
                PluginKind.Draw => "绘制",
                _ => "自由",
            }));
            var verT = new TextBlock { Text = $"v{m.Version}", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            verT.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
            nameRow.Children.Add(verT);

            var descT = new TextBlock
            {
                Text = (string.IsNullOrEmpty(m.Description) ? "内置插件 · 走统一宿主 API" : m.Description) + " · 组件从工具栏快捷面板打开",
                FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            descT.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(nameRow);
            textCol.Children.Add(descT);

            if (m.Permissions.Length > 0)
            {
                var permRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
                foreach (var perm in m.Permissions)
                    permRow.Children.Add(Badge(perm));
                textCol.Children.Add(permRow);
            }

            var sw = new Switch(_svc.Plugins.IsEnabled(plugin));
            sw.Toggled += on =>
            {
                if (on) _svc.Plugins.EnablePlugin(plugin);
                else _svc.Plugins.DisablePlugin(plugin);
            };

            var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(textCol);
            Grid.SetColumn(sw, 1);
            row.Children.Add(sw);
            list.Children.Add(row);
        }
        sp.Children.Add(Card("已安装", list));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 24, 24) };
        actions.Children.Add(ActionButton("安装插件包", "IconInstall", InstallPluginZip));
        actions.Children.Add(new Border { Width = 10 });
        actions.Children.Add(GhostButton("打开插件目录", "IconFolder", OpenPluginFolder));
        sp.Children.Add(actions);

        return sp;
    }

    private void InstallPluginZip()
    {
        var dlg = new OpenFileDialog { Filter = "插件包 (*.zip)|*.zip", Title = "选择插件包（manifest.json + Lua 脚本）" };
        if (dlg.ShowDialog(this) != true) return;

        if (_svc.Plugins.InstallFromZip(dlg.FileName, out var error))
        {
            Toast.Show("插件已装好，在列表里启用。");
            RefreshCurrentPage();
        }
        else
        {
            MessageBox.Show(this, $"安装失败：{error}", "插件安装", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPluginFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_svc.Plugins.PluginRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_svc.Plugins.PluginRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ══════════════════ 行为 ══════════════════

    private async Task RunAiHealthCheckAsync(Button button)
    {
        if (button.IsEnabled == false) return;
        button.IsEnabled = false;
        // v1.0.0：按钮文案实时反映环境配置进度（缺运行时可能下载数分钟，不能让用户以为卡死）
        var label = (button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
        void SetText(string t) { if (label is not null) label.Text = t; }
        SetText("AI 自检中…");
        try
        {
            var progress = new Progress<string>(msg => SetText("AI 自检中… " + msg));
            var result = await _svc.PythonBridge.CheckHealthAsync(default, progress);
            MessageBox.Show(this, result, "AI 自检结果",
                MessageBoxButton.OK,
                result.StartsWith("AI 功能检测通过", StringComparison.Ordinal) ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "AI 检测过程异常：" + ex.Message, "AI 自检结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.IsEnabled = true;
            SetText("快速检测 AI 功能");
        }
    }

    private FrameworkElement BuildBehaviorPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("行为与触控"));
        sp.Children.Add(PageSubtitle("输入、擦除、识别、系统集成的即时开关。"));

        var cfg = _svc.Config;

        var responseList = new StackPanel();
        responseList.Children.Add(SwitchRow("减少界面动效",
            "关掉页面淡入、弹层缩放和吸附动画，点了立刻到位。书写不受影响。",
            cfg.ReduceMotion,
            v => { cfg.ReduceMotion = v; cfg.Save(); Toast.Show(v ? "已减少界面动效" : "已恢复界面动效"); }));
        responseList.Children.Add(RowDivider());
        responseList.Children.Add(SwitchRow("轻量渲染模式",
            "关掉落笔墨圈、消散粒子和清屏扫光，旧机器或 4K 大屏更流畅。笔迹和擦除结果不变。",
            cfg.LightweightRendering,
            v =>
            {
                cfg.LightweightRendering = v;
                cfg.Save();
                _svc.UiOrchestrator.Overlay?.Invalidate();
                Toast.Show(v ? "轻量渲染已启用" : "完整视觉效果已恢复");
            }));
        sp.Children.Add(Card("交互响应", responseList));

        var navList = new StackPanel();
        navList.Children.Add(SwitchRow("翻页笔转发",
            "刚点过工具栏时，翻页笔的上下键、左右键直接翻 PPT。",
            cfg.PresenterForwardEnabled,
            v => { cfg.PresenterForwardEnabled = v; cfg.Save(); _svc.UiOrchestrator.RefreshPresenterForward(); }));
        navList.Children.Add(RowDivider());
        var eraseModeChoice = Segmented(
            new[] { ("涂抹", "stroke"), ("圈画", "lasso"), ("滑动清空", "clear") },
            cfg.DefaultEraseMode,
            mode => { cfg.DefaultEraseMode = mode; cfg.Save(); });
        navList.Children.Add(SettingsRow("默认擦除模式", "点擦除时先进入哪个模式，浮岛里随时能换。", eraseModeChoice));
        sp.Children.Add(Card("翻页与擦除", navList));

        // —— 书写与识别（第四轮）——
        var writeList = new StackPanel();

        var clearSeg = Segmented(
            new[] { ("智能", "smart"), ("书写笔", "pen"), ("触摸", "touch"), ("保持", "keep") },
            cfg.AfterClearTool,
            v => { cfg.AfterClearTool = v; cfg.Save(); });
        writeList.Children.Add(SettingsRow("清空整页后切到", "清空后自动切回哪个工具。智能=上次用的那个。", clearSeg));
        writeList.Children.Add(RowDivider());

        // 识别灵敏度：滑杆 0.75~1.3（严格 ← 标准 → 宽松）。
        // 识别灵敏度：使用网格行约束描述宽度，滑杆保持足够的触摸高度。
        var sensRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var sensValue = new TextBlock
        {
            Text = SensitivityLabel(cfg.RecognitionSensitivity), FontSize = 12,
            FontWeight = FontWeights.SemiBold, MinWidth = 56, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        sensValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var slider = new Slider
        {
            Minimum = 0.75, Maximum = 1.3, Width = 176,
            IsSnapToTickEnabled = true,
            SmallChange = 0.05, LargeChange = 0.05, TickFrequency = 0.05,
            Value = cfg.RecognitionSensitivity, ToolTip = "左：严格 · 右：宽松",
        };
        ApplyTouchSliderStyle(slider);
        slider.ValueChanged += (_, _) =>
        {
            cfg.RecognitionSensitivity = Math.Round(slider.Value * 20) / 20.0;
            QueueConfigSave();
            sensValue.Text = SensitivityLabel(cfg.RecognitionSensitivity);
        };
        sensRight.Children.Add(sensValue);
        sensRight.Children.Add(WrapSliderWithSteppers(slider, 0.05));
        writeList.Children.Add(SettingsRow("识别灵敏度", "左严格右宽松。拿不准就用中间。", sensRight));
        // 工具栏缩放：行布局与其他滑杆保持一致，不再依赖标题文本的自然宽度。
        var scaleValue = new TextBlock
        {
            Text = $"{(int)Math.Round(cfg.ToolbarScale * 100)}%", FontSize = 12,
            FontWeight = FontWeights.SemiBold, MinWidth = 54, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        scaleValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var scaleSlider = new Slider
        {
            Minimum = 0.7, Maximum = 1.6, Width = 176,
            IsSnapToTickEnabled = true,
            SmallChange = 0.05, LargeChange = 0.05, TickFrequency = 0.05,
            Value = cfg.ToolbarScale, ToolTip = "左：小 · 右：大",
        };
        ApplyTouchSliderStyle(scaleSlider);
        scaleSlider.ValueChanged += (_, _) =>
        {
            cfg.ToolbarScale = Math.Round(scaleSlider.Value * 20) / 20.0;
            QueueConfigSave();
            scaleValue.Text = $"{(int)Math.Round(cfg.ToolbarScale * 100)}%";
            _svc.UiOrchestrator.RefreshToolbarScale();
        };
        var scaleControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        scaleControl.Children.Add(scaleValue);
        scaleControl.Children.Add(WrapSliderWithSteppers(scaleSlider, 0.05));
        writeList.Children.Add(SettingsRow("工具栏大小", "站得远就调大，按钮更好按。", scaleControl));

        // 擦除模式。
        var erSeg = Segmented(new[] { ("像素级", "pixel"), ("整笔", "stroke") }, cfg.EraseMode,
            v => { cfg.EraseMode = v; cfg.Save(); });
        writeList.Children.Add(SettingsRow("擦除模式", "像素=擦到哪断哪；整笔=碰到就整条删。", erSeg));
        writeList.Children.Add(RowDivider());

        var esValue = new TextBlock
        {
            Text = $"{(int)Math.Round(cfg.EraserRadiusMm)} mm", FontSize = 12,
            FontWeight = FontWeights.SemiBold, MinWidth = 56, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        esValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var esSlider = new Slider
        {
            Minimum = 6, Maximum = 60, Width = 176,
            IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 4, TickFrequency = 1,
            Value = Math.Clamp(cfg.EraserRadiusMm, 6, 60), ToolTip = "左：细 · 右：粗",
        };
        ApplyTouchSliderStyle(esSlider);
        esSlider.ValueChanged += (_, _) =>
        {
            cfg.EraserRadiusMm = Math.Round(esSlider.Value);
            QueueConfigSave();
            _svc.InkEngine.EraserRadiusMm = cfg.EraserRadiusMm;
            esValue.Text = $"{(int)cfg.EraserRadiusMm} mm";
        };
        var esControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        esControl.Children.Add(esValue);
        esControl.Children.Add(WrapSliderWithSteppers(esSlider, 2));
        writeList.Children.Add(SettingsRow("橡皮大小", "调大擦得更快，适合清大块。", esControl));
        writeList.Children.Add(RowDivider());

        sp.Children.Add(Card("书写与识别", writeList));

        // —— 输入模式（v0.8.2 三档：「笔写不出来」环境的按档排查路径）——
        // 设计立场：程序无法区分「用户没摸屏」与「触摸未收到」，不做自动兜底 ——
        // 状态牌亮出事实 + 用户手动选档（档位说明随选择切换）。
        var inputList = new StackPanel();
        var imDesc = new TextBlock
        {
            Text = "墨迹层书写链路三档，切换实时生效（墨迹层自动重建，板书与设置完全保留）。书写不出来时按档排查",
            FontSize = 11, Margin = new Thickness(0, 2, 0, 10), TextWrapping = TextWrapping.Wrap,
        };
        imDesc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        inputList.Children.Add(imDesc);

        var imSeg = Segmented(
            new[] { ("触摸优先", "touch"), ("鼠标兼容", "compat"), ("超级兼容·白板", "super") },
            cfg.InputMode,
            v => { cfg.InputMode = v; cfg.Save(); _svc.UiOrchestrator.RefreshInputMode(); });
        imSeg.HorizontalAlignment = HorizontalAlignment.Left;
        inputList.Children.Add(imSeg);

        var modeNote = new TextBlock
        {
            Text = InputModeNote(cfg.InputMode),
            FontSize = 11, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap,
        };
        modeNote.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        inputList.Children.Add(modeNote);

        inputList.Children.Add(RowDivider());
        inputList.Children.Add(SwitchRow("触摸操作时隐藏板书",
            "切到【触摸】时暂时藏起板书，笔迹压住的 PPT 按钮也能点到；切回【笔】立刻回来。关掉则板书一直显示，但被笔迹盖住的地方点不动。",
            cfg.TouchThroughInkHidden,
            v => { cfg.TouchThroughInkHidden = v; cfg.Save(); _svc.UiOrchestrator.RefreshTouchThroughInkMode(); }));
        inputList.Children.Add(RowDivider());
        inputList.Children.Add(SwitchRow("调试模式下显示墨迹层状态牌",
            "开了调试模式才显示。平时不显示。",
            cfg.InkStatusBadgeEnabled,
            v => { cfg.InkStatusBadgeEnabled = v; cfg.Save(); _svc.UiOrchestrator.RefreshInkStatusBadge(); }));
        sp.Children.Add(Card("输入模式（书写链路）", inputList));

        var touchList = new StackPanel();
        touchList.Children.Add(SwitchRow("手掌误触抑制", "手掌压上去不落墨。阈值自适应，拿不准就放行。", cfg.PalmRejectionEnabled,
            v => { cfg.PalmRejectionEnabled = v; _svc.PalmRejection.Enabled = v; cfg.Save(); }));
        touchList.Children.Add(RowDivider());
        touchList.Children.Add(SwitchRow("严格手掌抑制", "判定更严：面积超日常书写 2.5 倍就算手掌。", cfg.OnlyFingerInk,
            v => { cfg.OnlyFingerInk = v; _svc.PalmRejection.OnlyFingerMode = v; cfg.Save(); }));
        touchList.Children.Add(RowDivider());
        touchList.Children.Add(SwitchRow("避让系统手势区", "工具栏和起笔都避开屏幕边缘的手势区。", cfg.RespectGestureZone,
            v => { cfg.RespectGestureZone = v; _svc.PalmRejection.RespectGestureZone = v; cfg.Save(); }));
        sp.Children.Add(Card("触控与误触", touchList));

        // 笔记本 / 普通触控屏试用：手掌抑制阈值按希沃红外框调校，电容屏/鼠标会误杀；
        // 档案切笔记本屏（毫米→像素粗细换算按 15.6 寸校准）。鼠标书写兜底始终可用。
        var laptopList = new StackPanel();
        laptopList.Children.Add(SwitchRow("笔记本 / 普通屏试用模式", "关掉手掌抑制，粗细按 15.6 寸屏算。鼠标一直能用。换回大屏记得关掉并重选机型。",
            cfg.LaptopTestMode,
            v =>
            {
                cfg.LaptopTestMode = v;
                if (v)
                {
                    if (_svc.DeviceProfiles.All.FirstOrDefault(p => p.Id == "laptop-1080p") is { } lap)
                    {
                        cfg.DeviceProfileId = lap.Id;
                        _svc.InkEngine.ApplyProfile(lap.Id);
                        _svc.PalmRejection.ApplyProfile(lap);
                        _svc.InkDocument.RenderProfile = lap;
                        _svc.TouchDigitizer.ApplyTo(lap, "laptop-test-mode");
                    }
                    _svc.PalmRejection.Enabled = false;
                    Toast.Show("试用模式已开：鼠标、触摸、笔都能写。");
                }
                else
                {
                    _svc.PalmRejection.Enabled = cfg.PalmRejectionEnabled;
                    Toast.Show("已恢复手掌抑制。机型请手动选。");
                }
                cfg.Save();
            }));
        sp.Children.Add(Card("笔记本试用", laptopList));

        var integList = new StackPanel();
        integList.Children.Add(SwitchRow("AI 形状规整（本地推理）", "停笔后把手绘图形变工整。与工具栏【AI】按钮同一个开关。", cfg.AiEnabled,
            v => _svc.UiOrchestrator.SetAiPower(v, notify: false)));
        integList.Children.Add(RowDivider());
        Button? aiHealthButton = null;
        aiHealthButton = ActionButton("快速检测 AI 功能", "IconCheck", () => _ = RunAiHealthCheckAsync(aiHealthButton!));
        integList.Children.Add(aiHealthButton);
        var aiHealthNote = new TextBlock
        {
            Text = "临时启动一次识别进程，验证依赖是否齐全。不改 AI 开关，也不留后台进程。",
            FontSize = 11, Margin = new Thickness(4, 6, 12, 8), TextWrapping = TextWrapping.Wrap,
        };
        aiHealthNote.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        integList.Children.Add(aiHealthNote);

        var modeTitle = new TextBlock { Text = "规整方式", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 10, 0, 2) };
        modeTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        integList.Children.Add(modeTitle);
        var modeDesc = new TextBlock
        {
            Text = "自动校正：停笔约 0.7 秒直接替换。气泡确认：先弹气泡，点了才换。",
            FontSize = 11, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap,
        };
        modeDesc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        integList.Children.Add(modeDesc);
        integList.Children.Add(SegmentedWrap(
            new[] { ("自动校正", "auto"), ("气泡确认", "bubble") },
            cfg.ShapeAssistMode,
            v => { cfg.ShapeAssistMode = v; cfg.Save(); }));

        var delaySlider = new Slider
        {
            Minimum = 150, Maximum = 5000, Width = 216,
            IsSnapToTickEnabled = true,
            SmallChange = 50, LargeChange = 250, TickFrequency = 50,
            Value = Math.Clamp(cfg.ShapeRecognizeDelayMs, 150, 5000),
            ToolTip = "抬笔后等多久变工整（150–5000 ms）",
        };
        ApplyTouchSliderStyle(delaySlider);
        var delayValue = new TextBlock
        {
            Text = $"{cfg.ShapeRecognizeDelayMs / 1000.0:F2} 秒", Width = 72,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        };
        delayValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        delaySlider.ValueChanged += (_, _) =>
        {
            cfg.ShapeRecognizeDelayMs = Math.Clamp((int)Math.Round(delaySlider.Value / 50.0) * 50, 150, 5000);
            delayValue.Text = $"{cfg.ShapeRecognizeDelayMs / 1000.0:F2} 秒";
            QueueConfigSave();
        };
        var delayControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        delayControl.Children.Add(delayValue);
        delayControl.Children.Add(delaySlider);
        integList.Children.Add(SettingsRow("识别等待时间", "抬笔后等多久变工整。短=反应快，长=不打断连笔。", delayControl));

        // ———— v1.0.0：AI 识别时机 / 范围 / 能力参数 ————

        integList.Children.Add(RowDivider());
        integList.Children.Add(SettingsRow("识别时机",
            "停顿后：抬笔等一会儿再认，最防误识。抬笔即识：最快，适合快速作图。仅手动：只点【AI】按钮才认。",
            SegmentedWrap(
                new[] { ("停顿后", "pause"), ("抬笔即识", "lift"), ("仅手动", "manual") },
                cfg.AiTriggerTiming,
                v => { cfg.AiTriggerTiming = v; cfg.Save(); })));

        integList.Children.Add(RowDivider());
        integList.Children.Add(SettingsRow("识别范围",
            "最后一笔：只认刚写那笔。最近几笔：把最近两笔合起来认（多笔组合图形）。",
            SegmentedWrap(
                new[] { ("最后一笔", "last"), ("最近几笔", "recent") },
                cfg.AiScope,
                v => { cfg.AiScope = v; cfg.Save(); })));

        integList.Children.Add(RowDivider());
        var confSlider = new Slider
        {
            Minimum = 0.30, Maximum = 0.98, Width = 180,
            IsSnapToTickEnabled = true, SmallChange = 0.05, LargeChange = 0.05, TickFrequency = 0.05,
            Value = Math.Clamp(cfg.AiConfidenceThreshold, 0.30, 0.98),
            ToolTip = "越高越保守（不易误识）",
        };
        ApplyTouchSliderStyle(confSlider);
        var confValue = new TextBlock
        {
            Text = cfg.AiConfidenceThreshold.ToString("P0"), Width = 56,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        };
        confValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        confSlider.ValueChanged += (_, _) =>
        {
            cfg.AiConfidenceThreshold = Math.Round(confSlider.Value / 0.05) * 0.05;
            confValue.Text = cfg.AiConfidenceThreshold.ToString("P0");
            QueueConfigSave();
        };
        var confControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        confControl.Children.Add(confValue);
        confControl.Children.Add(WrapSliderWithSteppers(confSlider, 0.05));
        integList.Children.Add(SettingsRow("置信度阈值",
            "识别结果低于这个把握就不规整。调高更保守，调低更积极。", confControl));

        integList.Children.Add(RowDivider());
        var minSizeSlider = new Slider
        {
            Minimum = 0, Maximum = 40, Width = 180,
            IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5, TickFrequency = 1,
            Value = Math.Clamp(cfg.AiMinSizeMm, 0, 40),
            ToolTip = "笔迹太小时不识别（毫米）",
        };
        ApplyTouchSliderStyle(minSizeSlider);
        var minSizeValue = new TextBlock
        {
            Text = $"{(int)cfg.AiMinSizeMm} mm", Width = 56,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        };
        minSizeValue.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        minSizeSlider.ValueChanged += (_, _) =>
        {
            cfg.AiMinSizeMm = Math.Round(minSizeSlider.Value);
            minSizeValue.Text = $"{(int)cfg.AiMinSizeMm} mm";
            QueueConfigSave();
        };
        var minSizeControl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        minSizeControl.Children.Add(minSizeValue);
        minSizeControl.Children.Add(WrapSliderWithSteppers(minSizeSlider, 2));
        integList.Children.Add(SettingsRow("最小识别尺寸",
            "笔迹任一边比这个长度短就不认（避开小勾、小点被误规整）。", minSizeControl));

        integList.Children.Add(RowDivider());
        var wlBox = new TextBox
        {
            Text = cfg.AiShapeWhitelist, Width = 260, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "分号分隔的形状名；留空 = 全部形状",
        };
        wlBox.SetResourceReference(Control.BackgroundProperty, "ItemHoverBrush");
        wlBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        void CommitWhitelist()
        {
            cfg.AiShapeWhitelist = wlBox.Text.Trim();
            QueueConfigSave();
        }
        wlBox.LostFocus += (_, _) => CommitWhitelist();
        wlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitWhitelist(); };
        integList.Children.Add(SettingsRow("形状白名单",
            "只认这里列出的形状，其余不规整。分号分隔：circle;line;arrow。留空 = 全部形状都认。", wlBox));

        integList.Children.Add(RowDivider());
        integList.Children.Add(SwitchRow("识别成功后提示", "规整成功时弹一句确认。关掉则静默替换。", cfg.AiHighlightOnSuccess,
            v => { cfg.AiHighlightOnSuccess = v; cfg.Save(); }));

        integList.Children.Add(RowDivider());
        integList.Children.Add(SwitchRow("新手引导", "每次进放映提示一次用法，走完自动关。", cfg.EnableOnboarding,
            v => { cfg.EnableOnboarding = v; cfg.Save(); }));
        integList.Children.Add(RowDivider());
        integList.Children.Add(SwitchRow("开机自启", "开机后静默启动（仅当前用户）。", cfg.AutoStart,
            v => { cfg.AutoStart = v; cfg.Save(); if (v) _svc.AutoStart.EnsureRegistered(silent: true); else _svc.AutoStart.Unregister(); }));
        integList.Children.Add(RowDivider());
        integList.Children.Add(SwitchRow("显示托盘图标", "改完要重启才生效。", !cfg.HideTrayIcon,
            v => { cfg.HideTrayIcon = !v; cfg.Save(); }));
        integList.Children.Add(RowDivider());
        // v0.4.0 调试模式：策略各层数据可视化（面向售后定位与调参，默认关闭）
        integList.Children.Add(SwitchRow("调试模式（诊断面板）", "角落常驻诊断信息：输入通道、压感、报点率、FPS。触摸时画出接触矩形（绿=接收，红=判为手掌）。", cfg.DebugMode,
            v => _svc.UiOrchestrator.SetDebugMode(v)));
        sp.Children.Add(Card("放映与集成", integList));

        var deviceCol = new StackPanel();
        var dt = new TextBlock { Text = "影响笔迹平滑参数与粗细换算（毫米→像素）", FontSize = 11, Margin = new Thickness(0, 2, 0, 10) };
        dt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        deviceCol.Children.Add(dt);
        // v0.4.1：DeviceProfileId 可为 null（自动选择）—— 高亮以引擎当前生效档案为准，
        // 避免 null 时全部选项无高亮、用户不知当前用哪个档案
        const string autoProfileId = "__auto_detect__";
        string currentProfileId = cfg.DeviceProfileId is null ? autoProfileId : _svc.InkEngine.Profile.Id;
        var profileChoices = new[] { ("自动识别（推荐）", autoProfileId) }
            .Concat(_svc.DeviceProfiles.All.Select(p => (p.ModelName, p.Id))).ToArray();
        deviceCol.Children.Add(SegmentedWrap(
            profileChoices,
            currentProfileId,
            id =>
            {
                DeviceProfile? profile;
                if (id == autoProfileId)
                {
                    cfg.DeviceProfileId = null;
                    profile = _svc.DeviceProfiles.ResolveBest(null, _svc.TouchDigitizer);
                    Toast.Show($"已开启自动识别：当前使用 {profile.ModelName}");
                }
                else
                {
                    profile = _svc.DeviceProfiles.All.FirstOrDefault(p => p.Id == id);
                    if (profile is null) return;
                    cfg.DeviceProfileId = id;
                }

                _svc.TouchDigitizer.ApplyTo(profile, "settings");
                _svc.InkEngine.ApplyProfile(profile.Id);
                _svc.PalmRejection.ApplyProfile(profile);
                _svc.InkDocument.RenderProfile = profile;   // 活体、缓存和导出统一使用同一个档案对象
                cfg.Save();
                if (id != autoProfileId)
                {
                    if (_svc.TouchDigitizer.IsLargeTouchPanel && profile.CalibratedPxPerMm is not null)
                        Toast.Show($"机型已切换：{profile.ModelName}（实测坐标校准有效）");
                    else
                        Toast.Show($"机型已切换：{profile.ModelName}（使用保守档案参数）");
                }
            }));
        sp.Children.Add(Card("大屏机型档案", deviceCol));

        var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
        dataRow.Children.Add(ActionButton("导出本次会话墨迹", "IconExport", () =>
        {
            var dir = _svc.InkEngine.ExportSessionSafe();   // 持锁快照 + 锁外渲染（并发书写安全）
            if (dir is null)
            {
                MessageBox.Show(this, "暂无板书可导出。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show(this, $"已导出到：\n{dir}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        dataRow.Children.Add(new Border { Width = 10 });
        dataRow.Children.Add(GhostButton("打开配置文件夹", "IconFolder", () =>
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk");
                System.IO.Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
        sp.Children.Add(Card("数据", dataRow));

        return sp;
    }

    // ══════════════════ 关于 ══════════════════

    /// <summary>灵敏度标签：≤0.85 严格 / 0.9~1.1 标准 / ≥1.15 宽松（中间地带按数值就近显示）。</summary>
    private static string SensitivityLabel(double k) => k switch
    {
        <= 0.85 => "严格",
        >= 1.15 => "宽松",
        _ => "标准",
    };

    /// <summary>输入模式档位说明（v0.8.2，随选中档位切换 —— 排查路径就地可读）。</summary>
    private static string InputModeNote(string mode) => mode switch
    {
        "compat" => "鼠标兼容：改走与工具栏按钮完全相同的输入链路（触摸转为鼠标书写）。工具栏可点而墨迹层写不出的环境选此项；代价：报点率与真实压感降级，手掌误触抑制不可用（触摸以鼠标形态到达，无接触面积可判）",
        "super" => "超级兼容：全屏深色画布（应急白板），普通不透明窗口整窗命中 —— 任何环境 100% 可写的最后手段。书写时遮挡幻灯片；点「触摸操作」或结束放映自动收回，PPT 重现",
        _ => "触摸优先：默认档。透明墨迹层 + 三输入通道全开（120Hz 低延迟报点 + 真实压感 + 鼠标兜底）。标准 Windows / 正常希沃环境用此档",
    };

    // ══════════════════ 高级（笔墨精调） ══════════════════

    /// <summary>高级模式页（v1.0.0）：精调笔墨参数磨合手感。改完立即生效、可一键恢复默认。
    /// 参数经 PenTuning / BuildWidths / DrawStroke 统一读取，实时书写、抬笔精修、导出共用同一组值，
    /// 不会出现"屏幕一套、导出一套"。</summary>
    private FrameworkElement BuildAdvancedPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("高级"));
        sp.Children.Add(PageSubtitle("精调笔墨手感。改完立即生效，随时可恢复默认。"));
        var cfg = _svc.Config;

        var topList = new StackPanel();
        topList.Children.Add(SwitchRow("高级模式",
            "打开后可用下面的精调参数。默认参数已适合大多数场景，不确定就别动。",
            cfg.AdvancedMode,
            v => { cfg.AdvancedMode = v; cfg.Save(); RefreshCurrentPage(); }));
        sp.Children.Add(Card("总开关", topList));

        if (!cfg.AdvancedMode)
        {
            var hint = new TextBlock
            {
                Text = "打开「高级模式」后，可精调写字手感、笔锋、荧光笔形态。",
                FontSize = 11.5, Margin = new Thickness(4, 2, 12, 10), TextWrapping = TextWrapping.Wrap,
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(hint);
            return sp;
        }

        // 一行精调：数值显示 + 触摸滑块 + −/+ 按钮
        FrameworkElement Row(string title, string desc, double value,
            double min, double max, double step, string fmt, Action<double> set)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Width = 180,
                IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step, TickFrequency = step,
                Value = Math.Clamp(value, min, max),
            };
            ApplyTouchSliderStyle(slider);
            var val = new TextBlock
            {
                Text = value.ToString(fmt), Width = 56,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
                FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
            };
            val.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            slider.ValueChanged += (_, _) =>
            {
                double v = Math.Round(slider.Value / step) * step;
                val.Text = v.ToString(fmt);
                set(v);
            };
            var ctrl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            ctrl.Children.Add(val);
            ctrl.Children.Add(WrapSliderWithSteppers(slider, step));
            return SettingsRow(title, desc, ctrl);
        }

        var penList = new StackPanel();
        penList.Children.Add(Row("平滑强度", "越大越跟手，越小越稳。", cfg.PenSmoothing, 0.20, 0.80, 0.05, "F2",
            v => { cfg.PenSmoothing = v; cfg.Save(); }));
        penList.Children.Add(RowDivider());
        penList.Children.Add(Row("笔锋幅度", "0 = 等宽无笔锋；1 = 快写明显收细。", cfg.PenTaperStrength, 0.0, 1.0, 0.05, "F2",
            v => { cfg.PenTaperStrength = v; cfg.Save(); }));
        penList.Children.Add(RowDivider());
        penList.Children.Add(Row("起笔渐入", "落笔由细到粗的过渡长度（点）。0 = 无。", cfg.PenLeadInPoints, 0, 20, 1, "F0",
            v => { cfg.PenLeadInPoints = (int)v; cfg.Save(); }));
        penList.Children.Add(RowDivider());
        penList.Children.Add(Row("收笔收锋", "提笔收细的过渡长度（点）。0 = 无。", cfg.PenLiftPoints, 0, 24, 1, "F0",
            v => { cfg.PenLiftPoints = (int)v; cfg.Save(); }));
        penList.Children.Add(RowDivider());
        penList.Children.Add(Row("宽度跟随", "越大宽度变化越快（越活），越小越平滑。", cfg.PenWidthLowpass, 0.10, 0.90, 0.05, "F2",
            v => { cfg.PenWidthLowpass = v; cfg.Save(); }));
        penList.Children.Add(RowDivider());
        penList.Children.Add(Row("压感强度", "0 = 忽略压感（恒等宽）；1 = 强调制。", cfg.PenPressureGain, 0.0, 1.0, 0.05, "F2",
            v => { cfg.PenPressureGain = v; cfg.Save(); }));
        sp.Children.Add(Card("墨迹", penList));

        var hlList = new StackPanel();
        hlList.Children.Add(Row("笔尖横竖比", "0.5 = 竖矩形（横画粗、竖画细）；1.0 = 圆头等宽。", cfg.HighlighterTipRatio, 0.30, 1.0, 0.05, "F2",
            v => { cfg.HighlighterTipRatio = v; cfg.Save(); }));
        hlList.Children.Add(RowDivider());
        hlList.Children.Add(Row("荧光笔透明度", "越低越透，越不遮文字。", cfg.HighlighterAlpha, 0.20, 0.80, 0.05, "F2",
            v => { cfg.HighlighterAlpha = v; cfg.Save(); }));
        sp.Children.Add(Card("荧光笔", hlList));

        // ———— v1.0.0Beta：高级识别矫正（多笔几何后处理） ————
        var aiList = new StackPanel();
        aiList.Children.Add(SwitchRow("高级识别矫正 (Beta)",
            "开启后进行多笔自动矫正：缝隙闭合、补笔接续、阴影对齐。实验特性，不同课堂场景收益不同。",
            cfg.AdvancedCorrectionEnabled,
            v => { cfg.AdvancedCorrectionEnabled = v; cfg.Save(); RefreshCurrentPage(); }));
        if (cfg.AdvancedCorrectionEnabled)
        {
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("缝隙闭合",
                "两条近乎相交的直线端点差几毫米没碰上时，自动延长对齐成拐角。", cfg.AdvCloseGapEnabled,
                v => { cfg.AdvCloseGapEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("补笔接续",
                "一笔画完又在后面补一小截时，自动接上成一笔。", cfg.AdvContinueStrokeEnabled,
                v => { cfg.AdvContinueStrokeEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("阴影斜杠对齐",
                "矩形内多条斜杠自动统一角度、两端吸附到轮廓。", cfg.AdvHatchAlignEnabled,
                v => { cfg.AdvHatchAlignEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("博弈回溯",
                "上下文变化时用保留的候选重新评估，而非只认第一次判断。", cfg.GameBacktrackEnabled,
                v => { cfg.GameBacktrackEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("疑惑笔迹课后询问",
                "把握不大的笔迹先保留，下课后单独弹窗问你怎么处理，据回答自优化。", cfg.DoubtReviewEnabled,
                v => { cfg.DoubtReviewEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(SwitchRow("文字 / 公式防误触",
                "像写字的密集短笔画不做图形规整，避免把汉字、公式的笔画认成箭头或直线。", cfg.TextGuardEnabled,
                v => { cfg.TextGuardEnabled = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(Row("缝隙容忍", "端点距离小于此值才自动闭合（毫米）。", cfg.AdvGapToleranceMm, 0.5, 20, 0.5, "F1",
                v => { cfg.AdvGapToleranceMm = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(Row("接续容忍", "补笔起点离已画终点多近才接上（毫米）。", cfg.AdvSnapToleranceMm, 1, 30, 1, "F0",
                v => { cfg.AdvSnapToleranceMm = v; cfg.Save(); }));
            aiList.Children.Add(RowDivider());
            aiList.Children.Add(Row("文字判据灵敏度", "越大越容易判为文字（越保守、越不误规整）。", cfg.TextGuardSensitivity, 0.5, 2.0, 0.1, "F1",
                v => { cfg.TextGuardSensitivity = v; cfg.Save(); }));
        }
        sp.Children.Add(Card("高级识别矫正 (Beta)", aiList));

        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 24, 24) };
        resetRow.Children.Add(GhostButton("恢复默认笔墨参数", "IconReset", () =>
        {
            cfg.PenSmoothing = 0.45; cfg.PenTaperStrength = 0.5; cfg.PenLeadInPoints = 8;
            cfg.PenLiftPoints = 12; cfg.PenWidthLowpass = 0.30; cfg.PenPressureGain = 0.5;
            cfg.HighlighterTipRatio = 0.5; cfg.HighlighterAlpha = 0.43;
            cfg.Save();
            RefreshCurrentPage();
            Toast.Show("已恢复默认笔墨参数。");
        }));
        sp.Children.Add(resetRow);

        return sp;
    }

    private FrameworkElement BuildAboutPage()
    {
        var sp = PageStack();
        sp.Children.Add(PageTitle("关于"));

        // —— 居中身份卡 ——
        var cardCol = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 36, 0, 0) };

        // Logo：强调色柔光圆底 + 画笔图标
        var penIcon = new Path
        {
            Data = (Geometry)FindResource("IconPen"),
            Stretch = Stretch.Uniform,
            Width = 38, Height = 38,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        penIcon.SetResourceReference(Path.FillProperty, "AccentBrush");
        var logo = new Border
        {
            Width = 84, Height = 84,
            CornerRadius = new CornerRadius(42),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = penIcon,
        };
        logo.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        cardCol.Children.Add(logo);

        var name = new TextBlock
        {
            Text = "融绘 PptInk",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        cardCol.Children.Add(name);

        var tagline = new TextBlock
        {
            Text = $"{AppVersion} · 希沃大屏 PPT 批注与演示辅助",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        tagline.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        cardCol.Children.Add(tagline);

        cardCol.Children.Add(new Border { Height = 18 });
        var divider = new Rectangle { Width = 200, Height = 1, RadiusX = 0.5, RadiusY = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
        divider.SetResourceReference(Rectangle.FillProperty, "PanelBorderBrush");
        cardCol.Children.Add(divider);
        cardCol.Children.Add(new Border { Height = 18 });

        var buildBy = new TextBlock
        {
            Text = "Build by Xianfish",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buildBy.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        cardCol.Children.Add(buildBy);

        var qq = new TextBlock
        {
            Text = "QQ: 3994184249",
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        qq.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        cardCol.Children.Add(qq);

        var card = new Border
        {
            Child = cardCol,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 22, 18, 22),
            Margin = new Thickness(0, 0, 12, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        sp.Children.Add(card);

        // —— 能力一览（低调收尾）——
        var feats = new TextBlock
        {
            Text = "放映检测 · 墨迹批注 · 擦除三模式 · 插件扩展 · 本地 AI 规整",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        feats.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
        sp.Children.Add(feats);

        return sp;
    }

    // ———————— 控件工厂（代码构建 · DynamicResource 主题热切换） ————————

    private static StackPanel PageStack() => new() { Margin = new Thickness(24, 20, 4, 24) };

    private static TextBlock PageTitle(string t)
    {
        var b = new TextBlock { Text = t, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        b.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return b;
    }

    private static TextBlock PageSubtitle(string t)
    {
        var b = new TextBlock { Text = t, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 14) };
        b.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return b;
    }

    /// <summary>分组卡片：标题 + 内容一次成型（避免外部覆盖 Child 丢失标题）。</summary>
    private static Border Card(string title, FrameworkElement content)
    {
        var t = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var sp = new StackPanel();
        sp.Children.Add(t);
        sp.Children.Add(content);

        var card = new Border { Child = sp, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 12), Margin = new Thickness(0, 0, 12, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        return card;
    }

    private static Rectangle RowDivider()
    {
        var r = new Rectangle { Height = 1, RadiusX = 0.5, RadiusY = 0.5, Margin = new Thickness(0, 6, 0, 6) };
        r.SetResourceReference(Rectangle.FillProperty, "PanelBorderBrush");
        return r;
    }

    private static Border Badge(string text)
    {
        var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
        var t = new TextBlock { Text = text, FontSize = 10 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        b.Child = t;
        return b;
    }

    /// <summary>设置行：左标题+描述，右开关。</summary>
    private static Grid SwitchRow(string header, string desc, bool initial, Action<bool> set)
    {
        var h = new TextBlock { Text = header, FontSize = 13, FontWeight = FontWeights.SemiBold };
        h.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var d = new TextBlock { Text = desc, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap };
        d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        col.Children.Add(h);
        col.Children.Add(d);

        // 整行都是触摸目标：标题/描述/开关任意处轻触均切换，避免用户必须精准点中小开关。
        // 视觉开关不单独命中，统一由行容器管理单一手势，杜绝一次触摸触发两次 Toggle。
        var sw = new Switch(initial)
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0),
            IsHitTestVisible = false,
        };
        sw.Toggled += set;

        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 6), MinHeight = 56,
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(col);
        Grid.SetColumn(sw, 1);
        row.Children.Add(sw);
        ToolbarFactory.PressWire(row, () => { AnimationKit.PressPulse(sw); sw.Toggle(); });
        return row;
    }

    /// <summary>分段选择器（单行；选中段强调）。选择后由回调自行决定是否刷新页面。</summary>
    private Border Segmented((string Label, string Tag)[] options, string current, Action<string> pick)
    {
        var host = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3) };
        host.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");

        var panel = new WrapPanel();
        host.Child = panel;

        foreach (var (label, tag) in options)
        {
            string t = tag;
            var tb = new TextBlock { Text = label, FontSize = 12 };
            var btn = new Button
            {
                Content = tb,
                Height = 44,
                MinWidth = 48,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 2, 0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Template = PlainButtonTemplate(8),
            };
            if (t == current)
            {
                btn.SetResourceReference(Button.BackgroundProperty, "ItemSelectedBrush");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                tb.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            ToolbarFactory.PressWire(btn, () => { AnimationKit.PressPulse(btn); pick(t); RefreshCurrentPage(); });
            panel.Children.Add(btn);
        }
        return host;
    }

    /// <summary>分段选择器（换行容器，机型档案等多选项）。</summary>
    private Border SegmentedWrap((string Label, string Tag)[] options, string current, Action<string> pick)
    {
        var host = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
        host.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");

        var panel = new WrapPanel();
        host.Child = panel;

        foreach (var (label, tag) in options)
        {
            string t = tag;
            var tb = new TextBlock { Text = label, FontSize = 12 };
            var btn = new Button
            {
                Content = tb,
                Height = 44,
                MinWidth = 48,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 2, 2),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Template = PlainButtonTemplate(8),
            };
            if (t == current)
            {
                btn.SetResourceReference(Button.BackgroundProperty, "ItemSelectedBrush");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                tb.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            ToolbarFactory.PressWire(btn, () => { AnimationKit.PressPulse(btn); pick(t); RefreshCurrentPage(); });
            panel.Children.Add(btn);
        }
        return host;
    }

    /// <summary>主操作按钮（强调色描边幽灵风，克制不喧宾）。</summary>
    private static Button ActionButton(string text, string iconKey, Action onClick)
    {
        var icon = new Path { Data = (Geometry)Application.Current.FindResource(iconKey), Stretch = Stretch.Uniform, Width = 14, Height = 14, IsHitTestVisible = false };
        icon.SetResourceReference(Path.FillProperty, "AccentBrush");
        var tb = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(tb);

        var b = new Button
        {
            Content = row,
            Height = 44,
            Padding = new Thickness(16, 0, 16, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Template = PlainButtonTemplate(9),
        };
        b.SetResourceReference(Border.BorderBrushProperty, "AccentSoftBrush");
        ToolbarFactory.PressWire(b, () => { AnimationKit.PressPulse(b); onClick(); });
        return b;
    }

    /// <summary>次级文字按钮。</summary>
    private static Button GhostButton(string text, string iconKey, Action onClick)
    {
        var icon = new Path { Data = (Geometry)Application.Current.FindResource(iconKey), Stretch = Stretch.Uniform, Width = 14, Height = 14, IsHitTestVisible = false };
        icon.SetResourceReference(Path.FillProperty, "TextSecondaryBrush");
        var tb = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(tb);

        var b = new Button
        {
            Content = row,
            Height = 44,
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = PlainButtonTemplate(9),
        };
        ToolbarFactory.PressWire(b, () => { AnimationKit.PressPulse(b); onClick(); });
        return b;
    }

    /// <summary>无系统 chrome 的触摸按钮模板：视觉不依赖鼠标悬停状态。</summary>
    private static ControlTemplate PlainButtonTemplate(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ———————— 自绘开关（46×28 触控目标 · 160ms 圆点滑动） ————————

    private sealed class Switch : Border
    {
        private readonly Border _track;
        private readonly Border _thumb;
        private readonly TranslateTransform _tt = new();

        public bool IsOn { get; private set; }
        public event Action<bool>? Toggled;

        public Switch(bool initial)
        {
            Width = 54; Height = 44;
            CornerRadius = new CornerRadius(22);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Focusable = false;

            _thumb = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                RenderTransform = _tt,
            };
            _track = new Border
            {
                Width = 46, Height = 28, CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _thumb,
            };
            Child = _track;

            IsOn = initial;
            ApplyState(animate: false);

            ToolbarFactory.PressWire(this, Toggle);
        }

        public void Toggle()
        {
            IsOn = !IsOn;
            ApplyState(animate: true);
            Toggled?.Invoke(IsOn);
        }

        private void ApplyState(bool animate)
        {
            double x = IsOn ? 20 : 0;   // track 46 − thumb 20 − 左/右余量
            if (animate)
            {
                var a = new DoubleAnimation(x, new Duration(TimeSpan.FromMilliseconds(160))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                _tt.BeginAnimation(TranslateTransform.XProperty, a);
            }
            else
            {
                _tt.X = x;
            }
            // 轨道色随主题：开=强调色，关=中性淡块（DynamicResource，主题热切换自动跟随）
            _track.SetResourceReference(Border.BackgroundProperty, IsOn ? "AccentBrush" : "ItemHoverBrush");
        }
    }
}
