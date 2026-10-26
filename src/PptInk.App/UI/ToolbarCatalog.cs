using System.Windows.Media;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>工具栏元素分组（组间自动插分隔线；同组相邻不插）。</summary>
public enum ToolbarGroup { Tools, Ops, Nav }

/// <summary>
/// 工具栏元素编目（纯数据）：设置中心"自定义工具栏"与 ToolbarWindow 渲染共用一份真相。
/// Id 与 AppConfig.ToolbarLayout 对齐；新增元素只需在此登记 + 默认布局补项。
/// </summary>
public static class ToolbarCatalog
{
    public sealed record Item(
        string Id,
        string Name,
        string Icon,
        string Tip,
        ToolbarGroup Group,
        bool LockVisible = false)   // 锁定可见：保底能力不可被隐藏
    {
        /// <summary>映射到引擎工具（null = 非工具类元素，如撤销/翻页）。</summary>
        public InkTool? Tool => Id switch
        {
            "touch" => InkTool.TouchThrough,
            "pen" => InkTool.Pen,
            "highlighter" => InkTool.Highlighter,
            "erase" => InkTool.EraseStroke, // 擦除默认模式由 DefaultEraseMode 决定
            _ => null,
        };
    }

    public static readonly Item[] All =
    {
        new("touch",       "触摸操作", "IconTouch",       "手指/触摸直接操作 PPT（点击链接、播放动画）", ToolbarGroup.Tools),
        new("pen",         "书写笔",   "IconPen",         "批注书写（平滑跟手）",                          ToolbarGroup.Tools, LockVisible: true),
        new("highlighter", "荧光笔",   "IconHighlighter", "半透明高亮，不遮文字",                          ToolbarGroup.Tools),
        new("erase",       "擦除",     "IconEraser",      "涂抹 / 圈画 / 滑动清空（点按后浮岛选择）",        ToolbarGroup.Tools),
        new("undo",        "撤销",     "IconUndo",        "撤销上一步",                                    ToolbarGroup.Ops),
        new("redo",        "重做",     "IconRedo",        "恢复撤销",                                    ToolbarGroup.Ops),
        new("ai",          "AI 规整",  "IconAi",          "AI 规整：自动模式下开关；仅手动模式下每次点按识别最近一笔",     ToolbarGroup.Ops),
        new("plugin",      "插件",     "IconPlugin",      "计时器 / 聚光灯 / 截图等",                       ToolbarGroup.Ops),
        new("nav",         "翻页岛",   "IconNext",        "屏幕左右下角各一份固定翻页岛，直接触摸翻页",  ToolbarGroup.Nav),
    };

    public static Item? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>擦除模式（浮岛三选）→ 引擎工具。</summary>
    public static InkTool EraseModeToTool(string mode) => mode switch
    {
        "lasso" => InkTool.EraseLasso,
        "clear" => InkTool.EraseClearSwipe,
        _ => InkTool.EraseStroke,
    };

    /// <summary>引擎工具 → 擦除模式字符串（高亮同步用）。</summary>
    public static string ToolToEraseMode(InkTool t) => t switch
    {
        InkTool.EraseLasso => "lasso",
        InkTool.EraseClearSwipe => "clear",
        InkTool.EraseStroke => "stroke",
        _ => "stroke",
    };

    /// <summary>当前布局的渲染序列（含被隐藏元素的占位 null 由调用方跳过）。</summary>
    public static IEnumerable<(Item Item, bool Visible)> ResolveLayout(IEnumerable<ToolbarItemConfig> layout)
    {
        foreach (var cfg in layout)
        {
            var item = Find(cfg.Id);
            if (item is null) continue;
            yield return (item, cfg.Visible || item.LockVisible);
        }
    }

    // v0.8.1：图标几何全部归一至 Icons.xaml（原 IconSwipe 静态属性已移除 ——
    // 见 Icons.xaml「引导 · 擦除扩展」组的 IconSwipe）。单一来源，杜绝重复维护。
}
