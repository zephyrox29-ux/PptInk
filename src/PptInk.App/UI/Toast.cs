using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PptInk.Core;
using System.Collections.Generic;

namespace PptInk.UI;

/// <summary>
/// 全局轻提示（v0.9.0 单窗口架构：原独立 Topmost ToastWindow 降维为舞台 Plugin 层元素）。
/// 视觉不变：岱青底 + 圆角 14 + 阴影令牌同款漫射 + 140ms 吮吸进场 + 2.3s 停留 +
/// 320ms EaseIn 退场；位置按实际尺寸锚定放映屏右下内边距 24（长文本不溢出屏外）。
/// 内容动画与载体分离（双层 host）：淡出只作用于内容层，元素本体零闪烁。
/// 行为差异（有意）：
///  - 元素 IsHitTestVisible=false —— 同窗口内 Toast 不参与 WPF 命中路由（事件不落在
///    Toast 自身、不会误触）；跨窗口穿透仍由逐像素 alpha 规则决定：卡片可见像素
///    在停留 2.6s 内仍占位（与旧独立窗口一致，但占位范围从「整窗」收敛为「卡片本身」）；
///  - 停用态（S0/未放映）「借窗显示」：舞台是透明穿透窗（alpha=0 全穿透 +
///    TOOLWINDOW 无任务条目 + ShowActivated=false），Show 出来对桌面零干扰，
///    Toast 退场后还原隐藏 —— 设置中心在未放映时触发的操作反馈不再丢失
///    （原独立 ToastWindow 任何时刻可显示，单窗口下由借窗机制等价承接）。
/// </summary>
public static class Toast
{
    private static ToastElement? _current;
    private static readonly Queue<string> Deferred = new();

    /// <summary>当前显示中的 Toast（诊断可观测）。</summary>
    internal static ToastElement? Current => _current;

    /// <summary>显示轻提示（线程安全：封送 UI 线程；无舞台宿主时降级日志，绝不崩）。
    /// 新 Toast 顶掉旧 Toast（单例纪律与原版一致）。</summary>
    public static void Show(string message)
    {
        if (Application.Current?.Dispatcher is not { } d) return;
        d.Invoke(() =>
        {
            if (StageUiLayout.HasExclusivePanel || StageUiLayout.HasActiveModal)
            {
                EnqueueDeferred(message);
                return;
            }
            if (!StageUiLayout.TryGetNotificationPosition(EstimatedWidth(message), EstimatedHeight(message), out _))
            {
                EnqueueDeferred(message);
                return;
            }
            ShowNow(message);
        });
    }

    // CJK glyphs are close to font-size width; use a conservative estimate so the visible
    // notification never has to be moved after it enters the shared upper-right notification slot.
    private static double EstimatedWidth(string message) => Math.Clamp(message.Length * 14.0 + 44, 180, 560);
    private static double EstimatedHeight(string message)
    {
        double w = EstimatedWidth(message);
        int lines = Math.Max(1, (int)Math.Ceiling(message.Length * 14.0 / Math.Max(80, Math.Min(520, w - 36))));
        return Math.Clamp(lines * 20 + 24, 44, 124);
    }

    private static void ShowNow(string message)
    {
        var stage = StageHost.Stage;
        if (stage is null)
        {
            AppLog.Instance.Warn($"Toast 丢弃（舞台未注册）：{message}");
            return;
        }
        // 新 Toast 顶旧：同一时刻只有一个通知载体。
        _current?.DismissNow();
        bool borrowed = !stage.IsVisible;
        if (borrowed)
        {
            stage.SetPassthrough(true);
            stage.ShowSilent();
        }
        var toast = new ToastElement(message, stage, borrowed);
        _current = toast;
        toast.ShowToast();
    }

    private static void EnqueueDeferred(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        // 课堂提示限队列长度，保留最新通知，避免高频计时/状态消息累积。
        while (Deferred.Count >= 5) Deferred.Dequeue();
        Deferred.Enqueue(message);
    }

    internal static void DismissForExclusivePanel()
    {
        _current?.DismissNow();
        _current = null;
    }

    internal static void FlushDeferredIfAvailable()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || !d.CheckAccess())
        {
            d?.BeginInvoke(new Action(FlushDeferredIfAvailable));
            return;
        }
        if (_current is not null || Deferred.Count == 0 || StageUiLayout.HasExclusivePanel || StageUiLayout.HasActiveModal)
            return;
        var next = Deferred.Peek();
        if (!StageUiLayout.TryGetNotificationPosition(EstimatedWidth(next), EstimatedHeight(next), out _)) return;
        Deferred.Dequeue();
        ShowNow(next);
    }

    internal static void DeferAfterFailedPlacement(string message) => EnqueueDeferred(message);

    /// <summary>Toast 退场收口（元素回调）：清静态引用，并在下一轮 UI 调度中尝试下一条提示。</summary>
    internal static void Retire(ToastElement el, bool scheduleDeferredFlush = true)
    {
        if (ReferenceEquals(_current, el)) _current = null;
        if (scheduleDeferredFlush) StageUiLayout.ScheduleToastFlush();
    }
}

