using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>工具栏 · 布局：按配置重建按钮行（RebuildLayout）/ 手柄与各按钮构建 / 组间分隔线 / 工具选中「朱砂点亮」与主题热切换回填。（v0.9.4 由 1119 行单文件按职责拆分）
/// v1.1.2 移除「可分割副岛」特性：分割缝是嵌在工具栏中部的 44px 隐形热区，触摸滑动极易
/// 误触发把 Ops 段切到副岛，而副岛在实际环境中经常不可见（「工具栏只剩基础工具、找不到
/// 撤销/AI/插件」的用户报告根因）。恢复能力小于误触代价，特性整体退场；旧配置的分割标记
/// 由 NormalizeToolbarLayout 清除。</summary>
public sealed partial class Toolbar
{
    // ———————— 布局渲染（设置中心可实时重排） ————————

    /// <summary>
    /// 按当前配置重建工具栏内容：
    ///  - nav 组跳过（v0.7.0 由独立翻页岛承载）；
    ///  - Tools→Ops 边界渲染细发丝线；
    ///  - 「结束放映」恒定尾部。
    /// </summary>
    public void RebuildLayout()
    {
        // 先按常规（图标 + 文字）构建并测量；槽位放不下再单向降级为纯图标重建一次。
        // 不做"降级后再尝试回退"的循环，避免测量/重建来回振荡。
        BuildButtons(compact: false);
        if (RowVisualWidth() > AvailableLaneWidth + 0.5)
            BuildButtons(compact: true);
        RefreshSelection();
        NotifyGeometryChanged();
    }

    /// <summary>当前按钮行在屏幕坐标系（含 LayoutTransform 缩放）的视觉宽度，含边框内边距余量。</summary>
    private double RowVisualWidth()
    {
        _rootRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _rootRow.DesiredSize.Width;
        return (double.IsFinite(w) && w > 0 ? w : 0) * ScaleFactor + 18;
    }

