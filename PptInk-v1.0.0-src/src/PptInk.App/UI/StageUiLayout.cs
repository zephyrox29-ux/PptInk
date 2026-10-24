using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 放映界面的硬性槽位规则（不是碰撞检测器）：
/// 1) 左/右翻页岛各占一个底角；主工具栏只占两岛之间的中央底栏；
/// 2) 分割副栏只占主栏上方的独立横行；
/// 3) 所有可交互浮动面板共享一个工作槽，同一时刻只准一个面板拥有它；
/// 4) 调试 HUD 占固定左侧区域，工作槽只从 HUD 右侧开始；普通浮动面板不能进入底栏；
/// 5) 模态引导/聚光灯是单独模式，不与普通浮岛并行显示。
/// 位置由操作与槽位约束，而不是等元素重叠后再进行补救。
/// </summary>
internal static class StageUiLayout
{
    private static Rect _screen = new(0, 0, 1280, 720);
    private static double _laneLeft = 250;
    private static double _laneRight = 1030;
    private static double _dockBottom = 680;
    private static double _chromeTop = 600;
    private static double _popupBottom = 588;
    private static Toolbar? _toolbar;
    private static SegmentIsland? _segment;
    private static StageElement? _activePopover;
    // The toolbar palette is hosted inside the toolbar itself but owns the same exclusive popup lease.
    private static bool _embeddedChromePanel;
    private static Rect _popoverAnchor;
    private static double _popoverWidth;
    private static double _popoverHeight;
    private static double _popoverGap = 12;
    private static StageElement? _activeModal;
    private static readonly Dictionary<StageElement, Visibility> ModalPreviousVisibility = new();
    private static bool _configured;

    private static void EnsureScreenInitialized()
    {
        if (_configured) return;
        var screen = ShowScreen.Bounds;
        if (screen.Width < 100 || screen.Height < 100) return;
        _screen = screen;
        _laneLeft = screen.Left + 240;
        _laneRight = screen.Right - 240;
        _dockBottom = screen.Bottom - (AppConfig.Current.RespectGestureZone ? 64 : 16);
        _chromeTop = _dockBottom - 64;
        _popupBottom = _chromeTop - 12;
    }

    internal static double LaneLeft => _laneLeft;
    internal static double LaneRight => _laneRight;
    internal static double DockBottom => _dockBottom;
    internal static double ChromeTop => _chromeTop;
    internal static bool HasExclusivePanel => _activePopover is not null;
    internal static bool HasActiveModal => _activeModal is not null;
    internal static bool IsEmbeddedChromePanel(StageElement owner) =>
        _embeddedChromePanel && ReferenceEquals(_activePopover, owner);

    internal static void Configure(
        Rect screen, double laneLeft, double laneRight, double dockBottom,
        Toolbar? toolbar, SegmentIsland? segment)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return;
        _screen = screen;
        _configured = true;
        double laneMax = Math.Max(screen.Left + 9, screen.Right - 8);
        _laneLeft = Math.Clamp(laneLeft, screen.Left + 8, laneMax - 1);
        _laneRight = Math.Clamp(laneRight, _laneLeft + 1, laneMax);
        _dockBottom = Math.Clamp(dockBottom, screen.Top + 48, screen.Bottom - 8);
        _toolbar = toolbar;
        _segment = segment;

        // chrome 边界来自固定槽位自身；不遍历任意窗口矩形，也不做相交扫描。
        double top = toolbar is not null ? toolbar.ScreenTop : _dockBottom - 64;
        // 副栏即使当前隐藏，也保留它的专属行，避免面板刚好占到该行后副栏又被唤起。
        if (segment is not null) top = Math.Min(top, segment.ScreenTop);
        _chromeTop = Math.Clamp(top, screen.Top + 8, _dockBottom);
        _popupBottom = _chromeTop - 12;

