using System.Windows;

namespace PptInk.Ink;

/// <summary>
/// 高级识别矫正（v1.0.0Beta）：在单笔识别之上，分析页面笔迹之间的几何关系，
/// 自动修正课堂手绘的常见瑕疵。纯几何运算（无 IO / 无 UI），后台线程可安全调用；
/// 结果以「矫正动作」返回，由调用方在 UI 线程提交（走撤销栈，一次可整体回退）。
///
/// 三个场景（均可在设置中单独开关）：
///   1) 缝隙闭合 CloseGap —— 两条近乎相交的直线端点差几毫米没碰上，自动延长对齐成闭合拐角；
///   2) 补笔接续 ContinueStroke —— 一笔画完又在后面补一小截，自动接上成一笔；
///   3) 阴影斜杠对齐 HatchAlign —— 矩形内多条斜杠（阴影填充）统一角度、两端吸附到矩形边。
///
/// 说明：本模块是实验特性（Beta），算法基于直线拟合与端点距离启发式，不保证覆盖所有手绘形态。
/// </summary>
internal static class AdvancedCorrector
{
    /// <summary>一次矫正：把 Target 替换为 Replacement，附一句人类可读说明（可空）。</summary>
    public sealed record Correction(StrokeData Target, StrokeData Replacement, string Description);

    // ═══════════ 几何辅助 ═══════════

    private readonly record struct LineFit(bool Ok, Point Origin, Point Dir, double Length, double MaxDev);

