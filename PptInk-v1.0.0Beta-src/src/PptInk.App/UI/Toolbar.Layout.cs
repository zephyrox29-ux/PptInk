using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>工具栏 · 布局与分割：按配置重建按钮行（RebuildLayout）/ 分割段清单与拉出合并（SplitSegmentItems · SplitAtOps · MergeOpsBack）/ 手柄与各按钮构建 / Tools→Ops 可拖拽分割缝 / 工具选中「朱砂点亮」与主题热切换回填。（v0.9.4 由 1119 行单文件按职责拆分，纯搬运，零逻辑改动）</summary>
public sealed partial class Toolbar
{
    // ———————— 布局渲染（设置中心可实时重排） ————————

    /// <summary>
    /// 按当前配置重建工具栏内容：
    ///  - nav 组跳过（v0.7.0 由独立翻页岛承载）；
    ///  - 分割态：分割点（Tools 组最后元素）之后的元素归副岛，主栏截断渲染；
    ///  - Tools→Ops 边界渲染「可拖拽分割缝」（一体态时），其余组界为细发丝线；
    ///  - 「结束放映」恒定尾部（分割时归副岛，防误触不进翻页岛）。
    /// </summary>
    public void RebuildLayout()
    {
        _rootRow.Children.Clear();
        _buttons.Clear();

        // 手柄永远在最前（拖动/收纳是工具栏自身能力，不参与布局）
        _rootRow.Children.Add(BuildGrip());

        string? splitAfter = _svc.Config.ToolbarSplitAfterId;
        bool split = splitAfter is not null;
        // v0.9.7 自愈：分割标记还在但其后没有任何可见元素 —— 主栏会被截断成「笔/荧光笔/触摸/擦除」
        // 而副岛为空（撤销/AI/插件从界面上彻底消失）。此类失效分割自动清除，恢复完整主栏。
        if (split && SplitSegmentItems().Count == 0)
        {
            _svc.Config.ToolbarSplitAfterId = null;
            _svc.Config.Save();
            splitAfter = null;
            split = false;
        }
        bool afterSplit = false;
        ToolbarGroup? lastGroup = null;
        foreach (var (item, visible) in ToolbarCatalog.ResolveLayout(_svc.Config.ToolbarLayout))
        {
            if (!visible) continue;
            if (item.Group == ToolbarGroup.Nav) continue;   // v0.7.0：翻页组 → 独立翻页岛
            if (afterSplit) continue;                        // 分割点之后 → 副岛

            if (lastGroup is { } lg && lg != item.Group)
            {
                // v0.7.0：Tools→Ops 边界 = 可拖拽分割缝（一体态才渲染；分割态主栏不出现 Ops）
                if (!split && lg == ToolbarGroup.Tools && item.Group == ToolbarGroup.Ops)
                    _rootRow.Children.Add(BuildSplitGrip());
                else
                    _rootRow.Children.Add(MakeSeparator());
            }
            lastGroup = item.Group;

            switch (item.Id)
            {
                case "touch" or "pen" or "highlighter":
                    _rootRow.Children.Add(BuildToolButton(item));
                    break;
                case "erase":
                    _rootRow.Children.Add(BuildEraseButton(item));
                    break;
                case "undo":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconUndo", () => UndoRequested?.Invoke()));
                    break;
                case "redo":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconRedo", () => RedoRequested?.Invoke()));
                    break;
                case "ai":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconAi", () => ShapeAssistRequested?.Invoke()));
                    break;
                case "plugin":
                    _rootRow.Children.Add(BuildSimpleButton(item, "IconPlugin", () => _svc.Plugins.ToggleQuickPanel(GetPluginButtonRect())));
                    break;
            }

            if (split && item.Id == splitAfter) afterSplit = true;   // 本元素之后归副岛
        }

        // 结束放映：恒定尾部（分割时归副岛渲染）
        if (!afterSplit)
            _rootRow.Children.Add(BuildExitButton());
        NotifyGeometryChanged();
    }

    /// <summary>分割段元素清单（编排器构建副岛用）：分割点之后的可见元素（nav 除外）。</summary>
    public List<ToolbarCatalog.Item> SplitSegmentItems()
    {
        var splitAfter = _svc.Config.ToolbarSplitAfterId;
        var list = new List<ToolbarCatalog.Item>();
        if (splitAfter is null) return list;
        bool after = false;
        foreach (var (item, visible) in ToolbarCatalog.ResolveLayout(_svc.Config.ToolbarLayout))
        {
            if (!visible) continue;
            if (item.Group == ToolbarGroup.Nav) continue;
            if (after) list.Add(item);
            if (item.Id == splitAfter) after = true;
        }
        return list;
    }

    // ———————— 可分割：拉出 / 合并 ————————

