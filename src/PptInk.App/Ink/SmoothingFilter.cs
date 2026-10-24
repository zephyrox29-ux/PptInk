namespace PptInk.Ink;

/// <summary>
/// 针对希沃红外触摸框的报点整形（v0.9.7 书写体验重构）：
///  1) <b>OneEuro 自适应滤波</b>替代固定 EMA：低速（停顿/慢写）时强平滑防抖，高速（快写/连笔）
///     时弱平滑低延迟 —— 解决「慢写抖、快写滞后」的矛盾，红外框抖动与跟手性兼得；
///  2) 二次 Bezier 拟合：平滑点流 → (起点, 控制点, 终点) 三元组序列，渲染走 GPU 贝塞尔；
///  3) 断触插值补点：报点丢失 ≤100ms 时按速度线性插值，防"一笔断两截"；
///  4) 速度上限裁剪：红外框偶发飞点（瞬时位移 > 阈值）丢弃，属噪声而非书写。
///
/// OneEuro 滤波原理（Casiez et al., CHI 2012）：
///   α = 1 / (1 + τ/Te)，其中 τ = 1/(2π·fc)，fc 为截止频率
///   截止频率随速度自适应：fc = fcmin + β·|dx/dt|
///   → 慢速：fc ≈ fcmin（强滤波）；快速：fc 升高（弱滤波，接近原始点）
/// 对 X、Y 各维护一个滤波器；导数（速度）单独用一个低通滤波器估计。
/// </summary>
public sealed class SmoothingFilter
{
    private readonly double _alpha;          // 固定 EMA 系数的回退值（兼容路径用）
    private readonly double _maxJumpPx;      // 飞点判定（px）：两报点间最大合理位移
    private readonly double _dropoutMs;      // 断触判定窗口：超过则插值补点
    private readonly List<InkPoint> _smoothed = new(256);
    private InkPoint? _lastRaw;

    // —— OneEuro 滤波状态（v0.9.7）——
    private readonly OneEuroFilter _fx;
    private readonly OneEuroFilter _fy;

    /// <summary>构造：alpha 保留为机型档案的调参接口（旧 EMA 系数语义），
    /// v0.9.7 内部将其映射为 OneEuro 的 minCutoff/beta ——
    /// 档案 alpha 越大（原平滑越弱）→ OneEuro minCutoff 越高（快写更跟手）；
    /// 档案 alpha 越小（原平滑越强）→ OneEuro minCutoff 越低（慢写更稳）。
    /// v0.9.9 快速触摸书写调参：minCutoff 基准 1.0→1.2（慢写滞后减小），
    /// beta 0.020→0.045（快写时截止频率提得更快，轨迹跟手不滞后、转角不粘滞）。
    /// dCutoff 固定 1.0 Hz（速度估计低通，防速度尖峰污染截止频率）。</summary>
    public SmoothingFilter(double alpha = 0.45, double maxJumpPx = 480, double dropoutMs = 100)
    {
        _alpha = Math.Clamp(alpha, 0.05, 0.95);
        _maxJumpPx = maxJumpPx;
        _dropoutMs = dropoutMs;
        // OneEuro 参数：minCutoff 随 alpha 线性映射；beta 速度响应。
        double minCutoff = Math.Clamp(1.2 + (_alpha - 0.45) * 3.0, 0.8, 1.8);
        _fx = new OneEuroFilter(minCutoff, beta: 0.045, dCutoff: 1.0);
        _fy = new OneEuroFilter(minCutoff, beta: 0.045, dCutoff: 1.0);
    }

    /// <summary>喂入原始报点（120Hz+），内部平滑后缓存。</summary>
    public void Feed(in InkPoint raw)
    {
        // 飞点裁剪：单报点位移超过物理可能（红外框反射串扰）直接丢弃
        if (_lastRaw is { } last && Distance(last, raw) > _maxJumpPx)
        {
            if (raw.TimestampTicks - last.TimestampTicks < 8) return; // 亚采样间隔内的跳变视为噪声
        }

        // 断触插值：报点丢失窗口内按匀速补 2~3 个点，笔迹连贯
        //（TimestampTicks 为 TickCount64 毫秒语义，与 dropoutMs 直接比较）
        if (_lastRaw is { } l && raw.TimestampTicks - l.TimestampTicks > (long)_dropoutMs)
        {
            int fills = 3;
            for (int i = 1; i <= fills; i++)
            {
                double t = i / (double)(fills + 1);
                var mid = new InkPoint(
                    l.X + (raw.X - l.X) * t, l.Y + (raw.Y - l.Y) * t,
                    l.Pressure + (raw.Pressure - l.Pressure) * t,
                    l.TimestampTicks + (long)((raw.TimestampTicks - l.TimestampTicks) * t));
                PushSmoothed(mid);
            }
        }

        if (_smoothed.Count == 0)
        {
            _smoothed.Add(raw); // 首点直通：第一笔零延迟
            _fx.Reset(); _fy.Reset();
            _fx.Prime(raw.X, raw.TimestampTicks);
            _fy.Prime(raw.Y, raw.TimestampTicks);
        }
        else
        {
            var prev = _smoothed[^1];
            // v0.9.7 OneEuro：低速强平滑防抖、高速弱平滑低延迟。
            // 时间戳取 QPC 毫秒域（与 InkPoint.TimestampTicks 同源），内部自行换算秒。
            double sx = _fx.Filter(raw.X, raw.TimestampTicks);
            double sy = _fy.Filter(raw.Y, raw.TimestampTicks);
            double sp = prev.Pressure + 0.35 * (raw.Pressure - prev.Pressure);   // 压力仍用缓 EMA（避免粗细抖动）
            _smoothed.Add(new InkPoint(sx, sy, sp, raw.TimestampTicks));
        }

        _lastRaw = raw;
    }

