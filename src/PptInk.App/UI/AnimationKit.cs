using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 统一动画工具：全应用一致的时长 / 缓动 / 组合模式（设计语言不割裂的执行层）。
///  - 时长档位：Fast 160ms（反馈）/ Normal 220ms（进出场）/ Slow 320ms（内容呈现）
///  - 缓动：出场一律 EaseOut（内容"到达"即稳），收场一律 EaseIn（快速让位）
///  - 页面切换优先即时替换内容，再做轻量 Opacity/Transform 过渡；不使用昂贵的实时模糊效果
///  - 交互动画只改合成属性，减少布局重排；真正的输入与点击回调不等待动画结束
/// </summary>
public static class AnimationKit
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(160));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(220));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(320));

    private static readonly IEasingFunction EaseOutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInCubic = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseOutQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
    private static bool ReducedMotion => AppConfig.Current.ReduceMotion;

    // An element may be closed and reopened before its previous animation completes. A
    // per-element generation prevents stale Completed handlers from hiding a newer session.
    private sealed class AnimationStamp { public long Value; }
    private static readonly ConditionalWeakTable<UIElement, AnimationStamp> AnimationStamps = new();

    private static long BeginSession(UIElement element)
    {
        var stamp = AnimationStamps.GetValue(element, static _ => new AnimationStamp());
        return ++stamp.Value;
    }

    private static bool IsCurrentSession(UIElement element, long session) =>
        AnimationStamps.TryGetValue(element, out var stamp) && stamp.Value == session;

    /// <summary>淡入（默认 Normal 档）。减少动效时立即到达终态，避免动画回调成为交互锁。</summary>
    public static void FadeIn(UIElement el, int ms = 220)
    {
        BeginSession(el);
        if (ReducedMotion)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            return;
        }
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(ms)) { EasingFunction = EaseOutCubic });
    }

    /// <summary>淡出，可选完成回调（用于 Hide）。</summary>
    public static void FadeOut(UIElement el, int ms = 160, Action? completed = null)
    {
        long session = BeginSession(el);
        if (ReducedMotion)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            completed?.Invoke();
            return;
        }
        var a = new DoubleAnimation(1, 0, Ms(ms)) { EasingFunction = EaseInCubic };
        if (completed is not null) a.Completed += (_, _) => { if (IsCurrentSession(el, session)) completed(); };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>上浮入场：Y 偏移 + 淡入（工具栏/卡片/浮岛展开内容的统一进出场）。</summary>
    public static void RiseIn(UIElement el, double fromY = 18, int ms = 280)
    {
        BeginSession(el);
        if (ReducedMotion)
        {
            var reduced = EnsureTranslate(el);
            reduced.BeginAnimation(TranslateTransform.YProperty, null);
            reduced.Y = 0;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            return;
        }
        var slide = EnsureTranslate(el);
        var rise = new DoubleAnimation(fromY, 0, Ms(ms)) { EasingFunction = EaseOutQuint };
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(Math.Min(ms, 220))) { EasingFunction = EaseOutQuint });
    }

    /// <summary>轻微缩放入场（0.96 → 1，浮岛面板/弹层）。</summary>
    public static void ScaleIn(UIElement el, int ms = 240)
    {
        BeginSession(el);
        if (ReducedMotion)
        {
            var reduced = EnsureScale(el);
            reduced.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            reduced.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            reduced.ScaleX = reduced.ScaleY = 1;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            return;
        }
        var s = EnsureScale(el);
        var a = new DoubleAnimation(0.96, 1.0, Ms(ms)) { EasingFunction = EaseOutQuint };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(180)) { EasingFunction = EaseOutCubic });
    }

    /// <summary>
    /// 页面切换：先同步替换内容，再用短淡入/上浮确认切换。不会等待旧页动画完成，
    /// 因此连续轻触导航时最后一次选择总是优先；不使用实时模糊着色器。
    /// </summary>
    public static void BlurSwitch(FrameworkElement host, Action swap)
    {
        BeginSession(host);
        // Replace the page immediately. Waiting for a fade-out before swapping makes rapid
        // touch navigation feel delayed and allows stale Completed callbacks to reorder pages.
        // Cancel every prior transition first, then use a short opacity/translation transition.
        host.BeginAnimation(UIElement.OpacityProperty, null);
        host.Effect = null;
        var translate = EnsureTranslate(host);
        translate.BeginAnimation(TranslateTransform.YProperty, null);

        if (ReducedMotion)
        {
            swap();
            host.Opacity = 1;
            translate.Y = 0;
            return;
        }

        swap();
        host.Opacity = 0;
        translate.Y = 6;
        host.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, Ms(135)) { EasingFunction = EaseOutCubic });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, Ms(155)) { EasingFunction = EaseOutQuint });
    }

    /// <summary>命中反馈：轻缩放脉冲（按压确认感，触屏尤为需要）。</summary>
    public static void PressPulse(UIElement el)
    {
        // Scale-only feedback must not invalidate an opacity transition's completion callback.
        if (ReducedMotion) return;
        var s = EnsureScale(el);
        var a = new DoubleAnimation(1.0, 0.94, Ms(70)) { EasingFunction = EaseOutCubic, AutoReverse = true };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ———————— 浮岛吮吸（弹出/收回对称同款，第四轮体验打磨） ————————

    /// <summary>
    /// 弹层轻量放出：0.94→1.0 微放大 + 淡入。弹层不使用实时模糊，优先保证触摸与 4K 渲染响应。
    /// </summary>
    public static void PopOpen(UIElement el, int ms = 160)
    {
        BeginSession(el);
        // No animated BlurEffect: shader effects are costly on 4K interactive overlays and
        // stale blur completion callbacks can clear the effect of a newer open animation.
        el.Effect = null;
        var s = EnsureScale(el);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);

        if (ReducedMotion)
        {
            s.ScaleX = s.ScaleY = 1;
            el.Opacity = 1;
            return;
        }

        var scale = new DoubleAnimation(0.94, 1.0, Ms(Math.Clamp(ms, 100, 220)))
        { EasingFunction = EaseOutQuint };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, Ms(Math.Clamp(ms, 100, 180))) { EasingFunction = EaseOutCubic });
    }

    /// <summary>轻量收回：短淡出 + 微缩放；完成后在同帧归位并执行隐藏/布局回调。</summary>
    public static void PopClose(UIElement el, int ms = 125, Action? completed = null)
    {
        long session = BeginSession(el);
        el.Effect = null;
        var s = EnsureScale(el);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);

        if (ReducedMotion)
        {
            s.ScaleX = s.ScaleY = 1;
            el.Opacity = 1;
            completed?.Invoke();
            return;
        }

        var duration = Ms(Math.Clamp(ms, 85, 180));
        var scale = new DoubleAnimation(1.0, 0.96, duration) { EasingFunction = EaseInCubic };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = EaseInCubic };
        fade.Completed += (_, _) =>
        {
            // Opacity alone is not a reliable session test: a reopened fade starts near zero.
            if (!IsCurrentSession(el, session)) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            s.ScaleX = s.ScaleY = 1;
            completed?.Invoke();
        };
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ———————— 窗口弹簧吸附（v0.7.0 灵动岛体系） ————————

    /// <summary>
    /// 窗口位置弹簧动画（v0.8.0 收敛：Springiness 8→10、320→300ms）：
    /// ElasticEase 轻过冲一次回弹到位——「轻托一下」的物理感而非「弹跳」
    /// （过冲幅度更小、节奏更快一点，松手到停稳的等待更短）。
    /// 动画走 Left/Top 依赖属性（窗口移动由 Win32 合成层承担，内容零重绘）。
    /// </summary>
    public static void SpringTo(Window w, double left, double top, int ms = 300, Action? completed = null)
    {
        GlideCancel(w);
        if (ReducedMotion)
        {
            w.Left = left;
            w.Top = top;
            completed?.Invoke();
            return;
        }
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 10 };
        var ax = new DoubleAnimation(w.Left, left, Ms(ms)) { EasingFunction = ease };
        var ay = new DoubleAnimation(w.Top, top, Ms(ms)) { EasingFunction = ease };
        if (completed is not null) ax.Completed += (_, _) => completed();
        w.BeginAnimation(Window.LeftProperty, ax);
        w.BeginAnimation(Window.TopProperty, ay);
    }

    /// <summary>取消窗口位置动画（拖动抓取前必调：动画值持有 Left/Top，不清除会「拖不动」）。</summary>
    public static void GlideCancel(Window w)
    {
        w.BeginAnimation(Window.LeftProperty, null);
        w.BeginAnimation(Window.TopProperty, null);
    }

    /// <summary>
    /// 舞台元素位置弹簧动画（v0.9.0 单窗口架构，窗口版同款数学）：目标属性是
    /// StageElement.ScreenLeft/ScreenTop（DP 回调逐帧同步 Canvas 定位 —— Canvas
    /// 子元素定位是 O(1) 布局，代价可控）。动画持有 DP 与旧 Window.Left 同纪律：
    /// 宿主抓取拖动前必须 GlideCancel。
    /// </summary>
    public static void SpringTo(StageElement el, double left, double top, int ms = 300, Action? completed = null)
    {
        GlideCancel(el);
        if (ReducedMotion)
        {
            el.ScreenLeft = left;
            el.ScreenTop = top;
            completed?.Invoke();
            return;
        }
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 10 };
        var ax = new DoubleAnimation(el.ScreenLeft, left, Ms(ms)) { EasingFunction = ease };
        var ay = new DoubleAnimation(el.ScreenTop, top, Ms(ms)) { EasingFunction = ease };
        if (completed is not null) ax.Completed += (_, _) => completed();
        el.BeginAnimation(StageElement.ScreenLeftProperty, ax);
        el.BeginAnimation(StageElement.ScreenTopProperty, ay);
    }

    /// <summary>取消舞台元素位置动画（拖动抓取前必调，与窗口版同纪律）。</summary>
    public static void GlideCancel(StageElement el)
    {
        el.BeginAnimation(StageElement.ScreenLeftProperty, null);
        el.BeginAnimation(StageElement.ScreenTopProperty, null);
    }

    // ———————— RenderTransform 管理（复用已有变换，不覆盖布局变换） ————————

    /// <summary>取/建 平移+缩放 变换组：两者共存于 TransformGroup，
    /// 避免 EnsureTranslate 与 EnsureScale 互相覆盖 RenderTransform（RiseIn 后再 ScaleIn 会丢平移）。</summary>
    private static (TranslateTransform T, ScaleTransform S) EnsureTransformGroup(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count >= 2 &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (t, s);
        s = new ScaleTransform(1, 1);
        t = new TranslateTransform();
        var ng = new TransformGroup();
        ng.Children.Add(s);
        ng.Children.Add(t);
        el.RenderTransform = ng;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (t, s);
    }

    private static TranslateTransform EnsureTranslate(UIElement el) => EnsureTransformGroup(el).T;

    private static ScaleTransform EnsureScale(UIElement el) => EnsureTransformGroup(el).S;

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
}
