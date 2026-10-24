using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.UI.Theme;

namespace PptInk.UI;

/// <summary>
/// 擦除模式浮岛（通用浮岛 · 锚点弹出型）：
///  - 触发：点按工具栏"擦除"→ 在按钮旁弹出，三模式即点即换：
///      涂抹擦除（滑过即擦）/ 圈画擦除（闭合整块消散）/ 滑动清空（一挥清页，可撤销）；
///  - 选中后 260ms 礼貌退场（确认动效可见即走，不赖屏）；无操作 3s 自动淡出；
///  - 再次点按"擦除"按钮随时重新呼出；当前模式在按钮上以角标徽记提示；
///  - 全部 ≥56px 触控目标；主题/材质热切换自动跟随。
/// v0.9.0 单窗口架构：IslandWindow 窗口基类 → StageElement 舞台元素（锚点弹出/
/// 自动退场/BindTheme 全由基类承担，行为等价）。
/// </summary>
public sealed class EraseModeIsland : StageElement
{
    /// <summary>mode: "stroke" | "lasso" | "clear"（对应 InkTool.EraseStroke/EraseLasso/EraseClearSwipe）。</summary>
    public event Action<string>? EraseModeSelected;

    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Border _panel;
    private readonly AppServices _svc;
    private Slider _sizeSlider = null!;
    private TextBlock _sizeValue = null!;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private string _currentMode = "stroke";
    private const double PanelW = 220;

    private static readonly (string Mode, string Name, string Tip, string Icon)[] Modes =
    {
        ("stroke", "涂抹擦除", "橡皮滑过即擦", "IconEraser"),
        ("lasso", "圈画擦除", "圈选整块消散", "IconLasso"),
        ("clear", "滑动清空", "一挥清空本页笔迹", "IconSwipe"),
    };

