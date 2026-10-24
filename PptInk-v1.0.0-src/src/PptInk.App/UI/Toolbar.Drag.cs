using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>工具栏 · 拖动与收纳：手柄拖动三源定源（OnGripDown·Move·Up / SourceOf / PositionOf）/ 停靠位置恢复与屏幕钳制（RestoreDockPosition · ClampToScreen）/ 胶囊收纳三态（CollapseToCapsule · ExpandFromCapsule · SnapCapsuleToNearestEdge）/ 唤起与收回动画（ShowAnimated · HideAnimated）。（v0.9.4 由 1119 行单文件按职责拆分，纯搬运，零逻辑改动）</summary>
public sealed partial class Toolbar
{
    // ———————— 停靠与拖动 ————————

    /// <summary>恢复到专属底部中央槽。旧版任意屏幕坐标仅迁移 X 偏好，Y 不再自由漂移。</summary>
    public void RestoreDockPosition()
    {
        var wa = ShowScreen.Bounds;
        _rootRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredW = Math.Max(340, _rootRow.DesiredSize.Width + 18);
        double laneL = _dockLaneConfigured ? _dockLaneLeft : wa.Left + 240;
        double laneR = _dockLaneConfigured ? _dockLaneRight : wa.Right - 240;
        if (laneR <= laneL) { laneL = wa.Left + 8; laneR = wa.Right - 8; }
        double maxW = Math.Max(1, laneR - laneL);
        MaxWidth = maxW;
        _toolScroller.MaxWidth = maxW;
        double w = Math.Min(desiredW, maxW);
        double savedX = _svc.Config.ToolbarDockX ?? double.NaN;
        double savedY = _svc.Config.ToolbarDockY;
        bool staleTopLeft = double.IsFinite(savedX) && double.IsFinite(savedY) &&
                            Math.Abs(savedX - wa.Left) < 12 && Math.Abs(savedY - wa.Top) < 12;
        // 迁移旧配置：过去 Canvas 的 NaN/0 坐标会把元素留在左上角，不能再把这组坏位当偏好恢复。
        bool hasValidSavedDock = double.IsFinite(savedX) && double.IsFinite(savedY) && !staleTopLeft;
        double preferred = hasValidSavedDock ? savedX : wa.Left + (wa.Width - w) / 2;
        Left = Math.Clamp(preferred, laneL, Math.Max(laneL, laneR - w));
        UpdateLayout();
        Top = DockBottom - Math.Max(ActualHeight, Math.Max(_rootRow.DesiredSize.Height + 14, 52));
        _svc.Config.ToolbarDockX = Left;
        _svc.Config.ToolbarDockY = Top;
        _svc.Config.Save();
    }

    /// <summary>由编排器分配的中央槽位：宽度被限制在左右翻页岛之间，底边固定。</summary>
    internal void SetDockLane(double laneLeft, double laneRight, double dockBottom)
    {
        var wa = ShowScreen.Bounds;
        double rightEdge = Math.Max(wa.Left, wa.Right - 8);
        _dockLaneLeft = Math.Clamp(laneLeft, wa.Left + 8, rightEdge);
        _dockLaneRight = Math.Clamp(laneRight, _dockLaneLeft, rightEdge);
        _dockBottom = Math.Clamp(dockBottom, wa.Top + 48, wa.Bottom - 8);
        _dockLaneConfigured = true;
        MaxWidth = Math.Max(1, _dockLaneRight - _dockLaneLeft);
        _toolScroller.MaxWidth = MaxWidth;
        UpdateLayout();
        ClampToScreen();
    }

    private void OnGripDown(object? sender, InputEventArgs e)
    {
        // 拖动柄所在区域始终消费输入，不能继续路由成画布书写。
        e.Handled = true;
        if (_gripSource != 0) return; // 同一触点的 Stylus/Touch/Mouse 提升事件
        if (IsPaletteOpen) HidePalette(immediate: true);
        _gripSource = SourceOf(e);
        CaptureGesture(e, _gripVisual, out _gripTouchCapture, out _gripStylusCapture, out _gripMouseCapture);
        _holdOrigin = PositionOf(e);             // 固定舞台坐标
        _holdWindowOrigin = new Point(Left, Top);
        _dragging = false;
        _holdTimer.Start();
        AnimationKit.GlideCancel(this);          // 抓取时释放吸附动画对位置的持有
        if (_gripVisual is not null)
            _gripVisual.SetResourceReference(Border.BackgroundProperty, "ItemSelectedBrush");
    }

