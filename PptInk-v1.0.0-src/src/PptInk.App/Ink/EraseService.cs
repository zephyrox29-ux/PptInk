using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PptInk.Seewo;

namespace PptInk.Ink;

/// <summary>消散粒子：擦除时笔迹优雅消散的最小视觉单元。</summary>
public sealed class DissolveParticle
{
    public Point Pos;
    public Vector Vel;
    public double Life = 1.0;      // 1 → 0
    public double Decay;
    public double Size;
    public double Delay;           // 波浪清空：粒子随所属笔迹错峰启动（秒）
    public Color C;
}

/// <summary>
/// 擦除系统（两种范式）：
///  - 抹除：橡皮滑过即擦，笔迹沿路径实时消失（真实板挫体感）；
///  - 圈除：闭合判定 → 命中检测 → 整块消散。
/// 逻辑删除即时生效（滑过即从页面移除），视觉上保留"幽灵笔迹"播放消散动画：
/// 笔迹淡出 + 沿法线方向扩散的微粒，约 450ms 收敛。
/// 抹除路径期间的多次命中在抬笔时打包为一次撤销指令（一次撤销恢复整批）。
/// </summary>
public sealed class EraseService
{
    private readonly InkDocument _doc;
    private DeviceProfile _profile;
    private readonly List<StrokeData> _ghosts = new();
    private readonly List<DissolveParticle> _particles = new(512);
    private readonly List<StrokeData> _pendingRemoved = new();
    private readonly List<StrokeData> _pixelOriginals = new();   // v0.9.6 像素擦除会话：被切分的原笔
    private readonly List<StrokeData> _pixelKeep = new();        // \u4fdd\u7559\u6bb5\uff08\u62ac\u7b14\u6253\u5305 1 \u6761\u5386\u53f2\uff09
    private readonly DispatcherTimer? _anim;
    private DateTime _lastFrame;

    /// <summary>渲染互斥锁（锁序恒为 engine.Gate → RenderGate，输入/渲染两端一致防死锁）：
    /// 墨迹线程在引擎锁内写入 ghost/粒子，UI 线程（DispatcherTimer.Step 与 OnRender）读写，
    /// List 并发读写是未定义行为（枚举版本冲突 / 内部数组撕裂）。</summary>
    internal object RenderGate { get; } = new();

    public IReadOnlyList<StrokeData> Ghosts => _ghosts;
    public IReadOnlyList<DissolveParticle> Particles => _particles;
    /// <summary>消散动画帧事件（渲染层重绘 ghost 与粒子）。</summary>
    public event EventHandler? GhostFrame;
    /// <summary>消散动画全部结束（渲染层可整页重合成清屏）。</summary>
    public event EventHandler? GhostSettled;

    public EraseService(InkDocument doc, DeviceProfile profile)
    {
        _doc = doc;
        _profile = profile;
        if (Application.Current?.Dispatcher is { } d)
        {
            _anim = new DispatcherTimer(DispatcherPriority.Render, d) { Interval = TimeSpan.FromMilliseconds(16) };
            _anim.Tick += (_, _) => Step();
        }
    }

    public void ApplyProfile(DeviceProfile profile) => _profile = profile;

    // ———————————— 抹除：滑过即擦 ————————————

    /// <summary>橡皮路径推进：对新增路径段做命中，命中笔立即移除并入消散动画。
    /// v0.9.6：按 AppConfig.EraseMode 分流 —— "pixel"（默认）为像素级切分（只擦轨迹覆盖的局部）；
    /// "stroke" 为整笔擦除。
    /// v0.9.7 性能：像素模式只用<b>新增路径段</b>切分（滑过的区域已被擦，重复扫描是浪费），
    /// 并用包围盒快速排除未命中笔 —— 复杂板书场景从 O(n×m_total) 降到 O(n×m_delta)。</summary>
    public void EraseAlongPath(InkPage page, List<Point> path, double radiusPx)
    {
        if (path.Count < 2) return;
        if (Core.AppConfig.Current.EraseMode == "stroke")
        {
            lock (RenderGate)
            {
                var tail = path[^2..];
                List<StrokeData> hit = new(4);
                foreach (var s in page.Strokes)
                    if (HitByPathFast(s, tail, radiusPx)) hit.Add(s);
                if (hit.Count == 0) return;
                RemoveAndGhost(page, hit);
            }
            return;
        }
        lock (RenderGate) ErasePartial(page, path, radiusPx);
    }

