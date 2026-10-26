using System.Text.RegularExpressions;
using PptInk.PluginStudio.Core.Lua;
using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Validation;

/// <summary>脚本与 manifest 的交叉一致性：权限是否覆盖所用 API、面板回调是否在脚本中定义。</summary>
public static class CrossConsistencyChecker
{
    private static readonly Regex FunctionDef = new(@"\bfunction\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex WordCall = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    public static IReadOnlyList<ValidationIssue> Check(ProjectModel project)
    {
        var issues = new List<ValidationIssue>();
        var source = project.LuaSource ?? "";
        var perms = new HashSet<string>(project.Manifest.Permissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var available = LuaApiCatalog.ForPermissions(perms).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var allApi = LuaApiCatalog.All.ToDictionary(a => a.Name, StringComparer.Ordinal);

        foreach (Match call in WordCall.Matches(source))
        {
            var name = call.Groups[1].Value;
            if (!allApi.TryGetValue(name, out var api)) continue;
            if (!available.Contains(name))
                issues.Add(new ValidationIssue(IssueLevel.Error, "permissions",
                    $"脚本调用了 {name}，但未声明其所需权限「{api.RequiredPermission}」"));
        }

        if (project.Manifest.Panel is { } panel)
        {
            var defined = FunctionDef.Matches(source).Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < panel.Elements.Length; i++)
            {
                var cb = panel.Elements[i]?.OnEvent;
                if (string.IsNullOrEmpty(cb) || cb == "noop") continue;
                if (!defined.Contains(cb))
                    issues.Add(new ValidationIssue(IssueLevel.Error, $"panel.elements[{i}].onEvent",
                        $"面板元素引用了未定义的脚本函数：{cb}"));
            }
        }

        return issues;
    }
}