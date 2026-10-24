namespace PptInk.Ink;

/// <summary>操作级指令：双级存储的"操作级"。可 Apply/Revert，用于撤销重做与历史回放。</summary>
public interface IInkOp
{
    void Apply(InkPage page);
    void Revert(InkPage page);
}

public sealed class AddStrokeOp : IInkOp
{
    public StrokeData Stroke { get; }
    public AddStrokeOp(StrokeData stroke) => Stroke = stroke;
    public void Apply(InkPage page) => page.Strokes.Add(Stroke);
    public void Revert(InkPage page) => page.Strokes.Remove(Stroke);
}

/// <summary>整笔擦除（抹除/圈除命中后的整块消散）：撤销时按原对象引用恢复。</summary>
public sealed class EraseStrokesOp : IInkOp
{
    private readonly List<(int Index, StrokeData Stroke)> _removed = new();
    private readonly List<StrokeData> _targets;

    public IReadOnlyList<StrokeData> Removed => _removed.Select(r => r.Stroke).ToList();

    public EraseStrokesOp(IEnumerable<StrokeData> targets) => _targets = new List<StrokeData>(targets);

    /// <summary>
    /// 逐批捕获（滑过即擦）：命中笔在检测瞬间就从页面移除（不等 Commit），
    /// 每批移除时记录原索引，抬笔后经 CommitPrepared 入史，撤销可复原 z 序。
    /// v1.1.0 修复：原先命中即移除、抬笔才构造 op —— 届时笔早已不在页面，IndexOf 恒 -1
    /// 被跳过，_removed 恒空，Undo 返回 true 却什么都不恢复（笔迹永久丢失）。
    /// 索引必须在移除瞬间记录，这是本指令可逆的前提。
    /// </summary>
    internal void CaptureBatch(InkPage page, List<StrokeData> batch)
    {
        foreach (var t in batch)
        {
            _targets.Add(t);
            int idx = page.Strokes.IndexOf(t);
            if (idx >= 0)
            {
                _removed.Add((idx, t));
                page.Strokes.RemoveAt(idx);
            }
        }
    }

    // 审查修复：原实现带 _prepared 标志，预应用造出的 op 在 Redo/JumpTo 重放时
    // Apply 直接 return —— 擦除→撤销（笔画回来）→重做，笔画却不再消失，页面与历史树脱节。
    // 正确语义：Apply 无论何时被调用都真正执行（按引用重找、重记索引、重新移除）；
    // 首次生效走 CommitPrepared（不调 Apply），故无双重移除问题。
    public void Apply(InkPage page)
    {
        _removed.Clear();
        foreach (var t in _targets)
        {
            int idx = page.Strokes.IndexOf(t);
            if (idx >= 0) { _removed.Add((idx, t)); page.Strokes.RemoveAt(idx); }
        }
    }

    public void Revert(InkPage page)
    {
        // 按移除的逆序恢复：每笔记录的是「移除瞬间」的位置（后移除的笔在更靠前的批里可能
        // 记到相同索引），逆序插回恰好逐位复原，z 序（板书遮挡关系）不变。
        // 原先按记录索引 OrderByDescending：同批相邻命中会记到相同索引，稳定排序保持移除
        // 顺序，插回后相邻两笔层级互换（探针实证 [a,c,b,d]）。
        for (int i = _removed.Count - 1; i >= 0; i--)
            page.Strokes.Insert(Math.Min(_removed[i].Index, page.Strokes.Count), _removed[i].Stroke);
    }
}

/// <summary>
/// 像素级擦除（矢量切分，v0.9.6）：原笔被切分为保留段——
/// 撤销恢复原笔、移除保留段；只擦掉笔迹被抹过的局部，其余原样保留。
/// v1.1.0 修复：原笔带「擦除前真实索引」、保留段带「提交瞬间位置」，撤销/重做按索引
/// 原位插回，z 序与擦除前/后一致（原先两端都追加表尾，被局部擦除的旧笔会翻到其后书写之上）。
/// 原笔索引由擦除服务在提交瞬间反放手势推得（跨批次的移除索引互不可比，见 EraseService）。
/// </summary>
public sealed class ReplaceStrokesOp : IInkOp
{
    private readonly List<(int Index, StrokeData Stroke)> _originals;
    private readonly List<(int Pos, StrokeData Stroke)> _replacement;

    public ReplaceStrokesOp(List<(int Index, StrokeData Stroke)> originals,
                            List<(int Pos, StrokeData Stroke)> replacement)
    {
        _originals = new List<(int, StrokeData)>(originals);
        _originals.Sort((a, b) => a.Index.CompareTo(b.Index));   // 撤销按索引升序插回（前面各笔先就位，记录索引即真实落点）
        _replacement = new List<(int, StrokeData)>(replacement);
        _replacement.Sort((a, b) => a.Pos.CompareTo(b.Pos));
    }
    public void Apply(InkPage page)
    {
        foreach (var (_, o) in _originals) page.Strokes.Remove(o);
        foreach (var (pos, r) in _replacement)
            if (!page.Strokes.Contains(r)) page.Strokes.Insert(Math.Min(pos, page.Strokes.Count), r);
        page.MarkVisualDirty();
    }
    public void Revert(InkPage page)
    {
        foreach (var (_, r) in _replacement) page.Strokes.Remove(r);
        foreach (var (idx, o) in _originals)
            if (!page.Strokes.Contains(o)) page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), o);
        page.MarkVisualDirty();
    }
}

