using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>统一舞台窗口 · 墨迹层渲染：RenderInk 合成管线（5+2 层）/ 诊断绘制 / 状态牌 / 笔画绘制原语与合批。（v0.9.2 拆分，纯搬运）</summary>
public sealed partial class UnifiedStageWindow
{
    // ———————— 渲染合成（InkCanvasHost.OnRender 委托入口） ————————

    private int _renderInvalidateQueued;
    private Pen? _cachedGuideRenderPen;
    private double _cachedGuideRenderPenScale = double.NaN;
    private Pen? _cachedCursorRenderPen;
    private double _cachedCursorRenderPenScale = double.NaN;

    private Pen GetGuideRenderPen(double scaleX)
    {
        if (_cachedGuideRenderPen is null || _cachedGuideRenderPenScale != scaleX)
        {
            var pen = new Pen(_guidePen.Brush, _guidePen.Thickness / scaleX) { DashStyle = _guidePen.DashStyle };
            pen.Freeze();
            _cachedGuideRenderPen = pen;
            _cachedGuideRenderPenScale = scaleX;
        }
        return _cachedGuideRenderPen;
    }

    private Pen GetCursorRenderPen(double scaleX)
    {
        if (_cachedCursorRenderPen is null || _cachedCursorRenderPenScale != scaleX)
        {
            var pen = new Pen(Brushes.White, 1.5 / scaleX);
            pen.Freeze();
            _cachedCursorRenderPen = pen;
            _cachedCursorRenderPenScale = scaleX;
        }
        return _cachedCursorRenderPen;
    }