    /// <summary>最小二乘式直线判据：以端点连线为轴，逐点算垂距；最大垂距 &lt; 长度 8% 视为直线。</summary>
    private static LineFit FitLine(IReadOnlyList<InkPoint> pts)
    {
        if (pts.Count < 2) return new LineFit(false, default, default, 0, double.MaxValue);
        var a = pts[0];
        var b = pts[^1];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return new LineFit(false, default, default, 0, double.MaxValue);
        dx /= len; dy /= len;
        double maxDev = 0;
        foreach (var p in pts)
        {
            double dev = Math.Abs((p.X - a.X) * -dy + (p.Y - a.Y) * dx);
            if (dev > maxDev) maxDev = dev;
        }
        return new LineFit(maxDev < len * 0.08, new Point(a.X, a.Y), new Point(dx, dy), len, maxDev);
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>两直线求交（参数式，平行返回 null）。</summary>
    private static Point? Intersect(LineFit l1, LineFit l2)
    {
        double d = l1.Dir.X * l2.Dir.Y - l1.Dir.Y * l2.Dir.X;
        if (Math.Abs(d) < 1e-6) return null;   // 平行/共线
        double t = ((l2.Origin.X - l1.Origin.X) * l2.Dir.Y - (l2.Origin.Y - l1.Origin.Y) * l2.Dir.X) / d;
        return new Point(l1.Origin.X + l1.Dir.X * t, l1.Origin.Y + l1.Dir.Y * t);
    }

    /// <summary>夹角（度，0~90）。</summary>
    private static double AngleDeg(Point d1, Point d2)
    {
        double dot = Math.Abs(d1.X * d2.X + d1.Y * d2.Y);
        dot = Math.Clamp(dot, 0, 1);
        return Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static StrokeData CloneWith(StrokeData src, IEnumerable<Point> pts)
    {
        var s = new StrokeData(src.Pen);
        long t = src.Points.Count > 0 ? src.Points[0].TimestampTicks : 0;
        foreach (var p in pts) s.Add(new InkPoint(p.X, p.Y, 0.5, t));
        return s;
    }

    private static Point P(InkPoint p) => new(p.X, p.Y);
    private static Point First(IReadOnlyList<InkPoint> pts) => P(pts[0]);
    private static Point Last(IReadOnlyList<InkPoint> pts) => P(pts[^1]);

    // ═══════════ 场景 1：缝隙闭合 ═══════════

    /// <summary>两条近乎相交的直线端点没碰上时，延长对齐到同一点，形成闭合拐角。
    /// 判据：两笔都是直线；各自的「缺口端」相距 &lt; tolPx；两线夹角在 45°~135°（形成拐角而非平行线）。</summary>
    public static List<Correction> TryCloseGap(StrokeData current, IReadOnlyList<StrokeData> page, double tolPx)
    {
        var empty = new List<Correction>();
        var f1 = FitLine(current.Points);
        if (!f1.Ok || f1.Length < tolPx * 2) return empty;

        StrokeData? best = null;
        LineFit bestFit = default;
        double bestGap = double.MaxValue;
        int bestPair = 0;   // 1 = current.Last↔other.First 等组合
        foreach (var s in page)
        {
            if (ReferenceEquals(s, current) || s.Points.Count < 2) continue;
            var f2 = FitLine(s.Points);
            if (!f2.Ok || f2.Length < tolPx * 2) continue;
            if (AngleDeg(f1.Dir, f2.Dir) is < 45 or > 135) continue;   // 需要形成拐角

            // current 两端 × other 两端：取最小缺口
            var cands = new[]
            {
                (gap: Dist(Last(current.Points), First(s.Points)), pair: 1),
                (gap: Dist(Last(current.Points), Last(s.Points)),  pair: 2),
                (gap: Dist(First(current.Points), First(s.Points)), pair: 3),
                (gap: Dist(First(current.Points), Last(s.Points)),  pair: 4),
            };
            foreach (var (gap, pair) in cands)
                if (gap < bestGap) { bestGap = gap; best = s; bestFit = f2; bestPair = pair; }
        }
        if (best is null || bestGap > tolPx) return empty;

        var x = Intersect(f1, bestFit);
        if (x is not { } inter) return empty;
        // 交点不能离两笔太远（防延长到离谱的位置）
        if (Dist(inter, f1.Origin) > f1.Length * 3 || Dist(inter, bestFit.Origin) > bestFit.Length * 3) return empty;

        var cur = new List<Point>();
        // current 的缺口端替换为交点（保留另一端）
        if (bestPair is 1 or 3) { cur.Add(First(current.Points)); cur.Add(inter); }
        else { cur.Add(inter); cur.Add(Last(current.Points)); }

        var oth = new List<Point>();
        if (bestPair is 1 or 2) { oth.Add(First(best.Points)); oth.Add(inter); }
        else { oth.Add(inter); oth.Add(Last(best.Points)); }

        return new List<Correction>
        {
            new(best, CloneWith(best, oth), "闭合缝隙：两条直线端点没碰上，已延长对齐成拐角"),
            new(current, CloneWith(current, cur), ""),
        };
    }

    // ═══════════ 场景 2：补笔接续 ═══════════

    /// <summary>一笔画完后又在后面补了一小截：若新笔起点贴近已画笔终点、且方向延续，合并成一笔。</summary>
    public static List<Correction> TryContinueStroke(StrokeData current, IReadOnlyList<StrokeData> page, double tolPx)
    {
        var empty = new List<Correction>();
        if (current.Points.Count < 2) return empty;
        StrokeData? best = null; double bestGap = tolPx;
        foreach (var s in page)
        {
            if (ReferenceEquals(s, current) || s.Points.Count < 2) continue;
            double gap = Dist(Last(s.Points), First(current.Points));
            if (gap < bestGap) { bestGap = gap; best = s; }
        }
        if (best is null) return empty;

        // 方向延续：上一笔末段方向 与 新笔首段方向 夹角 < 50°
        var la = P(best.Points[^2]); var lb = Last(best.Points);
        var ca = First(current.Points); var cb = P(current.Points[1]);
        double ddx1 = lb.X - la.X, ddy1 = lb.Y - la.Y;
        double l1 = Math.Sqrt(ddx1 * ddx1 + ddy1 * ddy1);
        double ddx2 = cb.X - ca.X, ddy2 = cb.Y - ca.Y;
        double l2 = Math.Sqrt(ddx2 * ddx2 + ddy2 * ddy2);
        if (l1 < 1e-3 || l2 < 1e-3) return empty;
        if (AngleDeg(new Point(ddx1 / l1, ddy1 / l1), new Point(ddx2 / l2, ddy2 / l2)) > 50) return empty;

        // 合并：上一笔全部点 + 新笔全部点
        var merged = new List<Point>();
        foreach (var p in best.Points) merged.Add(P(p));
        foreach (var p in current.Points) merged.Add(P(p));

        return new List<Correction>
        {
            new(best, CloneWith(best, merged), "补笔接续：后面补的一小截已接上，合成一笔"),
            new(current, CloneWith(current, System.Array.Empty<Point>()), ""),   // 空替换 = 删除被合并的新笔
        };
    }

    // ═══════════ 场景 3：阴影斜杠对齐 ═══════════

    /// <summary>矩形内多条斜杠（阴影填充）：统一角度、两端吸附到矩形轮廓。
    /// 简化策略：以这些斜杠的包围盒为矩形；取所有斜杠方向的平均角；每条斜杠沿该角
    /// 重新铺满矩形、两端落到矩形边界上。</summary>
    public static List<Correction> TryAlignHatch(StrokeData current, IReadOnlyList<StrokeData> page, double tolPx)
    {
        var empty = new List<Correction>();
        var fc = FitLine(current.Points);
        if (!fc.Ok || fc.Length < tolPx) return empty;

        // 收集页面上方向近似（±35°）且长度相近的斜杠候选，连同当前笔
        var hatch = new List<StrokeData> { current };
        foreach (var s in page)
        {
            if (ReferenceEquals(s, current) || s.Points.Count < 2) continue;
            var f = FitLine(s.Points);
            if (!f.Ok) continue;
            if (AngleDeg(fc.Dir, f.Dir) <= 35) hatch.Add(s);
        }
        if (hatch.Count < 3) return empty;   // 少于 3 条不成"阴影"（避免误伤普通线条）

        // 统一角度：平均方向向量（按符号对齐）
        double sx = 0, sy = 0;
        foreach (var s in hatch)
        {
            var f = FitLine(s.Points);
            double dirX = f.Dir.X, dirY = f.Dir.Y;
            if (dirX * fc.Dir.X + dirY * fc.Dir.Y < 0) { dirX = -dirX; dirY = -dirY; }
            sx += dirX; sy += dirY;
        }
        double sl = Math.Sqrt(sx * sx + sy * sy);
        if (sl < 1e-6) return empty;
        var ux = new Point(sx / sl, sy / sl);

        // 包围盒（作为矩形轮廓近似）
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in hatch)
            foreach (var p in s.Points)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
        // 每条斜杠：沿统一方向穿过其质心，端点扩到包围盒边界
        var corrs = new List<Correction>();
        foreach (var s in hatch)
        {
            double cx = 0, cy = 0;
            foreach (var p in s.Points) { cx += p.X; cy += p.Y; }
            cx /= s.Points.Count; cy /= s.Points.Count;
            // 从质心沿 ±u 延伸到包围盒内最大距离
            double half = Math.Max(maxX - minX, maxY - minY);
            var p1 = new Point(cx - ux.X * half, cy - ux.Y * half);
            var p2 = new Point(cx + ux.X * half, cy + ux.Y * half);
            p1 = ClampToBox(p1, minX, minY, maxX, maxY);
            p2 = ClampToBox(p2, minX, minY, maxX, maxY);
            corrs.Add(new Correction(s, CloneWith(s, new[] { p1, p2 }),
                corrs.Count == 0 ? $"阴影对齐：{hatch.Count} 条斜杠已统一角度并吸附到轮廓" : ""));
        }
        return corrs;
    }

    private static Point ClampToBox(Point p, double minX, double minY, double maxX, double maxY)
        => new(Math.Clamp(p.X, minX, maxX), Math.Clamp(p.Y, minY, maxY));

    // ═══════════ 文字/公式防误触 ═══════════

    /// <summary>判断某笔是否"像写字"而非"像图形"：短笔画 + 方向杂乱 + 多笔密集。
    /// 文字/公式的特征是很多短笔画挤在一起、方向无规律；几何图形通常是少数几笔、走向明确。
    /// sensitivity 越大越容易判为文字（越保守）。</summary>
    public static bool LooksLikeText(StrokeData current, IReadOnlyList<StrokeData> page, double pxPerMm, double sensitivity)
    {
        if (pxPerMm <= 0) return false;
        var f = FitLine(current.Points);
        // 短笔画 + 附近同类短笔画数量（局部密度）
        double shortPx = pxPerMm * 12 * sensitivity;   // 「短」的阈值（毫米→像素）
        int nearby = 0;
        var cb = BoundsOf(current.Points);
        foreach (var s in page)
        {
            if (ReferenceEquals(s, current) || s.Points.Count < 2) continue;
            var sb = BoundsOf(s.Points);
            double dist = BoxDist(cb, sb);
            if (dist < pxPerMm * 8 * sensitivity) nearby++;
        }
        // 判据：当前笔短（< 12mm）且周围 0.8cm 内至少 4 笔同量级短笔画 → 大概率在写字
        bool selfShort = f.Ok && f.Length < shortPx;
        return selfShort && nearby >= 4;
    }

    private static Rect BoundsOf(IReadOnlyList<InkPoint> pts)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    private static double BoxDist(Rect a, Rect b)
    {
        double dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        double dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}