    /// <summary>像素级切分（v0.9.6）：对命中笔逐点判定是否被抹到（点到完整路径距离 ≤ 半径），
    /// 保留未被抹到的连续段为新笔（原笔移除并消散）。
    /// v0.9.7 性能：只用自上次调用以来的<b>新增路径段</b>（_pixelPathCursor 游标跟踪），
    /// 避免每个 Move 事件都对完整累积路径做 O(n×m) 扫描 —— 长擦除手势从掉帧降到平稳。</summary>
    private int _pixelPathCursor;   // 上次切分扫描到的路径下标（不含）

    private void ErasePartial(InkPage page, List<Point> path, double radiusPx)
    {
        // 只取新增路径段（自上次切分以来的移动）：首段需要 ≥2 点才构成线段
        int startIdx = Math.Max(0, _pixelPathCursor - 1);   // 回退 1 点保证与上次段连续
        if (path.Count - startIdx < 2) return;
        var delta = path[startIdx..];

        List<StrokeData> hits = new(4);
        foreach (var s in page.Strokes)
            if (HitByPathFast(s, delta, radiusPx)) hits.Add(s);
        _pixelPathCursor = path.Count;   // 无论是否命中都推进游标（未命中区已是空白）
        if (hits.Count == 0) return;

        foreach (var s in hits)
        {
            // 切分仍用<b>当前新增段</b>：已擦区域对应的笔点早已被移除/切走，不会重复出现
            var keep = SplitStroke(s, delta, radiusPx);
            page.Strokes.Remove(s);
            foreach (var k in keep) page.Strokes.Add(k);
            s.DissolveProgress = 0; s.DissolveDelay = 0;
            _ghosts.Add(s);
            _pixelOriginals.Add(s);
            _pixelKeep.AddRange(keep);
        }
        page.MarkVisualDirty();
        StartAnim();
        _lastFrame = DateTime.UtcNow;
    }