        ApplyDebugHudSlotToVisibleHud();
        ReflowActivePopover();
    }

    /// <summary>锚定工具浮岛申请唯一工作槽。新面板打开时旧面板让出槽位，不依靠 Z 序遮盖。</summary>
    internal static Rect AllocatePopover(StageElement owner, Rect anchor, double width, double height, double gap)
    {
        EnsureScreenInitialized();
        if (_activeModal is { } modal && !ReferenceEquals(modal, owner))
            return Rect.Empty;

        // 交互浮岛与状态提示互斥；Toast 会在空闲后补发，不会压在面板上。
        Toast.DismissForExclusivePanel();
        if (_activePopover is { } previous && !ReferenceEquals(previous, owner))
        {
            previous.HideForExclusiveSlotReplacement(_embeddedChromePanel);
        }
        _embeddedChromePanel = false;

        _activePopover = owner;
        _popoverAnchor = anchor;
        _popoverWidth = double.IsFinite(width) ? Math.Max(1, width) : 1;
        _popoverHeight = double.IsFinite(height) ? Math.Max(1, height) : 1;
        _popoverGap = Math.Max(4, double.IsFinite(gap) ? gap : 12);
        return ReflowActivePopover();
    }

    /// <summary>计时器、点名、板中板、Lua 面板申请同一工作槽，无法与其他浮面板同时占位。</summary>
    internal static Rect AllocateUtilityPanel(StageElement owner, double width, double height)
    {
        EnsureScreenInitialized();
        double left = PopupLeftLimit();
        double right = _screen.Right - 8;
        if (right <= left) return Rect.Empty;
        double center = (left + right) / 2;
        return AllocatePopover(owner, new Rect(center, _screen.Top + 16, 1, 1), width, height, 12);
    }

    /// <summary>固定左侧调试 HUD 会保留专属列；其宽度不与右侧浮动工作区共享。</summary>
    private static double PopupLeftLimit()
    {
        double left = _screen.Left + 8;
        var stage = StageHost.Stage;
        if (stage is null) return left;
        var hud = stage.EnumerateStageElements().OfType<DebugHudElement>().FirstOrDefault(e => e.IsVisible);
        if (hud is null) return left;
        hud.UpdateLayout();
        double w = hud.ActualWidth > 1 ? hud.ActualWidth : hud.DesiredSize.Width;
        if (!double.IsFinite(w) || w < 1) w = Math.Min(430, _screen.Width * .42);
        // HUD 的可拖动横向区就是自己的固定列；浮动工作区始终从列右侧开始。
        return Math.Max(left, hud.ScreenLeft + w + 16);
    }

    /// <summary>Apply a dedicated left-column slot to the diagnostic HUD. Width is capped to
    /// leave a meaningful work column on the right; height is clipped above the current chrome.
    /// The HUD's inner ScrollViewer keeps diagnostics accessible when the available band shrinks.</summary>
    internal static void ApplyDebugHudSlot(DebugHudElement hud)
    {
        EnsureScreenInitialized();
        double availableWidth = Math.Max(1, _screen.Width - 48);
        double maxWidth = Math.Min(430, Math.Min(availableWidth, Math.Max(160, _screen.Width * .38)));
        hud.MinWidth = Math.Min(360, maxWidth);
        hud.MaxWidth = maxWidth;
        // Slot height is measured from the highest legal HUD position (top + 8) to the
        // chrome boundary (minus 8). If the toolbar/palette rises, the HUD viewport shrinks too.
        hud.MaxHeight = Math.Max(1, _chromeTop - _screen.Top - 16);
        hud.ScreenLeft = _screen.Left + 24;
        hud.UpdateLayout();
        double hudHeight = hud.ActualHeight > 1 ? hud.ActualHeight : hud.DesiredSize.Height;
        if (!double.IsFinite(hudHeight) || hudHeight < 1) hudHeight = Math.Min(160, hud.MaxHeight);
        double latestTop = Math.Max(_screen.Top + 8, _chromeTop - hudHeight - 8);
        hud.ScreenTop = Math.Clamp(hud.ScreenTop, _screen.Top + 8, latestTop);
    }

    private static void ApplyDebugHudSlotToVisibleHud()
    {
        var hud = StageHost.Stage?.EnumerateStageElements().OfType<DebugHudElement>()
            .FirstOrDefault(e => e.IsVisible);
        if (hud is not null) ApplyDebugHudSlot(hud);
    }

    /// <summary>Top of the shared work area. The ink-layer status badge, when enabled in debug
    /// mode, owns the top-right system band; normal floating panels must start below that band.</summary>
    private static double PopupWorkspaceTop => _screen.Top +
        (AppConfig.Current.DebugMode && AppConfig.Current.InkStatusBadgeEnabled ? 96 : 8);

    /// <summary>Enabling debug chrome changes the reserved top-right band. Evict the one current
    /// work panel before the badge/HUD appears, so a panel opened in normal mode cannot remain
    /// beneath the newly enabled diagnostics UI.</summary>
    internal static void PrepareForDebugModeChange(bool enabling)
    {
        if (!enabling) return;
        PrepareForDebugBadgeChange();
    }

    /// <summary>Reserve the debug badge lane before turning it visible. A panel or older toast
    /// from normal mode must not survive underneath the newly enabled badge.</summary>
    internal static void PrepareForDebugBadgeChange()
    {
        if (_activePopover is { } panel)
            panel.HideForExclusiveSlotReplacement(_embeddedChromePanel);
        Toast.DismissForExclusivePanel();
    }

    private static Rect ReflowActivePopover()
    {
        // The toolbar palette grows within the permanent central chrome lane; it reserves this
        // lease but its geometry is managed by Toolbar.Palette, not moved as a floating element.
        if (_embeddedChromePanel) return Rect.Empty;
        if (_activePopover is not { } owner || _popoverHeight <= 0)
            return Rect.Empty;

        double xMin = PopupLeftLimit();
        double xMax = _screen.Right - 8;
        double maxW = xMax - xMin;
        double workspaceTop = PopupWorkspaceTop;
        double maxH = _popupBottom - workspaceTop - _popoverGap - 8;
        // 不把面板缩成不可用的残片，也不让它压住 HUD/chrome。没有完整可用区域就拒绝显示。
        if (maxW < 80 || maxH < 64 || _popoverWidth > maxW || _popoverHeight > maxH)
        {
            _activePopover = null;
            _popoverWidth = _popoverHeight = 0;
            owner.CancelAutoDismiss();
            owner.Visibility = Visibility.Collapsed;
            QueueToastFlush();
            return Rect.Empty;
        }

        double w = _popoverWidth;
        double h = _popoverHeight;
        double wantedX = _popoverAnchor.Left + _popoverAnchor.Width / 2 - w / 2;
        double x = Math.Clamp(wantedX, xMin, xMax - w);
        double y = Math.Clamp(_popupBottom - _popoverGap - h, workspaceTop, _screen.Bottom - h - 8);

        owner.Width = w;
        owner.Height = h;
        owner.MaxHeight = h;
        owner.ClipToBounds = true;
        owner.ScreenLeft = x;
        owner.ScreenTop = y;
        return new Rect(x, y, w, h);
    }

    internal static void ReleasePopover(StageElement owner)
    {
        if (!ReferenceEquals(_activePopover, owner)) return;
        _activePopover = null;
        _embeddedChromePanel = false;
        _popoverWidth = _popoverHeight = 0;
        _popupBottom = _chromeTop - 12;
        QueueToastFlush();
    }

    /// <summary>
    /// The embedded brush palette uses the same single-owner popup lease as every floating panel.
    /// It stays inside the toolbar's central lane; it does not get a separate Z-order exception.
    /// </summary>
    internal static bool TryAcquireEmbeddedChromePanel(Toolbar owner)
    {
        EnsureScreenInitialized();
        if (_activeModal is not null) return false;
        if (ReferenceEquals(_activePopover, owner) && _embeddedChromePanel) return true;

        Toast.DismissForExclusivePanel();
        if (_activePopover is { } previous && !ReferenceEquals(previous, owner))
            previous.HideForExclusiveSlotReplacement(_embeddedChromePanel);

        _activePopover = owner;
        _embeddedChromePanel = true;
        _popoverWidth = _popoverHeight = 0;
        return true;
    }

    /// <summary>Maximum vertical growth available to the toolbar palette without entering the
    /// reserved segment row above the bar or crossing the fixed diagnostics-badge band.</summary>
    internal static double AvailableChromeExpansion(Toolbar owner)
    {
        EnsureScreenInitialized();
        if (!ReferenceEquals(_toolbar, owner) || _activeModal is not null) return 0;
        double reservedSegmentHeight = 0;
        if (_segment is { } segment)
        {
            segment.Measure(new Size(Math.Max(1, _laneRight - _laneLeft), _screen.Height));
            segment.UpdateLayout();
            reservedSegmentHeight = segment.ActualHeight > 1 ? segment.ActualHeight : segment.DesiredSize.Height;
            if (!double.IsFinite(reservedSegmentHeight) || reservedSegmentHeight < 1) reservedSegmentHeight = 64;
            reservedSegmentHeight += 12;
        }
        return Math.Max(0, owner.ScreenTop - PopupWorkspaceTop - reservedSegmentHeight);
    }

    /// <summary>Called synchronously whenever the toolbar's outer height changes. The segment row
    /// moves before WPF renders the next frame, keeping both rows disjoint during palette animation.</summary>
    internal static void ChromeGeometryChanged()
    {
        if (_toolbar is null) return;
        if (_segment is { } segment)
        {
            segment.Measure(new Size(Math.Max(1, _laneRight - _laneLeft), _screen.Height));
            double sh = segment.ActualHeight > 1 ? segment.ActualHeight : segment.DesiredSize.Height;
            if (!double.IsFinite(sh) || sh < 1) sh = 64;
            segment.SetChromeSlot(_laneLeft, _laneRight, _toolbar.ScreenTop - sh - 12);
        }
        double top = _toolbar.ScreenTop;
        if (_segment is not null) top = Math.Min(top, _segment.ScreenTop);
        _chromeTop = Math.Clamp(top, _screen.Top + 8, _dockBottom);
        _popupBottom = _chromeTop - 12;
        ApplyDebugHudSlotToVisibleHud();
        ReflowActivePopover();
    }

    internal static void ScheduleToastFlush()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Toast.FlushDeferredIfAvailable));
    }

    private static void QueueToastFlush() => ScheduleToastFlush();

    /// <summary>
    /// Toast has a fixed top-right notification lane. If the debug HUD is visible, the lane's
    /// left edge is statically placed to the right of the HUD's reserved column; narrow screens
    /// simply defer notifications instead of testing intersections or drawing over another item.
    /// </summary>
    internal static bool TryGetNotificationPosition(double width, double height, out Point position)
    {
        EnsureScreenInitialized();
        position = new Point();
        if (HasExclusivePanel || HasActiveModal || !double.IsFinite(width) || !double.IsFinite(height))
            return false;

        double leftLimit = _screen.Left + 8;
        var stage = StageHost.Stage;
        var hud = stage?.EnumerateStageElements().OfType<DebugHudElement>().FirstOrDefault(e => e.IsVisible);
        if (hud is not null)
        {
            hud.UpdateLayout();
            double hudWidth = hud.ActualWidth > 1 ? hud.ActualWidth : hud.DesiredSize.Width;
            if (!double.IsFinite(hudWidth) || hudWidth < 1) hudWidth = Math.Min(430, _screen.Width * .42);
            // Fixed HUD column and top-right toast lane are disjoint by construction.
            leftLimit = Math.Max(leftLimit, hud.ScreenLeft + hudWidth + 12);
        }

        double rightLimit = _screen.Right - width - 8;
        if (rightLimit < leftLimit) return false;
        double x = Math.Clamp(_screen.Right - width - 24, leftLimit, rightLimit);
        double y = _screen.Top + (AppConfig.Current.DebugMode && AppConfig.Current.InkStatusBadgeEnabled ? 104 : 48);
        // Toast owns a fixed band below the optional debug badge and above ordinary panels/chrome.
        // Insufficient vertical room means
        // defer it; never search for a free spot by comparing live rectangles.
        if (width <= 0 || height <= 0 || y + height > _chromeTop - 8) return false;
        position = new Point(x, y);
        return true;
    }

    /// <summary>DragKit 输入阶段的硬约束。元素被限制在自己所属槽位，拖动本身无法进入别人的区域。</summary>
    internal static Point ConstrainDrag(StageElement owner, Point requested)
    {
        EnsureScreenInitialized();
        double w = Math.Max(owner.ActualWidth, owner.DesiredSize.Width);
        double h = Math.Max(owner.ActualHeight, owner.DesiredSize.Height);
        if (!double.IsFinite(w) || w < 1) w = Math.Max(owner.Width, 80);
        if (!double.IsFinite(h) || h < 1) h = Math.Max(owner.Height, 48);

        if (owner is Toolbar)
        {
            double leftMax = Math.Max(_laneLeft, _laneRight - w);
            return new Point(Math.Clamp(requested.X, _laneLeft, leftMax), _dockBottom - h);
        }
        if (owner is SegmentIsland segment)
            return segment.ConstrainToChromeSlot(requested, w, h);

        if (owner is DebugHudElement)
        {
            // HUD 占左侧专属列：允许上下移动，不允许横跨到工作面板列。
            double fixedLeft = _screen.Left + 24;
            double maxTop = Math.Max(_screen.Top + 8, _chromeTop - h - 8);
            return new Point(fixedLeft, Math.Clamp(requested.Y, _screen.Top + 8, maxTop));
        }

        if (ReferenceEquals(owner, _activePopover))
        {
            double left = PopupLeftLimit();
            double right = Math.Max(left, _screen.Right - w - 8);
            double top = PopupWorkspaceTop;
            double bottomTop = Math.Max(top, _popupBottom - _popoverGap - h);
            return new Point(Math.Clamp(requested.X, left, right), Math.Clamp(requested.Y, top, bottomTop));
        }

        // 其他可移动 UI 若没有占到槽位，禁止拖动到有效区域外；可见普通面板都会在 Show 时先申请槽位。
        double safeRight = Math.Max(_screen.Left + 8, _screen.Right - w - 8);
        double safeBottom = Math.Max(_screen.Top + 8, _chromeTop - h - 8);
        return new Point(Math.Clamp(requested.X, _screen.Left + 8, safeRight),
                         Math.Clamp(requested.Y, _screen.Top + 8, safeBottom));
    }

    /// <summary>显式模态界面（聚光灯）显示前，先撤下普通 UI 和提示，避免模态层与浮岛并行。</summary>
    internal static bool EnterModal(StageElement modal, bool preserveChromeUnderHole = false)
    {
        if (_activeModal is { } active && !ReferenceEquals(active, modal)) return false;
        if (ReferenceEquals(_activeModal, modal)) return true;

        if (_activePopover is { } panel && !ReferenceEquals(panel, modal))
            panel.HideForExclusiveSlotReplacement(_embeddedChromePanel);
        Toast.DismissForExclusivePanel();
        _activeModal = modal;
        _embeddedChromePanel = false;
        ModalPreviousVisibility.Clear();
        var stage = StageHost.Stage;
        if (stage is not null)
            {
                foreach (var element in stage.EnumerateStageElements())
                {
                    if (ReferenceEquals(element, modal) || !element.IsVisible) continue;
                    // Onboarding is a hole-punch modal: permanent chrome must stay available
                    // inside the holes, while every transient panel/HUD/toast is dismissed.
                    if (preserveChromeUnderHole &&
                        element is Toolbar or PageTurnIsland or SegmentIsland) continue;
                    ModalPreviousVisibility[element] = element.Visibility;
                    element.Visibility = Visibility.Collapsed;
                }
        }
        return true;
    }

    internal static void ExitModal(StageElement modal)
    {
        if (!ReferenceEquals(_activeModal, modal)) return;
        _activeModal = null;
        foreach (var pair in ModalPreviousVisibility.ToArray())
            {
                if (pair.Key.Stage is null) continue;
                if (pair.Key is DebugHudElement)
                {
                    // The persisted toggle is authoritative if the user changed DebugMode while
                    // the modal was active; don't resurrect a HUD after it was turned off.
                    if (AppConfig.Current.DebugMode && pair.Value == Visibility.Visible)
                        pair.Key.Visibility = Visibility.Visible;
                    else
                        pair.Key.Visibility = Visibility.Collapsed;
                    continue;
                }
                if (pair.Value == Visibility.Visible)
                    pair.Key.Visibility = Visibility.Visible;
        }
        ModalPreviousVisibility.Clear();
        if (AppConfig.Current.DebugMode && StageHost.Stage?.EnumerateStageElements()
                    .OfType<DebugHudElement>().FirstOrDefault() is { IsVisible: false } hud)
            hud.ShowPanel();
        QueueToastFlush();
    }


}