    /// <summary>
    /// 拉出分割：Tools 组最后可见元素设为分割点（其后的 Ops 段归副岛）。
    /// 主栏宽度骤减时中心保持，不跳位。
    /// </summary>
    public void SplitAtOps()
    {
        if (IsSplit) return;
        // 找 Tools 组最后可见元素 id
        string? splitId = null;
        foreach (var (item, visible) in ToolbarCatalog.ResolveLayout(_svc.Config.ToolbarLayout))
        {
            if (!visible) continue;
            if (item.Group == ToolbarGroup.Nav) continue;
            if (item.Group == ToolbarGroup.Tools) splitId = item.Id;
            else break;   // 进入 Ops 段
        }
        if (splitId is null) return;   // 无 Tools→Ops 边界（用户自定义重排）：分割不可达，静默返回

        _svc.Config.ToolbarSplitAfterId = splitId;
        _svc.Config.Save();
        double cx = Left + ActualWidth / 2;
        RebuildLayout();
        UpdateLayout();
        var wa = ShowScreen.Bounds;
        Left = Math.Clamp(cx - ActualWidth / 2, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - ActualWidth - 4));
        SplitChanged?.Invoke();
    }

    /// <summary>合并回去：清分割点，主栏恢复全量（中心保持），副岛由编排器收回。</summary>
    public void MergeOpsBack()
    {
        if (!IsSplit) return;
        _svc.Config.ToolbarSplitAfterId = null;
        _svc.Config.Save();
        double cx = Left + ActualWidth / 2;
        RebuildLayout();
        UpdateLayout();
        var wa = ShowScreen.Bounds;
        Left = Math.Clamp(cx - ActualWidth / 2, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - ActualWidth - 4));
        SplitChanged?.Invoke();
        NotifyGeometryChanged();
    }

    private Border BuildGrip()
    {
        // 不用 ButtonBase 做拖动柄：Button 的按压/点击状态机与拖动会争夺同一触点。
        // Border 只负责承接手势，不自动点击、不夺键盘焦点；44×44 以上的独立命中区。
        var b = new Border
        {
            Width = 56,
            Height = 56,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Child = new Path
            {
                Style = (Style)Application.Current.FindResource("IconFill"),
                Data = (Geometry)Application.Current.FindResource("IconGrip"),
                Width = 20,
                Height = 20,
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

    private Button BuildToolButton(ToolbarCatalog.Item item)
    {
        var tool = item.Tool!.Value;
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButtonStyle"),
            ToolTip = item.Tip,
            Content = new Path
            {
                Style = (Style)Application.Current.FindResource("IconFill"),
                Data = (Geometry)Application.Current.FindResource(item.Icon),
            },
        };
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

    private Button BuildEraseButton(ToolbarCatalog.Item item)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButtonStyle"),
            ToolTip = item.Tip,
            Content = new Path
            {
                Style = (Style)Application.Current.FindResource("IconFill"),
                Data = (Geometry)Application.Current.FindResource("IconEraser"),
            },
        };
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

    private Button BuildSimpleButton(ToolbarCatalog.Item item, string icon, Action onClick)
    {
        var b = ToolbarFactory.BuildSimple(ToolbarFactory.Icon(icon), item.Tip, onClick);
        _buttons[item.Id] = b;
        return b;
    }

    /// <summary>结束放映：恒定尾部元素（低频高危操作，不进翻页岛防误触）。
    /// v0.8.0：图标染警示琥珀——高危操作一眼可辨（与衢青交互色明确区分）。</summary>
    private Button BuildExitButton()
    {
        var exit = ToolbarFactory.BuildSimple(ToolbarFactory.Icon("IconStop"), "结束放映",
            () => ExitShowRequested?.Invoke());
        if (exit.Content is Path p)
            p.SetResourceReference(Path.FillProperty, "AccentAmberBrush");
        _buttons["nav.exit"] = exit;
        return exit;
    }

    // ———————— 分割缝（Tools→Ops 边界的可拖拽手柄） ————————

    /// <summary>
    /// 分割缝：22px 宽透明命中区 + 中央 1px 发丝线。拖动超阈值即拉出副岛
    /// （本次拖动会话随即终结——副岛出现在主栏右侧，松手后重新拖副岛定位）。
    /// </summary>
    private UIElement BuildSplitGrip()
    {
        var line = new Rectangle { Width = 1, Height = 26, RadiusX = 0.5, RadiusY = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
        line.SetResourceReference(Rectangle.FillProperty, "PanelBorderBrush");

        var grip = new Border
        {
            Width = 44,     // v0.8.0：命中区 44px（触摸目标合规；视觉仍是中央 1px 发丝线）
            Background = Brushes.Transparent,   // 透明背景：命中测试必需（null 不参与命中）
            Cursor = Cursors.SizeWE,
            ToolTip = "拖到右边，可把工具栏拆成两段",
            Child = line,
        };
        var dk = new DragKit(this);
        dk.Wire(grip);
        dk.Pressed += () => AnimationKit.GlideCancel(this);
        dk.DragStarted += () =>
        {
            dk.EndSession();     // 拉出即拆：本次会话终结，副岛接管后续拖动
            SplitAtOps();
        };
        return grip;
    }

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
            ToolbarFactory.SetToolActive(b, id == activeId);   // 朱砂点亮 / 暖白常态
        RefreshAiBadge();   // AI 是开关（非工具）：其点亮态独立于工具选中，需在工具刷新后回填
    }

    /// <summary>主题热切换后刷新选中高亮（brush 实例已换；编排器 ThemeChanged 转发调用）。</summary>
    public void OnThemeApplied() => RefreshSelection();
}