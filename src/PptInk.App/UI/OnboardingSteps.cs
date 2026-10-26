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
    Rect? Ai();
    Rect? Plugin();
    Rect? ExitShow();
    Rect? Grip();
    IReadOnlyList<Rect> NavBlocks();
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

    /// <summary>单步构造：阅读时长按文案长度自适应（原固定 3.2s，长句没读完就跳走）。</summary>
    private static OnboardingStep Step(string icon, string title, string body,
        Func<IReadOnlyList<Rect>?>? targets = null)
        => new()
        {
            Icon = icon,
            Title = title,
            Body = body,
            Targets = targets,
            StayMs = Math.Max(StepMs, Math.Min(9000, body.Length * 110)),
        };

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

        Step("IconPen", "批注",
            "需要批注？点这里是笔；再点一次展开颜色和粗细面板。写字时手掌压屏也不会误画。",
            () => Many(t.Tool(InkTool.Pen))),

        Step("IconHighlighter", "荧光笔与擦除",
            "圈重点用【荧光笔】，半透明不挡字。擦除滑过哪儿擦哪儿，轻点一下也能擦掉一个墨点；再点一次可换圈选 / 滑动清空。",
            () => Many(t.Tool(InkTool.Highlighter), t.Tool(InkTool.EraseStroke))),

        Step("IconNext", "翻页",
            "左右下角各有一个固定翻页岛，点一下就能翻。翻页笔的上下键、音量键和亮度键也能翻（设置里可关）。",
            () => t.NavBlocks()),

        Step("IconStop", "结束放映",
            "讲完点这里退出放映。板书会按你的设置自动留存，误触也不会丢笔迹。",
            () => Many(t.ExitShow())),

        Step("IconAi", "AI 规整",
            "手绘的圆、矩形、箭头等，停笔后自动变工整。这个按钮是开关；第一次开启时我会自动把运行环境准备好。",
            () => Many(t.Ai())),

        Step("IconPlugin", "插件",
            "计时器、聚光灯、截图等都在这里。也可以稍后在设置 → 插件里安装更多。",
            () => Many(t.Plugin())),

        Step("IconGrip", "嫌碍事？",
            "长按这里能把工具栏收成小球；拖动可以把它挪到顺手的位置。设置里还能让它闲置时半透明。",
            () => Many(t.Grip())),

        Step("IconUndo", "撤销",
            "基于分支的撤销与恢复，容错度拉满。空历史时按钮会置灰，不会让你误以为坏了。",
            () => Many(t.UndoRedo())),

        new OnboardingStep
        {
            Icon = "IconSparkle",
            Title = "就这些",
            Body = "忘了也没关系：托盘菜单可以随时重播引导，设置中心每一项都有说明。",
            StayMs = ClosingMs,
        },
    ];
}