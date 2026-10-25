using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>画笔面板：与主工具栏同一舞台/同一视觉树；由对应笔工具按钮锚定，按可用空间上下吮吸展开。</summary>
public sealed partial class Toolbar
{
    private WrapPanel? _paletteRecentHost;
    private WrapPanel? _paletteQuickSizes;
    private Slider? _paletteR, _paletteG, _paletteB, _paletteThickness;
    private Canvas? _palettePreview;
    private ScrollViewer? _paletteScroller;
    private Border? _paletteBody;
    private TextBlock? _paletteTitle;   // v0.9.7：标题随工具切换（画笔设置 / 荧光笔设置）
    private double _paletteGrowH;
    private string _paletteColor = AppConfig.Current.DefaultColor;
    private double _paletteMm = AppConfig.Current.DefaultThicknessMm;
    private bool _paletteBuilt;
    private bool _paletteClosing;
    private bool _paletteUpdatingRgb;
    private int _paletteTransition;
    private InkTool _paletteTool = InkTool.Pen;   // v0.9.7：浮岛按工具区分颜色集（笔/荧光笔）

    // v0.9.7：与设置中心「画笔」页的色板同源 —— 浮岛默认色即用户在设置里配的画笔/荧光笔颜色。
    // 原先硬编码 5 色 PaletteSuggest 与设置完全脱节（用户只见红黄两色而最近书写沉淀不足），
    // 改为：默认色板（设置同步）+ 最近使用（去重合并），笔/荧光笔各自一集。
    internal static readonly (string Name, string Hex)[] PenPresets =
    {
        ("朱红", "#E8442E"), ("明黄", "#F5D000"), ("天蓝", "#3D9BE8"), ("青绿", "#34B37A"),
        ("白",   "#F2F3F5"), ("紫",   "#A78BFA"), ("橙",   "#F08A24"), ("墨黑", "#232323"),
    };

    internal static readonly (string Name, string Hex)[] HighlighterPresets =
    {
        ("荧光黄", "#F5D000"), ("薄荷绿", "#8ED081"), ("冰蓝", "#6DC8F2"),
        ("柔粉",   "#FF8FB3"), ("杏橙",   "#F6A96B"),
    };

    private static readonly (string Label, double Mm, double Dot)[] PaletteSizes =
    {
        ("细", 1.2, 5), ("标准", 2.2, 9), ("粗", 4.0, 15),
    };

    public bool IsPaletteOpen => _palettePanel.Visibility == Visibility.Visible;

    private void TogglePalette(InkTool tool)
    {
        if (IsPaletteOpen && !_paletteClosing) HidePalette();
        else ShowPalette(tool);
    }

