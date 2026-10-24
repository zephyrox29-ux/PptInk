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
    /// 预应用构造：滑过即擦要求命中笔在检测瞬间就从页面移除（不等 Commit），
    /// 此工厂在移除时记录原索引，抬笔后经 CommitPrepared 入史，撤销可复原 z 序。
    /// </summary>
    internal static EraseStrokesOp PrepareApplied(InkPage page, List<StrokeData> targets)
    {
        var op = new EraseStrokesOp(targets);
        foreach (var t in targets)
        {
            int idx = page.Strokes.IndexOf(t);
            if (idx >= 0)
            {
                op._removed.Add((idx, t));
                page.Strokes.RemoveAt(idx);
            }
        }
        return op;
    }

    // 审查修复：原实现带 _prepared 标志，PrepareApplied 造出的 op 在 Redo/JumpTo 重放时
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
        // 按原索引逆序恢复，保持 z 序不变（板书遮挡关系可复原）
        foreach (var (idx, stroke) in _removed.OrderByDescending(r => r.Index))
            page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), stroke);
    }
}

/// <summary>
/// 像素级擦除（矢量切分，v0.9.6）：原笔被切分为保留段——
/// 撤销恢复原笔、移除保留段；只擦掉笔迹被抹过的局部，其余原样保留。
/// </summary>
public sealed class ReplaceStrokesOp : IInkOp
{
    private readonly List<StrokeData> _originals;
    private readonly List<StrokeData> _replacement;
    public ReplaceStrokesOp(List<StrokeData> originals, List<StrokeData> replacement)
    {
        _originals = new List<StrokeData>(originals);
        _replacement = new List<StrokeData>(replacement);
    }
    public void Apply(InkPage page)
    {
        foreach (var o in _originals) page.Strokes.Remove(o);
        foreach (var r in _replacement) if (!page.Strokes.Contains(r)) page.Strokes.Add(r);
        page.MarkVisualDirty();
    }
    public void Revert(InkPage page)
    {
        foreach (var r in _replacement) page.Strokes.Remove(r);
        foreach (var o in _originals) if (!page.Strokes.Contains(o)) page.Strokes.Add(o);
        page.MarkVisualDirty();
    }
}

/// <summary>像素级区域擦除：仅从位图层抠除，矢量层保留（撤销=恢复位图快照区域）。</summary>
public sealed class EraseRegionOp : IInkOp
{
    public required System.Windows.Rect RegionPx { get; init; }
    private byte[]? _snapshotBefore;

    /// <summary>快照字节量（历史树内存预算统计用）。</summary>
    internal long SnapshotBytes => _snapshotBefore?.LongLength ?? 0;

    /// <summary>释放位图快照（历史树预算淘汰时调用）。释放后该节点若被 Undo 到达，
    /// Revert 退化为无操作（位图已失联）；再次 Redo 时 Apply 会重新生成快照。</summary>
    internal long ReleaseSnapshot()
    {
        var n = _snapshotBefore?.LongLength ?? 0;
        _snapshotBefore = null;
        return n;
    }

    public void Apply(InkPage page)
    {
        _snapshotBefore = page.CopyRegionBitmap(RegionPx);
        page.EraseRegionBitmap(RegionPx);
        page.MarkVisualDirty();
    }

    public void Revert(InkPage page)
    {
        if (_snapshotBefore is not null) page.RestoreRegionBitmap(RegionPx, _snapshotBefore);
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
        foreach (var (idx, stroke) in _removed.OrderByDescending(r => r.Index))
            page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), stroke);
    }
}

/// <summary>历史节点：分支树的节点。撤销后新绘制 = 在当前节点下开新分支。</summary>
public sealed class HistoryNode
{
    public HistoryNode? Parent { get; }
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
    /// 超限剪除最老的非活跃分支（当前 Head 祖先链永不在剪除范围）。</summary>
    public const int MaxNodes = 2000;

    /// <summary>像素快照内存预算（审查修复 #5）：EraseRegionOp 区域 RGBA 累计超预算时，
    /// 从最老的非祖先链节点释放快照，回到 75% 水位。4K 整屏快照 ≈ 33MB，48MB ≈ 一屏余量。</summary>
    public const long SnapshotBudgetBytes = 48L << 20;

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

    // ———————— 内存治理（审查修复 #4/#5） ————————

    /// <summary>每 32 次提交触发一次分摊成本的全树治理：
    ///  1) 节点总数超 MaxNodes → 从 Root 起剪除最老的不含 Head 祖先链的整枝（连同 _lastRedo 记录与快照）；
    ///  2) 快照字节超预算 → 广度优先（老优先）释放非祖先链上 EraseRegionOp 的位图快照，回到 75% 水位。
    /// Head 祖先链 = 撤销必需路径，任何情况下不动。</summary>
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
        }

        long bytes = 0;
        ForEachNode(Root, n => bytes += (n.Op as EraseRegionOp)?.SnapshotBytes ?? 0);
        if (bytes > SnapshotBudgetBytes)
        {
            var keep = AncestorChain(Head);
            var queue = new Queue<HistoryNode>();
            queue.Enqueue(Root);
            while (bytes > SnapshotBudgetBytes * 3 / 4 && queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (!keep.Contains(n) && n.Op is EraseRegionOp er)
                    bytes -= er.ReleaseSnapshot();
                foreach (var ch in n.Children) queue.Enqueue(ch);
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

    private static void ForEachNode(HistoryNode n, Action<HistoryNode> act)
    {
        act(n);
        foreach (var ch in n.Children) ForEachNode(ch, act);
    }

    private static HashSet<HistoryNode> AncestorChain(HistoryNode head)
    {
        var set = new HashSet<HistoryNode>();
        for (var n = head; n != null; n = n.Parent) set.Add(n);
        return set;
    }
}
