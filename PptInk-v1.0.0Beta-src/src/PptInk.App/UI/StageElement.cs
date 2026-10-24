using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PptInk.UI.Theme;

namespace PptInk.UI;

/// <summary>
/// 舞台元素基类（v0.9.0 单窗口架构）：原浮岛/工具栏独立 Topmost 窗口的公共能力
/// 全部由本类承担 —— 各「岛」降维为 UnifiedStageWindow 分层 Canvas 的子元素。
///  - 坐标：ScreenLeft/ScreenTop = 屏幕 DIP（与旧 Window.Left/Top 完全同语义，
///    位置记忆/钳制/吸附数学零迁移）；变更自动换算 Canvas 定位（屏幕 DIP − 舞台
///    窗口原点），舞台 LocationChanged（多屏迁移）自动重同步；
///  - 锚点弹出 / 自动退场 / 吮吸显隐（原 IslandWindow 能力，语义不变）；
///  - 主题：BindTheme 闭包登记 + ThemeChanged 重放。不注册 DWM 亚克力 ——
///    AccentPolicy 是整窗特效，挂舞台会连墨迹层一起模糊（多窗口时代各岛窗口
///    各自挂的安全性在单窗口下不成立），Acrylic 材质由面板前景色表达降级观感；
///  - 禁系统长按右击；永不抢焦点（Focusable=false，元素无窗口焦点语义）；
///  - Close()：从舞台卸载 + 释放静态事件订阅（原窗口 Closed 退订的等价物，
///    短命元素（规整气泡）每次 new 的泄漏防线）。
/// 命中语义（逐像素穿透环境）：元素面板像素 alpha&gt;0 → OS 级命中可点；
/// 面板外无背景像素 → 点击直达 PPT —— 与旧独立透明窗口行为逐像素等价。
/// </summary>
public class StageElement : ContentControl
{
    private UnifiedStageWindow? _stage;
    private StageLayer _ownLayer;
    private DispatcherTimer? _autoDismiss;
    private readonly List<Action> _themeRefreshers = new();
    private bool _borrowedStage;   // 停用态借窗显示标记（HideBorrowedAware 还原用）

    public StageElement()
    {
        Focusable = false;
        // 「创建但不显示」语义（原独立窗口 new 不 Show 即隐）：元素默认 Visible，
        // 预创建的工具栏/翻页岛会闪现 —— 显式 Collapsed，ShowAnimated/ShowNearAnchor 才上屏
        Visibility = Visibility.Collapsed;
        // 禁触摸长按右击（press-and-hold）：系统右击转换会吞掉触摸 up、打断
        // 长按手势判定（与旧浮岛窗口同款防御，元素级附加属性同样生效）
        Stylus.SetIsPressAndHoldEnabled(this, false);
        ThemeManager.ThemeChanged += OnThemeChanged;
        IsVisibleChanged += OnStageElementVisibilityChanged;
    }

    // ———————— 坐标（屏幕 DIP 语义，位置记忆零迁移） ————————

    /// <summary>屏幕 DIP X（与旧 Window.Left 同语义；动画可驱动，变更自动同步 Canvas 定位）。</summary>
    public static readonly DependencyProperty ScreenLeftProperty = DependencyProperty.Register(
        nameof(ScreenLeft), typeof(double), typeof(StageElement),
        new FrameworkPropertyMetadata(0.0, OnScreenPositionChanged));

    /// <summary>屏幕 DIP Y（与旧 Window.Top 同语义）。</summary>
    public static readonly DependencyProperty ScreenTopProperty = DependencyProperty.Register(
        nameof(ScreenTop), typeof(double), typeof(StageElement),
        new FrameworkPropertyMetadata(0.0, OnScreenPositionChanged));

    public double ScreenLeft
    {
        get => (double)GetValue(ScreenLeftProperty);
        set => SetValue(ScreenLeftProperty, value);
    }

    public double ScreenTop
    {
        get => (double)GetValue(ScreenTopProperty);
        set => SetValue(ScreenTopProperty, value);
    }