    /// <summary>包围盒快速排除 + 精确命中（v0.9.7）：先比较笔的 Bounds 与路径段包围盒
    ///（含半径外扩），不相交直接返回 false —— 对屏幕上大多数远离橡皮的笔省掉
    /// O(n×m) 的逐点逐段扫描。命中检测本身不变。</summary>
    private static bool HitByPathFast(StrokeData s, List<Point> path, double radiusPx)
    {
        if (path.Count == 0 || s.Points.Count == 0) return false;
        // 路径段包围盒（含半径外扩）
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in path)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        var pathBounds = new Rect(minX - radiusPx, minY - radiusPx,
            (maxX - minX) + radiusPx * 2, (maxY - minY) + radiusPx * 2);
        // 笔的包围盒与路径包围盒快速排除
        if (!pathBounds.IntersectsWith(s.Bounds)) return false;
        // 精确命中（原 HitByErasePath 逻辑）
        return s.HitByErasePath(path, radiusPx);
    }

    /// <summary>将一笔按擦除轨迹切分：返回保留段（每段 ≥ 2 点；空 = 整笔被擦）。
    /// v0.9.7 性能：逐点判定前先用点与路径包围盒粗筛（BoxContains），
    /// 粗筛通过才做 O(m) 精确扫描 —— 笔上远离橡皮的点 O(1) 跳过。</summary>
    private static List<StrokeData> SplitStroke(StrokeData s, List<Point> path, double radiusPx)
    {
        var segs = new List<StrokeData>();
        StrokeData? cur = null;
        // 路径包围盒（含半径外扩）：点不在盒内必未被擦
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in path)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        double bx0 = minX - radiusPx, by0 = minY - radiusPx, bx1 = maxX + radiusPx, by1 = maxY + radiusPx;

        foreach (var p in s.Points)
        {
            // 粗筛：点远在路径包围盒外 → 必未被擦，O(1) 跳过精确扫描
            bool erased = p.X >= bx0 && p.X <= bx1 && p.Y >= by0 && p.Y <= by1
                          && DistPointToPath(p, path) <= radiusPx;
            if (erased)
            {
                if (cur is not null) { if (cur.Points.Count >= 2) segs.Add(cur); cur = null; }
            }
            else
            {
                cur ??= new StrokeData(s.Pen);
                cur.Add(p);
            }
        }
        if (cur is not null && cur.Points.Count >= 2) segs.Add(cur);
        return segs;
    }

    private static double DistPointToPath(InkPoint p, List<Point> path)
    {
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++)
            best = Math.Min(best, DistPointToSeg(p, path[i - 1], path[i]));
        return best;
    }

    private static double DistPointToSeg(InkPoint p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double ex = p.X - (a.X + t * dx), ey = p.Y - (a.Y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>抬笔：把本次抹除累计命中的整批笔打包成一条撤销指令。
    /// v0.9.6：像素模式下把「原笔 → 保留段」打包为一条 ReplaceStrokesOp（一次撤销整批复原）。
    /// v0.9.7：同时重置像素路径游标（下次擦除从路径头开始扫描）。</summary>
    public void FlushErase(InkPage page)
    {
        lock (RenderGate)
        {
            _pixelPathCursor = 0;   // v0.9.7：会话结束重置游标，下一次擦除从路径头开始
            if (_pixelOriginals.Count > 0)
            {
                page.History.CommitPrepared(new ReplaceStrokesOp(_pixelOriginals, _pixelKeep));
                _pixelOriginals.Clear();
                _pixelKeep.Clear();
            }
            if (_pendingRemoved.Count == 0) return;
            var op = EraseStrokesOp.PrepareApplied(page, _pendingRemoved);
            page.History.CommitPrepared(op);
            _pendingRemoved.Clear();
        }
    }

    // ———————————— 圈除：闭合判定 + 整块消散 ————————————

    /// <summary>圈选结束：判定闭合 → 命中（≥70% 采样点在多边形内）→ 整块消散。</summary>
    public void TryLassoErase(InkPage page, List<Point> lasso)
    {
        if (lasso.Count < 3) return;

        lock (RenderGate)
        {
            // v0.9.6：未闭合时按首尾相连的闭合多边形处理；仅保留面积极小保护。
            if (PolygonArea(lasso) < 4096) return;
            List<StrokeData> hit = new(8);
            foreach (var s in page.Strokes)
                if (s.HitByLassoPolygon(lasso)) hit.Add(s);
            if (hit.Count == 0) return;

            RemoveAndGhost(page, hit);
            // 审查修复：原先误用 _pendingRemoved（抹除模式的跨笔累计）打包 —— 若抹除被翻页中断
            // （残留未 Flush），圈除会把旧批混进本次提交（旧笔 IndexOf=-1 被跳过，其历史恢复永久丢失）。
            // 圈除是一次性手势：只提交本次命中的 hit。
            var op = EraseStrokesOp.PrepareApplied(page, hit);
            page.History.CommitPrepared(op);
            _pendingRemoved.Clear();   // RemoveAndGhost 已把 hit 累计进来，清掉防串入下批
        }
    }

    /// <summary>多边形面积（鞋带公式，取绝对值）。圈选的最小面积保护用。</summary>
    private static double PolygonArea(List<Point> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y);
        return Math.Abs(a) / 2;
    }

    /// <summary>
    /// 将一组已从页面移除的笔迹转为消散动画（滑动清空整页复用此路径：
    /// ClearPageOp 已完成逻辑移除，此处只负责"优雅地消失"的视觉）。
    /// </summary>
    public void GhostStrokes(IReadOnlyList<StrokeData> removed)
    {
        if (removed.Count == 0) return;
        lock (RenderGate)
        {
            foreach (var s in removed)
            {
                s.DissolveProgress = 0;
                s.DissolveDelay = 0;
                _ghosts.Add(s);
                if (!Core.AppConfig.Current.LightweightRendering)
                    _particles.AddRange(SpawnParticles(s, delay: 0)); // 纯装饰粒子可由轻量渲染关闭
            }
            StartAnim();
            _lastFrame = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 波浪消散（滑动清空专属，第四轮仪式感）：
    /// 按笔迹水平中心位置错峰启动（左先右后，delay = 归一化 × 0.42s），
    /// 延迟期内笔迹保持可见 → 整页像被"一浪扫过"依次化开，而非齐刷闪没。
    /// </summary>
    public void GhostStrokesWave(IReadOnlyList<StrokeData> removed)
    {
        if (removed.Count == 0) return;
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var s in removed)
        {
            if (s.Points.Count == 0) continue;
            var b = s.Bounds;
            minX = Math.Min(minX, b.Left);
            maxX = Math.Max(maxX, b.Right);
        }
        double span = Math.Max(1.0, maxX - minX);
        var rnd = Random.Shared;
        // 粒子总量预算 300：整页清空时按笔数摊薄（每笔 2~16 个），保证 4K 帧率稳定
        const int particleBudget = 300;
        int perStroke = (int)Math.Clamp(particleBudget / Math.Max(removed.Count, 1), 2, 16);

        lock (RenderGate)
        {
            foreach (var s in removed)
            {
                s.DissolveProgress = 0;
                double norm = s.Points.Count == 0 ? 0
                    : Math.Clamp((s.Bounds.Left + s.Bounds.Width / 2 - minX) / span, 0, 1);
                s.DissolveDelay = norm * 0.42;                       // 错峰：水平位置 → 启动延迟
                _ghosts.Add(s);
                if (!Core.AppConfig.Current.LightweightRendering)
                    _particles.AddRange(SpawnParticles(s, delay: s.DissolveDelay + rnd.NextDouble() * 0.08, cap: perStroke));
            }
            StartAnim();
            _lastFrame = DateTime.UtcNow;
        }
    }

    // ———————————— 清空扫光带（仪式感的"一挥而过"） ————————————

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _sweepStartMs = double.NaN;
    /// <summary>扫光总时长：须覆盖"波浪错峰(0.42s) + 单笔消散(0.45s)"的整个化开期，
    /// 否则柔光带先于右端笔迹结束（v0.9.2 擦除复查：原 640ms 与注释所述"与波浪消散同步"不符）。</summary>
    private const double SweepMs = 880.0;

    /// <summary>启动扫光带：240px 柔光带横扫整屏（与波浪消散同步，清空的"扫过"实感）。</summary>
    public void BeginSweep()
    {
        lock (RenderGate)
            _sweepStartMs = Core.AppConfig.Current.LightweightRendering ? double.NaN : _clock.ElapsedMilliseconds;
    }

    /// <summary>扫光进度 0~1（NaN = 未在扫或轻量渲染关闭该视觉层）。</summary>
    public double SweepProgress => Core.AppConfig.Current.LightweightRendering || double.IsNaN(_sweepStartMs) || SweepDone
        ? double.NaN : (_clock.ElapsedMilliseconds - _sweepStartMs) / SweepMs;

    private bool SweepDone => !double.IsNaN(_sweepStartMs) && _clock.ElapsedMilliseconds - _sweepStartMs > SweepMs;

    // ———————————— 消散动画 ————————————

    private void RemoveAndGhost(InkPage page, List<StrokeData> removed)
    {
        lock (RenderGate)   // 可重入：public 入口已持 RenderGate（Monitor 同线程重入不阻塞）
        {
            foreach (var s in removed)
            {
                page.Strokes.Remove(s);
                _pendingRemoved.Add(s);
                s.DissolveProgress = 0;
                s.DissolveDelay = 0;
                _ghosts.Add(s);
                if (!Core.AppConfig.Current.LightweightRendering)
                    _particles.AddRange(SpawnParticles(s, delay: 0));
            }
            StartAnim();
            _lastFrame = DateTime.UtcNow;
        }
    }

    /// <summary>沿笔迹采样 10~16 个粒子（可被预算上限压制），速度取随机方向 + 微上飘，
    /// 模拟"墨屑散开"。cap 用于整页清空时按笔数摊薄 —— 否则百笔整页会产生上千粒子，
    /// 每帧 O(n) 更新叠加全层重绘，在 4K 上会掉帧。</summary>
    private List<DissolveParticle> SpawnParticles(StrokeData s, double delay, int? cap = null)
    {
        var list = new List<DissolveParticle>(14);
        if (s.Points.Count < 2) return list;
        var rnd = Random.Shared;
        int n = Math.Clamp(s.Points.Count / 8, 10, 16);
        if (cap is int c)
            n = Math.Max(1, Math.Min(n, c));
        for (int i = 0; i < n; i++)
        {
            var p = s.Points[rnd.Next(s.Points.Count)];
            var dir = rnd.NextDouble() * Math.PI * 2;
            double speed = 40 + rnd.NextDouble() * 90; // px/s
            list.Add(new DissolveParticle
            {
                Pos = new Point(p.X, p.Y),
                Vel = new Vector(Math.Cos(dir) * speed, Math.Sin(dir) * speed - 30), // 微微上飘
                Decay = 2.0 + rnd.NextDouble() * 1.5, // 生命衰减速率（1/s）
                Size = _profile.MmToPx(0.4 + rnd.NextDouble() * 0.5),
                Delay = delay,
                C = s.Pen.Color,
            });
        }
        return list;
    }

    /// <summary>线程安全启动消散动画：DispatcherTimer 成员非线程安全，
    /// 墨迹线程调用时必须封送回其 Dispatcher（审查修复：跨线程直接 Start 是未定义行为）。</summary>
    private void StartAnim()
    {
        if (_anim is null) return;
        if (_anim.Dispatcher.CheckAccess()) _anim.Start();
        else _anim.Dispatcher.BeginInvoke(new Action(_anim.Start)); // 重复 Start 幂等，多投无副作用
    }

    private void Step()
    {
        bool settled;
        lock (RenderGate)   // 与墨迹线程写入 / OnRender 读取互斥（Step 本身在 UI 线程跑）
        {
            var now = DateTime.UtcNow;
            double dt = (now - _lastFrame).TotalSeconds;
            _lastFrame = now;
            dt = Math.Min(dt, 0.05); // 防掉帧补爆

            bool anyAlive = false;
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                var g = _ghosts[i];
                if (g.DissolveDelay > 0)
                {
                    g.DissolveDelay -= dt;   // 波浪错峰：延迟期内保持可见（等待被"扫到"）
                    anyAlive = true;
                    continue;
                }
                g.DissolveProgress += dt / 0.45; // 450ms 消散
                if (g.DissolveProgress >= 1.0)
                {
                    _ghosts.RemoveAt(i);
                    continue;
                }
                anyAlive = true;
            }

            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                if (p.Delay > 0)
                {
                    p.Delay -= dt;           // 粒子随所属笔迹错峰启动
                    anyAlive = true;
                    continue;
                }
                p.Pos += p.Vel * dt;
                p.Vel *= 0.96;                    // 阻尼
                p.Vel += new Vector(0, 60 * dt);  // 轻微重力回落
                p.Life -= p.Decay * dt;
                if (p.Life <= 0) _particles.RemoveAt(i);
            }

            settled = !anyAlive && _particles.Count == 0;
            if (settled) _sweepStartMs = double.NaN;   // 消散收敛：扫光带一并复位
        }

        GhostFrame?.Invoke(this, EventArgs.Empty);     // 事件移出锁外：订阅方重绘/回调不再放大临界区
        if (settled)
        {
            _anim?.Stop();                             // Stop 只在 UI 线程（Tick）执行，安全
            GhostSettled?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void OnGhostFrameFromParticles(StrokeData ghost) { /* 预留：单笔粒子的定向接口 */ }
}