    private void PushSmoothed(in InkPoint p)
    {
        if (_smoothed.Count == 0) { _smoothed.Add(p); return; }
        // 插值补点走同一 OneEuro 状态机，保持滤波连续性
        double sx = _fx.Filter(p.X, p.TimestampTicks);
        double sy = _fy.Filter(p.Y, p.TimestampTicks);
        var prev = _smoothed[^1];
        double sp = prev.Pressure + 0.35 * (p.Pressure - prev.Pressure);
        _smoothed.Add(new InkPoint(sx, sy, sp, p.TimestampTicks));
    }

    /// <summary>取当前平滑点流（增量渲染用）。
    /// 返回内部列表的活引用（零拷贝）：调用方必须与 Feed 同互斥域（InkEngine.Gate 内）访问，
    /// 锁外跨线程枚举会与追加中的写入竞态（List 版本冲突抛异常）。</summary>
    public IReadOnlyList<InkPoint> EmitSmoothed() => _smoothed;

    /// <summary>隔点抽稀折半平滑点流（保留偶数索引）。与 <see cref="StrokeData.CompactPoints"/> 配套：
    /// 引擎在活笔点数触顶的同一时机把两侧一起抽稀 —— 本列表只增不减的话，Points 与平滑流索引
    /// 错位会让引擎的逐索引补点循环重放笔迹中段且永不终止（v1.0.1 修复）。
    /// 滤波状态（OneEuro）不受影响：仅重排已产出的平滑点，后续 Feed 照常续流。</summary>
    public void CompactSmoothed()
    {
        if (_smoothed.Count < 2) return;
        var kept = new List<InkPoint>(_smoothed.Count / 2 + 1);
        for (int i = 0; i < _smoothed.Count; i += 2) kept.Add(_smoothed[i]);
        _smoothed.Clear();
        _smoothed.AddRange(kept);
    }

    /// <summary>
    /// 二次 Bezier 拟合：相邻平滑点取中点为段端点、原点为控制点，
    /// 一次遍历 O(n) 生成 C1 连续曲线（经典中点法）。
    /// </summary>
    public IReadOnlyList<(InkPoint Start, InkPoint Ctrl, InkPoint End)> BuildBezierSegments()
    {
        var result = new List<(InkPoint, InkPoint, InkPoint)>(_smoothed.Count);
        int n = _smoothed.Count;
        if (n < 3)
        {
            if (n == 2) result.Add((_smoothed[0], Mid(_smoothed[0], _smoothed[1]), _smoothed[1]));
            return result;
        }

        var start = _smoothed[0];
        for (int i = 1; i < n - 1; i++)
        {
            var ctrl = _smoothed[i];
            var end = Mid(ctrl, _smoothed[i + 1]);
            result.Add((start, ctrl, end));
            start = end;
        }
        result.Add((start, _smoothed[n - 1], _smoothed[n - 1])); // 收尾段退化为直线
        return result;
    }

    private static InkPoint Mid(in InkPoint a, in InkPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Pressure + b.Pressure) / 2, b.TimestampTicks);

    private static double Distance(in InkPoint a, in InkPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// OneEuro 滤波器（v0.9.7）：低速强防抖、高速低延迟的书写平滑核心。
    /// 参考 Casiez, Roussel, Vogel "1€ Filter" (CHI 2012)。
    ///
    /// 工作流：
    ///  1) 用低通滤波估计导数（速度）dx；
    ///  2) 截止频率 fc = fcmin + beta·|dx|（速度越快截止越高 → 滤波越弱 → 延迟越低）；
    ///  3) 用 fc 计算 α 并对原值做一阶低通。
    /// 坐标单位为文档像素，时间戳为 QPC 毫秒（内部换算秒）。
    /// </summary>
    private sealed class OneEuroFilter
    {
        private readonly double _minCutoff;   // 慢速截止频率（Hz）：越小慢写越稳
        private readonly double _beta;        // 速度系数：越大快写越跟手
        private readonly double _dCutoff;     // 导数低通截止频率（Hz）
        private double _xPrev;                // 上次滤波输出
        private double _dxPrev;               // 上次导数估计
        private long _tPrev;                  // 上次时间戳（QPC ms）
        private bool _hasPrev;

        public OneEuroFilter(double minCutoff, double beta, double dCutoff)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        public void Reset() { _hasPrev = false; _dxPrev = 0; }

        public void Prime(double x, long tMs) { _xPrev = x; _tPrev = tMs; _hasPrev = true; _dxPrev = 0; }

        private static double Alpha(double cutoffHz, double dtSec)
        {
            double tau = 1.0 / (2.0 * Math.PI * cutoffHz);
            return 1.0 / (1.0 + tau / dtSec);
        }

        public double Filter(double x, long tMs)
        {
            if (!_hasPrev) { Prime(x, tMs); return x; }
            // 时间差（秒）：QPC 毫秒 → 秒；钳制到合理区间防异常
            double dt = Math.Clamp((tMs - _tPrev) / 1000.0, 0.0005, 0.1);
            // 1) 导数估计（速度）：原始差分 → 低通滤波
            double dx = (x - _xPrev) / dt;
            double aD = Alpha(_dCutoff, dt);
            double dxHat = _dxPrev + aD * (dx - _dxPrev);
            // 2) 自适应截止频率：速度越快截止越高
            double cutoff = _minCutoff + _beta * Math.Abs(dxHat);
            // 3) 一阶低通滤波
            double a = Alpha(cutoff, dt);
            double xHat = _xPrev + a * (x - _xPrev);
            // 更新状态
            _xPrev = xHat;
            _dxPrev = dxHat;
            _tPrev = tMs;
            return xHat;
        }
    }
}