    /// <summary>v0.9.7：浮岛颜色集（与设置中心同源）。
    /// 笔 → 设置「默认画笔颜色」预设 8 色 + 最近使用（去重合并，最近优先）；
    /// 荧光笔 → 设置「荧光笔颜色」预设 5 色。
    /// 原先硬编码的 5 色 PaletteSuggest 只覆盖笔且与设置脱节（用户反馈只见红黄 2 色），
    /// 现按工具区分、与设置双向同步 —— 设置改默认色，浮岛立即跟随。</summary>
    private IEnumerable<(string Name, string Hex)> GetPaletteColors(InkTool tool)
    {
        var presets = tool == InkTool.Highlighter ? HighlighterPresets : PenPresets;
        // 最近使用色（去重）：用户最近书写沉淀的排最前，但避开预设已有的颜色
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tool == InkTool.Pen)
        {
            foreach (var hex in _svc.Config.RecentColors)
                if (seen.Add(hex))
                    yield return ("最近", hex);
        }
        foreach (var p in presets)
            if (seen.Add(p.Hex))
                yield return p;
    }

    /// <summary>画笔参数面板占用工具栏中央槽上方的独占行；空间不够时拒绝显示，不向角落岛或其他面板溢出。</summary>
    public void ShowPalette(InkTool tool)
    {
        _paletteTool = tool;   // v0.9.7：记录当前工具，颜色集按笔/荧光笔区分
        if (!_paletteBuilt) BuildPalettePanel();
        // v0.9.7：荧光笔使用独立的默认色 —— 打开荧光笔浮岛时以 HighlightColor 为当前色，
        // 笔浮岛则用 InkEngine.Pen.ColorHex；切换工具时颜色不互相污染。
        if (tool == InkTool.Highlighter)
            _paletteColor = _svc.Config.HighlightColor;
        else
            _paletteColor = _svc.InkEngine.Pen.ColorHex;
        // 标题、RGB 滑块、预览全部与当前工具色同步（换工具再开浮岛不残留上一次颜色）
        if (_paletteTitle is not null)
            _paletteTitle.Text = tool == InkTool.Highlighter ? "荧光笔设置" : "画笔设置";
        if (_paletteBuilt)
        {
            Color cur;
            try { cur = (Color)ColorConverter.ConvertFromString(_paletteColor); }
            catch { cur = (Color)ColorConverter.ConvertFromString(_svc.Config.DefaultColor); }
            _paletteUpdatingRgb = true;
            try { _paletteR!.Value = cur.R; _paletteG!.Value = cur.G; _paletteB!.Value = cur.B; }
            finally { _paletteUpdatingRgb = false; }
            DrawPalettePreview();
        }
        if (IsPaletteOpen)
        {
            // 收回动画尚未结束就再次点击时，反转动画，不排队、不锁输入，也不重复增加 Top 偏移。
            _paletteTransition++;
            _paletteClosing = false;
            if (_palettePanel.RenderTransform is TransformGroup oldGroup && oldGroup.Children.Count >= 2 && oldGroup.Children[0] is ScaleTransform oldScale)
            {
                oldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                oldScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                oldScale.ScaleX = oldScale.ScaleY = 1;
            }
            _palettePanel.BeginAnimation(OpacityProperty, null);
            _palettePanel.Opacity = 1;
            _palettePanel.Effect = null;
            _palettePanel.RenderTransformOrigin = new Point(0.5, 1);
            AnimationKit.PopOpen(_palettePanel, 145);
            return;
        }

        AnimationKit.GlideCancel(this);
        RefreshPaletteRecent();
        var wa = ShowScreen.Bounds;
        double scaleFactor = Math.Clamp(_svc.Config.ToolbarScale, 0.7, 1.6);
        double laneWidth = Math.Max(0, DockLaneRight - DockLaneLeft);
        double availableScreenH = StageUiLayout.AvailableChromeExpansion(this);
        double availableLocalH = availableScreenH / scaleFactor;
        double availableLocalW = laneWidth / scaleFactor;

        // The palette's touch sliders need a usable width and a scroll viewport; do not render a
        // clipped strip or place it over a corner island when the central slot is too narrow.
        if (availableLocalW < 220 || availableLocalH < 210)
        {
            Toast.Show("画笔面板放不下。把工具栏调小一点再试。");
            return;
        }

        _rootRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rowWidth = Math.Max(_rootRow.DesiredSize.Width, 1);
        _palettePanel.Width = Math.Min(Math.Clamp(rowWidth * 0.58, 256, 336), availableLocalW);
        _palettePanel.MaxWidth = _palettePanel.Width;
        _palettePanel.MaxHeight = availableLocalH;
        _palettePanel.Visibility = Visibility.Hidden; // Hidden 参与测量，但无可见帧
        _paletteScroller!.MaxHeight = Math.Max(70, availableLocalH - 108);
        _paletteBody!.MaxHeight = Math.Max(102, availableLocalH - 78);
        _paletteBody.MaxWidth = _palettePanel.Width;
        _palettePanel.Measure(new Size(_palettePanel.Width, availableLocalH));
        double naturalLocalH = Math.Min(availableLocalH, Math.Max(1, _palettePanel.DesiredSize.Height));
        if (naturalLocalH < 180)
        {
            ResetPaletteMeasureConstraints();
            Toast.Show("高度不够，画笔面板没打开。");
            return;
        }

        var buttonRect = GetToolScreenRect(tool) ?? GetToolScreenRect(InkTool.Pen);
        double anchorX = buttonRect is Rect anchorRect
            ? anchorRect.X + anchorRect.Width / 2
            : Left + VisualWidth / 2;
        double panelScreenWidth = _palettePanel.Width * scaleFactor;
        if (panelScreenWidth > laneWidth + 0.5)
        {
            ResetPaletteMeasureConstraints();
            Toast.Show("宽度不够，画笔面板没打开。");
            return;
        }
        double localLeft = (anchorX - Left) / scaleFactor - _palettePanel.Width / 2;
        double clampedScreenLeft = Math.Clamp(Left + localLeft * scaleFactor,
            DockLaneLeft, Math.Max(DockLaneLeft, DockLaneRight - panelScreenWidth));
        localLeft = (clampedScreenLeft - Left) / scaleFactor;

        var transformGroup = _palettePanel.RenderTransform as TransformGroup;
        if (transformGroup is null || transformGroup.Children.Count < 2 ||
            transformGroup.Children[0] is not ScaleTransform ||
            transformGroup.Children[1] is not TranslateTransform)
        {
            transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(1, 1));
            transformGroup.Children.Add(new TranslateTransform());
            _palettePanel.RenderTransform = transformGroup;
        }
        var translate = (TranslateTransform)transformGroup.Children[1];
        translate.X = localLeft;
        translate.Y = 0;
        _paletteDown = false; // the panel is never allowed to grow into the permanent bottom row
        SetPaletteRow(down: false);
        _palettePanel.RenderTransformOrigin = new Point(0.5, 1);

        if (!StageUiLayout.TryAcquireEmbeddedChromePanel(this))
        {
            ResetPaletteMeasureConstraints();
            Toast.Show("先关掉全屏界面再用画笔面板。");
            return;
        }

        _paletteGrowH = naturalLocalH * scaleFactor;
        Top -= _paletteGrowH; // bottom toolbar stays in its reserved row; palette grows only upward
        _palettePanel.Visibility = Visibility.Visible;
        _paletteClosing = false;
        UpdateLayout();
        double measuredScreenH = Math.Min(availableScreenH, _palettePanel.ActualHeight * scaleFactor);
        if (measuredScreenH > 0 && Math.Abs(measuredScreenH - _paletteGrowH) > 1)
        {
            Top -= measuredScreenH - _paletteGrowH;
            _paletteGrowH = measuredScreenH;
            UpdateLayout();
        }
        StageUiLayout.ChromeGeometryChanged(); // moves the segment row in this dispatcher turn, before render
        AnimationKit.PopOpen(_palettePanel, 190);
        Stage?.RefreshUiInputBounds();
        NotifyGeometryChanged();
    }

    private void ResetPaletteMeasureConstraints()
    {
        // A rejected open must not leave a stale viewport height that clips a later valid open.
        _palettePanel.Visibility = Visibility.Collapsed;
        _palettePanel.MaxHeight = double.PositiveInfinity;
        if (_paletteBody is not null) _paletteBody.MaxHeight = double.PositiveInfinity;
        if (_paletteScroller is not null) _paletteScroller.MaxHeight = double.PositiveInfinity;
    }

    /// <summary>正常收回播放对称吮吸动画；立即收回用于拖动、隐藏舞台等输入所有权切换。</summary>
    public void HidePalette(bool immediate = false)
    {
        if (_palettePanel.Visibility != Visibility.Visible) return;
        if (_paletteClosing && !immediate) return;
        AnimationKit.GlideCancel(this);
        int token = ++_paletteTransition;
        _paletteClosing = !immediate;

        void FinishClose()
        {
            if (token != _paletteTransition) return;
            _palettePanel.Visibility = Visibility.Collapsed;
            _paletteClosing = false;
            if (!_paletteDown) Top += _paletteGrowH;
            _paletteGrowH = 0;
            _palettePanel.MaxHeight = double.PositiveInfinity;
            _paletteBody!.MaxHeight = double.PositiveInfinity;
            _paletteScroller!.MaxHeight = double.PositiveInfinity;
            StageUiLayout.ReleasePopover(this);
            StageUiLayout.ChromeGeometryChanged();
            // 复位面板变换，下一次弹出始终从干净状态开始。
            _palettePanel.BeginAnimation(OpacityProperty, null);
            _palettePanel.Opacity = 1;
            _palettePanel.Effect = null;
            if (_palettePanel.RenderTransform is TransformGroup group && group.Children.Count >= 1 && group.Children[0] is ScaleTransform s)
            {
                s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                s.ScaleX = s.ScaleY = 1;
            }
            Stage?.RefreshUiInputBounds();
            NotifyGeometryChanged();
        }

        if (immediate)
        {
            if (_palettePanel.RenderTransform is TransformGroup group && group.Children.Count >= 1 && group.Children[0] is ScaleTransform s)
            {
                s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            _palettePanel.BeginAnimation(OpacityProperty, null);
            _palettePanel.Opacity = 1;
            _palettePanel.Effect = null;
            FinishClose();
        }
        else
        {
            AnimationKit.PopClose(_palettePanel, 150, FinishClose);
        }
    }

    private bool _paletteDown;

    private void SetPaletteRow(bool down)
    {
        Grid.SetRow(_palettePanel, down ? 2 : 0);
        _palettePanel.Margin = down ? new Thickness(0, 6, 0, 0) : new Thickness(0, 0, 0, 6);
    }

    public void RefreshPaletteRecent()
    {
        if (!_paletteBuilt || _paletteRecentHost is null) return;
        var host = _paletteRecentHost;
        host.Children.Clear();
        // v0.9.7：色板 = 设置中心同源预设（笔 8 色 / 荧光笔 5 色）+ 最近使用（去重合并）。
        // 原来 RecentColors 为空时回退硬编码 5 色 PaletteSuggest，与设置完全脱节 ——
        // 用户改完设置的默认画笔颜色，浮岛仍只见红黄两色（PaletteSuggest 前两项）。
        foreach (var (name, hex) in GetPaletteColors(_paletteTool).Take(10))
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); }
            catch { continue; }
            var swatch = new Border
            {
                Width = 44, Height = 44,
                Margin = new Thickness(2),
                Padding = new Thickness(7),
                CornerRadius = new CornerRadius(22),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = $"{name} {hex}",
                Child = new Ellipse
                {
                    Width = 27, Height = 27,
                    Fill = new SolidColorBrush(c),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                },
            };
            swatch.Focusable = false;
            Stylus.SetIsPressAndHoldEnabled(swatch, false);
            // 当前选中色高亮：与设置/引擎当前色一致时给 Accent 底圈
            if (string.Equals(hex, _paletteColor, StringComparison.OrdinalIgnoreCase))
                swatch.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            ToolbarFactory.PressWire(swatch, () => PickPaletteColor(c));
            host.Children.Add(swatch);
        }
    }

    private void PickPaletteColor(Color c)
    {
        _paletteColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (_paletteBuilt)
        {
            _paletteUpdatingRgb = true;
            try { _paletteR!.Value = c.R; _paletteG!.Value = c.G; _paletteB!.Value = c.B; }
            finally { _paletteUpdatingRgb = false; }
        }
        ApplyPalettePen();
        // v0.9.7：仅在拾取色板颜色时刷新选中高亮；ApplyPalettePen 不再自刷，
        // 避免拖动 RGB 滑块每帧重建整个色板（对象分配风暴）。
        RefreshPaletteRecent();
    }

    private void BuildPalettePanel()
    {
        _paletteBuilt = true;
        _palettePanel.Padding = new Thickness(0);
        _palettePanel.BorderThickness = new Thickness(0);
        _palettePanel.Background = Brushes.Transparent;
        _palettePanel.CornerRadius = new CornerRadius(0);
        _palettePanel.Focusable = false;
        _palettePanel.IsHitTestVisible = true;

        // 常用色胶囊与设置卡视觉上分开，但都属于 Toolbar 的同一棵 WPF 视觉树。
        _paletteRecentHost = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var swatchPill = new Border
        {
            Child = _paletteRecentHost,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(28),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        swatchPill.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        swatchPill.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");

        var content = new StackPanel();
        // v0.9.7：标题按浮岛工具区分（笔/荧光笔），与设置中心「画笔」页命名同源
        _paletteTitle = MakePaletteLabel(_paletteTool == InkTool.Highlighter ? "荧光笔设置" : "画笔设置", true);
        _paletteTitle.Margin = new Thickness(2, 0, 0, 7);
        content.Children.Add(_paletteTitle);
        var sizeTitle = MakePaletteLabel("笔触粗细", false);
        sizeTitle.Margin = new Thickness(2, 0, 0, 5);
        content.Children.Add(sizeTitle);
        _paletteQuickSizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (label, mm, dot) in PaletteSizes)
            _paletteQuickSizes.Children.Add(PaletteSizeChip(label, mm, dot));
        content.Children.Add(_paletteQuickSizes);

        var initial = (Color)ColorConverter.ConvertFromString(_paletteColor);
        content.Children.Add(PaletteSlider(out _paletteR, "R", 0, 255, initial.R));
        content.Children.Add(PaletteSlider(out _paletteG, "G", 0, 255, initial.G));
        content.Children.Add(PaletteSlider(out _paletteB, "B", 0, 255, initial.B));
        content.Children.Add(PaletteSlider(out _paletteThickness, "粗细", 0.6, 6.0, _paletteMm));

        _palettePreview = new Canvas { Height = 36, Margin = new Thickness(2, 3, 2, 0), ClipToBounds = true };
        content.Children.Add(_palettePreview);
        _paletteScroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            CanContentScroll = false,
            Focusable = false,
        };
        _paletteBody = new Border
        {
            Child = _paletteScroller,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _paletteBody.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _paletteBody.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");

        var group = new StackPanel { Orientation = Orientation.Vertical };
        group.Children.Add(swatchPill);
        group.Children.Add(_paletteBody);
        _palettePanel.Child = group;

        Action applyRgb = () =>
        {
            if (_paletteUpdatingRgb) return;
            var nc = Color.FromRgb((byte)_paletteR!.Value, (byte)_paletteG!.Value, (byte)_paletteB!.Value);
            _paletteColor = $"#{nc.R:X2}{nc.G:X2}{nc.B:X2}";
            ApplyPalettePen();
        };
        _paletteR!.ValueChanged += (_, _) => applyRgb();
        _paletteG!.ValueChanged += (_, _) => applyRgb();
        _paletteB!.ValueChanged += (_, _) => applyRgb();
        _paletteThickness!.ValueChanged += (_, _) =>
        {
            if (_paletteUpdatingRgb) return;
            _paletteMm = _paletteThickness.Value;
            ApplyPalettePen();
        };
        ApplyPalettePen();
    }

    private static TextBlock MakePaletteLabel(string text, bool title)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = title ? 13 : 11.5,
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, title ? "TextPrimaryBrush" : "TextSecondaryBrush");
        return tb;
    }

    private Border PaletteSizeChip(string label, double mm, double dot)
    {
        var dotEl = new Ellipse { Width = dot, Height = dot, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        dotEl.SetResourceReference(Ellipse.FillProperty, "TextPrimaryBrush");
        var tb = MakePaletteLabel(label, false);
        tb.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dotEl);
        row.Children.Add(tb);
        var chip = new Border
        {
            Child = row,
            MinWidth = 66,
            MinHeight = 42,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 0, 7, 0),
            Cursor = Cursors.Hand,
            Tag = mm,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Focusable = false,
        };
        ToolbarFactory.PressWire(chip, () => PickPaletteSize(mm));
        return chip;
    }

    private void PickPaletteSize(double mm)
    {
        _paletteMm = mm;
        if (_paletteBuilt) _paletteThickness!.Value = mm;
        ApplyPalettePen();
    }

    private static UIElement PaletteSlider(out Slider slider, string label, double min, double max, double value)
    {
        slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            MinWidth = 90,
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = false,
        };
        // v0.9.7：应用触摸友好样式（32px Thumb / 6px 轨道 / 44px 高 / 点轨即跳）。
        // v0.9.8：再挂触摸直驱（TouchSliderKit）—— 浮岛 RGB/粗细滑块在手指/红外笔下可靠跟手。
        if (Application.Current.TryFindResource("TouchSliderStyle") is Style touchStyle)
            slider.Style = touchStyle;
        TouchSliderKit.Attach(slider);
        slider.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        var tb = MakePaletteLabel(label, false);
        var host = new Grid { Height = 44, Margin = new Thickness(1, 0, 1, 0) };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(tb, 0);
        Grid.SetColumn(slider, 1);
        host.Children.Add(tb);
        host.Children.Add(slider);
        return host;
    }

    /// <summary>设置中心修改默认笔色/粗细后同步内嵌面板状态，避免滑条和实际笔属性不一致。
    /// v0.9.7：同步后重建色板（最近色 + 设置预设合并集），高亮圈跟随新当前色。</summary>
    public void SyncPaletteToCurrentPen()
    {
        // 浮岛工具决定同步源：荧光笔浮岛跟 HighlightColor，笔浮岛跟 InkEngine.Pen
        if (_paletteTool == InkTool.Highlighter)
        {
            _paletteColor = _svc.Config.HighlightColor;
            _paletteMm = Math.Clamp(_svc.InkEngine.Pen.ThicknessMm, 0.6, 6.0);
        }
        else
        {
            var pen = _svc.InkEngine.Pen;
            _paletteColor = pen.ColorHex;
            _paletteMm = Math.Clamp(pen.ThicknessMm, 0.6, 6.0);
        }
        if (!_paletteBuilt) return;

        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(_paletteColor); }
        catch { c = (Color)ColorConverter.ConvertFromString(_svc.Config.DefaultColor); }
        _paletteUpdatingRgb = true;
        try
        {
            _paletteR!.Value = c.R;
            _paletteG!.Value = c.G;
            _paletteB!.Value = c.B;
            _paletteThickness!.Value = _paletteMm;
        }
        finally { _paletteUpdatingRgb = false; }
        RefreshPaletteSizeHighlight();
        DrawPalettePreview();
        RefreshPaletteRecent();
    }

    private void ApplyPalettePen()
    {
        // v0.9.7：按浮岛所属工具分别落色 ——
        //   荧光笔浮岛：只写 Config.HighlightColor（下次书写即生效；InkEngine.OnStylusDown
        //               会用 Pen with { ColorHex = HighlightColor } 临时构造荧光笔），
        //               不污染 InkEngine.Pen 的 Highlighter 状态；
        //   笔浮岛：直接写 InkEngine.Pen（普通笔的当前色/粗细）。
        // 两者与设置中心「画笔」页数据同源（改任一处另两处立即跟随）。
        if (_paletteTool == InkTool.Highlighter)
        {
            _svc.Config.HighlightColor = _paletteColor;
            // 荧光笔粗细由 InkEngine.Pen.ThicknessMm 派生（OnStylusDown 中 Math.Max(Pen.ThicknessMm, 8)），
            // 浮岛粗细滑块同样写到这里 —— 普通笔/荧光笔共享同一粗细基准。
            _svc.InkEngine.Pen = _svc.InkEngine.Pen with { ThicknessMm = _paletteMm };
            _svc.Config.Save();
        }
        else
        {
            _svc.InkEngine.Pen = new PenInfo { ColorHex = _paletteColor, ThicknessMm = _paletteMm };
        }
        RefreshPaletteSizeHighlight();
        DrawPalettePreview();
        // 说明：此处不重建色板 —— 由调用方按需刷新（PickPaletteColor 拾色后刷新高亮；
        // RGB 滑块每次拖动都调本方法，若在此重建会产生逐帧的对象分配风暴）。
    }

    private void RefreshPaletteSizeHighlight()
    {
        if (!_paletteBuilt || _paletteQuickSizes is null) return;
        foreach (var el in _paletteQuickSizes.Children.OfType<Border>())
        {
            double mm = (double)el.Tag;
            bool on = Math.Abs(_paletteMm - mm) <= mm * 0.15;
            if (on)
            {
                el.SetResourceReference(Border.BackgroundProperty, "ItemSelectedBrush");
                el.BorderThickness = new Thickness(1);
                el.SetResourceReference(Border.BorderBrushProperty, "AccentSoftBrush");
            }
            else
            {
                el.Background = Brushes.Transparent;
                el.BorderThickness = new Thickness(1);
                el.BorderBrush = Brushes.Transparent;
            }
        }
    }

    private void DrawPalettePreview()
    {
        if (!_paletteBuilt || _palettePreview is null) return;
        _palettePreview.Children.Clear();
        double px = Math.Max(1.5, _svc.InkEngine.Profile.MmToDip(_paletteMm));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(8, 27), false, false);
            g.BezierTo(new Point(36, 2), new Point(80, 46), new Point(123, 21), true, false);
            g.BezierTo(new Point(153, 3), new Point(182, 39), new Point(220, 27), true, false);
        }
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_paletteColor));
        _palettePreview.Children.Add(new Path
        {
            Data = geo,
            Stroke = brush,
            StrokeThickness = px,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        });
    }
}
