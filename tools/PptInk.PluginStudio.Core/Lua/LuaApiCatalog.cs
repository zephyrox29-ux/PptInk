using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Lua;

/// <summary>注入沙箱的 API（编辑器补全与交叉校验的数据源）。
/// 同步来源：src/PptInk.App/Plugins/ScriptPlugin.cs 的 Globals 注入清单。</summary>
public sealed record LuaApi(string Name, string Signature, string? RequiredPermission);

public static class LuaApiCatalog
{
    public static IReadOnlyList<LuaApi> All { get; } = new LuaApi[]
    {
        new("setPen", "setPen(colorHex, thicknessMm, alpha)", null),
        new("drawPolyline", "drawPolyline({ {x, y}, ... })", null),
        new("drawText", "drawText(text, x, y, sizeMm)", null),
        new("eraseBehavior", "eraseBehavior(mode)  -- stroke / pixel / region / clear", null),
        new("startTimer", "startTimer(intervalSeconds, callbackName)", null),
        new("stopTimers", "stopTimers()", null),
        new("showToast", "showToast(message)", null),
        new("closePanel", "closePanel()", null),
        new("updatePanelElement", "updatePanelElement(elementId, value)", null),
        new("readClipboard", "readClipboard()", "clipboard"),
        new("writeClipboard", "writeClipboard(text)", "clipboard"),
        new("captureScreen", "captureScreen(savePath)", "file"),
    };

    public static IReadOnlyList<LuaApi> ForPermissions(IEnumerable<string> permissions)
    {
        var set = new HashSet<string>(permissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return All.Where(a => a.RequiredPermission is null || set.Contains(a.RequiredPermission)).ToList();
    }

    /// <summary>入口/停止回调约定：Panel 用 onActivate，Draw/Free 用 onStart；均含 onDeactivate。</summary>
    public static IReadOnlyList<string> EntryCallbacks(PluginKind kind)
    {
        var list = new List<string>
        {
            kind == PluginKind.Panel ? "onActivate" : "onStart",
            "onDeactivate",
        };
        if (kind == PluginKind.Panel) list.Add("onTick");
        return list;
    }
}