    public EraseModeIsland(AppServices svc)
    {
        _svc = svc;
        _panel = BuildPanel();
        Content = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            CanContentScroll = false,
            ClipToBounds = true,
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _svc.Config.Save(); };
    }

    private Border BuildPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };

        foreach (var (mode, name, tip, icon) in Modes)
        {
            var iconPath = new Path
            {
                Data = (Geometry)Application.Current.Resources[icon],   // v0.8.1：IconSwipe 已归一至 Icons.xaml，去掉原特殊分支
                Stretch = Stretch.Uniform,
                Width = 22,
                Height = 22,
                IsHitTestVisible = false,
            };
            BindTheme(() =>
            {
                if (Application.Current?.Resources["TextPrimaryBrush"] is Brush tb) iconPath.Fill = tb;
            });

            var label = new TextBlock
            {
                Text = name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            BindTheme(() =>
            {
                if (Application.Current?.Resources["TextPrimaryBrush"] is Brush tb) label.Foreground = tb;
            });

            var sub = new TextBlock
            {
                Text = tip,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            BindTheme(() =>
            {
                if (Application.Current?.Resources["TextSecondaryBrush"] is Brush tb) sub.Foreground = tb;
            });

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(label);
            textCol.Children.Add(sub);

            var row = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(iconPath, 0);
            Grid.SetColumn(textCol, 1);
            row.Children.Add(iconPath);
            row.Children.Add(textCol);

            var b = new Button
            {
                Content = row,
                Height = 56,
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = mode,
            };
            b.Template = PlainButtonTemplate(14);
            // v0.7.0 点击即有：按下瞬间切换模式（无需松手，微滑不丢点）
            ToolbarFactory.PressWire(b, () => Select(mode));
            stack.Children.Add(b);
            _buttons[mode] = b;
        }

        var sizeBlock = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
        var sizeHeader = new Grid();
        sizeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sizeTitle = new TextBlock { Text = "橡皮大小", FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        sizeTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _sizeValue = new TextBlock
        {
            Text = $"{(int)Math.Round(_svc.Config.EraserRadiusMm)} mm",
            FontSize = 12, MinWidth = 44, TextAlignment = TextAlignment.Right,
        };
        _sizeValue.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(_sizeValue, 1);
        sizeHeader.Children.Add(sizeTitle);
        sizeHeader.Children.Add(_sizeValue);
        _sizeSlider = new Slider
        {
            Minimum = 6, Maximum = 60, TickFrequency = 1, SmallChange = 1, LargeChange = 4,
            IsMoveToPointEnabled = true, IsSnapToTickEnabled = false,
            Value = Math.Clamp(_svc.Config.EraserRadiusMm, 6, 60),
            Height = 38, Margin = new Thickness(0, 2, 0, 0),
            ToolTip = "拖动调节橡皮半径（毫米）",
            Focusable = false, IsTabStop = false,
        };
        _sizeSlider.ValueChanged += (_, _) =>
        {
            double radius = Math.Round(_sizeSlider.Value);
            _svc.Config.EraserRadiusMm = radius;
            _svc.InkEngine.EraserRadiusMm = radius;
            _sizeValue.Text = $"{radius:F0} mm";
            AutoDismissIn(3.0); // 调参时重置退场计时，避免拖到一半浮岛自行收起
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        sizeBlock.Children.Add(sizeHeader);
        sizeBlock.Children.Add(_sizeSlider);
        stack.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 2, 12, 4), Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)) });
        stack.Children.Add(sizeBlock);

        var panel = new Border
        {
            Child = stack,
            // v0.8.0 嵌套公式：内按钮圆角 14 + Padding 4 = 18（与 SegmentIsland 20 同律）
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };
        BindTheme(() =>
        {
            panel.Background = ThemeManager.ContentBackground;
            if (Application.Current?.Resources["PanelBorderBrush"] is Brush bb) panel.BorderBrush = bb;
        });

        // 高亮随主题刷新（登记一次；SetCurrentMode 只改状态，不再累积闭包）
        BindTheme(() => RefreshHighlights());
        return panel;
    }

    private void RefreshHighlights()
    {
        foreach (var (m, b) in _buttons)
        {
            bool on = m == _currentMode;
            if (on && Application.Current?.Resources["ItemSelectedBrush"] is Brush sel)
                b.Background = sel;
            else if (on)
                b.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            else
                b.Background = Brushes.Transparent;
        }
    }

    /// <summary>Plain Border 模板（圆角按钮；点击态由高亮管理，不做系统 chrome）。</summary>
    private static ControlTemplate PlainButtonTemplate(double radius)
    {
        var tf = new FrameworkElementFactory(typeof(Border));
        tf.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        tf.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        tf.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        tf.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(Control.MarginProperty, new Thickness(10, 0, 10, 0));
        tf.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = tf };
    }

    // ———————— 对外 ————————

    /// <summary>当前模式高亮同步（工具栏切换 / 插件请求后调用）。</summary>
    public void SetCurrentMode(string mode)
    {
        _currentMode = mode;
        RefreshHighlights();
    }

    /// <summary>在触发按钮（工具栏"擦除"）旁弹出，含礼貌退场。</summary>
    public void ShowNear(Rect anchorRect)
    {
        SetCurrentMode(_currentMode);
        // 面板实际尺寸先量出来（内容驱动，不硬编码高度）
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (ShowNearAnchor(anchorRect, PanelW, _panel.DesiredSize.Height))
            AutoDismissIn(3.0);
        else
            Toast.Show("当前没有可用的独立工具面板区域；请先关闭其他浮动面板。");
    }

    private void Select(string mode)
    {
        CancelAutoDismiss();
        SetCurrentMode(mode);
        EraseModeSelected?.Invoke(mode);
        // 确认动效可见即走（260ms 后吮吸收回）：交由【基类】定时器管理 ——
        // 裸 DispatcherTimer 无人停止：退出放映（元素 Close）后仍会 Tick 并回调已卸载元素。
        AutoDismissIn(0.26);
    }
}
