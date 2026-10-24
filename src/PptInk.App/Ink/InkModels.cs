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
    public byte Alpha => AlphaOverride ?? (Highlighter ? (byte)110 : (byte)255);
}

/// <summary>
/// 一笔矢量墨迹：点序列 + 笔属性。底层矢量化存储点序列，
/// 支持无损缩放与 AI 识别；渲染几何带脏缓存。
/// </summary>
public sealed class StrokeData
{
    private PathGeometry? _cachedGeometry;
    private (double Alpha, double Jump, double Drop) _cachedGeoFilterKey;   // 缓存口径：平滑参数变化（切机型）时重建几何
    private Rect _cachedBounds;
    private double[] _cachedWidths = Array.Empty<double>();
    private int _cachedWidthCount;
    private (double BaseWidth, double VelocityScale, double MinimumWidth) _cachedWidthKey;
    private bool _hasCachedWidthKey;

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
            // 隔点抽稀（保留偶数索引，含起点）：点数折半后继续累积。
            var kept = new List<InkPoint>(Points.Count / 2);
            for (int i = 0; i < Points.Count; i += 2) kept.Add(Points[i]);
            Points.Clear();
            Points.AddRange(kept);
            // Compaction changes existing point indices, so incremental width state is no longer valid.
            _cachedWidthCount = 0;
            _hasCachedWidthKey = false;
        }
        Points.Add(p);
        _cachedGeometry = null;
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
    public PathGeometry BuildGeometry(double pxPerMm, double emaAlpha = 0.45, double maxJumpPx = 480, double dropoutMs = 100)
    {
        var filterKey = (emaAlpha, maxJumpPx, dropoutMs);
        if (_cachedGeometry is not null && _cachedGeoFilterKey == filterKey) return _cachedGeometry;

        var g = new PathGeometry();
        if (Points.Count == 0)
        {
            g.Freeze();
            return g;
        }

        var f = new SmoothingFilter(emaAlpha, maxJumpPx, dropoutMs);
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
    /// 逐点宽度（速度笔锋 + 起收笔 Taper + 自适应低通，v0.9.7 书写体验重构）：
    ///  - <b>速度笔锋</b>：快划收窄、慢写饱满（宽度因子 0.45~1.25），压感进一步调制；
    ///  - <b>起笔 Taper（渐入）</b>：前 6 点宽度按 0.35 → 1.0 渐入，模拟真实笔墨「落笔由细到粗」；
    ///  - <b>收笔 Taper（渐出）</b>：实时书写阶段未知终点，不做渐出；抬笔精修时由
    ///    ApplyLiftTaper 统一处理（最后 8 点按 1.0 → 0.25 渐出，笔锋收细不突兀）；
    ///  - <b>自适应低通</b>：低通系数按速度动态调整（快写 0.55 灵敏跟随、慢写 0.32 稳如泰山），
    ///    取代固定 0.45 —— 快写笔锋变化更利落，慢写不会因微小速度波动而粗细抖动；
    ///  - 渲染层按相邻段宽度差 &lt;5% 合批（Web 原型同构），兼顾墨感与 draw call。
    /// TimestampTicks 为 InkClock 的 QPC 毫秒域；速度计算只允许与同源时间戳求差。
    /// </summary>
    public ReadOnlySpan<double> BuildWidths(double basePx, double velocityScale = 1.0, double minimumWidth = 0.8)
    {
        var pts = Points;
        var key = (basePx, velocityScale, minimumWidth);
        if (!_hasCachedWidthKey || _cachedWidthKey != key || _cachedWidthCount > pts.Count)
        {
            _cachedWidthKey = key;
            _hasCachedWidthKey = true;
            _cachedWidthCount = 0;
        }

        if (_cachedWidths.Length < pts.Count)
        {
            // Grow geometrically so continuous handwriting doesn't allocate a new width array
            // on every point. The returned span exposes only the populated prefix.
            int capacity = Math.Max(64, _cachedWidths.Length);
            while (capacity < pts.Count) capacity = Math.Min(MaxPoints, capacity * 2);
            Array.Resize(ref _cachedWidths, capacity);
        }

        // v0.9.7：起笔渐入窗口 —— 前 6 个点从 0.35× 渐入到 1.0×（真实笔墨的落笔笔锋）
        const int leadInPoints = 6;
        const double leadInFloor = 0.35;

        int start = _cachedWidthCount;
        // 起笔段的 smooth 从 floor × basePx 起步（而非 basePx），避免第一点就有一整段粗宽度
        double smooth = start > 0 ? _cachedWidths[start - 1] : basePx * leadInFloor;
        long lastTick = start > 0 ? pts[start - 1].TimestampTicks : (pts.Count > 0 ? pts[0].TimestampTicks : 0);
        for (int i = start; i < pts.Count; i++)
        {
            double target = basePx;
            double speedNorm = 0.0;   // 归一化速度（用于自适应低通）
            if (Pen.Tapered && i > 0)
            {
                double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy) *
                    (double.IsFinite(velocityScale) && velocityScale > 0 ? velocityScale : 1.0);
                double dtMs = Math.Max(1.0, pts[i].TimestampTicks - lastTick);
                double speed = dist / dtMs;                                    // px/ms
                speedNorm = Math.Clamp(speed / 6.0, 0, 1);                     // ~6px/ms 视作「很快」
                double f = Math.Clamp(1.15 - speed * 0.055, 0.45, 1.25);       // 快→窄 慢→饱满
                f *= 0.7 + 0.6 * pts[i].Pressure;                              // 压感调制（0.5 → ×1.0）
                target = basePx * f;
            }
            lastTick = pts[i].TimestampTicks;

            // v0.9.7 起笔渐入：前 leadInPoints 点宽度按比例渐入（0.35 → 1.0）
            if (i < leadInPoints)
            {
                double t = (i + 1) / (double)leadInPoints;                     // 1/6 … 6/6
                double ease = 1 - Math.Pow(1 - t, 2);                          // EaseOutQuad：起笔利落
                target *= leadInFloor + (1 - leadInFloor) * ease;
            }

            // v0.9.7 自适应低通：快写系数高（0.55，灵敏跟随速度变化），
            // 慢写系数低（0.32，稳如泰山不抖动）—— 取代固定 0.45。
            double lowpassK = 0.32 + 0.23 * speedNorm;
            smooth += (target - smooth) * lowpassK;
            // Keep even a fast stroke above the renderer's visibility floor.
            double floor = double.IsFinite(minimumWidth) && minimumWidth > 0 ? minimumWidth : 0.8;
            _cachedWidths[i] = Math.Max(floor, smooth);
        }

        _cachedWidthCount = pts.Count;
        return new ReadOnlySpan<double>(_cachedWidths, 0, _cachedWidthCount);
    }

    /// <summary>
    /// 抬笔收锋（v0.9.7）：一笔提交时调用，对最后 8 个点施加 1.0 → 0.25 渐出宽度，
    /// 模拟真实笔墨「提笔由粗到细」的收笔笔锋。渐出系数覆盖已缓存宽度（不影响增量缓存机制，
    /// 因为提交后该笔不再追加新点，缓存失效重算路径不会回来）。
    /// </summary>
    public void ApplyLiftTaper()
    {
        const int liftPoints = 8;
        const double liftFloor = 0.25;
        int n = _cachedWidthCount;
        if (n < 3) return;   // 点状笔（≤2 点）无需收锋
        int k = Math.Min(liftPoints, n - 1);
        for (int j = 0; j < k; j++)
        {
            int i = n - 1 - j;                                  // 最后一点 j=0
            double t = j / (double)(k - 1 > 0 ? k - 1 : 1);     // 0（末点）→ 1（前 k-1 点）
            double ease = t * t;                                // EaseInQuad：末点收得最细
            double factor = liftFloor + (1 - liftFloor) * ease;
            _cachedWidths[i] = Math.Max(0.5, _cachedWidths[i] * factor);
        }
    }

    #region 命中检测（抹除/圈除）

    /// <summary>抹除命中：橡皮圆心沿路径滑动，任一线段进入半径即命中（滑过即擦）。</summary>
    public bool HitByErasePath(IEnumerable<Point> eraserPath, double radiusPx)
    {
        if (Points.Count == 0) return false;
        foreach (var e in eraserPath)
            for (int i = 1; i < Points.Count; i++)
                if (DistancePointToSegment(e, (Points[i - 1].X, Points[i - 1].Y), (Points[i].X, Points[i].Y)) <= radiusPx)
                    return true;
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