/// <summary>
/// 滑动清空整页：记录清空前的全部笔迹及索引，撤销时按原 z 序整页复原。
/// 与 EraseStrokesOp 同构（预应用 + 可逆），"清空"永远可被一次撤销救回 —— 课堂安全网。
/// </summary>
public sealed class ClearPageOp : IInkOp
{
    private readonly List<(int Index, StrokeData Stroke)> _removed = new();

    public IReadOnlyList<StrokeData> Removed => _removed.Select(r => r.Stroke).ToList();

    /// <summary>预应用构造：页面笔迹已在构造瞬间被清空（视觉同步），撤销可整体复原。</summary>
    internal static ClearPageOp PrepareApplied(InkPage page)
    {
        var op = new ClearPageOp();
        for (int i = 0; i < page.Strokes.Count; i++)
            op._removed.Add((i, page.Strokes[i]));
        page.Strokes.Clear();
        return op;
    }

    // 审查修复：同 EraseStrokesOp —— 去掉 _prepared 短路，Redo/JumpTo 重放时真正重新清空；
    // 首次生效走 CommitPrepared（不调 Apply），无双重清空问题。
    public void Apply(InkPage page)
    {
        _removed.Clear();
        for (int i = 0; i < page.Strokes.Count; i++)
            _removed.Add((i, page.Strokes[i]));
        page.Strokes.Clear();
    }

    public void Revert(InkPage page)
    {
        // 记录的是清空前的原位索引（0..n-1 升序），按记录顺序逐位插回恰好复原整页 z 序。
        // 原先 OrderByDescending 逆序插回：空列表先插最高位笔，后续 Math.Min 钳制把
        // 其余笔反复压在其上/间，≥3 笔即乱序（探针实证 [s0,s2,s1]）。
        foreach (var (idx, stroke) in _removed)
            page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), stroke);
    }
}

/// <summary>历史节点：分支树的节点。撤销后新绘制 = 在当前节点下开新分支。</summary>
public sealed class HistoryNode
{
    /// <summary>父节点。internal set 仅供历史树重新定根（线性修剪）改挂保留链，外部只读。</summary>
    public HistoryNode? Parent { get; internal set; }
    public IInkOp Op { get; }
    public List<HistoryNode> Children { get; } = new();
    public DateTime At { get; } = DateTime.Now;
    /// <summary>节点对应的像素级快照 ID（懒生成，用于回放时快速跳转）。</summary>
    public string? BitmapSnapshotId { get; set; }

    public HistoryNode(HistoryNode? parent, IInkOp op)
    {
        Parent = parent;
        Op = op;
    }
}

/// <summary>
/// 操作级历史 + 分支树：
///  - Undo/Redo 沿当前分支移动 Head；
///  - Undo 后新 Commit 在 Head 下追加子节点 → 天然形成分支树；
///  - 可回溯任一历史节点（回放/时间旅行调试）。
/// </summary>
public sealed class HistoryTree
{
    private readonly InkPage _page;
    public HistoryNode Root { get; } = new(null, new NoOp());
    public HistoryNode Head { get; private set; }

    public bool CanUndo => Head != Root;
    public int Depth { get; private set; }

    /// <summary>矢量历史节点总数上限（审查修复 #4）：长课堂大量书写/撤销下防无界增长，
    /// 超限先剪除最老的非活跃分支；整条历史都在当前链上（正常线性书写）时重新定根（见 TryTrim）。</summary>
    public const int MaxNodes = 2000;

    private int _commitsSinceTrim;

    private sealed class NoOp : IInkOp
    {
        public void Apply(InkPage _) { }
        public void Revert(InkPage _) { }
    }

    public HistoryTree(InkPage page)
    {
        _page = page;
        Head = Root;
    }

    /// <summary>提交操作：Apply + 挂到当前 Head 分支。</summary>
    public HistoryNode Commit(IInkOp op)
    {
        op.Apply(_page);
        var node = new HistoryNode(Head, op);
        Head.Children.Add(node);
        Head = node;
        Depth++;
        _page.MarkVisualDirty();
        TryTrim();
        return node;
    }

