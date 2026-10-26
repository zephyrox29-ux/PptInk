using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Templates;

/// <summary>模板描述（向导选择用）。</summary>
public sealed record TemplateInfo(string Id, PluginKind Kind, string Name, string Description);

/// <summary>三类模板与示例库：一键克隆为可编辑、可校验通过的工程。</summary>
public static class TemplateLibrary
{
    public static IReadOnlyList<TemplateInfo> All { get; } = new[]
    {
        new TemplateInfo("panel.timer", PluginKind.Panel, "面板 · 课堂正倒计时", "文本 + 滑条 + 按钮，脚本驱动计时逻辑"),
        new TemplateInfo("draw.board", PluginKind.Draw, "绘制 · 画板批注", "启用时调用绘制 SDK 落一笔矢量墨迹"),
        new TemplateInfo("free.rollcall", PluginKind.Free, "自由 · 随机点名", "点按启用，自行接管交互（Toast 反馈）"),
    };

    public static ProjectModel Create(string templateId) => templateId switch
    {
        "panel.timer" => Timer(),
        "draw.board" => Board(),
        "free.rollcall" => RollCall(),
        _ => throw new ArgumentException($"未知模板：{templateId}", nameof(templateId)),
    };

    /// <summary>按类型生成最小骨架（向导"从空白开始"用）。</summary>
    public static ProjectModel Blank(PluginKind kind)
    {
        var (name, entryLua) = kind switch
        {
            PluginKind.Panel => ("我的面板插件", PanelSkeleton),
            PluginKind.Draw => ("我的绘制插件", DrawSkeleton),
            _ => ("我的自由插件", FreeSkeleton),
        };
        return new ProjectModel
        {
            Manifest = new PluginManifest
            {
                Magic = (int)kind,
                Id = "com.example.myplugin",
                Name = name,
                Entry = "main.lua",
                Panel = kind == PluginKind.Panel
                    ? new PanelDecl { Elements = new[] { new PanelElement { Type = "text", Id = "display", Text = "Hello", OnEvent = "noop" } } }
                    : null,
            },
            LuaSource = entryLua,
        };
    }

    private static ProjectModel Timer() => new()
    {
        Manifest = new PluginManifest
        {
            Magic = 1,
            Id = "com.example.timer",
            Name = "课堂正倒计时",
            Version = "1.0.0",
            Author = "PptInk 插件工作室",
            Description = "面板类示例：宿主渲染文本/滑条/按钮，脚本驱动计时逻辑",
            Entry = "main.lua",
            Panel = new PanelDecl
            {
                Dock = "float",
                Width = 240,
                Height = 170,
                Alpha = 0.9,
                ShowInIsland = true,
                Elements = new[]
                {
                    new PanelElement { Type = "text", Id = "display", Text = "05:00", OnEvent = "onTick" },
                    new PanelElement { Type = "slider", Min = 10, Max = 3600, Value = 300, OnEvent = "onDurationChange" },
                    new PanelElement { Type = "button", Text = "开始 / 暂停", OnEvent = "onToggle" },
                },
            },
        },
        LuaSource = TimerLua,
    };

    private static ProjectModel Board() => new()
    {
        Manifest = new PluginManifest
        {
            Magic = 2,
            Id = "com.example.board",
            Name = "画板批注",
            Version = "1.0.0",
            Author = "PptInk 插件工作室",
            Description = "绘制类示例：启用时调用绘制 SDK 画一圈边框",
            Entry = "main.lua",
        },
        LuaSource = BoardLua,
    };

    private static ProjectModel RollCall() => new()
    {
        Manifest = new PluginManifest
        {
            Magic = 3,
            Id = "com.example.rollcall",
            Name = "随机点名",
            Version = "1.0.0",
            Author = "PptInk 插件工作室",
            Description = "自由类示例：点按启用，随机抽取一个名字并 Toast 反馈",
            Entry = "main.lua",
        },
        LuaSource = RollCallLua,
    };

    private const string TimerLua = """
-- 课堂正倒计时（PANEL 类，魔数 = 1）
-- 宿主负责渲染面板元素；本脚本只写逻辑。
-- 可用沙箱 API：startTimer / stopTimers / updatePanelElement / showToast / closePanel
local running = false
local remain = 300          -- 秒

local function fmt(s)
    return string.format("%02d:%02d", math.floor(s / 60), s % 60)
end

function onActivate()
    updatePanelElement("display", fmt(remain))
end

function onDeactivate()
    stopTimers()
    running = false
end

-- 滑条：设定倒计时时长（10s ~ 60min）
function onDurationChange(v)
    remain = math.floor(tonumber(v) or 300)
    updatePanelElement("display", fmt(remain))
end

-- 按钮：开始 / 暂停
function onToggle()
    if running then
        stopTimers()
        running = false
    else
        startTimer(1.0, "onTick")
        running = true
    end
end

-- 宿主计时回调（每 1 秒）
function onTick()
    if remain > 0 then
        remain = remain - 1
        updatePanelElement("display", fmt(remain))
        if remain == 0 then
            stopTimers()
            running = false
            showToast("时间到！")
        end
    end
end
""";

    private const string BoardLua = """
-- 画板批注（DRAW 类，魔数 = 2）
-- 可用沙箱 API：setPen / drawPolyline / drawText / eraseBehavior / showToast
function onStart()
    setPen("#E74C3C", 2.0, 1.0)
    -- 坐标使用墨迹模型坐标系；这里画一圈边框示意
    drawPolyline({ {400, 300}, {2400, 300}, {2400, 1200}, {400, 1200}, {400, 300} })
    showToast("画板批注：已绘制边框（可撤销）")
end

function onDeactivate()
end
""";

    private const string RollCallLua = """
-- 随机点名（FREE 类，魔数 = 3）
-- 可用沙箱 API：showToast / closePanel / startTimer / stopTimers 等
local names = { "张三", "李四", "王五", "赵六", "钱七" }

function onStart()
    local i = math.random(#names)
    showToast("点名：" .. names[i])
end

function onDeactivate()
end
""";

    private const string PanelSkeleton = """
-- 面板类骨架：onActivate 里初始化，回调名需与面板元素的 onEvent 一致
function onActivate()
    updatePanelElement("display", "Hello")
end

function onDeactivate()
end
""";

    private const string DrawSkeleton = """
-- 绘制类骨架
function onStart()
    setPen("#2E86DE", 2.0, 1.0)
    drawPolyline({ {600, 400}, {1800, 400} })
end

function onDeactivate()
end
""";

    private const string FreeSkeleton = """
-- 自由类骨架
function onStart()
    showToast("我的自由插件已启用")
end

function onDeactivate()
end
""";
}