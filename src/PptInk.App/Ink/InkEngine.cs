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
    // 当前物理接触上的手势是否仍有效：Down 置 true，CancelLiveStroke（翻页/S2 避让/模式切换）置 false。
    // Pen/Highlighter 分支靠 `_live is null` 对中断天然免疫；擦除/圈除/一挥清空没有等价守卫 ——
    // 不作废的话，手势中途翻页后未抬笔的续点会继续累积并落在新页上（错页被擦/被清）。
    private bool _gestureValid;

    /// <summary>内部锁（渲染层读取 Strokes/LiveStroke 时也必须持此锁）：
    /// 墨迹线程（StylusPlugIn）写、UI 线程读，List 并发访问是未定义行为。</summary>
    internal object Gate => _gate;

    public InkTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;      // 同值短路：防编排器 ↔ 工具栏高亮同步的死循环
            Action<InkTool>? changed;
            lock (_gate)
            {
                // v0.9.2（擦除复查修复）：工具切换 = 隐式抬笔 —— 抹除批必须在此收口。
                // 否则「抹到一半换工具」（另一只手点工具栏）会让已抹笔迹不进历史（无法撤销恢复，
                // 历史黑洞），残留批还会串入下一次 FlushErase（一次撤销误恢复两批，粒度错乱）。
                // v1.0.1（并发修复）：收口全程持 _gate —— 本类纪律（见 Gate 注释）要求墨迹线程写、
                // UI 线程读共持此锁，setter 曾是唯一例外：无锁 Clear/FlushErase 与墨迹线程在
                // RenderGate 内的 path[^2..] 切片竞态可抛越界（笔线程事件链无 catch → 进程崩溃），
                // flush 与墨迹线程的批次提交并发则把已擦笔迹搁浅在历史之外（撤销黑洞）。
                if (_tool is InkTool.EraseStroke or InkTool.EraseLasso or InkTool.EraseClearSwipe)
                {
                    _eraser.FlushErase(CurrentPage);     // 抹除批打包提交（空批自助返回）
                    _erasePath.Clear();
                    _lassoPath.Clear();
                    _swipePath.Clear();
                }
                _tool = value;
                changed = ToolChanged;
            }
            changed?.Invoke(value);   // 锁外触发：订阅方经 Dispatcher 回 UI，不得持锁
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

    /// <summary>换课件检测（v1.1.0 修复）：以演示文稿身份（ActivePresentation.FullName）区分课件，
    /// 身份变化 = 换了课件 → 先按退出兜底同路径自动存档旧会话，再清空全部页面与历史。
    /// 不修的后果：文档全进程单例、页面按幻灯片序号缓存且从不清理——同一节课换一份课件放映，
    /// 旧课件的批注会原样出现在新课件相同页码上（上一节课的答案带到下一节课）。
    /// 身份不可得（WPS/兜底通道）或同一课件重开放映时不动，保持既有行为。</summary>
    public void BeginPresentationSession(string? identity)
    {
        if (string.IsNullOrEmpty(identity)) return;
        List<(int Idx, InkPage Page, StrokeData[] Strokes)>? snapToSave = null;
        lock (_gate)
        {
            if (_doc.SessionIdentity == identity) return;      // 同课件重开放映：保留批注
            bool hadInk = false;
            foreach (var p in _doc.Pages.Values) { if (p.Strokes.Count > 0) { hadInk = true; break; } }
            _doc.SessionIdentity = identity;
            if (!hadInk) return;                                // 首场（或旧课件无墨迹）：登记身份即可
            CancelLiveStroke();                                 // 收尾进行中的笔/擦除批，随旧会话一起存档
            snapToSave = _doc.SnapshotPages();                  // 锁内取快照，重置后旧墨迹仍可写盘
            _doc.ResetSession();
        }
        _doc.TrySaveSession(snapToSave);                        // 锁外 IO（桌面 PptInk导出），与退出兜底同路径
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
    }

    // ———————— 书写输入（StylusPlugIn 墨迹线程调用） ————————

    public void OnStylusDown(in InkPoint p)
    {
        bool isWriteTool = false;
        StrokeData? isolatedStroke = null;
        lock (_gate)
        {
            _gestureValid = true;   // 新接触开始：手势有效（上一接触被中断作废的，自此重新计）
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    // v0.9.9 会话隔离：上一笔尚未收尾（快速连写、触摸系统乱序）时新笔已落下 ——
                    // 先把上一笔用其尾点收尾提交，再开新笔。不隔离的后果：第一笔被静默覆盖丢笔迹，
                    // 第二笔被第一笔迟到的 Up 误提交（不完整），视觉上「连笔」「粗细突然跳变」。
                    if (_live is not null)
                    {
                        CompactLiveStroke();   // 触顶预折半：先与平滑流对齐，下面的收尾补点循环才有对齐索引
                        var prevFilter = _filter;
                        prevFilter.Feed(_live.Points[^1]);
                        var prevSm = prevFilter.EmitSmoothed();
                        while (_live.Points.Count < prevSm.Count) _live.Add(prevSm[_live.Points.Count]);
                        // 收锋基于渲染层最后一次 BuildWidths 留下的缓存 —— 此处**不**以引擎参数调 BuildWidths：
                        // 隔离分支的参数（basePx/minimumWidth）与渲染层不一致，重算会让缓存 key 漂移，
                        // 渲染随后用正确参数重算、直接覆盖掉刚施加的收锋。ApplyLiftTaper 内部只用缓存 key
                        // 里记录的渲染同参补算宽度（无漂移）；缓存未建立时 no-op（无害）。
                        _live.ApplyLiftTaper();
                        var prevStroke = _live;
                        _live = null;
                        CurrentPage.History.Commit(new AddStrokeOp(prevStroke));
                        isolatedStroke = prevStroke;   // 锁外触发 StrokeCommitted（最近色沉淀 + AI 识别不被吞）
                    }
                    var pen = Tool == InkTool.Highlighter
                        ? Pen with { Highlighter = true, ColorHex = AppConfig.Current.HighlightColor, ThicknessMm = Math.Max(Pen.ThicknessMm, 8) }
                        : Pen;
                    _filter = new SmoothingFilter(PenTuning.EffectiveAlpha(Profile.EmaAlpha), Profile.MaxJumpPx * Profile.CanvasScale, Profile.DropoutMs);
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
        if (isolatedStroke is not null) StrokeCommitted?.Invoke(isolatedStroke);   // 隔离收尾的上一笔同样通知（最近色 + AI 识别）
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.LiveStroke, DirtyPx = RectAround(p, Profile.MmToPx(Pen.ThicknessMm)) });
    }

    public void OnStylusMove(in InkPoint p)
    {
        InvalidateReason reason;
        Rect dirty;
        lock (_gate)
        {
            if (!_gestureValid) return;   // 手势已被翻页/避让中断：同一接触的续点全部作废
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    if (_live is null) return;
                    CompactLiveStroke();               // 触顶预折半：Points 与平滑流一起抽稀，保持等长对齐
                    _filter.Feed(p);
                    var sm = _filter.EmitSmoothed();   // 单次取引用（活引用，Feed 后 Count 已增长）
                    // 逐点补齐而非只补尾点：断触插值一次会多产出 3 个平滑点，只取尾点会让
                    // Points 不再是平滑流的前缀，Up 的补点循环随后按错位索引重放出重复段。
                    while (sm.Count > _live.Points.Count)
                        _live.Add(sm[_live.Points.Count]);
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
            if (!_gestureValid) return;   // 手势已被翻页/避让中断：半截手势不再判定/提交
            switch (Tool)
            {
                case InkTool.Pen or InkTool.Highlighter:
                    if (_live is null) return;
                    CompactLiveStroke();   // 触顶预折半：先对齐再补点（否则下面的补点循环永不终止）
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
            _gestureValid = false;   // 作废当前接触上的半截手势：续到的 Move/Up 一律忽略（防翻页后错页续擦）
            _erasePath.Clear();
            _lassoPath.Clear();
            _swipePath.Clear();
        }
    }

    // ———————— 活笔触顶折半（v1.0.1：修复 Points 与平滑流索引错位的补点死循环） ————————

    /// <summary>活笔点数触顶时的同步折半（须在 _gate 内、Feed 之前调用）。
    /// StrokeData.Add 的触顶抽稀只折 Points，而 SmoothingFilter 的平滑流只增不减 —— 两流一旦
    /// 索引错位，Up 的逐索引补点循环会把笔迹中段整段重放到尾点之后，且循环内 Add 再次折半
    /// 令循环永不终止（锁内死循环：墨迹线程 100% CPU 挂死、渲染/撤销/导出全部冻结）。
    /// 在 Feed 前把两侧一起隔点抽稀，等长前缀关系得以永久保持。</summary>
    private void CompactLiveStroke()
    {
        if (_live is null || _live.Points.Count < StrokeData.MaxPoints) return;
        _live.CompactPoints();
        _filter.CompactSmoothed();
    }

    // ———————— 滑动清空（一挥清页） ————————

    /// <summary>
    /// 清空手势判定：大位移（≥ 屏宽 22%）或"快而长"（速度 ≥ 2.2 px/ms 且路径 ≥ 20% 屏宽）。
    /// 慢速涂画 / 轻点 / 小幅移动都不触发 —— 清空是重操作，门槛宁高勿低。
    /// </summary>
    private bool IsClearSwipeGesture(List<Point> path, long elapsedMs)
    {
        if (path.Count < 4) return false;
        // v1.0.1 修复：入参已是 InkClock 毫秒域（QPC 换算后的毫秒差，见 InkPoint.TimestampTicks），
        // 此前按 TimeSpan.FromTicks 的 100ns tick 解释，span 缩小 1 万倍 → 速度项对任何真实速度
        // 恒过，「慢速涂画 / 轻点不触发」的承诺失效（慢写大字、慢涂来回都被判成「快而长」清页）。
        double spanMs = Math.Max(1, elapsedMs);
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
        bool fastLong = (pathLen / spanMs) >= 2.2 && pathLen >= minDim * 0.20 && maxSeg < minDim * 0.5; // maxSeg 防飞点假快
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

    /// <summary>持锁快照当前页最近 N 笔（AI「最近几笔」范围合并用）。
    /// v1.1.0 修复：原先调用方在 UI 线程无锁枚举 CurrentPage.Strokes —— 停笔排程的识别
    /// 触发时老师可能已开始写下一笔，与墨迹线程抬笔提交（List.Add）并发会抛「集合已修改」，
    /// AI 识别随机失灵且无日志。（与上方 LastDrawableStroke 同一教训的另一半。）</summary>
    public List<StrokeData> SnapshotRecentStrokes(int count)
    {
        lock (_gate)
        {
            var strokes = CurrentPage.Strokes;
            int start = Math.Max(0, strokes.Count - count);
            var list = new List<StrokeData>(Math.Min(count, strokes.Count));
            for (int i = start; i < strokes.Count; i++) list.Add(strokes[i]);
            return list;
        }
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

    /// <summary>AI 多笔范围规整（AiScope=recent）：识别几何覆盖范围内全部原笔，
    /// 故须删除 originals 在页上的每一笔、插入一笔规整笔，整批打成一个 ReplaceStrokesOp
    /// （一次撤销同时恢复全部原笔）。anchor（触发识别的最后一笔）已被撤销/擦除时整批
    /// 放弃并返回 false —— 与 ReplaceStroke 同语义。规整笔按 ReplaceStrokesOp 语义追加
    /// 到页尾（z 序顶层），与像素擦除的整批替换口径一致。</summary>
    public bool ReplaceStrokeRange(IReadOnlyList<StrokeData> originals, StrokeData anchor, StrokeData regularized)
    {
        lock (_gate)
        {
            var page = CurrentPage;
            if (page.Strokes.IndexOf(anchor) < 0) return false;
            // 只带仍在页上的原笔入史：识别等待期间被用户擦/撤销的笔不属于本操作，
            // 否则撤销本操作会把先前已消失的笔迹一并复活。
            // v1.1.0：ReplaceStrokesOp 改带索引模型 —— 原笔记录当前索引，规整笔记录页尾落点。
            var present = originals.Where(page.Strokes.Contains)
                .Select(s => (Index: page.Strokes.IndexOf(s), Stroke: s)).ToList();
            page.History.Commit(new ReplaceStrokesOp(present,
                new List<(int Pos, StrokeData Stroke)> { (page.Strokes.Count, regularized) }));
        }
        VisualInvalidated?.Invoke(this, new InkVisualInvalidated { Reason = InvalidateReason.HistoryChanged });
        return true;
    }

    private static Rect RectAround(in InkPoint p, double r) => new(p.X - r, p.Y - r, r * 2, r * 2);
}
