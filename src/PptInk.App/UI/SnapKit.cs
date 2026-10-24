using System.Windows;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 吸附引擎（v0.7.0 灵动岛体系）：窗口松手后飞向停靠位的统一数学与动画。
///  - 主工具栏 / 副岛（Toolbar 语义）：中央区 → 顶/底停靠带 + 水平居中
///    （主命令条的「回家」位置）；左右 22% 边区 → 贴侧边（给自由度）。
///  - 翻页岛（Corner 语义）：就近四角吸附。
///  动画统一走 AnimationKit.SpringTo（弹簧轻回弹）；坐标全部钳制放映屏内。
/// </summary>
public static class SnapKit
{
    /// <summary>停靠边距（DIP）。</summary>
    private const double Edge = 16;

    /// <summary>底部避让：尊重系统手势区（希沃底部滑动条）时多让 32px。</summary>
    private static double BottomMargin => AppConfig.Current.RespectGestureZone ? Edge + 32 : Edge;

    /// <summary>
    /// 主工具栏 / 副岛吸附位：
    ///  - 水平中央 56% 区域 → 水平居中 + 垂直吸附最近停靠带（上带 / 下带）；
    ///  - 左右 22% 边区 → 贴对应侧边（垂直位置保持，仅钳制屏内）。
    /// </summary>
    public static (double Left, double Top) ResolveToolbar(Window w)
        => ResolveToolbarImpl(w.Left, w.Top, w.ActualWidth, w.ActualHeight);

    /// <summary>舞台元素版（v0.9.0 单窗口架构）：ScreenLeft/ScreenTop 与旧 Window.Left/Top 同语义，
    /// 吸附数学零迁移。</summary>
    public static (double Left, double Top) ResolveToolbar(StageElement el)
        => ResolveToolbarImpl(el.ScreenLeft, el.ScreenTop, el.ActualWidth, el.ActualHeight);

    private static (double Left, double Top) ResolveToolbarImpl(double hostLeft, double hostTop, double actualW, double actualH)
    {
        var wa = ShowScreen.Bounds;
        double width = Math.Max(actualW, 52), height = Math.Max(actualH, 52);
        double cx = hostLeft + width / 2;

        // 左右边区：贴侧（垂直保持）
        if (cx < wa.Left + wa.Width * 0.22)
            return (wa.Left + Edge, ClampTop(wa, hostTop, height));
        if (cx > wa.Right - wa.Width * 0.22)
            return (wa.Right - width - Edge, ClampTop(wa, hostTop, height));

        // 中央区：水平居中 + 顶/底停靠带（宿主中心在屏高上 30% 内 → 顶带，否则底带）
        double left = wa.Left + (wa.Width - width) / 2;
        double cy = hostTop + height / 2;
        double top = cy < wa.Top + wa.Height * 0.30
            ? wa.Top + Edge
            : wa.Bottom - height - BottomMargin;
        return (left, ClampTop(wa, top, height));
    }

    /// <summary>翻页岛吸附位：就近四角（中心距最小者）。</summary>
    public static (double Left, double Top) ResolveCorner(Window w)
        => ResolveCornerImpl(w.Left, w.Top, w.ActualWidth, w.ActualHeight);

    /// <summary>舞台元素版（v0.9.0 单窗口架构）。</summary>
    public static (double Left, double Top) ResolveCorner(StageElement el)
        => ResolveCornerImpl(el.ScreenLeft, el.ScreenTop, el.ActualWidth, el.ActualHeight);

    private static (double Left, double Top) ResolveCornerImpl(double hostLeft, double hostTop, double actualW, double actualH)
    {
        var wa = ShowScreen.Bounds;
        double width = Math.Max(actualW, 52), height = Math.Max(actualH, 52);
        double cx = hostLeft + width / 2, cy = hostTop + height / 2;

        (double L, double T)[] corners =
        {
            (wa.Left + Edge, wa.Top + Edge),
            (wa.Right - width - Edge, wa.Top + Edge),
            (wa.Left + Edge, wa.Bottom - height - BottomMargin),
            (wa.Right - width - Edge, wa.Bottom - height - BottomMargin),
        };
        var best = corners.OrderBy(c => Dist(cx, cy, c.L + width / 2, c.T + height / 2)).First();
        return (best.L, best.T);

        static double Dist(double x1, double y1, double x2, double y2)
            => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }

    /// <summary>弹簧飞向停靠位（300ms ElasticEase 轻托回弹）；完成回调（宿主落盘）。</summary>
    public static void GlideTo(Window w, double left, double top, Action? completed = null)
        => AnimationKit.SpringTo(w, left, top, 300, completed);

    /// <summary>舞台元素版（v0.9.0 单窗口架构，动画走 ScreenLeft/ScreenTop DP）。</summary>
    public static void GlideTo(StageElement el, double left, double top, Action? completed = null)
        => AnimationKit.SpringTo(el, left, top, 300, completed);

    /// <summary>垂直钳制（顶/底不出屏）。</summary>
    private static double ClampTop(Rect wa, double top, double height)
        => Math.Clamp(top, wa.Top + 2, Math.Max(wa.Top + 2, wa.Bottom - height - 2));
}
