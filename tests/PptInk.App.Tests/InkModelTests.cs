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
    public void HighlighterOutline_HasRoundCapsAndNonzeroFill()
    {
        // 荧光笔连贯性回归锚点：
        //  ① 急转弯处左右边界自交，EvenOdd 会镂空 —— 必须 Nonzero 实心填充；
        //  ② 两端原本平切，加半圆端帽后应越过起终点。
        var s = new StrokeData(Pen with { Highlighter = true });
        s.Add(new InkPoint(0, 0, 0.5, 1));
        s.Add(new InkPoint(100, 0, 0.5, 2));

        var outline = s.BuildHighlighterOutline(6, 14);
        Assert.Equal(2 * 2 + 7 * 2, outline.Length);   // 左右各 2 点 + 两端各 7 个半圆采样
        Assert.Contains(outline, p => p.X < -1);
        Assert.Contains(outline, p => p.X > 101);

        var geo = s.BuildHighlighterRibbon(6, 14);
        Assert.Equal(System.Windows.Media.FillRule.Nonzero, geo.FillRule);
    }

    [Fact]
    public void ErasePath_HitsLongSegmentBetweenSparsePoints()
    {
        // 稀疏采样下的长线段：橡皮穿过两点正中间（两端距橡皮都超过半径）时，
        // 按「点」判定会整段漏擦。命中检测本身按线段距离判定，这里锁定该语义。
        var s = new StrokeData(Pen);
        s.Add(new InkPoint(0, 0, 0.5, 1));
        s.Add(new InkPoint(200, 0, 0.5, 2));
        Assert.True(s.HitByErasePath(new[] { new System.Windows.Point(100, 5) }, 10));
    }
}
