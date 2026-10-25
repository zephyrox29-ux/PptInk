using PptInk.Ink;
using Xunit;

namespace PptInk.App.Tests;

/// <summary>笔迹数据模型与笔墨参数纯函数（v1.1.1 测试体系）。</summary>
public sealed class InkModelTests
{
    private static readonly PenInfo Pen = new() { ColorHex = "#E8442E", ThicknessMm = 2.2 };

    [Fact]
    public void Add_PointsThinNeverExceedCap()
    {
        // 触顶抽稀（CompactPoints 折半）语义：永不越过 MaxPoints（超限曾导致 4 万点
        // 全量插入卡死），抽稀后点数单调受控、可继续书写。
        var s = new StrokeData(Pen);
        for (int i = 0; i < StrokeData.MaxPoints + 500; i++)
        {
            s.Add(new InkPoint(i, i, 0.5, i));
            Assert.True(s.Points.Count <= StrokeData.MaxPoints,
                $"第 {i} 次添加后点数 {s.Points.Count} 越过上限");
        }
        Assert.True(s.Points.Count >= StrokeData.MaxPoints / 2);
        Assert.True(s.Points.Count <= StrokeData.MaxPoints);
    }

    [Fact]
    public void Bounds_SinglePointIsDot()
    {
        var s = new StrokeData(Pen);
        s.Add(new InkPoint(100, 200, 0.5, 1));
        var b = s.Bounds;
        Assert.True(b.Width >= 0 && b.Height >= 0);
        Assert.Equal(100, b.X, 0);
        Assert.Equal(200, b.Y, 0);
    }

    [Fact]
    public void Bounds_CoversAllPoints()
    {
        var s = new StrokeData(Pen);
        s.Add(new InkPoint(10, 20, 0.5, 1));
        s.Add(new InkPoint(300, 400, 0.5, 2));
        s.Add(new InkPoint(50, 350, 0.5, 3));
        var b = s.Bounds;
        Assert.True(b.Contains(new System.Windows.Point(10, 20)));
        Assert.True(b.Contains(new System.Windows.Point(300, 400)));
        Assert.True(b.Contains(new System.Windows.Point(50, 350)));
    }

    [Fact]
    public void ErasePath_HitsSinglePointStroke()
    {
        // v1.1.2 回归锚点：单点笔迹（轻点墨点）原先永远不进命中循环，涂抹擦除对其
        // 完全失效 —— 「残留红点擦不掉」。
        var dot = new StrokeData(Pen);
        dot.Add(new InkPoint(100, 100, 0.5, 1));
        Assert.True(dot.HitByErasePath(new[] { new System.Windows.Point(103, 103) }, 10));
        Assert.False(dot.HitByErasePath(new[] { new System.Windows.Point(500, 500) }, 10));
    }
}