/// <summary>
/// 轻提示元素（<see cref="Toast"/> 静态入口的载体；internal —— 外部一律走 Toast.Show）。
/// 尺寸由内容撑开（Canvas 内 Auto 尺寸 = 原 SizeToContent.WidthAndHeight 等价），
/// Loaded 后按 ActualWidth/Height 精调右下锚点。
/// </summary>
internal sealed class ToastElement : StageElement
{
    private readonly Grid _host = new() { Opacity = 0 };   // 动画宿主：与卡片阴影互不覆盖
    private readonly UnifiedStageWindow _stage;
    private readonly bool _borrowed;   // 停用态借窗显示（自然退场后需还原隐藏）
    private readonly string _message;

    public ToastElement(string message, UnifiedStageWindow stage, bool borrowed)
    {
        _stage = stage;
        _borrowed = borrowed;
        _message = message;
        IsHitTestVisible = false;   // 纯展示：不参与命中（触摸直穿，不挡板书/翻页/插件面板）

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x20, 0x1E)),   // 岱青（PanelBrush 口径）
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 10, 18, 10),
            Effect = new DropShadowEffect
            { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.28 },      // ShadowChrome 同款
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xEE, 0xF2, 0xF0)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
        };
        _host.Children.Add(card);
        Content = _host;

        // 先按估计位落点（可见前必须有位置，否则 Canvas 定位闪跳到 (0,0)），Loaded 后按实际尺寸精调
        var est = ShowScreen.Bounds;
        ScreenLeft = est.Right - 380;
        ScreenTop = est.Bottom - 96;

        stage.AddToLayer(StageLayer.Plugin, this, 0, 0);

        // 定位 + 进退场动画：必须等首次布局完成（元素 Loaded 早于 layout，此时 ActualWidth 仍为 0，
        // 直接按 Loaded 精调会把 Toast 算到屏幕外 —— 用 SizeChanged 首次拿到真实尺寸为准，
        // Loaded 作兜底（布局若已完成的极端时序））
        Loaded += (_, _) => PositionAndAnimate();
        SizeChanged += (_, _) => PositionAndAnimate();
    }

    private bool _positioned;

    /// <summary>首次获得真实尺寸时精调右下锚点并启动进/退场动画（幂等：只执行一次）。</summary>
    private void PositionAndAnimate()
    {
        if (_positioned) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;   // 布局未完成：等 SizeChanged
        _positioned = true;
        if (!StageUiLayout.TryGetNotificationPosition(ActualWidth, ActualHeight, out var position))
        {
            Retire(restoreBorrowed: true, scheduleDeferredFlush: false);
            Toast.DeferAfterFailedPlacement(_message);
            return;
        }
        ScreenLeft = position.X;
        ScreenTop = position.Y;
        Visibility = Visibility.Visible; // first visible frame already has a valid exclusive slot
        AnimationKit.PopOpen(_host, 140);   // 快速吮吸进场（克制：Toast 是配角）
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = TimeSpan.FromMilliseconds(2300),   // 课堂节奏：稍长停留更稳
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Retire(restoreBorrowed: true);   // 自然退场：借窗还原
        _host.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>先以 Hidden 参与 WPF 测量；只有取得通知槽的准确位置后才改为 Visible，避免闪过左上/右下旧坐标。</summary>
    public void ShowToast() => Visibility = Visibility.Hidden;

    /// <summary>新 Toast 顶掉旧 Toast：立即卸载，不还原借窗（窗接着给新 Toast 用）。</summary>
    public void DismissNow() => Retire(restoreBorrowed: false, scheduleDeferredFlush: false);

    /// <summary>退场收口：清静态引用 + 卸载退订 + 借窗还原。</summary>
    private void Retire(bool restoreBorrowed, bool scheduleDeferredFlush = true)
    {
        Toast.Retire(this, scheduleDeferredFlush);
        Close();   // 基类：从舞台卸载 + 退订 ThemeChanged/LocationChanged（短命元素防泄漏）
        // 借窗还原：仅自然退场路径（DismissNow 顶替时窗还在给新 Toast 用）；
        // 退场前若已进入放映激活态则不还原（S0→S1 转换期间显示的 Toast 不吞掉舞台）。
        if (restoreBorrowed && _borrowed && !_stage.StageActive)
            _stage.HideSilent();
    }
}
