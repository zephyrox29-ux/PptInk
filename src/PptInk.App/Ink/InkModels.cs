using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PptInk.Ink;

/// <summary>墨迹点（统一文档坐标，3840×2160 逻辑画布单位）。所有输入通道在进入引擎前映射至此坐标系。</summary>
/// <param name="X">文档画布 X 坐标。</param>
/// <param name="Y">文档画布 Y 坐标。</param>
/// <param name="Pressure">压力 0~1（无压感设备恒 0.5）。</param>
/// <param name="TimestampTicks">高精度毫秒时间戳（<see cref="InkClock"/> QPC 语义，
/// 用于断触检测与速度计算；仅与同源时间戳做差值比较）。</param>
public readonly record struct InkPoint(double X, double Y, double Pressure, long TimestampTicks);

/// <summary>
/// 墨迹时钟（v0.4.1）：InkPoint.TimestampTicks 的唯一时间源 —— QPC 高精度毫秒。
///
/// 修复动机：原用 Environment.TickCount64（GetTickCount64 语义，分辨率 10~16ms），
/// 而 120Hz 报点间隔仅 8ms 低于其分辨率 —— 相邻报点时间戳相同，速度笔锋的
/// dtMs 恒被 Max(1.0, Δ) 钳到 1ms → 速度虚高 8~16 倍 → 快写笔锋过度收细失真。
/// QueryPerformanceCounter（Win8+ 恒可用、恒单调）换算毫秒后分辨率 &lt;0.1ms，
/// 速度笔锋计算精度提升约 100 倍。
///
/// 纪律：仅 InkPoint 构造处与渲染层淡圈进度计算使用（同一时钟做差值）；
/// 绝不与 Environment.TickCount64 混用于同一差值表达式。
/// </summary>
public static class InkClock
{
    private static readonly double MsPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>当前墨迹毫秒时间戳（QPC，分辨率 &lt;0.1ms，单调递增）。</summary>
    public static long NowMs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * MsPerTick);
}

/// <summary>笔属性：颜色 / 粗细（物理毫米，跨机型视觉一致）/ 荧光笔 / 笔锋。</summary>
public sealed record PenInfo
{
    public required string ColorHex { get; init; }
    public double ThicknessMm { get; init; } = 2.2;
    public bool Highlighter { get; init; }
    public bool Tapered { get; init; } = true; // 速度+压力调制宽度（笔锋）
    /// <summary>可选透明度覆写（0..255），供插件/导入笔迹使用；null 保持普通笔/荧光笔默认语义。</summary>
    public byte? AlphaOverride { get; init; }

    public Color Color => (Color)ColorConverter.ConvertFromString(ColorHex);
    /// <summary>荧光笔：半透明不遮盖文字；插件显式透明度优先于默认笔型透明度。</summary>
    public byte Alpha => AlphaOverride ?? (Highlighter
        ? (byte)System.Math.Round(System.Math.Clamp(Core.AppConfig.Current.HighlighterAlpha, 0.20, 0.80) * 255)
        : (byte)255);
}

/// <summary>
/// 一笔矢量墨迹：点序列 + 笔属性。底层矢量化存储点序列，
/// 支持无损缩放与 AI 识别；渲染几何带脏缓存。
/// </summary>
public sealed class StrokeData
{
    private PathGeometry? _cachedGeometry;
    private (double Alpha, double Jump, double Drop, bool SmoothPos) _cachedGeoFilterKey;   // 缓存口径：平滑参数/开关变化（切机型/切模式）时重建几何
    private Rect _cachedBounds;
    private double[] _cachedWidths = Array.Empty<double>();
    private int _cachedWidthCount;
    private (double BaseWidth, double VelocityScale, double MinimumWidth, double Taper, int LeadIn, double Lowpass, double Pressure) _cachedWidthKey;
    private bool _hasCachedWidthKey;
    private double _velSmooth;   // v0.9.9：会话内速度平滑状态（红外框报点抖动的第一道抚平）