    /// <summary>槽位/缩放变化后重新判定紧凑模式：只在状态不匹配时重建，稳定后零开销。
    /// 极端窄屏连紧凑都放不下时打开横向滚动条 —— 按钮按下即执行，无法靠拖动滚动，
    /// 这条滚动条是「结束放映」等尾部按钮最后一条可达路径。</summary>
    private void FitCompactLayout()
    {
        bool overflow = RowVisualWidth() > AvailableLaneWidth + 0.5;
        if (overflow && !_compact)
        {
            BuildButtons(compact: true);
            RefreshSelection();
        }
        else if (_compact && !overflow)
        {
            BuildButtons(compact: false);
            if (RowVisualWidth() > AvailableLaneWidth + 0.5) BuildButtons(compact: true);   // 回退失败：保持紧凑
            RefreshSelection();
        }
        _toolScroller.HorizontalScrollBarVisibility =
            RowVisualWidth() > AvailableLaneWidth + 0.5 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private void BuildButtons(bool compact)
    {
        _compact = compact;
        _rootRow.Children.Clear();
        _buttons.Clear();

        // 手柄永远在最前（拖动/收纳是工具栏自身能力，不参与布局）
        _rootRow.Children.Add(BuildGrip());

        ToolbarGroup? lastGroup = null;
        foreach (var (item, visible) in ToolbarCatalog.ResolveLayout(_svc.Config.ToolbarLayout))
        {
            if (!visible) continue;
            if (item.Group == ToolbarGroup.Nav) continue;   // v0.7.0：翻页组 → 独立翻页岛

            if (lastGroup is { } lg && lg != item.Group)
                _rootRow.Children.Add(MakeSeparator());
            lastGroup = item.Group;

            switch (item.Id)
            {
                case "touch" or "pen" or "highlighter":
                    _rootRow.Children.Add(BuildToolButton(item, compact));
                    break;
                case "erase":
                    _rootRow.Children.Add(BuildEraseButton(item, compact));
                    break;
                case "undo":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconUndo", () => UndoRequested?.Invoke(), compact));
                    break;
                case "redo":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconRedo", () => RedoRequested?.Invoke(), compact));
                    break;
                case "ai":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconAi", () => ShapeAssistRequested?.Invoke(), compact));
                    break;
                case "plugin":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconPlugin", () => _svc.Plugins.ToggleQuickPanel(GetPluginButtonRect()), compact));
                    break;
            }
        }

        // 结束放映：恒定尾部
        _rootRow.Children.Add(BuildExitButton(compact));
    }

    private Border BuildGrip()
    {
        // 不用 ButtonBase 做拖动柄：Button 的按压/点击状态机与拖动会争夺同一触点。
        // Border 只负责承接手势，不自动点击、不夺键盘焦点；44×44 以上的独立命中区。
        var b = new Border
        {
            Width = 48,
            Height = 48,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Child = new Path
            {
                Style = (Style)Application.Current.FindResource("IconFill"),
                Data = ToolbarFactory.Icon("IconGrip").Data,
                Width = 18,
                Height = 18,
                IsHitTestVisible = false,
            },
            ToolTip = "按住拖动可移工具栏，长按收成小球",
            Focusable = false,
        };
        _gripVisual = b;
        Stylus.SetIsPressAndHoldEnabled(b, false);
        Stylus.SetIsFlicksEnabled(b, false);
        // 触摸/笔/鼠标统一进入同一拖动会话；不会触发 Button.Click。
        b.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnGripDown), true);
        b.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnGripUp), true);
        b.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnGripMove), true);
        b.AddHandler(UIElement.PreviewStylusDownEvent, new StylusDownEventHandler(OnGripDown), true);
        b.AddHandler(UIElement.PreviewStylusMoveEvent, new StylusEventHandler(OnGripMove), true);
        b.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler(OnGripUp), true);
        b.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnGripDown), true);
        b.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnGripMove), true);
        b.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnGripUp), true);
        b.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnGripMouseCaptureLost), true);
        b.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(OnGripTouchCaptureLost), true);
        b.AddHandler(UIElement.LostStylusCaptureEvent, new StylusEventHandler(OnGripStylusCaptureLost), true);
        return b;
    }

    private Button BuildToolButton(ToolbarCatalog.Item item, bool compact)
    {
        var tool = item.Tool!.Value;
        var icon = ToolbarFactory.Icon(item.Icon);
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButtonStyle"),
            ToolTip = item.Tip,
            Content = ToolbarFactory.MakeButtonContent(icon, item.Name, compact),
        };
        ToolbarFactory.ApplyLabelSize(b, compact);
        // v0.7.0 点击即有：按下瞬间执行（微滑不丢点）
        ToolbarFactory.PressWire(b, () => HandleToolClick(tool, b));
        _buttons[item.Id] = b;
        return b;
    }

    /// <summary>
    /// 书写工具 toggle 语义（v0.5.0 调色面板内嵌化）：
    ///  - 首次点击（当前不是该工具）→ 直接进入绘制模式（沿用上次笔色/粗细，零等待）；
    ///  - 再次点击（已是该工具）→ 工具栏头顶弹出内嵌调色面板（最近颜色 + 三档粗细 + RGB 微调）。
    /// </summary>
    private void HandleToolClick(InkTool tool, Button btn)
    {
        AnimationKit.PressPulse(btn);
        if (tool == _currentTool)
            TogglePalette(tool);    // 再次点击 → 切换内嵌调色面板
        else
            SelectTool(tool);       // 首次点击 → 直接进模式（上次设置）
    }

    private Button BuildEraseButton(ToolbarCatalog.Item item, bool compact)
    {
        var icon = ToolbarFactory.Icon("IconEraser");
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButtonStyle"),
            ToolTip = item.Tip,
            Content = ToolbarFactory.MakeButtonContent(icon, item.Name, compact),
        };
        ToolbarFactory.ApplyLabelSize(b, compact);
        // 与书写工具同款 toggle：首次点击直接进默认擦除模式，再次点击才弹三模式浮岛
        ToolbarFactory.PressWire(b, () =>
        {
            AnimationKit.PressPulse(b);
            if (_currentTool is InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe)
                EraseRequested?.Invoke();
            else
                SelectTool(ToolbarCatalog.EraseModeToTool(_svc.Config.DefaultEraseMode));
        });
        _buttons["erase"] = b;
        return b;
    }

    private Button BuildSimpleButton(ToolbarCatalog.Item item, string icon, Action onClick, bool compact)
    {
        var b = ToolbarFactory.BuildSimple(ToolbarFactory.Icon(icon), item.Tip, onClick, item.Name, compact);
        _buttons[item.Id] = b;
        return b;
    }

    /// <summary>结束放映：恒定尾部元素（低频高危操作，不进翻页岛防误触）。
    /// v0.8.0：图标染警示琥珀——高危操作一眼可辨（与衢青交互色明确区分）。</summary>
    private Button BuildExitButton(bool compact)
    {
        var exit = ToolbarFactory.BuildSimple(ToolbarFactory.Icon("IconStop"), "结束放映",
            () => ExitShowRequested?.Invoke(), "结束", compact);
        if (ToolbarFactory.IconPathOf(exit.Content) is { } p)
            p.SetResourceReference(Path.FillProperty, "AccentAmberBrush");
        _buttons["nav.exit"] = exit;
        return exit;
    }

    // ———————— 分割缝（v1.1.2 已移除：见类注释） ————————

    private Rectangle MakeSeparator() => new() { Style = (Style)Application.Current.FindResource("GroupSeparator") };

    // ———————— 工具选择与高亮（朱砂点亮） ————————

    public void SelectTool(InkTool tool)
    {
        if (tool != _currentTool && IsPaletteOpen) HidePalette(immediate: true);
        _currentTool = tool;
        RefreshSelection();
        ToolSelected?.Invoke(tool);
    }

    /// <summary>AI Power 激活态刷新（按钮=开关：开则朱砂点亮，与工具选中同一视觉语言）。</summary>
    public void RefreshAiBadge()
    {
        if (_buttons.TryGetValue("ai", out var b))
            ToolbarFactory.SetToolActive(b, _svc.Config.AiEnabled);
    }

    private void RefreshSelection()
    {
        string? activeId = _currentTool switch
        {
            InkTool.TouchThrough => "touch",
            InkTool.Pen => "pen",
            InkTool.Highlighter => "highlighter",
            InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe => "erase",
            _ => null,
        };
        foreach (var (id, b) in _buttons)
        {
            // 结束放映恒定警示琥珀：SetToolActive 会把图标改回暖白，原实现每次
            // 切换工具都把这条高危警示抹掉（与 BuildExitButton 的染色直接冲突）。
            if (id == "nav.exit") continue;
            ToolbarFactory.SetToolActive(b, id == activeId);   // 朱砂点亮 / 暖白常态
        }
        RefreshAiBadge();        // AI 是开关（非工具）：其点亮态独立于工具选中，需在工具刷新后回填
        RefreshHistoryButtons(); // 撤销/重做可用性随页面历史变化
    }

    /// <summary>撤销/重做可用性：无历史时置灰并降透明度，避免"点了没反应"被当成按钮故障。
    /// 禁用态同时阻断 PressWire（WPF 不向 IsEnabled=false 的元素派发输入）。</summary>
    public void RefreshHistoryButtons()
    {
        SetButtonEnabled("undo", _svc.InkEngine.CanUndo);
        SetButtonEnabled("redo", _svc.InkEngine.CanRedo);
    }

    private void SetButtonEnabled(string id, bool enabled)
    {
        if (!_buttons.TryGetValue(id, out var b)) return;
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1.0 : 0.4;
    }

    /// <summary>主题热切换后刷新选中高亮（brush 实例已换；编排器 ThemeChanged 转发调用）。</summary>
    public void OnThemeApplied() => RefreshSelection();
}