    // Frozen resources shared by every frame. The render path can run at display refresh rate;
    // allocating brushes/pens/gradient stops on every frame adds avoidable Gen0 pressure.
    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashStyle is not null) pen.DashStyle = dashStyle;
        pen.Freeze();
        return pen;
    }

    private static readonly SolidColorBrush FeedbackBrush = FrozenBrush(Colors.White);
    private static readonly ConcurrentDictionary<int, Pen> FeedbackPenCache = new();
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> ParticleBrushCache = new();
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> StrokeBrushCache = new();
    private static readonly ConcurrentDictionary<(uint Argb, int WidthEighth), Pen> StrokePenCache = new();
    private static readonly SolidColorBrush DiagnosticFrameBrush = FrozenBrush(Color.FromArgb(0xC0, 0x40, 0xA0, 0xFF));
    private static readonly Pen DiagnosticFramePen = FrozenPen(DiagnosticFrameBrush, 2, DashStyles.Dash);
    private static readonly Pen DiagnosticCornerPen = FrozenPen(DiagnosticFrameBrush, 3);
    private static readonly SolidColorBrush DiagnosticBadgeBackground = FrozenBrush(Color.FromArgb(0xB4, 0x14, 0x18, 0x20));
    private static readonly SolidColorBrush DiagnosticBadgeForeground = FrozenBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush StatusBadgeBackground = FrozenBrush(Color.FromArgb(0xB4, 0x14, 0x18, 0x20));
    private static readonly SolidColorBrush StatusBadgeForeground = FrozenBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush StatusBadgeEdge = FrozenBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
    private static readonly Pen StatusBadgeEdgePen = FrozenPen(StatusBadgeEdge, 1);
    private static readonly SolidColorBrush LiveWidthBrush = FrozenBrush(Color.FromArgb(0xC0, 0x58, 0xB8, 0xFF));
    private static readonly Pen LiveWidthPen = FrozenPen(LiveWidthBrush, 1);
    private static readonly SolidColorBrush LivePointBrush = FrozenBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ContactAcceptedBrush = FrozenBrush(Color.FromArgb(0xE0, 0x40, 0xFF, 0x80));
    private static readonly SolidColorBrush ContactRejectedBrush = FrozenBrush(Color.FromArgb(0xE0, 0xFF, 0x50, 0x50));
    private static readonly Pen ContactAcceptedPen = FrozenPen(ContactAcceptedBrush, 1.5);
    private static readonly Pen ContactRejectedPen = FrozenPen(ContactRejectedBrush, 1.5);
    private static readonly LinearGradientBrush SweepGradientBrush = CreateSweepGradientBrush();

    private static LinearGradientBrush CreateSweepGradientBrush()
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1));
        brush.Freeze();
        return brush;
    }

    private static Pen GetFeedbackPen(double thickness)
    {
        // Quarter-DIP buckets avoid creating subtly different pens on every monitor scale.
        int key = Math.Clamp((int)Math.Round(thickness * 4), 1, 256);
        return FeedbackPenCache.GetOrAdd(key, k => FrozenPen(FeedbackBrush, k / 4.0));
    }

    private static SolidColorBrush GetParticleBrush(Color color)
    {
        uint key = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return ParticleBrushCache.GetOrAdd(key, _ => FrozenBrush(Color.FromRgb(color.R, color.G, color.B)));
    }

    private static SolidColorBrush GetStrokeBrush(Color color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return StrokeBrushCache.GetOrAdd(key, _ => FrozenBrush(color));
    }

    private static Pen GetStrokePen(Brush brush, double thickness)
    {
        if (brush is not SolidColorBrush solid)
            return FrozenPen(brush, Math.Max(0.8, thickness));
        Color c = solid.Color;
        uint argb = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        // 1/8 model-pixel buckets retain the variable-width look without allocating a Pen for
        // every tiny speed change during every render pass.
        int widthEighth = Math.Clamp((int)Math.Round(Math.Max(0.8, thickness) * 8), 7, 4096);
        var key = (argb, widthEighth);
        return StrokePenCache.GetOrAdd(key, _ => FrozenStrokePen(GetStrokeBrush(c), widthEighth / 8.0));
    }

    private static Pen FrozenStrokePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            MiterLimit = 1.5,
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>渲染失效请求（线程安全、跨线程合并）：StylusPlugIn 在专用墨迹线程报点，
    /// WPF 视觉树只能由 UI Dispatcher 失效。旧实现每个点都独立排队，120/240Hz 输入
    /// 可能在 UI 忙碌时堆积大量过期重绘请求；现在每次最多保留一个 Render 优先级请求，
    /// 并在真正失效前释放门闩，让并发到达的最后一点仍能安排下一帧。</summary>
    public void Invalidate()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _renderInvalidateQueued, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
            {
                Interlocked.Exchange(ref _renderInvalidateQueued, 0);
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                _inkLayer.InvalidateVisual();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _renderInvalidateQueued, 0); // 窗口/Dispatcher 正在关闭
        }
    }

    public void PreWarmRender()
    {
        // 放映屏感知（S0 预热时尚无放映信息 → ShowScreen 默认主屏，S1 激活时再对位）
        RelocateToScreen();
        EnsurePageCache(); // 提前分配 4K 位图，首笔不卡
    }

    /// <summary>墨迹层渲染合成（5+2 层，锁序与输入侧一致：engine.Gate →
    /// eraser.RenderGate，防反序死锁）。层序：
    /// 层1 页面缓存位图（按物理像素创建，防「写完一笔变糊」）/ 层2 live 增量直绘 /
    /// 层2.5 落笔微反馈 / 层3 ghost 消散 / 层4 粒子 / 层4.5 清空扫光 / 层5 引导线 /
    /// 层6 诊断绘制（锁内）/ 层7 输入状态牌（锁外）。super 档画布隐藏时本方法
    /// 不被调用（元素 Visibility 链），墨迹随画布一并让位 —— 与原版「窗口隐藏」等价。</summary>
    private void RenderInk(DrawingContext dc)
    {
        InputDiagnostics.OnFrame();   // 渲染帧计数（HUD 差分算 FPS；关闭调试时开销≈一次自增）
        // 渲染尺寸以画布实测为准（布局后=窗口尺寸；预热期未布局回退放映屏口径）
        var sz = new Size(_inkLayer.ActualWidth, _inkLayer.ActualHeight);
        if (sz.Width > 0 && sz.Height > 0) _renderSize = sz;

        var engine = _svc.InkEngine;
        var eraser = _svc.Eraser;

        // 渲染锁（锁序与输入侧一致）：墨迹线程在引擎锁内写 Strokes/LiveStroke/粒子，
        // UI 渲染读 —— List 并发读写是未定义行为（枚举版本冲突抛异常 / 内部数组撕裂）。
        lock (engine.Gate)
        lock (eraser.RenderGate)
        {
        // —— 穿透态「隐藏板书」：跳过全部笔迹层绘制（页面缓存/live/反馈/ghost/粒子/扫光）。
        // 该像素 alpha=0 → 触摸穿透直达 PPT（板书密布时仍能点 PPT 可交互项）；
        // 数据无损（缓存与历史都在），切回书写态首帧即完整重现。
        // 状态牌/诊断/引导线不在此列 —— 穿透态下仍照常显示（排障与圈选提示不受影响）。
        if (!_hideInkWhilePassthrough)
        {
        // 层1：页面缓存（翻页/历史变更/擦除落定时重合成）
        if (_pageCache is null) EnsurePageCache();
        if (RecompositeNeeded(engine)) RecomposePageCache(engine);
        dc.DrawImage(_pageCache!, new Rect(0, 0, _renderSize.Width, _renderSize.Height));
        // Cache is in document pixels, not WPF DIPs. Everything below this push uses the same
        // canonical model coordinates as input, erase geometry, history and export.
        double modelScaleX = CanvasToDipScaleX;
        double modelScaleY = CanvasToDipScaleY;
        dc.PushTransform(CanvasToDipTransform());

        // 层2：live 增量笔迹（正在书写：直接画几何，绕过缓存，保证 60fps 跟手；速度笔锋逐段变宽）。
        // 单点 live（按下未移动/长按）同样绘制为圆点 —— 落笔即见墨、长按期间墨点常驻，
        // 不必等抬笔（点状判定与绘制在 DrawStroke 内统一处理）
        if (engine.LiveStroke is { } live && live.Points.Count > 0)
            DrawStroke(dc, live, engine);

        // 层2.5：落笔微反馈（180ms 扩张淡圈，克制的书写确认感）
        if (!_svc.Config.LightweightRendering && engine.PenDownFeedback is { } fb)
        {
            // v0.4.1：与 fb.TimestampTicks（InkClock QPC 毫秒）同源做差 ——
            // 原用 Environment.TickCount64 跨时钟基准，淡圈进度计算不可靠
            double t = (InkClock.NowMs() - fb.TimestampTicks) / 180.0;
            if (t is >= 0 and < 1)
            {
                double ease = 1 - Math.Pow(1 - t, 3);              // EaseOutCubic
                // Feedback is specified in DIPs for consistent perceived size across resolutions.
                double rDip = 8 + 18 * ease;
                double rX = rDip / Math.Max(0.01, modelScaleX);
                double rY = rDip / Math.Max(0.01, modelScaleY);
                byte a = (byte)(90 * (1 - t));
                dc.PushOpacity(a / 255.0);
                dc.DrawEllipse(null, GetFeedbackPen(1.5 / Math.Max(0.01, modelScaleX)),
                    new Point(fb.X, fb.Y), rX, rY);
                dc.Pop();
            }
            else if (t >= 1 && engine.LiveStroke is null)
                engine.PenDownFeedback = null;                     // 过期清理
        }

        // 层3：消散 ghost（淡出中的被擦笔迹；波浪延迟期内保持原样可见）
        foreach (var g in eraser.Ghosts)
        {
            if (g.Points.Count < 2) continue;
            dc.PushOpacity((1 - g.DissolveProgress) * (1 - g.DissolveProgress)); // 二次缓出
            DrawStroke(dc, g, engine);
            dc.Pop();
        }

        // 层4：粒子（随所属笔迹错峰启动，延迟期内不显示）
        if (!_svc.Config.LightweightRendering)
        {
            foreach (var p in eraser.Particles)
            {
                if (p.Delay > 0) continue;
                dc.PushOpacity(Math.Clamp(p.Life, 0, 1) * (160.0 / 255.0));
                dc.DrawEllipse(GetParticleBrush(p.C), null, p.Pos, p.Size * p.Life, p.Size * p.Life);
                dc.Pop();
            }
        }

        // 层4.5：清空扫光。轻量渲染下禁用该纯装饰层，避免在全屏 4K 画布上持续混合渐变。
        double sweep = _svc.Config.LightweightRendering ? double.NaN : eraser.SweepProgress;
        if (sweep is >= 0 and < 1)
        {
            // Sweep band width is visually 240 DIP on both 1080p and 4K screens.
            double bandW = 240 / Math.Max(0.01, modelScaleX);
            double canvasW = _svc.InkDocument.ScreenWidthPx;
            double canvasH = _svc.InkDocument.ScreenHeightPx;
            double x = -bandW + (canvasW + bandW) * sweep;
            double env = Math.Min(1.0, Math.Min(sweep / 0.15, (1 - sweep) / 0.15)); // 首尾柔入柔出
            dc.PushOpacity(env);
            dc.DrawRectangle(SweepGradientBrush, null, new Rect(x, 0, bandW, canvasH));
            dc.Pop();
        }

        dc.Pop(); // Back to viewport DIPs for UI diagnostics and status overlays.
        } // —— 穿透态隐藏板书：笔迹层绘制结束 ——

        // 层5：操作引导线（圈选虚线 / 抹除半径提示）；模型坐标，需要与输入/缓存同变换。
        dc.PushTransform(CanvasToDipTransform());
        if (engine.Tool == InkTool.EraseLasso && engine.LiveLasso.Count > 1)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(engine.LiveLasso[0], false, true);
                // Avoid allocating a fresh Skip(...).ToList() for every touch-move frame.
                for (int i = 1; i < engine.LiveLasso.Count; i++)
                    g.LineTo(engine.LiveLasso[i], true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, GetGuideRenderPen(Math.Max(0.01, CanvasToDipScaleX)), geo);
        }
        else if (engine.Tool == InkTool.EraseStroke && engine.LiveErasePath.Count > 0)
        {
            var last = engine.LiveErasePath[^1];
            double r = engine.Profile.MmToPx(engine.EraserRadiusMm);
            dc.DrawEllipse(null, GetCursorRenderPen(Math.Max(0.01, CanvasToDipScaleX)), last, r, r);
        }
        dc.Pop();

        // 层6：诊断绘制（v0.4.0 调试模式）：接触矩形实况（绿=接受/红=手掌拒绝）、
        // live 原始报点、当前笔宽示意 —— 策略各层「看得见」，现场定位不再靠猜
        if (Core.AppConfig.Current.DebugMode)
            DrawDiagnostics(dc, engine, CanvasToDipScaleX, CanvasToDipScaleY);

        // 层7：输入状态牌（v0.8.2 可观测性，独立于 DebugMode 常驻）：层态/指针/
        // 会话三行实况。读 InputDiagnostics（自带锁）与自身字段，不占 engine
        // 渲染临界区 —— 放锁外。
        DrawStatusBadge(dc);

        } // lock(eraser.RenderGate) / lock(engine.Gate)
    }

    /// <summary>诊断层绘制（调试模式开启时）。全程 try-catch：诊断异常绝不影响墨迹渲染。
    /// v0.4.1：判定颜色读结构化 Accepted（不再解析 Verdict 中文前缀）；Typeface 缓存
    ///（每帧构造有字体解析开销）。</summary>
    private static readonly Typeface DiagTypeface =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private void DrawDiagnostics(DrawingContext dc, InkEngine engine, double modelScaleX, double modelScaleY)
    {
        try
        {
            double k = DevicePerDip;

            // v0.6.0 可观测性核心：调试模式下墨迹层描边 + 状态徽标 —— 肉眼直接确认
            // 「透明层是否真实出现 / 盖住哪块区域 / 什么模式」。穿透只影响 hit-test
            // 不影响渲染 —— 穿透模式下徽标照常可见（「接收/穿透」一目了然）。
            var frameBrush = DiagnosticFrameBrush;
            var framePen = DiagnosticFramePen;
            dc.DrawRectangle(null, framePen, new Rect(1, 1, _renderSize.Width - 2, _renderSize.Height - 2));
            // 四角 L 形角标（20px），一眼锁定层边界
            double L = 20;
            dc.DrawLine(DiagnosticCornerPen, new Point(2, 2), new Point(2 + L, 2));
            dc.DrawLine(DiagnosticCornerPen, new Point(2, 2), new Point(2, 2 + L));
            dc.DrawLine(DiagnosticCornerPen, new Point(_renderSize.Width - 2, 2), new Point(_renderSize.Width - 2 - L, 2));
            dc.DrawLine(DiagnosticCornerPen, new Point(_renderSize.Width - 2, 2), new Point(_renderSize.Width - 2, 2 + L));
            dc.DrawLine(DiagnosticCornerPen, new Point(2, _renderSize.Height - 2), new Point(2 + L, _renderSize.Height - 2));
            dc.DrawLine(DiagnosticCornerPen, new Point(2, _renderSize.Height - 2), new Point(2, _renderSize.Height - 2 - L));
            dc.DrawLine(DiagnosticCornerPen, new Point(_renderSize.Width - 2, _renderSize.Height - 2), new Point(_renderSize.Width - 2 - L, _renderSize.Height - 2));
            dc.DrawLine(DiagnosticCornerPen, new Point(_renderSize.Width - 2, _renderSize.Height - 2), new Point(_renderSize.Width - 2, _renderSize.Height - 2 - L));
            // 左上角状态徽标（半透明底条 + 文字，避让 HUD 默认停靠位 24px）
            bool passMode = engine.Tool == InkTool.TouchThrough;
            string badge = $"墨迹层·{(passMode ? "穿透模式(触摸直达PPT)" : "接收模式")}·{(_guard is null ? "守卫缺位!" : "守卫就位")}";
            var badgeText = new FormattedText(badge, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, DiagTypeface, 15, DiagnosticBadgeForeground, k);
            double bx = 24, by = 64;
            dc.DrawRoundedRectangle(DiagnosticBadgeBackground, null, new Rect(bx - 8, by - 5, badgeText.Width + 16, badgeText.Height + 10), 6, 6);
            dc.DrawText(badgeText, new Point(bx, by));

            var s = InputDiagnostics.SessionSnapshot();

            // 接触矩形 + 中心十字 + 面积标注（跟随当前/被拒指针）
            if (s is { HasContact: true } && s.ContactW > 0 && s.ContactH > 0)
            {
                var r = new Rect(s.ContactX / k - Left, s.ContactY / k - Top, s.ContactW / k, s.ContactH / k);
                bool accepted = s.Accepted;   // v0.4.1：结构化判定（原 StartsWith("接受") 字符串解析）
                var b = accepted ? ContactAcceptedBrush : ContactRejectedBrush;
                var pen = accepted ? ContactAcceptedPen : ContactRejectedPen;
                dc.DrawRectangle(null, pen, r);
                double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                dc.DrawLine(pen, new Point(cx - 10, cy), new Point(cx + 10, cy));
                dc.DrawLine(pen, new Point(cx, cy - 10), new Point(cx, cy + 10));
                var ft = new FormattedText($"{s.AreaPx2:F0}px²", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, DiagTypeface, 14, b, k);
                dc.DrawText(ft, new Point(r.Right - ft.Width, r.Top - ft.Height - 4));
            }

            // live 笔迹：最近 24 个原始报点（暗白小点）+ 最新点笔宽示意圆（蓝）
            if (engine.LiveStroke is { Points: { Count: > 1 } pts })
            {
                var last = pts[^1];
                var prev = pts[^2];
                double dx = last.X - prev.X, dy = last.Y - prev.Y;
                double speed = Math.Sqrt(dx * dx + dy * dy) / Math.Max(1.0, last.TimestampTicks - prev.TimestampTicks);
                double f = Math.Clamp(1.15 - speed * 0.055, 0.45, 1.25) * (0.7 + 0.6 * last.Pressure);
                double minModelWidth = 2.5 / Math.Max(0.01, modelScaleX);
                double w = Math.Max(minModelWidth, Math.Max(minModelWidth, engine.Profile.MmToPx(engine.LiveStroke.Pen.ThicknessMm) * f)) * modelScaleX;
                dc.DrawEllipse(null, LiveWidthPen, new Point(last.X * modelScaleX, last.Y * modelScaleY), w / 2, w / 2);
                for (int i = Math.Max(0, pts.Count - 24); i < pts.Count; i++)
                    dc.DrawEllipse(LivePointBrush, null, new Point(pts[i].X * modelScaleX, pts[i].Y * modelScaleY), 1.3, 1.3);
            }
        }
        catch { }
    }

    private void DrawStatusBadge(DrawingContext dc)
    {
        // 状态牌是诊断 UI：即使用户曾经在旧版本配置中开启，也不得在正常模式显示。
        if (!Core.AppConfig.Current.DebugMode || !Core.AppConfig.Current.InkStatusBadgeEnabled) return;
        try
        {
            // 行1：层态 —— 整窗环境穿透位 Win32 实读（亮的是事实而非期望）；
            // 逐像素环境穿透位恒无（区域穿透由底刷 alpha 执行），读穿透期望即读事实。
            bool pass = _source is { } src && _wholeWindowHit
                ? (GetWindowLongW(src.Handle, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0
                : _passthroughWanted;
            string l1 = SuperCompat
                ? $"融绘墨迹层 · {(_passthroughWanted ? "穿透中（PPT重现）" : "全屏白板")} · {ModeLabel}档"
                : $"融绘墨迹层 · {(pass ? "穿透中（触摸直达PPT）" : "接收输入")} · {ModeLabel}档";

            // 行2：指针 —— 任意通道（鼠标/触摸/笔）1.5s 内有输入视为在层上
            bool ptrLive = _pointerOnLayer && Environment.TickCount64 - _lastPtrTick < 1500;
            string l2 = ptrLive
                ? $"指针 在层上 ({_lastPtrX:F0}, {_lastPtrY:F0})"
                : "指针 不在层上";

            // 行3：触摸会话 —— 报点数增长本身就是「程序收到了触摸」的直接证据
            var s = InputDiagnostics.SessionSnapshot();
            string l3;
            if (s is null || string.IsNullOrEmpty(s.Channel))
                l3 = "触摸 等待输入…";
            else
            {
                double age = (Environment.TickCount64 - s.LastUpdateTick) / 1000.0;
                l3 = $"触摸 {(age < 0.35 ? "进行中" : "已结束")} · {s.Channel} · {s.Points}点";
            }

            double k = DevicePerDip;
            var text = new FormattedText($"{l1}\n{l2}\n{l3}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, DiagTypeface, 13.5, StatusBadgeForeground, k);

            const double pad = 10;
            double bw = text.Width + pad * 2, bh = text.Height + pad * 2;
            // The HUD reserves up to 430 DIP on the left. If this screen cannot fit both the
            // HUD column and the status badge lane side-by-side, omit the badge instead of
            // painting it underneath the HUD. This is a slot-capacity rule, not overlap scanning.
            if (_renderSize.Width < 430 + bw + 48) return;
            double bx = _renderSize.Width - bw - 24;
            dc.DrawRoundedRectangle(StatusBadgeBackground, StatusBadgeEdgePen, new Rect(bx, 24, bw, bh), 8, 8);
            dc.DrawText(text, new Point(bx + pad, 24 + pad));
        }
        catch { /* 状态牌异常绝不影响墨迹渲染 */ }
    }

    /// <summary>v0.9.7：擦除手势激活期间跳过 StrokeCount 变化引发的整页重合成 ——
    /// 被切分的笔已进入 ghost 层以淡出动画呈现（视觉反馈已存在），抬笔时
    /// StrokesRemoved 一次性重合成即可。中途每笔都重合成 4K 位图是擦除掉帧的
    /// 真正源头（960 万像素的 Clear+Render × 每 Move 一次）。
    /// 视觉重叠说明：被切走的原笔在缓存层仍可见，同时 ghost 层以淡出叠加 ——
    /// 形成"笔迹正被擦掉"的渐隐过渡，比"闪没+淡出"更柔和，符合直觉。</summary>
    private bool RecompositeNeeded(InkEngine engine)
    {
        if (_forceRecompose) return true;   // 切档/画布组合变化后的显式失效
        if (_composedProfileId != engine.Profile.Id ||
            !double.IsFinite(_composedProfilePxPerMm) ||
            Math.Abs(_composedProfilePxPerMm - engine.Profile.PxPerMm) > 0.0001 ||
            Math.Abs(_composedViewportScaleX - CanvasToDipScaleX) > 0.0001 ||
            Math.Abs(_composedViewportScaleY - CanvasToDipScaleY) > 0.0001)
            return true; // profile/DPI/monitor changed: rebuild the retained bitmap with the same visual metrics
        var page = engine.CurrentPage;
        // v0.9.7：擦除手势激活期间（路径点存在），跳过 StrokeCount 变化的重合成
        bool eraseGestureActive = engine.Tool is InkTool.EraseStroke or InkTool.EraseLasso
                                  && (engine.LiveErasePath.Count > 0 || engine.LiveLasso.Count > 0);
        if (_composedPage != engine.CurrentSlideIndex ||
            (!eraseGestureActive && _composedStrokeCount != page.Strokes.Count) ||
            _composedHistoryDepth != page.History.Depth ||
            _lastComposedTool != InkTool.TouchThrough && engine.Tool == InkTool.TouchThrough)
        {
            return true;
        }
        return false;
    }

    private void RecomposePageCache(InkEngine engine)
    {
        var page = engine.CurrentPage;
        EnsurePageCache();
        _pageCache!.Clear();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var s in page.Strokes)
            {
                if (s.Points.Count == 0) continue;
                // Single taps are real strokes too. DrawStroke has the same dot semantics
                // for the page cache and the live layer; skipping Count < 2 made taps vanish
                // on the next cache recomposition.
                DrawStroke(dc, s, engine);
            }
        }
        _pageCache.Render(dv);
        _composedPage = engine.CurrentSlideIndex;
        _composedStrokeCount = page.Strokes.Count;
        _composedHistoryDepth = page.History.Depth;
        _lastComposedTool = engine.Tool; // 支撑"进入/退出触摸穿透需重合成"判定（否则条件永真）
        _composedProfileId = engine.Profile.Id;
        _composedProfilePxPerMm = engine.Profile.PxPerMm;
        _composedViewportScaleX = CanvasToDipScaleX;
        _composedViewportScaleY = CanvasToDipScaleY;
        _forceRecompose = false;
    }

    /// <summary>
    /// 逐段变宽绘制（第四轮书写体验，Web 原型同构）：
    /// 宽度 = 速度笔锋 + 低通（StrokeData.BuildWidths），快划收窄、慢写饱满；
    /// 相邻段宽度差 &lt;5% 合批为一条折线（降低 draw call），圆头衔接无接缝。
    /// </summary>
    private void DrawStroke(DrawingContext dc, StrokeData s, InkEngine engine)
    {
        var pts = s.Points;
        if (pts.Count == 0) return;

        // v0.9.9 荧光笔：方向性笔尖 ribbon 填充 —— 真实荧光笔笔尖是竖矩形（横画粗、竖画细）。
        // 整条笔迹单一填充、恒定透明度；原先按描边分段 + 圆头衔接，分段圆头在半透明度下
        // 两两重叠叠加，一条没画重叠的线也会「深~浅~深」。填充渲染从机制上消除这个伪影。
        if (s.Pen.Highlighter)
        {
            var highBrush = GetStrokeBrush(Color.FromArgb(s.Pen.Alpha, s.Pen.Color.R, s.Pen.Color.G, s.Pen.Color.B));
            double baseH = Math.Max(2.5 / Math.Max(0.01, CanvasToDipScaleX), engine.Profile.MmToPx(s.Pen.ThicknessMm));
            if (pts.Count <= 2 && IsDegenerate(pts))
            {
                // 点状荧光笔：画一个竖矩形近似的椭圆（笔尖形状）
                double tipRatioDot = Math.Clamp(Core.AppConfig.Current.HighlighterTipRatio, 0.30, 1.0);
                dc.DrawEllipse(highBrush, null, new Point(pts[^1].X, pts[^1].Y), baseH * tipRatioDot * 0.56, baseH * 0.5);
                return;
            }
            // v1.0.0：笔尖横竖比可由高级模式调整（0.5 = 竖矩形「横粗竖细」；1.0 = 圆头等宽）
            double tipRatio = Math.Clamp(Core.AppConfig.Current.HighlighterTipRatio, 0.30, 1.0);
            double tipW = baseH * tipRatio, tipH = baseH;
            dc.DrawGeometry(highBrush, null, s.BuildHighlighterRibbon(tipW, tipH));
            return;
        }

        // 点状笔迹（轻触/长按未产生位移：1~2 个几乎重合的点）→ 实心圆点。
        // 「点一下就有墨」是书写工具的自然语义（与多段笔迹同色、同笔宽、同 2.5 DIP
        // 视觉下限），不是对 tap 的特判：任何总位移 < 1.5px 的笔迹都按点渲染。
        if (pts.Count <= 2 && IsDegenerate(pts))
        {
            var dotBrush = GetStrokeBrush(Color.FromArgb(s.Pen.Alpha, s.Pen.Color.R, s.Pen.Color.G, s.Pen.Color.B));
            double dotDiameter = Math.Max(2.5 / Math.Max(0.01, CanvasToDipScaleX), engine.Profile.MmToPx(s.Pen.ThicknessMm));
            double dotR = dotDiameter * 0.5;
            dc.DrawEllipse(dotBrush, null, new Point(pts[^1].X, pts[^1].Y), dotR, dotR);
            return;
        }

        if (!s.Pen.Tapered)
        {
            // 平滑参数与活体书写同源（机型档案）：抬笔精修重绘不跳变（审查修复 #1）
            dc.DrawGeometry(null, MakeWpfPen(s.Pen, engine),
                s.BuildGeometry(1.0, PenTuning.EffectiveAlpha(engine.Profile.EmaAlpha), engine.Profile.MaxJumpPx * engine.Profile.CanvasScale, engine.Profile.DropoutMs, Core.AppConfig.Current.SmoothInk));
            return;
        }

        var brush = GetStrokeBrush(Color.FromArgb(s.Pen.Alpha, s.Pen.Color.R, s.Pen.Color.G, s.Pen.Color.B));
        // 视觉下限 2.5 DIP（v0.3.0）：机型档案失配时 PxPerMm 可能严重偏小（华为屏命中
        // 希沃档案 → 2.2mm 笔只剩 ~1px，速度笔锋再乘 0.45 成亚像素 —— "写了看不见"）。
        // 档案再错，笔迹粗细也不得低于肉眼可见下限。
        double basePx = Math.Max(2.5 / Math.Max(0.01, CanvasToDipScaleX), engine.Profile.MmToPx(s.Pen.ThicknessMm));
        var w = s.BuildWidths(basePx, 1.0 / Math.Max(0.01, engine.Profile.CanvasScale), 2.5 / Math.Max(0.01, CanvasToDipScaleX));

        int i = 1;
        while (i < pts.Count)
        {
            int start = i - 1;
            double width = w[i];
            int end = i;
            while (end + 1 < pts.Count && Math.Abs(w[end + 1] - width) < width * 0.05)
                end++;
            DrawBatch(dc, brush, s, start, end, width);
            i = end + 1;
        }
    }

    /// <summary>退化（点状）笔迹判据：≤2 点且总位移 &lt; 1.5px（轻触/长按未移动）。</summary>
    private static bool IsDegenerate(IReadOnlyList<InkPoint> pts)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return (maxX - minX) + (maxY - minY) < 1.5;
    }

    /// <summary>一个合批段：二次贝塞尔曲线 + 圆头笔（宽度近似恒定，接缝由圆头盖住）。
    /// v0.9.7：由折线升级为二次贝塞尔（中点法，与 StrokeData.BuildGeometry 同源），
    /// 笔画从「折线拼接」变为真正圆润的曲线 —— 书写流畅感的核心提升。
    /// 合批内点序列 [from..to]：以相邻点中点为段端点、原点为控制点，C1 连续。</summary>
    private static void DrawBatch(DrawingContext dc, Brush brush, StrokeData s, int from, int to, double width)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var pts = s.Points;
            ctx.BeginFigure(new Point(pts[from].X, pts[from].Y), false, false);
            int count = to - from + 1;
            if (count == 2)
            {
                // 仅两点：退化为直线（贝塞尔无意义）
                ctx.LineTo(new Point(pts[to].X, pts[to].Y), true, false);
            }
            else
            {
                // 中点法二次贝塞尔：段端点 = 相邻点中点，控制点 = 原点。
                // 与 SmoothingFilter.BuildBezierSegments 同算法，保证实时渲染与抬笔精修形态一致。
                Point start = new(pts[from].X, pts[from].Y);
                for (int i = from + 1; i < to; i++)
                {
                    var ctrl = new Point(pts[i].X, pts[i].Y);
                    var end = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                    ctx.QuadraticBezierTo(ctrl, end, true, false);
                    start = end;
                }
                // 收尾段：连到终点（退化为直线）
                ctx.LineTo(new Point(pts[to].X, pts[to].Y), true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, GetStrokePen(brush, width), g);
    }

}