    public Guid Id { get; } = Guid.NewGuid();
    public PenInfo Pen { get; }
    public List<InkPoint> Points { get; } = new(256);
    /// <summary>消散动画进度 0~1（1 = 完全消失，从页面移除）。</summary>
    public double DissolveProgress { get; set; }
    /// <summary>消散开始前的延迟（秒）：波浪清空按笔迹水平位置错峰，延迟期内笔迹保持可见。</summary>
    public double DissolveDelay { get; set; }
    public bool IsAlive => DissolveProgress < 1.0;

    public StrokeData(PenInfo pen) => Pen = pen;

    /// <summary>单笔点数上限（v0.8.1 内存保护）：正常一笔远低于此值（120Hz 采样 10s ≈ 1200 点）；
    /// 仅「久压不抬笔」的极端笔画会触及。触顶后隔点抽稀折半，形状近似不变，防止无限增长。</summary>
    public const int MaxPoints = 8192;

    public void Add(in InkPoint p)
    {
        if (Points.Count >= MaxPoints)
        {
            CompactPoints();   // 触顶抽稀；活笔书写路径由引擎同步折半平滑流（见 InkEngine.CompactLiveStroke）
        }
        Points.Add(p);
        _cachedGeometry = null;
    }

    /// <summary>触顶隔点抽稀折半（保留偶数索引，含起点）：点数减半、形状近似不变。
    /// 公开给引擎在折半 Points 的同一时机调 <see cref="SmoothingFilter.CompactSmoothed"/> 同步
    /// 折半平滑流 —— 只折 Points 会让两流索引错位，抬笔补点循环重放中段且永不终止（v1.0.1）。</summary>
    public void CompactPoints()
    {
        // 隔点抽稀（保留偶数索引，含起点）：点数折半后继续累积。
        var kept = new List<InkPoint>(Points.Count / 2);
        for (int i = 0; i < Points.Count; i += 2) kept.Add(Points[i]);
        Points.Clear();
        Points.AddRange(kept);
        // Compaction changes existing point indices, so incremental width state is no longer valid.
        _cachedWidthCount = 0;
        _hasCachedWidthKey = false;
    }

    public Rect Bounds
    {
        get
        {
            if (_cachedGeometry is null || _cachedBounds.IsEmpty) ComputeBounds();
            return _cachedBounds;
        }
    }

    private void ComputeBounds()
    {
        if (Points.Count == 0) { _cachedBounds = Rect.Empty; return; }
        var r = new Rect(Points[0].X, Points[0].Y, 0, 0);
        foreach (var p in Points) r.Union(new Rect(p.X, p.Y, 0, 0));
        _cachedBounds = r;
    }

    /// <summary>渲染几何：平滑点 → 二次贝塞尔折线（抗锯齿；笔锋变宽由渲染层逐段完成）。
    /// 平滑参数必须与活体书写同源（机型档案 EmaAlpha/MaxJumpPx/DropoutMs，由调用方传入）——
    /// 否则抬笔精修重绘与实时渲染走两套 EMA，笔画形状会在落笔收尾瞬间跳变（审查修复 #1）。</summary>
    public PathGeometry BuildGeometry(double pxPerMm, double emaAlpha = 0.45, double maxJumpPx = 480, double dropoutMs = 100, bool positionSmoothing = true)
    {
        var filterKey = (emaAlpha, maxJumpPx, dropoutMs, positionSmoothing);
        if (_cachedGeometry is not null && _cachedGeoFilterKey == filterKey) return _cachedGeometry;

        var g = new PathGeometry();
        if (Points.Count == 0)
        {
            g.Freeze();
            return g;
        }

        var f = new SmoothingFilter(emaAlpha, maxJumpPx, dropoutMs, positionSmoothing);
        foreach (var p in Points) f.Feed(p);

        var fig = new PathFigure { StartPoint = new Point(Points[0].X, Points[0].Y), IsClosed = false };
        var segs = f.BuildBezierSegments();
        if (segs.Count == 0 && Points.Count > 1)
        {
            fig.Segments.Add(new PolyLineSegment(Points.Select(p => new Point(p.X, p.Y)), true));
        }
        else
        {
            // PolyQuadraticBezierSegment：control、end 成对交替
            var beziers = new PolyQuadraticBezierSegment();
            foreach (var (s, c, e) in segs) { beziers.Points.Add(new Point(c.X, c.Y)); beziers.Points.Add(new Point(e.X, e.Y)); }
            fig.Segments.Add(beziers);
        }
        g.Figures.Add(fig);
        // This geometry is immutable until the next point arrives; freezing lets WPF use its
        // cheaper immutable render path for both live-stroke redraws and the composed page cache.
        g.Freeze();

        _cachedGeometry = g;
        _cachedGeoFilterKey = filterKey;
        return g;
    }