    private void OnGripMove(object? sender, InputEventArgs e)
    {
        e.Handled = true;
        if (_gripSource == 0 || SourceOf(e) != _gripSource) return;
        bool pressed = _gripSource == 1
            ? e is MouseEventArgs m && m.LeftButton == MouseButtonState.Pressed
            : true;
        if (!pressed) return;
        var p = PositionOf(e);
        double dx = p.X - _holdOrigin.X, dy = p.Y - _holdOrigin.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 6)
        {
            _dragging = true;
            _holdTimer.Stop();
        }
        if (_dragging)
        {
            // 用“按下时位置 + 当前绝对增量”，不累计总增量，也不读正在移动的本地坐标。
            Left = _holdWindowOrigin.X + dx;
            Top = _holdWindowOrigin.Y + dy;
            ClampToScreen();
        }
    }

    private void OnGripUp(object? sender, InputEventArgs e)
    {
        e.Handled = true;
        if (_gripSource == 0 || SourceOf(e) != _gripSource) return;
        _holdTimer.Stop();
        bool wasDragging = _dragging;
        _dragging = false;
        _gripSource = 0; // 先清会话所有权，释放 Capture 引发 LostCapture 时不重入取消逻辑
        ReleaseGestureCapture(_gripVisual, ref _gripTouchCapture, ref _gripStylusCapture, ref _gripMouseCapture);
        if (_gripVisual is not null) _gripVisual.Background = Brushes.Transparent;
        if (wasDragging)
        {
            // 手柄拖动只改变中央槽位内的横向位置；Y 轴固定在专属底部行，
            // 因此操作轨迹本身就不可能把工具栏拖到翻页岛或其他面板上。
            ClampToScreen();
            _svc.Config.ToolbarDockX = Left;
            _svc.Config.ToolbarDockY = Top;
            _svc.Config.Save();
            NotifyGeometryChanged();
        }
    }

    /// <summary>捕获当前物理接触，确保手指滑出柄区后 Move/Up 仍回到同一手势，而非落到墨迹层。</summary>
    private static void CaptureGesture(InputEventArgs e, UIElement? target, out TouchDevice? touch, out StylusDevice? stylus, out bool mouse)
    {
        touch = null; stylus = null; mouse = false;
        if (target is null) return;
        try
        {
            switch (e)
            {
                case TouchEventArgs t:
                    if (t.TouchDevice.Capture(target, CaptureMode.Element)) touch = t.TouchDevice;
                    break;
                case StylusEventArgs s:
                    if (s.StylusDevice.Capture(target, CaptureMode.Element)) stylus = s.StylusDevice;
                    break;
                case MouseButtonEventArgs:
                    mouse = Mouse.Capture(target, CaptureMode.Element);
                    break;
            }
        }
        catch { /* 设备在同帧切换/系统取消接触时，仍由 Up 与失焦恢复路径释放会话 */ }
    }

    private static void ReleaseGestureCapture(UIElement? target, ref TouchDevice? touch, ref StylusDevice? stylus, ref bool mouse)
    {
        // 先清引用再调用 Capture(null)：释放会同步触发 LostCapture，必须避免重入递归。
        var oldTouch = touch;
        var oldStylus = stylus;
        bool oldMouse = mouse;
        touch = null; stylus = null; mouse = false;
        try { if (oldTouch is not null) oldTouch.Capture(null); } catch { }
        try { if (oldStylus is not null) oldStylus.Capture(null); } catch { }
        try { if (oldMouse && ReferenceEquals(Mouse.Captured, target)) Mouse.Capture(null); } catch { }
    }

    private void CancelGripGestureIfCaptureLost()
    {
        if (_gripSource == 0) return;
        _holdTimer.Stop();
        _gripSource = 0;
        _dragging = false;
        if (_gripVisual is not null) _gripVisual.Background = Brushes.Transparent;
        ReleaseGestureCapture(_gripVisual, ref _gripTouchCapture, ref _gripStylusCapture, ref _gripMouseCapture);
    }

    private void OnGripMouseCaptureLost(object? sender, MouseEventArgs e)
    {
        if (_gripMouseCapture) CancelGripGestureIfCaptureLost();
    }

    private void OnGripTouchCaptureLost(object? sender, TouchEventArgs e)
    {
        if (ReferenceEquals(_gripTouchCapture, e.TouchDevice)) CancelGripGestureIfCaptureLost();
    }

    private void OnGripStylusCaptureLost(object? sender, StylusEventArgs e)
    {
        if (ReferenceEquals(_gripStylusCapture, e.StylusDevice)) CancelGripGestureIfCaptureLost();
    }

    private void CancelCapsuleGestureIfCaptureLost()
    {
        if (_capSource == 0) return;
        _capSource = 0;
        _capDownTick = 0;
        _capDragging = false;
        ReleaseGestureCapture(_capsuleRoot, ref _capTouchCapture, ref _capStylusCapture, ref _capMouseCapture);
    }

    private void OnCapsuleMouseCaptureLost(object? sender, MouseEventArgs e)
    {
        if (_capMouseCapture) CancelCapsuleGestureIfCaptureLost();
    }

    private void OnCapsuleTouchCaptureLost(object? sender, TouchEventArgs e)
    {
        if (ReferenceEquals(_capTouchCapture, e.TouchDevice)) CancelCapsuleGestureIfCaptureLost();
    }

    private void OnCapsuleStylusCaptureLost(object? sender, StylusEventArgs e)
    {
        if (ReferenceEquals(_capStylusCapture, e.StylusDevice)) CancelCapsuleGestureIfCaptureLost();
    }

    /// <summary>输入源归类：1=鼠标，2=触摸，3=手写笔；不把不同设备的 Up/Move 混成同一会话。</summary>
    private static int SourceOf(InputEventArgs e) => e switch
    {
        MouseEventArgs => 1,
        TouchEventArgs => 2,
        StylusEventArgs => 3,
        _ => 0,
    };

    /// <summary>跨源取指针在元素内的位置。</summary>
    private Point PositionOf(InputEventArgs e)
    {
        // Stage and Toolbar are different concrete types; coalesce through their shared
        // WPF input-element interface instead of the invalid Stage ?? this expression.
        IInputElement coordinateRoot = (IInputElement?)Stage ?? this;
        return e switch
        {
            MouseButtonEventArgs m => m.GetPosition(coordinateRoot),
            MouseEventArgs m => m.GetPosition(coordinateRoot),
            TouchEventArgs t => t.GetTouchPoint(coordinateRoot).Position,
            StylusEventArgs s => s.GetStylusPoints(coordinateRoot) is { Count: > 0 } pts ? pts[0].ToPoint() : new Point(),
            _ => new Point(),
        };
    }

    private void ClampToScreen()
    {
        var wa = ShowScreen.Bounds;
        double laneL = _dockLaneConfigured ? _dockLaneLeft : wa.Left + 240;
        double laneR = _dockLaneConfigured ? _dockLaneRight : wa.Right - 240;
        if (laneR < laneL) laneR = laneL;
        double w = Math.Max(ActualWidth, _capsuleMode ? 52 : Math.Max(1, _rootRow.DesiredSize.Width));
        double desiredHeight = Math.Max(_rootRow.DesiredSize.Height + 14, 52);
        double h = Math.Max(ActualHeight, _capsuleMode ? 52 : desiredHeight);
        double maxLeft = Math.Max(laneL, laneR - w);
        Left = Math.Clamp(Left, laneL, maxLeft);
        Top = DockBottom - h;
        _svc.Config.ToolbarDockX = Left;
        _svc.Config.ToolbarDockY = Top;
    }

    // ———————— 胶囊收纳（v0.2.0：收纳在屏幕上，不进托盘） ————————

    private bool _capsuleMode;
    private bool _capDragging;
    private Point _capOrigin;
    private Point _capDownScreen;
    private long _capDownTick;
    private int _capSource;                    // 胶囊定源：0=空闲、1=鼠标、2=触摸、3=手写笔
    private TouchDevice? _capTouchCapture;
    private StylusDevice? _capStylusCapture;
    private bool _capMouseCapture;

    /// <summary>当前是否处于胶囊收纳态。</summary>
    public bool IsCapsuleMode => _capsuleMode;

    /// <summary>收缩为屏幕胶囊：以工具栏中心为视觉锚点切形态，再动画滑向最近边缘吸附。</summary>
    public void CollapseToCapsule()
    {
        if (_capsuleMode) return;
        if (IsPaletteOpen) HidePalette(immediate: true);
        _capsuleMode = true;
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        _panelRoot.Visibility = Visibility.Collapsed;
        _capsuleRoot.Visibility = Visibility.Visible;
        UpdateLayout();
        Left = cx - ActualWidth / 2;
        Top = cy - ActualHeight / 2;
        SnapCapsuleToNearestEdge(animated: true);
        Minimized?.Invoke();
    }

    /// <summary>从胶囊展开回完整工具栏：以球心为锚展开并钳制屏内。</summary>
    public void ExpandFromCapsule()
    {
        if (!_capsuleMode) return;
        _capsuleMode = false;
        _capDragging = false;
        _capDownTick = 0;
        _capSource = 0;
        double bx = Left + ActualWidth / 2, by = Top + ActualHeight / 2;
        _capsuleRoot.Visibility = Visibility.Collapsed;
        _panelRoot.Visibility = Visibility.Visible;
        UpdateLayout();
        Left = bx - ActualWidth / 2;
        Top = by - ActualHeight / 2;
        ClampToScreen();
        AnimationKit.PopOpen(_panelRoot);
        NotifyGeometryChanged();
    }

    /// <summary>胶囊吸附最近屏幕边缘（左/右/底三选一，贴边留 4px）。</summary>
    private void SnapCapsuleToNearestEdge(bool animated)
    {
        var wa = ShowScreen.Bounds;
        double w = Math.Max(ActualWidth, 52), h = Math.Max(ActualHeight, 52);
        double leftMin = _dockLaneConfigured ? _dockLaneLeft : wa.Left + 240;
        double leftMax = _dockLaneConfigured ? _dockLaneRight - w : wa.Right - 240 - w;
        if (leftMax < leftMin) { leftMin = wa.Left + 8; leftMax = Math.Max(leftMin, wa.Right - w - 8); }
        double center = Left + w / 2;
        double targetLeft = Math.Abs(center - (leftMin + w / 2)) <= Math.Abs(center - (leftMax + w / 2))
            ? leftMin : leftMax;
        double targetTop = DockBottom - h;

        if (!animated)
        {
            Left = targetLeft;
            Top = targetTop;
            NotifyGeometryChanged();
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animL = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        animL.Completed += (_, _) =>
        {
            _svc.Config.ToolbarDockX = Left;
            _svc.Config.ToolbarDockY = Top;
            _svc.Config.Save();
            NotifyGeometryChanged();
        };
        BeginAnimation(ScreenLeftProperty, animL);
        BeginAnimation(ScreenTopProperty, new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void OnCapsuleDown(object? sender, InputEventArgs e)
    {
        e.Handled = true;
        if (_capSource != 0) return;
        _capSource = SourceOf(e);
        CaptureGesture(e, _capsuleRoot, out _capTouchCapture, out _capStylusCapture, out _capMouseCapture);
        _capOrigin = PositionOf(e);
        _capDownScreen = new Point(Left, Top);
        _capDownTick = Environment.TickCount64;
        _capDragging = false;
        AnimationKit.GlideCancel(this);   // 清吸附动画残留
        e.Handled = true;
    }

    private void OnCapsuleMove(object? sender, InputEventArgs e)
    {
        e.Handled = true;
        if (_capDownTick == 0) return;
        if (SourceOf(e) != _capSource) return;
        var p = PositionOf(e);
        if (!_capDragging && Math.Abs(p.X - _capOrigin.X) + Math.Abs(p.Y - _capOrigin.Y) > 8)
            _capDragging = true;
        if (_capDragging)
        {
            Left = _capDownScreen.X + p.X - _capOrigin.X;
            Top = _capDownScreen.Y + p.Y - _capOrigin.Y;
            ClampToScreen();
        }
    }

    private void OnCapsuleUp(object? sender, InputEventArgs e)
    {
        e.Handled = true;
        if (_capDownTick == 0) return;
        if (SourceOf(e) != _capSource) return;
        long held = Environment.TickCount64 - _capDownTick;
        bool wasDragging = _capDragging;
        _capDownTick = 0;
        _capDragging = false;
        _capSource = 0; // 正常释放前先复位会话标志，LostCapture 不得取消新状态
        ReleaseGestureCapture(_capsuleRoot, ref _capTouchCapture, ref _capStylusCapture, ref _capMouseCapture);
        if (!wasDragging && held < 450)
        {
            ExpandFromCapsule();
            return;
        }
        if (wasDragging) SnapCapsuleToNearestEdge(animated: true);
        e.Handled = true;
    }

    // ———————— 唤起/收回动画 ————————

    public void ShowAnimated(bool fromBottomEdge)
    {
        if (Visibility != Visibility.Visible)
        {
            Opacity = 0;
            _slide.Y = fromBottomEdge ? 24 : 8;
            Visibility = Visibility.Visible;
        }
        var fade = new DoubleAnimation(0, 1, (Duration)Application.Current.FindResource("ShowDuration")) { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseShow") };
        var rise = new DoubleAnimation(_slide.Y, 0, (Duration)Application.Current.FindResource("ShowDuration")) { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseShow") };
        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    public override void HideAnimated()
    {
        var fade = new DoubleAnimation(1, 0, (Duration)Application.Current.FindResource("HideDuration")) { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseHide") };
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
    }
}