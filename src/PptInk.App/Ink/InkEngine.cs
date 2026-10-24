using System.Windows;
using PptInk.Core;
using PptInk.Seewo;

namespace PptInk.Ink;

public enum InkTool
{
    /// <summary>触摸操作模式：墨迹层穿透，触摸直通 PPT（翻页/点击链接）。</summary>
    TouchThrough,
    Pen,
    Highlighter,
    /// <summary>抹除：橡皮滑过即擦，笔迹沿路径实时消失。</summary>
    EraseStroke,
    /// <summary>圈除：圈选闭合区域，整块擦除 + 消散动画。</summary>
    EraseLasso,
    /// <summary>滑动清空：在页面快速一挥 → 整页笔迹消散（可一次撤销复原）。</summary>
    EraseClearSwipe,
}

public enum InvalidateReason
{
    LiveStroke,      // 正在书写：增量段
    StrokeFinished,  // 抬笔：该笔后台精修重绘
    ErasePreview,    // 抹除/圈选进行中：引导线
    StrokesRemoved,  // 擦除落定：整页重合成
    PageSwitched,    // 翻页：整页重合成
    HistoryChanged,  // 撤销/重做/分支跳转
    GhostFrame,      // 消散动画帧
}

public sealed class InkVisualInvalidated : EventArgs
{
    public required InvalidateReason Reason { get; init; }
    /// <summary>脏矩形（3840×2160 文档逻辑像素）。整页重合成时为空 Rect。</summary>
    public Rect DirtyPx { get; init; }
}

/// <summary>
/// 墨迹引擎：输入 → 平滑 → 存储 → 失效通知。
/// 输入来自 StylusPlugIn 墨迹线程（绕过 UI 消息泵，第一笔零延迟），
/// 渲染层（InkOverlayWindow）订阅 VisualInvalidated 按 60fps 合成。
/// "输入即画 + 后台精修"：Move 期间产出低质量快段，Up 后精修该笔。
/// </summary>
public sealed class InkEngine
{
    private readonly InkDocument _doc;
    private readonly DeviceProfileStore _profiles;
    private readonly EraseService _eraser;
    private readonly object _gate = new();

    private StrokeData? _live;
    private SmoothingFilter _filter = new();
    private readonly List<Point> _erasePath = new(128);
    private readonly List<Point> _lassoPath = new(256);
    private readonly List<Point> _swipePath = new(64);
    private long _swipeStartTick;
    private int _lastRenderedLiveIndex;
    private InkTool _tool = InkTool.Pen;

    /// <summary>内部锁（渲染层读取 Strokes/LiveStroke 时也必须持此锁）：
    /// 墨迹线程（StylusPlugIn）写、UI 线程读，List 并发访问是未定义行为。</summary>
    internal object Gate => _gate;

