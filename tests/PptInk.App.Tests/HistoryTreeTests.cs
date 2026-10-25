using System.Windows;
using PptInk.Ink;
using Xunit;

namespace PptInk.App.Tests;

/// <summary>历史树核心语义（v1.1.1 建立测试体系的第一批用例：提交/撤销/重做分支
/// 偏好/线性重定根内存治理）。全部纯内存操作，无 STA/窗口依赖。</summary>
public sealed class HistoryTreeTests
{
    private static InkPage NewPage() => new(3840, 2160);

    private static StrokeData NewStroke(int id)
    {
        var s = new StrokeData(new PenInfo { ColorHex = "#E8442E", ThicknessMm = 2.2 });
        s.Add(new InkPoint(id, id, 0.5, 1));
        return s;
    }

    [Fact]
    public void Commit_AppliesAndUndoReverts()
    {
        var page = NewPage();
        var s = NewStroke(1);
        page.History.Commit(new AddStrokeOp(s));
        Assert.Single(page.Strokes);

        Assert.True(page.History.Undo());
        Assert.Empty(page.Strokes);
        Assert.False(page.History.CanUndo);
    }

    [Fact]
    public void Redo_PrefersLastUsedBranch()
    {
        var page = NewPage();
        var a = NewStroke(1);
        var b = NewStroke(2);
        page.History.Commit(new AddStrokeOp(a));
        page.History.Undo();                    // 回到 Root，留下分支 A
        page.History.Commit(new AddStrokeOp(b)); // 分支 B
        page.History.Undo();

        Assert.True(page.History.Redo());       // 最近使用分支 = B
        Assert.Single(page.Strokes);
        Assert.Same(b, page.Strokes[0]);
    }

    [Fact]
    public void UndoRedoRoundTrip_PreservesStrokes()
    {
        var page = NewPage();
        var s1 = NewStroke(1);
        var s2 = NewStroke(2);
        page.History.Commit(new AddStrokeOp(s1));
        page.History.Commit(new AddStrokeOp(s2));
        Assert.Equal(2, page.Strokes.Count);

        Assert.True(page.History.Undo());
        Assert.True(page.History.Undo());
        Assert.Empty(page.Strokes);
        Assert.True(page.History.Redo());
        Assert.True(page.History.Redo());
        Assert.Equal(2, page.Strokes.Count);
        Assert.Same(s2, page.Strokes[1]);
    }

    [Fact]
    public void LinearHistory_ReRootTrimKeepsBound()
    {
        // v1.1.0 修复验证：线性书写（无分支）超过 MaxNodes 时必须重新定根斩掉最老
        // 一段，否则 2000 节点上限在整节课连续书写中形同虚设。
        var page = NewPage();
        for (int i = 0; i < HistoryTree.MaxNodes + 200; i++)
            page.History.Commit(new AddStrokeOp(NewStroke(i)));

        // 全部笔迹仍在页上（重定根只影响可撤销深度，不删数据）
        Assert.Equal(HistoryTree.MaxNodes + 200, page.Strokes.Count);
        // 重定根已发生且深度受控：2200 次线性提交后 Depth 必须明显低于提交总数，
        // 且不超过 MaxNodes（否则"2000 节点上限形同虚设"的缺陷回归）
        Assert.True(page.History.Depth < HistoryTree.MaxNodes,
            $"Depth={page.History.Depth} 未被重定根治理");
        Assert.True(page.History.Depth >= HistoryTree.MaxNodes / 2,   // 保留链还在
            $"Depth={page.History.Depth} 过度截断");
        // 保留链挂回唯一 Root 子节点
        Assert.Single(page.History.Root.Children);
        // 全树节点数受控
        int count = 0;
        CountNodes(page.History.Root, ref count);
        Assert.True(count <= HistoryTree.MaxNodes + 64, $"节点总数 {count} 超限");
    }

    private static void CountNodes(HistoryNode node, ref int count)
    {
        count++;
        foreach (var c in node.Children) CountNodes(c, ref count);
    }
}

/// <summary>测试辅助：内联 Tap（链式副作用）。</summary>
internal static class TestExtensions
{
    public static T Tap<T>(this T self, Action<T> action)
    {
        action(self);
        return self;
    }
}