    /// <summary>提交"已提前生效"的操作（滑过即擦路径）：不再 Apply，只入史。</summary>
    public HistoryNode CommitPrepared(IInkOp alreadyApplied)
    {
        var node = new HistoryNode(Head, alreadyApplied);
        Head.Children.Add(node);
        Head = node;
        Depth++;
        _page.MarkVisualDirty();
        TryTrim();
        return node;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        Head.Op.Revert(_page);
        Head = Head.Parent!;
        Depth--;
        return true;
    }

    /// <summary>重做：优先走"最后一次走过的子分支"（最近使用分支），无记录时走末子节点。</summary>
    public bool Redo()
    {
        if (Head.Children.Count == 0) return false;
        var next = _lastRedo.TryGetValue(Head, out var remembered) && Head.Children.Contains(remembered)
            ? remembered
            : Head.Children[^1];
        _lastRedo[Head] = next;
        next.Op.Apply(_page);
        Head = next;
        Depth++;
        return true;
    }

    private readonly Dictionary<HistoryNode, HistoryNode> _lastRedo = new();

    /// <summary>回溯到树上任意节点（历史回放）：沿 Parent 链 Revert / 沿子链 Apply。</summary>
    public void JumpTo(HistoryNode target)
    {
        var path = new List<HistoryNode>();
        for (var n = target; n != null && n != Root; n = n.Parent) path.Add(n);
        path.Reverse();
        while (Undo()) { }
        foreach (var n in path) { n.Op.Apply(_page); }
        Head = target;
        Depth = path.Count;
        _page.MarkVisualDirty();
    }

    // ———————— 内存治理 ————————

    /// <summary>每 32 次提交触发一次分摊成本的全树治理：
    ///  1) 节点总数超 MaxNodes → 先从 Root 剪除最老的不含 Head 祖先链的整枝（连同 _lastRedo 记录）；
    ///  2) 仍超限（正常线性书写：整条历史都在 Head 祖先链上，无枝可剪）→ 重新定根，
    ///     把 Head 上方最老的超出部分斩掉，保留链挂回哨兵 Root。
    /// v1.1.0 修复：原先只有第 1 步——线性历史下 Root.Children 唯一的孩子永远在祖先链上，
    /// 循环一步都走不到，"2000 节点上限"在一整节课的连续书写中形同虚设（内存只增不减）。
    /// 重定根的代价是最早的若干步从此不可撤销，与"撤销救回最近误操作"的课堂语义不冲突。</summary>
    private void TryTrim()
    {
        if (++_commitsSinceTrim < 32) return;
        _commitsSinceTrim = 0;

        int total = CountSubtree(Root);
        if (total > MaxNodes)
        {
            var keep = AncestorChain(Head);
            for (int i = 0; i < Root.Children.Count && total > MaxNodes;)
            {
                var child = Root.Children[i];
                if (keep.Contains(child)) { i++; continue; }            // 当前活跃分支：跳过
                int sz = CountSubtree(child);
                total -= sz;
                var removedSet = new HashSet<HistoryNode>(sz);
                CollectSubtree(child, removedSet);
                Root.Children.RemoveAt(i);
                // _lastRedo 清理：key 或 value 任一落进被剪子树即作废（#4：字典原先只增不减）
                foreach (var k in _lastRedo.Keys.Where(removedSet.Contains).ToList()) _lastRedo.Remove(k);
                foreach (var kv in _lastRedo.Where(e => removedSet.Contains(e.Value)).ToList()) _lastRedo.Remove(kv.Key);
            }

            if (total > MaxNodes)
            {
                // 线性链兜底：一次砍到一半，摊薄重定根的频率。chain[0]=Head、末位=Root 直系孩子。
                var chain = new List<HistoryNode>();
                for (var n = Head; n != Root; n = n.Parent!) chain.Add(n);
                int keepCount = Math.Min(chain.Count, MaxNodes / 2);
                if (keepCount > 0 && keepCount < chain.Count)
                {
                    var dropped = new HashSet<HistoryNode>(chain.Skip(keepCount));
                    foreach (var k in _lastRedo.Keys.Where(dropped.Contains).ToList()) _lastRedo.Remove(k);
                    foreach (var kv in _lastRedo.Where(e => dropped.Contains(e.Value)).ToList()) _lastRedo.Remove(kv.Key);
                    var oldestKept = chain[keepCount];
                    oldestKept.Parent = Root;
                    Root.Children.Clear();
                    Root.Children.Add(oldestKept);
                    Depth -= chain.Count - keepCount;
                }
            }
        }
    }

    private static int CountSubtree(HistoryNode n)
    {
        int c = 1;
        foreach (var ch in n.Children) c += CountSubtree(ch);
        return c;
    }

    private static void CollectSubtree(HistoryNode n, HashSet<HistoryNode> into)
    {
        into.Add(n);
        foreach (var ch in n.Children) CollectSubtree(ch, into);
    }

    private static HashSet<HistoryNode> AncestorChain(HistoryNode head)
    {
        var set = new HashSet<HistoryNode>();
        for (var n = head; n != null; n = n.Parent) set.Add(n);
        return set;
    }
}
