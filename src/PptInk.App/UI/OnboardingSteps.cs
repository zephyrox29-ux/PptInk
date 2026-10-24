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
            Title = "老师，你好！",
            Body = "你正在使用 Xianfish 开发的 PPT 播放辅助工具！\n现在要快速了解一下软件功能吗？",
            AwaitChoice = true,
            StayMs = WelcomeMs,
        },

        new OnboardingStep
        {
            Icon = "IconPen",
            Title = "直接开写",
            Body = "这是【笔】。PPT 一进放映它就已就绪：红外框的抖动被智能平滑，第一笔下笔零延迟，粗细随笔速自然变化：快写收细、慢写饱满。",
            Targets = () => Many(t.Tool(InkTool.Pen)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconHighlighter",
            Title = "重点标注",
            Body = "【荧光笔】半透明不遮文字，适合圈重点；【抹除】滑过即擦，像用黑板擦一样自然。",
            Targets = () => Many(t.Tool(InkTool.Highlighter), t.Tool(InkTool.EraseStroke)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconLasso",
            Title = "成片擦除",
            Body = "【圈除】把要删掉的内容圈起来，松手整块粒子消散——比一点点擦省事得多。",
            Targets = () => Many(t.Tool(InkTool.EraseLasso)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconPalette",
            Title = "调色面板",
            Body = "再点一次【笔】按钮，工具栏头顶弹出调色面板：默认画笔颜色 + 最近用色 + 三档粗细 + RGB 微调。粗细按毫米换算：86 寸和 75 寸大屏写出来一样粗。",
            Targets = () => Many(t.Tool(InkTool.Pen)),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconUndo",
            Title = "有后悔药",
            Body = "【撤销】随时退一步；还有分支历史：撤销后再写会开出新分支，用【重做】还能回去探索原来的路径。",
            Targets = () => Many(t.UndoRedo()),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconGrip",
            Title = "工具栏想隐就隐",
            Body = "全屏板书时工具栏碍事？【长按工具栏最左侧的拖动柄 0.6 秒】即可收纳成一颗小胶囊，胶囊可拖到屏幕任意边缘停靠；要用时【点一下胶囊】立即展开。拖动手柄本身还能在底部槽位内左右移动工具栏。",
            Targets = () => Many(t.Tool(InkTool.Pen)),
            StayMs = StepMs + 900,
        },

        new OnboardingStep
        {
            Icon = "IconAi",
            Title = "AI 形状规整",
            Body = "随手画的圆、矩形、箭头，落笔停顿约 1 秒自动规整为标准图形，可撤销；偏好先确认再替换，可在设置中切换为气泡模式。",
            Targets = () => Many(t.AiPlugin()),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconNext",
            Title = "翻页与插件",
            Body = "屏幕左右下角各有一份【翻页岛】：直接触摸即可翻页，位置固定，不会误拖；【插件】里还有计时器、聚光灯、随机点名。",
            Targets = () => Many(t.NavBlock()),
            StayMs = StepMs,
        },

        new OnboardingStep
        {
            Icon = "IconCheck",
            Title = "就绪，放心板书",
            Body = "工具和用法都认识了。以后随时可以在托盘菜单「新手引导」里重播，或取消勾选不再提示。",
            StayMs = ClosingMs,
        },
    ];
}