    /// <summary>
    /// 逐点宽度（v0.9.9 快速触摸书写重构）：
    ///  - <b>速度平滑</b>：即时速度先过 EMA 低通（系数 0.30）—— 红外框报点在运动时会抖动
    ///    ±1~2px，位移/时间除法把抖动放大成 2 倍速度跳变，直接喂给宽度公式就是「粗细跳变」；
    ///    平滑后的速度参与笔锋因子与低通系数，宽度自然稳定；
    ///  - <b>宽度变化率限制</b>：相邻点宽度差 ≤12% —— 即使速度平滑后偶发尖峰也被截断，
    ///    「粗→突然细→粗」的突变从机制上消除；
    ///  - <b>速度笔锋</b>：快划收窄、慢写饱满（因子 0.50~1.20），压感进一步调制；
    ///  - <b>起笔 Taper</b>：前 8 点按 0.20 → 1.0 渐入（Cubic 缓出），落笔笔锋可见；
    ///  - <b>收笔 Taper</b>：抬笔时 `ApplyLiftTaper` 对最后 12 点按 1.0 → 0.12 渐出；
    ///  - <b>自适应低通</b>：快写 0.55 / 慢写 0.30，平滑速度驱动，系数不再跳变。
    /// TimestampTicks 为 InkClock 的 QPC 毫秒域；速度计算只允许与同源时间戳求差。
    /// </summary>
    public ReadOnlySpan<double> BuildWidths(double basePx, double velocityScale = 1.0, double minimumWidth = 0.8)
    {
        var pts = Points;
        // v1.0.0：高级模式精调参数（单一真源 = AppConfig）—— 渲染、导出、各调用点自动一致，
        // 不会出现「同一支笔在不同路径算出不同宽度」。改任一项 → 缓存 key 变化 → 下次重算生效。
        var cfg = Core.AppConfig.Current;
        double taperStrength = double.IsFinite(cfg.PenTaperStrength) ? Math.Clamp(cfg.PenTaperStrength, 0.0, 1.0) : 0.5;
        int leadInPoints = Math.Clamp(cfg.PenLeadInPoints, 0, 20);
        double widthLowpass = double.IsFinite(cfg.PenWidthLowpass) ? Math.Clamp(cfg.PenWidthLowpass, 0.10, 0.90) : 0.30;
        double pressureGain = double.IsFinite(cfg.PenPressureGain) ? Math.Clamp(cfg.PenPressureGain, 0.0, 1.0) : 0.5;

        var key = (basePx, velocityScale, minimumWidth, taperStrength, leadInPoints, widthLowpass, pressureGain);
        if (!_hasCachedWidthKey || _cachedWidthKey != key || _cachedWidthCount > pts.Count)
        {
            _cachedWidthKey = key;
            _hasCachedWidthKey = true;
            _cachedWidthCount = 0;
            _velSmooth = 0;   // 新会话（或口径变化）重置速度平滑状态
        }

        if (_cachedWidths.Length < pts.Count)
        {
            int capacity = Math.Max(64, _cachedWidths.Length);
            while (capacity < pts.Count) capacity = Math.Min(MaxPoints, capacity * 2);
            Array.Resize(ref _cachedWidths, capacity);
        }

        const double leadInFloor = 0.20;
        const double maxStepRatio = 0.12;   // 相邻宽度变化率上限：防速度尖峰突变
        const double velK = 0.30;           // 速度 EMA 低通：抚平红外框报点抖动

        int start = _cachedWidthCount;
        double smooth = start > 0 ? _cachedWidths[start - 1] : basePx * (leadInPoints > 0 ? leadInFloor : 1.0);
        double velSmooth = start > 0 ? _velSmooth : 0;   // 会话内连续（增量重算不清零）
        long lastTick = start > 0 ? pts[start - 1].TimestampTicks : (pts.Count > 0 ? pts[0].TimestampTicks : 0);
        for (int i = start; i < pts.Count; i++)
        {
            double target = basePx;
            double speedNorm = 0.0;
            if (Pen.Tapered && i > 0)
            {
                double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy) *
                    (double.IsFinite(velocityScale) && velocityScale > 0 ? velocityScale : 1.0);
                double dtMs = Math.Max(1.0, pts[i].TimestampTicks - lastTick);
                double rawSpeed = dist / dtMs;                                   // px/ms（瞬时，可能抖）
                velSmooth += (rawSpeed - velSmooth) * velK;                      // 速度低通抚平
                double speed = velSmooth;
                speedNorm = Math.Clamp(speed / 6.0, 0, 1);
                // 速度笔锋：基准曲线围绕 1.0 按 taperStrength 缩放（0=等宽 / 1=满幅）
                double baseF = Math.Clamp(1.18 - speed * 0.05, 0.50, 1.20);
                double f = 1.0 + taperStrength * (baseF - 1.0);
                // 压感调制：pressureGain 控制幅度（0=忽略压感；0.5→×1.0 @ pressure 0.5）
                double pf = Math.Clamp(1.0 + pressureGain * 1.2 * (pts[i].Pressure - 0.5), 0.5, 1.5);
                target = basePx * f * pf;
            }
            lastTick = pts[i].TimestampTicks;

            if (leadInPoints > 0 && i < leadInPoints)
            {
                double t = (i + 1) / (double)leadInPoints;
                double ease = 1 - Math.Pow(1 - t, 3);                            // EaseOutCubic
                target *= leadInFloor + (1 - leadInFloor) * ease;
            }

            double lowpassK = widthLowpass + 0.25 * speedNorm;
            smooth += (target - smooth) * lowpassK;

            // 宽度变化率截断：相邻宽度差 ≤12%，突变从机制上消除。
            double w = smooth;
            if (i > 0 && i >= Math.Max(1, leadInPoints))
            {
                double prev = _cachedWidths[i - 1];
                double lo = prev * (1 - maxStepRatio), hi = prev * (1 + maxStepRatio);
                w = Math.Clamp(w, lo, hi);
            }

            double floor = double.IsFinite(minimumWidth) && minimumWidth > 0 ? minimumWidth : 0.8;
            _cachedWidths[i] = Math.Max(floor, w);
        }

