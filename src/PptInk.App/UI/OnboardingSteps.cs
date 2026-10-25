using System;
using System.Collections.Generic;
using System.Windows;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>
/// 一张引导步骤：0..n 个高亮目标 + 图标 + 文案 + 自动推进时长。
/// Targets 为延迟求值：引导弹起时 Chrome 已显示，此时取元素矩形才有效。
/// </summary>
public sealed record OnboardingStep
{
    /// <summary>引导图标的流式几何资源名（Tokens/矢量，见 OnboardingGuideElement 内置集）。</summary>
    public string Icon { get; init; } = "IconPen";

    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>高亮目标（屏幕物理像素矩形）。null 表示本步无高亮。
    /// v0.8.0：坐标契约从「屏幕 DIP」改为「物理像素」——引导覆盖窗口用自身 DPI
    /// 统一换算为本地 DIP（单一换算源，混合 DPI 多屏部署零错位）。</summary>
    public Func<IReadOnlyList<Rect>?>? Targets { get; init; }

    /// <summary>自动推进间隔（ms）；越长的文案给越长。</summary>
    public double StayMs { get; init; } = 3200;

    /// <summary>等待用户决策（欢迎步）。超过 StayMs 无操作按"先跳过"处理，不无限驻留课堂。</summary>
    public bool AwaitChoice { get; init; }
}

/// <summary>
/// 引导目标定位器 —— UiOrchestrator 实现，持有真实窗口元素。
/// 坐标约定（v0.8.0）：屏幕物理像素（PointToScreen 原生值，不做 DPI 换算）；
/// 引导覆盖窗口负责用自身 DPI 统一换算。
/// </summary>
public interface IOnboardingTargets
{
    Rect? Tool(InkTool tool);
    Rect? UndoRedo();
    Rect? AiPlugin();
    Rect? NavBlock();
    Rect? Grip();
}

/// <summary>
/// 引导流程编目：文案与顺序固定；时长参数化（总长约 30s）。
/// 写成纯数据便于测试与后续多语言/多主题扩展。
/// </summary>
public static class OnboardingCatalog
{
    /// <summary>常规讲解步（含高亮聚焦 + 阅读）按人体阅读节奏：~3.2s。</summary>
    public const double StepMs = 3200;

    /// <summary>欢迎步最长等待：12s 无操作自动视作"先跳过"，绝不无限占用课堂。</summary>
    public const double WelcomeMs = 12000;

    /// <summary>收尾步：完成动画后的驻留。</summary>
    public const double ClosingMs = 4600;

    /// <summary>目标封装：null（元素未建/不可见）会被过滤——本步退化为无高亮，不崩溃。</summary>
    private static Rect[] Many(params Rect?[] rects) =>
        [.. rects.Where(r => r is not null).Select(r => r!.Value)];

    public static IReadOnlyList<OnboardingStep> BuildDefault(IOnboardingTargets t) =>
    [
        new OnboardingStep
        {
            Icon = "IconSparkle",
            Title = "嗨，老师！",
            Body = "您正在使用 PptInk 融绘（Xianfish · Hydrotessera 开发）。功能还在测试，欢迎提反馈 😊",
            AwaitChoice = true,
            StayMs = WelcomeMs,
        },

        new OnboardingStep
        {
            Icon = "IconPen",
            Title = "批注",
            Body = "需要批注？这里是笔，点击一次选中，再次点击展开面板。",
            Targets = () => Many(t.Tool(InkTool.Pen)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconHighlighter",
            Title = "荧光笔与擦除",
            Body = "圈重点用【荧光笔】，半透明不挡字。擦除滑过哪儿擦哪儿。",
            Targets = () => Many(t.Tool(InkTool.Highlighter), t.Tool(InkTool.EraseStroke)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconNext",
            Title = "翻页",
            Body = "随时按。不管是翻页笔还是点这里，不管什么模式，PptInk 都会识别到并恰到好处地处理。",
            Targets = () => Many(t.NavBlock()),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconGrip",
            Title = "嫌碍事？",
            Body = "长按这里最小化工具栏。你也可以在设置中让它在闲置时处于透明状态。",
            Targets = () => Many(t.Grip()),
            StayMs = StepMs + 900,
        },

        new OnboardingStep
        {
            Icon = "IconUndo",
            Title = "撤销",
            Body = "基于分支的撤销与恢复，容错度拉满。",
            Targets = () => Many(t.UndoRedo()),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconPlugin",
            Title = "插件",
            Body = "万能插件！这里可以安装任何您上课可能用到的插件，也可以稍后在设置中安装。",
            Targets = () => Many(t.AiPlugin()),
            StayMs = StepMs,
        },
    ];
}