    private static void OnScreenPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StageElement)d).SyncCanvasPosition();

    /// <summary>舞台接线（AddToLayer 自动调用）：记层 + 订舞台移动 + 立即同步一次。</summary>
    internal StageLayer OwnLayer => _ownLayer;

    internal void AttachStage(UnifiedStageWindow stage, StageLayer layer)
    {
        if (_stage == stage) return;
        _stage = stage;
        _ownLayer = layer;
        stage.LocationChanged += OnStageLocationChanged;
        Loaded += OnElementLoaded;
        SyncCanvasPosition();
        stage.NotifyStageElementsChanged();
    }

    private void OnStageElementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible)
        {
            StageUiLayout.ReleasePopover(this);
            StageUiLayout.ExitModal(this);
        }
        _stage?.NotifyStageElementsChanged();
        StageUiLayout.ScheduleToastFlush();
    }

    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        // The stage may have had NaN Left/Top while its children were pre-created.
        // Loaded is the first point where WPF's final window coordinates are reliable.
        SyncCanvasPosition();
    }

    /// <summary>舞台窗口移动（多屏迁移后 RelocateToScreen）→ 元素重同步（屏幕记忆位换算新 Canvas 偏移）。</summary>
    private void OnStageLocationChanged(object? sender, EventArgs e) => SyncCanvasPosition();

    /// <summary>屏幕 DIP → 舞台本地 Canvas 坐标换算（舞台 Left/Top 即其屏幕 DIP 原点，
    /// 同一虚拟屏幕坐标系内严格等价变换）。</summary>
    internal void SyncCanvasPosition()
    {
        if (_stage is null) return;
        // WPF Window.Left/Top can be NaN before the first Show. Canvas interprets a NaN
        // attached coordinate as an unset value (0,0), which stacks every pre-created
        // toolbar/island in the screen's upper-left corner. Never propagate non-finite
        // coordinates; UnifiedStageWindow resyncs all children again after Show/Loaded.
        double stageLeft = double.IsFinite(_stage.Left) ? _stage.Left : 0.0;
        double stageTop = double.IsFinite(_stage.Top) ? _stage.Top : 0.0;
        double x = double.IsFinite(ScreenLeft) ? ScreenLeft - stageLeft : 0.0;
        double y = double.IsFinite(ScreenTop) ? ScreenTop - stageTop : 0.0;
        Canvas.SetLeft(this, double.IsFinite(x) ? x : 0.0);
        Canvas.SetTop(this, double.IsFinite(y) ? y : 0.0);
    }

    // ———————— 闲置淡出（v0.9.6：闲置 2~3 秒半透明，互动即凝实） ————————

    private DispatcherTimer? _idleTimer;
    private double _idleOpacity = 0.45;
    private double _idleSeconds = 2.5;
    private bool _idleFadeEnabled;

    /// <summary>
    /// 配置闲置淡出。触摸优先默认关闭；开启后无交互指定秒数才淡化，任何设备的 Down/Move 都会立即恢复。
    /// 关闭时清掉淡化动画并恢复不透明状态，不能留下半透明或被旧定时器重新淡化。
    /// </summary>
    public void SetIdleFade(bool enabled, double seconds = 2.5, double idleOpacity = 0.45)
    {
        _idleFadeEnabled = enabled;
        _idleSeconds = Math.Clamp(seconds, 0.5, 30.0);
        _idleOpacity = Math.Clamp(idleOpacity, 0.2, 1.0);
        if (!enabled)
        {
            _idleTimer?.Stop();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
            return;
        }

        if (_idleTimer is null)
        {
            _idleTimer = new DispatcherTimer();
            _idleTimer.Tick += (_, _) =>
            {
                _idleTimer!.Stop();
                if (_idleFadeEnabled) FadeTo(_idleOpacity);
            };
            PreviewMouseDown += (_, _) => WakeUp();
            PreviewMouseMove += (_, _) => WakeUp();
            PreviewTouchDown += (_, _) => WakeUp();
            PreviewTouchMove += (_, _) => WakeUp();
            PreviewStylusDown += (_, _) => WakeUp();
            PreviewStylusMove += (_, _) => WakeUp();
        }
        _idleTimer.Interval = TimeSpan.FromSeconds(_idleSeconds);
        WakeUp();
    }

    /// <summary>兼容旧调用点；新代码应由设置中心显式配置 SetIdleFade。</summary>
    public void EnableIdleFade(double seconds = 2.5, double idleOpacity = 0.45)
        => SetIdleFade(true, seconds, idleOpacity);

    private void WakeUp()
    {
        if (!_idleFadeEnabled) return;
        FadeTo(1.0);
        _idleTimer?.Stop();
        if (_idleTimer is not null)
        {
            _idleTimer.Interval = TimeSpan.FromSeconds(_idleSeconds);
            _idleTimer.Start();
        }
    }

    private void FadeTo(double target)
    {
        if (Math.Abs(Opacity - target) < 0.01) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, target, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>所属舞台（未挂载时 null）。</summary>
    public UnifiedStageWindow? Stage => _stage;

    /// <summary>从舞台卸载 + 释放静态订阅（原窗口 Close 的元素等价物；移除 StageElement 一律走此口，
    /// 否则 ThemeChanged 静态订阅泄漏）。</summary>
    public void Close()
    {
        StageUiLayout.ReleasePopover(this);
        StageUiLayout.ExitModal(this);
        if (_stage is { } s)
        {
            s.LocationChanged -= OnStageLocationChanged;
            s.RemoveFromLayer(_ownLayer, this);
        }
        _stage = null;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        CancelAutoDismiss();
        _idleTimer?.Stop();
    }

    // ———————— 显隐（v0.9.0 借窗纪律：插件族/HUD 等常驻面板共用） ————————

    /// <summary>元素上屏：停用态（舞台不可见）先把舞台作为透明穿透窗 Show 出来
    ///（alpha=0 全穿透 + TOOLWINDOW 无任务条目 + ShowActivated=false，对桌面零干扰），
    /// 再显示元素 —— S0 设置中心触发的操作（调试 HUD / 插件面板）不再丢失。</summary>
    protected void ShowBorrowedAware()
    {
        if (Stage is { } st && !st.IsVisible)
        {
            st.SetPassthrough(true);   // 借窗前提：穿透态（逐像素=底刷 alpha=0；整窗=穿透位）
            st.ShowSilent();
            _borrowedStage = true;
        }
        if (!IsVisible) Visibility = Visibility.Visible;
    }

    /// <summary>元素下屏（借窗还原：显示期间已进入放映激活态则不还原，不吞舞台）。</summary>
    protected void HideBorrowedAware()
    {
        Visibility = Visibility.Collapsed;
        if (_borrowedStage && Stage is { } st && !st.StageActive)
        {
            st.HideSilent();
            _borrowedStage = false;
        }
    }

    /// <summary>
    /// Slot owner replacement is a normal hide operation, not a Z-order cover. The toolbar is
    /// special: when its embedded palette owns the shared popup slot, hide only the palette and
    /// keep the permanent toolbar visible in its own lane.
    /// </summary>
    internal void HideForExclusiveSlotReplacement(bool preserveChromeHost)
    {
        CancelAutoDismiss();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (preserveChromeHost && this is Toolbar toolbar)
        {
            toolbar.HidePalette(immediate: true);
            return;
        }
        HideBorrowedAware();
    }


    /// <summary>
    /// 普通工具面板必须先获得全局唯一的悬浮槽，之后才能显示或参与拖动。
    /// 没有完整空间时返回 false，调用方可提示用户，而不是把面板挤进其他控件下面。
    /// </summary>
    protected bool ShowInExclusivePanelSlot(double preferredWidth, double preferredHeight)
    {
        var slot = StageUiLayout.AllocateUtilityPanel(this, preferredWidth, preferredHeight);
        if (slot.IsEmpty) return false;
        Width = slot.Width;
        Height = slot.Height;
        MaxHeight = slot.Height;
        ScreenLeft = slot.Left;
        ScreenTop = slot.Top;
        ShowBorrowedAware();
        return true;
    }

    // ———————— 主题（BindTheme 闭包登记，热切换重放） ————————

    /// <summary>登记主题刷新闭包（立即执行一次；ThemeManager 变更时重放）。</summary>
    protected void BindTheme(Action refresh)
    {
        refresh();
        _themeRefreshers.Add(refresh);
    }

    private void OnThemeChanged()
    {
        foreach (var refresh in _themeRefreshers.ToArray()) refresh();
    }

    // ———————— 锚点弹出（即时选择型浮岛） ————————

    /// <summary>
    /// 在锚点矩形（触发按钮）旁弹出，优先上方。元素尺寸需先由内容确定（Measure）。
    /// 配合 AutoDismissIn 使用；选择后调用 HideAnimated。
    /// 进场 = AnimationKit.PopOpen 吮吸放出（与收回 PopClose 对称同款）。
    /// </summary>
    public bool ShowNearAnchor(Rect anchor, double panelW, double panelH, double gap = 14)
    {
        // 所有锚定浮岛共用唯一的弹出槽。槽位由 StageUiLayout 分配，位于工具栏/副栏
        // 预留区上方；不再各自自由定位，因此不会盖住拖动柄、翻页栏或其他浮岛。
        var slot = StageUiLayout.AllocatePopover(this, anchor, panelW, panelH, gap);
        if (slot.IsEmpty) return false; // 没有足够的专属槽位时不显示，绝不压到工具栏上
        Width = slot.Width;
        Height = slot.Height;
        ScreenLeft = slot.Left;
        ScreenTop = slot.Top;
        if (!IsVisible) { Opacity = 0; Visibility = Visibility.Visible; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (Content is UIElement panel) AnimationKit.PopOpen(panel);   // 内容吮吸放出（模糊→聚焦 + 微放大）
        return true;
    }

    /// <summary>无操作自动退场（弹出型浮岛的礼貌性：绝不赖屏）。</summary>
    public void AutoDismissIn(double seconds)
    {
        _autoDismiss?.Stop();
        _autoDismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _autoDismiss.Tick += (_, _) => { _autoDismiss.Stop(); HideAnimated(); };
        _autoDismiss.Start();
    }

    public void CancelAutoDismiss() => _autoDismiss?.Stop();

    // ———————— 屏幕矩形换算（引导定位/擦除锚点链路共用） ————————

    /// <summary>元素 → 屏幕 DIP 矩形（PointToScreen 为物理像素，除以 DPI 得 DIP）。
    /// v0.9.0 由 ToolbarWindow 迁入（元素在舞台上，PresentationSource 即舞台 hwnd，同屏等价）。</summary>
    internal static Rect ScreenRectOf(UIElement el)
    {
        var pt = el.PointToScreen(new Point(0, 0));
        var size = el.RenderSize;
        double dpi = 1.0;
        if (PresentationSource.FromVisual(el) is { CompositionTarget: { } ct })
            dpi = ct.TransformToDevice.M11;
        return new Rect(pt.X / dpi, pt.Y / dpi, size.Width / dpi, size.Height / dpi);
    }

    /// <summary>元素 → 屏幕物理像素矩形（不除 DPI）。
    /// v0.8.0 引导定位专用：目标矩形以物理像素传递，引导覆盖窗口用【自身】DPI
    /// 统一换算为本地 DIP —— 混合 DPI 多屏部署（教师屏 100% + 大屏 150%）下，
    /// ScreenRectOf 的「除以元素所在屏 DPI」在跨屏目标时会错位，物理像素 +
    /// 单一换算源是唯一不会错的传递方式。</summary>
    internal static Rect ScreenRectOfPhysical(UIElement el)
    {
        var pt = el.PointToScreen(new Point(0, 0));
        var size = el.RenderSize;
        double dpi = 1.0;
        if (PresentationSource.FromVisual(el) is { CompositionTarget: { } ct })
            dpi = ct.TransformToDevice.M11;
        return new Rect(pt.X, pt.Y, size.Width * dpi, size.Height * dpi);
    }

    // ———————— 公共 ————————

    /// <summary>
    /// 吮吸收回（175ms EaseIn 缩小+失焦+淡出 → 隐藏）：与弹出 PopOpen 对称同款，
    /// 快、高效、不视觉割裂。子类可 override 挂接还原逻辑。
    /// </summary>
    public virtual void HideAnimated()
    {
        CancelAutoDismiss();
        AnimationKit.PopClose(this, 175, () =>
        {
            Visibility = Visibility.Collapsed;
            StageUiLayout.ReleasePopover(this);
        });
    }

    /// <summary>
    /// 唤起（与旧翻页岛窗口同款动画语言）：淡入 ShowDuration/EaseShow。
    /// 子类有专属进场（副岛 PopOpen 内容 / 工具栏上浮）时 override。
    /// </summary>
    public virtual void ShowAnimated()
    {
        if (Visibility != Visibility.Visible)
        {
            Opacity = 0;
            Visibility = Visibility.Visible;
        }
        var fade = new DoubleAnimation(0, 1, (Duration)Application.Current.FindResource("ShowDuration"))
        { EasingFunction = (IEasingFunction)Application.Current.FindResource("EaseShow") };
        BeginAnimation(OpacityProperty, fade);
    }
}