        _velSmooth = velSmooth;   // 缓存速度平滑状态（下一次增量从这里继续）
        _cachedWidthCount = pts.Count;
        return new ReadOnlySpan<double>(_cachedWidths, 0, _cachedWidthCount);
    }

    /// <summary>
    /// 抬笔收锋（v0.9.9 加深）：一笔提交时调用，对最后 12 个点施加 1.0 → 0.12 渐出宽度
    /// （Cubic 缓入），模拟真实笔墨「提笔由粗到细」的收笔笔锋。
    /// 渐出系数覆盖已缓存宽度（提交后该笔不再追加新点，缓存失效重算路径不会回来）。
    /// </summary>
    public void ApplyLiftTaper()
    {
        // v1.0.0：收锋长度可由高级模式调整（0 = 关闭收锋）
        int liftPoints = Math.Clamp(Core.AppConfig.Current.PenLiftPoints, 0, 24);
        const double liftFloor = 0.12;
        if (liftPoints <= 0) return;
        // v1.0.1 修复收锋错位：_cachedWidthCount 是渲染层上一帧 BuildWidths 留下的点数，而抬笔
        // 补点总晚于渲染帧 —— 直接以它为基准，收锋会错位落在倒数第 2~3 点上、真实笔尖无收锋。
        // 缓存未建立（渲染层从未画过该笔）保持 no-op；已建立则用缓存 key 里记录的渲染同参把
        // 宽度增量补算到最终点数（key 相同走增量路径，不触发重置/覆盖），收锋落在真实尾段。
        if (!_hasCachedWidthKey) return;
        if (_cachedWidthCount < Points.Count)
            BuildWidths(_cachedWidthKey.BaseWidth, _cachedWidthKey.VelocityScale, _cachedWidthKey.MinimumWidth);
        int n = _cachedWidthCount;
        if (n < 3) return;   // 点状笔（≤2 点）无需收锋
        int k = Math.Min(liftPoints, n - 1);
        for (int j = 0; j < k; j++)
        {
            int i = n - 1 - j;                                  // 最后一点 j=0
            double t = j / (double)(k - 1 > 0 ? k - 1 : 1);     // 0（末点）→ 1（前 k-1 点）
            double ease = t * t * t;                            // EaseInCubic：末点收得最细
            double factor = liftFloor + (1 - liftFloor) * ease;
            _cachedWidths[i] = Math.Max(0.5, _cachedWidths[i] * factor);
        }
    }

    // ═══════════ 荧光笔：方向性笔尖 ribbon（v0.9.9） ═══════════

    private PathGeometry? _cachedRibbon;
    private (double W, double H, int Count) _cachedRibbonKey;
    private bool _hasRibbonKey;

    /// <summary>
    /// 荧光笔 ribbon 几何（方向性笔尖填充）。
    /// 真实荧光笔的笔尖是竖矩形（宽 W × 高 H，H &gt; W）：横画时墨带宽（=H），竖画时窄（=W）。
    /// 每点按运动方向求垂直于轨迹的半宽 halfW = (W/2)·|dir.Y| + (H/2)·|dir.X|，
    /// 生成左右边界闭合多边形交渲染层 Fill 填充。
    /// 相比描边分段渲染：整条笔迹单一填充、恒定透明度 —— 描边分段圆头在半透明度下
    /// 重叠叠加产生的「深~浅~深」从机制上消除。
    /// 结果带缓存：笔迹追加点时增量失效重建。
    /// </summary>
    /// <param name="tipWidthPx">笔尖水平宽（W）—— 竖画时的墨带宽度（细）。</param>
    /// <param name="tipHeightPx">笔尖垂直高（H）—— 横画时的墨带宽度（粗）。</param>
    public PathGeometry BuildHighlighterRibbon(double tipWidthPx, double tipHeightPx)
    {
        var key = (tipWidthPx, tipHeightPx, Points.Count);
        if (_hasRibbonKey && _cachedRibbonKey == key && _cachedRibbon is not null)
            return _cachedRibbon;

        var geo = new PathGeometry();
        var outline = BuildHighlighterOutline(tipWidthPx, tipHeightPx);
        if (outline.Length >= 3)
        {
            var fig = new PathFigure { StartPoint = outline[0], IsClosed = true, IsFilled = true };
            var segs = new List<PathSegment>(outline.Length - 1);
            for (int i = 1; i < outline.Length; i++) segs.Add(new LineSegment(outline[i], true));
            fig.Segments = new PathSegmentCollection(segs);
            geo.Figures.Add(fig);
        }
        geo.Freeze();
        _cachedRibbon = geo;
        _cachedRibbonKey = key;
        _hasRibbonKey = true;
        return geo;
    }

    /// <summary>
    /// 荧光笔 ribbon 的闭合轮廓点序列（左边界正向 + 右边界反向）。
    /// 与 <see cref="BuildHighlighterRibbon"/> 共用同一套边界计算 —— 屏幕填充渲染（WPF PathGeometry）
    /// 与文件导出（SVG path）走同一轮廓，保证「所见即所得」，不会出现导出件与屏幕不一致。
    /// 点数 &lt;2 返回空数组。
    /// </summary>
    public Point[] BuildHighlighterOutline(double tipWidthPx, double tipHeightPx)
    {
        var pts = Points;
        int n = pts.Count;
        if (n < 2) return Array.Empty<Point>();
        double hw2 = tipWidthPx / 2, hh2 = tipHeightPx / 2;

        // 每点运动方向（中心差分；端点用相邻段；静止点默认水平方向）
        var left = new Point[n];
        var right = new Point[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double dx = pts[b].X - pts[a].X, dy = pts[b].Y - pts[a].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            // 法向量（垂直于运动方向）
            double nx = -dy, ny = dx;
            // 方向相关半宽：横画（dir 水平）→ half=H/2（粗）；竖画（dir 垂直）→ half=W/2（细）
            double half = hw2 * Math.Abs(dy) + hh2 * Math.Abs(dx);
            var p = pts[i];
            left[i] = new Point(p.X - nx * half, p.Y - ny * half);
            right[i] = new Point(p.X + nx * half, p.Y + ny * half);
        }

        // 边界三点平滑：消除方向跳变产生的锯齿，墨带边缘顺滑
        var sl = SmoothBoundary(left);
        var sr = SmoothBoundary(right);

        // 闭合多边形：左边界正向 + 右边界反向，两端自然闭合
        var outline = new Point[2 * n];
        for (int i = 0; i < n; i++) outline[i] = sl[i];
        for (int i = 0; i < n; i++) outline[2 * n - 1 - i] = sr[i];
        return outline;
    }

    /// <summary>边界三点平滑（1/4, 1/2, 1/4 加权）：ribbon 边缘去锯齿。</summary>
    private static Point[] SmoothBoundary(Point[] pts)
    {
        int n = pts.Length;
        if (n < 3) return pts;
        var r = new Point[n];
        r[0] = pts[0]; r[n - 1] = pts[n - 1];
        for (int i = 1; i < n - 1; i++)
            r[i] = new Point((pts[i - 1].X + pts[i].X * 2 + pts[i + 1].X) / 4,
                             (pts[i - 1].Y + pts[i].Y * 2 + pts[i + 1].Y) / 4);
        return r;
    }

    #region 命中检测（抹除/圈除）

    /// <summary>抹除命中：橡皮圆心沿路径滑动，任一线段进入半径即命中（滑过即擦）。
    /// v1.1.2 修复：单点笔迹（轻点留下的墨点）原先永远不进线段循环，涂抹擦除对它
    /// 完全失效 —— 「莫名其妙的残留红点擦不掉」的根因。点按点距判定。</summary>
    public bool HitByErasePath(IEnumerable<Point> eraserPath, double radiusPx)
    {
        if (Points.Count == 0) return false;
        foreach (var e in eraserPath)
        {
            if (Points.Count == 1)
            {
                double dx = e.X - Points[0].X, dy = e.Y - Points[0].Y;
                if (dx * dx + dy * dy <= radiusPx * radiusPx) return true;
                continue;
            }
            for (int i = 1; i < Points.Count; i++)
                if (DistancePointToSegment(e, (Points[i - 1].X, Points[i - 1].Y), (Points[i].X, Points[i].Y)) <= radiusPx)
                    return true;
        }
        return false;
    }

    /// <summary>圈除命中：闭合多边形包含本笔 ≥70% 采样点 → 整笔消散（真实板擦体感）。</summary>
    public bool HitByLassoPolygon(IReadOnlyList<Point> polygon, double ratio = 0.7)
    {
        if (polygon.Count < 3 || Points.Count == 0) return false;
        int hit = 0, step = Math.Max(1, Points.Count / 40); // 40 个采样点足够判定，避免长笔画 O(n·m) 放大
        int sampled = 0;
        for (int i = 0; i < Points.Count; i += step)
        {
            sampled++;
            if (PointInPolygon(Points[i].X, Points[i].Y, polygon)) hit++;
        }
        return sampled > 0 && hit >= sampled * ratio;
    }

    private static double DistancePointToSegment(Point p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double ex = p.X - (a.X + t * dx), ey = p.Y - (a.Y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static bool PointInPolygon(double x, double y, IReadOnlyList<Point> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > y) != (poly[j].Y > y) &&
                x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    #endregion

    public StrokeData Clone()
    {
        var s = new StrokeData(Pen) { DissolveProgress = DissolveProgress };
        s.Points.AddRange(Points);
        return s;
    }
}