    public InkTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;      // 同值短路：防编排器 ↔ 工具栏高亮同步的死循环
            // v0.9.2（擦除复查修复）：工具切换 = 隐式抬笔 —— 抹除批必须在此收口。
            // 否则「抹到一半换工具」（另一只手点工具栏）会让已抹笔迹不进历史（无法撤销恢复，
            // 历史黑洞），残留批还会串入下一次 FlushErase（一次撤销误恢复两批，粒度错乱）。
            if (_tool is InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe)
            {
                _eraser.FlushErase(CurrentPage);     // 抹除批打包提交（空批自助返回）
                _erasePath.Clear();
                _lassoPath.Clear();
                _swipePath.Clear();
            }
            _tool = value;
            ToolChanged?.Invoke(value);
        }
    }
    public PenInfo Pen { get; set; }
    public double EraserRadiusMm { get; set; } = 18;
    public int CurrentSlideIndex { get; private set; } = 1;

    /// <summary>上次书写工具（pen/highlighter）——"清空后智能切回"的依据（smart 模式）。</summary>
    public InkTool LastWriteTool { get; private set; } = InkTool.Pen;

    /// <summary>落笔微反馈：落笔原始样本（渲染层画 180ms 扩张淡圈；null = 无进行中的反馈；过期由渲染层清理）。</summary>
    public InkPoint? PenDownFeedback { get; internal set; }

    public event EventHandler<InkVisualInvalidated>? VisualInvalidated;

    /// <summary>工具被外部（清空后智能切换/插件）改变 → 编排器同步工具栏高亮并收回浮岛。</summary>
    public event Action<InkTool>? ToolChanged;

    /// <summary>书写工具落笔（墨迹线程触发；编排器借此收回浮岛，书写不被面板遮挡）。</summary>
    public event Action? PenDown;

    /// <summary>一笔提交完成（抬笔）。负载 = 整笔 StrokeData：最近使用颜色沉淀 + AI 停顿自动识别都以此为目标。</summary>
    public event Action<StrokeData>? StrokeCommitted;

    public InkEngine(InkDocument doc, DeviceProfileStore profiles, string profileId, EraseService eraser)
    {
        _doc = doc;
        _profiles = profiles;
        _eraser = eraser;
        Profile = profiles.Get(profileId);
        Pen = new PenInfo { ColorHex = AppConfig.Current.DefaultColor, ThicknessMm = AppConfig.Current.DefaultThicknessMm };
        EraserRadiusMm = AppConfig.Current.EraserRadiusMm;   // v0.9.6：橡皮大小跟随设置
    }

    public DeviceProfile Profile { get; private set; }

    public InkPage CurrentPage => _doc.GetPage(CurrentSlideIndex);
    public StrokeData? LiveStroke => _live;
    public int LiveRenderedUpto => _lastRenderedLiveIndex;
    public IReadOnlyList<Point> LiveErasePath => _erasePath;
    public IReadOnlyList<Point> LiveLasso => _lassoPath;

    /// <summary>运行中切换机型档案（设置页可换）。</summary>
    public void ApplyProfile(string profileId)
    {
        Profile = _profiles.Get(profileId);
        _eraser.ApplyProfile(Profile);
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
    }

    /// <summary>翻页：以幻灯片索引为键，翻回自动恢复该页墨迹。</summary>
    public void SwitchPage(int slideIndex)
    {
        lock (_gate)
        {
            CancelLiveStroke();
            CurrentSlideIndex = Math.Max(1, slideIndex);
        }
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.PageSwitched });
    }

    // ———————— 书写输入（StylusPlugIn 墨迹线程调用） ————————

    public void OnStylusDown(in InkPoint p)
    {
        bool isWriteTool = false;
        lock (_gate)
        {
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    var pen = Tool == InkTool.Highlighter
                        ? Pen with { Highlighter = true, ColorHex = AppConfig.Current.HighlightColor, ThicknessMm = Math.Max(Pen.ThicknessMm, 8) }
                        : Pen;
                    _filter = new SmoothingFilter(Profile.EmaAlpha, Profile.MaxJumpPx * Profile.CanvasScale, Profile.DropoutMs);
                    _live = new StrokeData(pen);
                    _live.Add(p);
                    _lastRenderedLiveIndex = 0;
                    LastWriteTool = Tool;                                    // 清空后"智能切回"依据
                    PenDownFeedback = p;                                   // 落笔微反馈（淡圈）
                    isWriteTool = true;
                    break;
                case InkTool.EraseStroke:
                    _erasePath.Clear();
                    _erasePath.Add(new Point(p.X, p.Y));
                    _lastEraseExecTick = p.TimestampTicks - EraseExecIntervalMs;   // v0.9.7：首 Move 即执行擦除
                    break;
                case InkTool.EraseLasso:
                    _lassoPath.Clear();
                    _lassoPath.Add(new Point(p.X, p.Y));
                    break;
                case InkTool.EraseClearSwipe:
                    _swipePath.Clear();
                    _swipePath.Add(new Point(p.X, p.Y));
                    _swipeStartTick = p.TimestampTicks;
                    break;
            }
        }
        if (isWriteTool) PenDown?.Invoke();   // lock 外触发：订阅方自行 Dispatcher（浮岛收回等 UI 动作）
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.LiveStroke, DirtyPx = RectAround(p, Profile.MmToPx(Pen.ThicknessMm)) });
    }

    public void OnStylusMove(in InkPoint p)
    {
        InvalidateReason reason;
        Rect dirty;
        lock (_gate)
        {
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    if (_live is null) return;
                    _filter.Feed(p);
                    var sm = _filter.EmitSmoothed();   // 单次取引用：Count 与尾点一次定位，免二次接口查询
                    if (sm.Count > _live.Points.Count)
                        _live.Add(sm[^1]);
                    dirty = RectAround(p, Profile.MmToPx(Pen.ThicknessMm));
                    reason = InvalidateReason.LiveStroke;
                    break;
                case InkTool.EraseStroke:
                    _erasePath.Add(new Point(p.X, p.Y));
                    // v0.9.7 擦除节流：120/240Hz 报点下，每个 Move 都执行像素级切分 +
                    // 整页重合成会让 UI 线程跟不上 —— 判定频率钳到 ~125Hz 上限（8ms 间隔），
                    // 路径仍持续记录不丢精度；Invalidate 始终触发让橡皮圈光标跟手。
                    long nowTick = p.TimestampTicks;
                    if (_erasePath.Count >= 2 && (nowTick - _lastEraseExecTick) >= EraseExecIntervalMs)
                    {
                        _lastEraseExecTick = nowTick;
                        _eraser.EraseAlongPath(CurrentPage, _erasePath, Profile.MmToPx(EraserRadiusMm));
                    }
                    dirty = RectAround(p, Profile.MmToPx(EraserRadiusMm) * 2);
                    reason = InvalidateReason.ErasePreview;
                    break;
                case InkTool.EraseLasso:
                    _lassoPath.Add(new Point(p.X, p.Y));
                    dirty = RectAround(p, 8);
                    reason = InvalidateReason.ErasePreview;
                    break;
                case InkTool.EraseClearSwipe:
                    _swipePath.Add(new Point(p.X, p.Y));
                    dirty = RectAround(p, 28);
                    reason = InvalidateReason.ErasePreview;
                    break;
                default:
                    return;
            }
        }
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = reason, DirtyPx = dirty });
    }

    // v0.9.7 擦除执行节流（EraseStroke 专属）：~8ms 间隔 ≈ 125Hz 上限。
    // 判定节流 ≠ 视觉节流：Invalidate 仍每 Move 触发（橡皮圈光标跟手），
    // 跳过的仅是像素切分与页面缓存重合成 —— 这是 4K 掉帧的真正源头。
    private const long EraseExecIntervalMs = 8;
    private long _lastEraseExecTick;

    public void OnStylusUp(in InkPoint p)
    {
        InvalidateReason reason;
        StrokeData? committedStroke = null;
        bool clearedBySwipe = false;
        lock (_gate)
        {
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    if (_live is null) return;
                    _filter.Feed(p);
                    var smoothed = _filter.EmitSmoothed();
                    while (_live.Points.Count < smoothed.Count) _live.Add(smoothed[_live.Points.Count]);
                    // v0.9.7 收笔笔锋：最后 8 点宽度按 1.0 → 0.25 渐出（提笔由粗到细），
                    // 在提交前生效；整笔随后的精修重绘自然带上收锋形态。
                    _live.ApplyLiftTaper();
                    // 操作级提交：撤销栈记录整笔
                    var stroke = _live;
                    _live = null;
                    CurrentPage.History.Commit(new AddStrokeOp(stroke));
                    committedStroke = stroke;             // 提交整笔 → 最近颜色沉淀 + AI 自动识别目标
                    reason = InvalidateReason.StrokeFinished;
                    break;
                case InkTool.EraseStroke:
                    _eraser.FlushErase(CurrentPage);
                    _erasePath.Clear();
                    reason = InvalidateReason.StrokesRemoved;
                    break;
                case InkTool.EraseLasso:
                    _eraser.TryLassoErase(CurrentPage, _lassoPath);
                    _lassoPath.Clear();
                    reason = InvalidateReason.StrokesRemoved;
                    break;
                case InkTool.EraseClearSwipe:
                    // 一挥清空：手势达标 → 整页消散（可一次撤销复原）；不达标视为试探性轻扫，静默忽略
                    if (IsClearSwipeGesture(_swipePath, p.TimestampTicks - _swipeStartTick))
                    {
                        ClearPageCore();
                        clearedBySwipe = true;
                    }
                    _swipePath.Clear();
                    reason = InvalidateReason.StrokesRemoved;
                    break;
                default:
                    return;
            }
        }
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = reason });
        if (committedStroke is not null) StrokeCommitted?.Invoke(committedStroke);
        if (clearedBySwipe) ApplyAfterClearTool();   // 手势清空同样享受"清空后智能切回"
    }

    /// <summary>中断当前未完成笔迹（翻页/S2 避让/模式切换时）。可重入：SwitchPage 在锁内调用。</summary>
    public void CancelLiveStroke()
    {
        lock (_gate)
        {
            // 审查修复：抹除中断（翻页/S2 避让/模式切换）时，已擦笔必须打包入史（此刻
            // CurrentSlideIndex 仍是旧页）—— 原先只清路径不提交，笔已消失却不在历史里，
            // 无法撤销恢复（历史黑洞），且残留批会在下次 Flush 时提交到错误的页。
            _eraser.FlushErase(CurrentPage);
            _live = null;
            _erasePath.Clear();
            _lassoPath.Clear();
            _swipePath.Clear();
        }
    }

    // ———————— 滑动清空（一挥清页） ————————

    /// <summary>
    /// 清空手势判定：大位移（≥ 屏宽 22%）或"快而长"（速度 ≥ 2.2 px/ms 且路径 ≥ 20% 屏宽）。
    /// 慢速涂画 / 轻点 / 小幅移动都不触发 —— 清空是重操作，门槛宁高勿低。
    /// </summary>
    private bool IsClearSwipeGesture(List<Point> path, long elapsedTicks)
    {
        if (path.Count < 4) return false;
        var span = TimeSpan.FromTicks(Math.Max(1, elapsedTicks)).TotalMilliseconds;
        double pathLen = 0, maxSeg = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
            pathLen += d;
            maxSeg = Math.Max(maxSeg, d);
        }
        double travel = Math.Sqrt(Math.Pow(path[^1].X - path[0].X, 2) + Math.Pow(path[^1].Y - path[0].Y, 2));
        // 阈值与路径同为 DIP 口径（审查修复：原用机型档案物理像素，150% 缩放机型上阈值虚高约 1.5 倍）；
        // 取放映屏尺寸 —— 书写发生在大屏，主屏是笔记本时宽高完全不符（双屏部署修复）
        // Points now use the stable document coordinate space, not physical pixels or DIPs.
        double minDim = Math.Min(_doc.ScreenWidthPx, _doc.ScreenHeightPx);
        if (minDim <= 0) minDim = Math.Min(Profile.ScreenWidthPx, Profile.ScreenHeightPx);
        bool bigSweep = travel >= minDim * 0.22;
        bool fastLong = span > 0 && (pathLen / span) >= 2.2 && pathLen >= minDim * 0.20 && maxSeg < minDim * 0.5; // maxSeg 防飞点假快
        return bigSweep || fastLong;
    }

    /// <summary>
    /// 清空当前页整页笔迹（第四轮仪式感升级）：
    ///  - 视觉：波浪消散（笔迹按水平位置左先右后错峰化开）+ 240px 柔光带 640ms 横扫；
    ///  - 历史：ClearPageOp 逻辑清空（撤销一次全回来）；
    ///  - 焦点：ApplyAfterClearTool —— 清空即重新板书，默认智能切回上次书写工具（设置可调）。
    /// 供滑动清空手势与插件 RequestEraseBehavior("clear") 共用。
    /// </summary>
    public void ClearPage()
    {
        lock (_gate) ClearPageCore();
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.StrokesRemoved });
        ApplyAfterClearTool();
    }

    /// <summary>清空核心（须在 _gate 内调用）：波浪 ghost + 扫光 + 历史提交。</summary>
    private void ClearPageCore()
    {
        var page = CurrentPage;
        if (page.Strokes.Count == 0) return;
        var op = ClearPageOp.PrepareApplied(page);          // 逻辑清空（记录索引，可逆）
        _eraser.GhostStrokesWave(op.Removed);               // 视觉：整页波浪消散（错峰 0.42s）
        _eraser.BeginSweep();                               // 视觉：柔光带横扫（"一挥而过"实感）
        page.History.CommitPrepared(op);
    }

    /// <summary>
    /// 清空后的工具焦点（设置 → 行为 → "清空整页后切到"）：
    /// smart（默认）= 清空意味着重新板书 → 切回上次书写工具；pen / touch / keep 见 AppConfig.AfterClearTool。
    /// </summary>
    private void ApplyAfterClearTool()
    {
        InkTool? target = AppConfig.Current.AfterClearTool switch
        {
            "pen" => InkTool.Pen,
            "touch" => InkTool.TouchThrough,
            "keep" => null,
            _ => LastWriteTool,   // smart
        };
        if (target is { } t)
            Tool = t;             // setter 触发 ToolChanged → 编排器同步工具栏高亮（同值短路防环）
    }

    /// <summary>取当前页最后一笔可识别笔迹（AI 手动触发的目标）。
    /// v0.8.1 审查修复：原调用方（编排器 AI 按钮）在 UI 线程无锁读 Strokes ——
    /// 与墨迹线程抬笔提交（List.Add）并发是未定义行为，且此刻老师可能正在书写。
    /// 统一走引擎锁取快照引用（StrokeData 提交后内容不再变更，引用安全）。</summary>
    public StrokeData? LastDrawableStroke()
    {
        lock (_gate) return CurrentPage.Strokes.LastOrDefault(s => s.Points.Count >= 4);
    }

    /// <summary>外部（插件 SDK / Lua 脚本）提交一笔矢量墨迹：持引擎锁入历史 + 通知重绘。
    /// 审查修复：HostApiBridge 原直接 CurrentPage.History.Commit —— 与墨迹线程抬笔提交竞态
    /// （HistoryTree 非线程安全），且不触发 VisualInvalidated，插件画的笔画要等下次无关重绘才出现。</summary>
    public void CommitExternalStroke(StrokeData stroke)
    {
        lock (_gate) CurrentPage.History.Commit(new AddStrokeOp(stroke));
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
    }

    // ———————— 撤销/重做（分支树） ————————

    public bool Undo()
    {
        bool ok;
        lock (_gate) ok = CurrentPage.History.Undo();
        if (ok) VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
        return ok;
    }

    public bool Redo()
    {
        bool ok;
        lock (_gate) ok = CurrentPage.History.Redo();
        if (ok) VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
        return ok;
    }

    // ———————— AI 形状规整回写 ————————

    /// <summary>导出全部页板书（审查修复并发导出）：持引擎锁构造页面快照（与墨迹线程的
    /// GetPage 新页 / 抬笔 AddStrokeOp 提交互斥），渲染与文件 IO 在锁外执行（不卡书写）。
    /// 返回 null = 无墨迹。供托盘手动导出 / 设置中心导出 / 放映结束兜底共用。</summary>
    public string? ExportSessionSafe()
    {
        List<(int Idx, InkPage Page, StrokeData[] Strokes)> snap;
        lock (_gate) snap = _doc.SnapshotPages();
        return snap.Count == 0 ? null : _doc.ExportSession(snap);
    }

    /// <summary>程序退出兜底（持锁快照 + 锁外 IO）。</summary>
    public void TrySaveSessionSafe() 
    {
        List<(int Idx, InkPage Page, StrokeData[] Strokes)> snap;
        lock (_gate) snap = _doc.SnapshotPages();
        _doc.TrySaveSession(snap);
    }

    /// <summary>AI 返回规整图形后：以"擦旧笔 + 加新笔"组合指令提交，可整体撤销。</summary>
    /// <summary>用规整结果替换原笔画；返回是否成功（目标已被撤销/擦除时返回 false）。
    /// 调用方据此决定反馈文案 —— 目标已消失时提示「已规整」是误报。</summary>
    public bool ReplaceStroke(StrokeData original, StrokeData regularized)
    {
        lock (_gate)
        {
            var page = CurrentPage;
            int idx = page.Strokes.IndexOf(original);
            if (idx < 0) return false;
            page.Strokes.RemoveAt(idx);
            page.Strokes.Insert(idx, regularized);
            // 记录组合历史（自实现复合提交，撤销时逆操作）
            page.History.Commit(new ReplaceStrokeOp(original, regularized));
        }
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
        return true;
    }

    private sealed class ReplaceStrokeOp : IInkOp
    {
        private readonly StrokeData _from, _to;
        public ReplaceStrokeOp(StrokeData from, StrokeData to) { _from = from; _to = to; }
        public void Apply(InkPage page) { int i = page.Strokes.IndexOf(_from); if (i >= 0) page.Strokes[i] = _to; }
        public void Revert(InkPage page) { int i = page.Strokes.IndexOf(_to); if (i >= 0) page.Strokes[i] = _from; }
    }

    private static Rect RectAround(in InkPoint p, double r) => new(p.X - r, p.Y - r, r * 2, r * 2);
}
