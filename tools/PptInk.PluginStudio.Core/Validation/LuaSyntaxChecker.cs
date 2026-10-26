using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace PptInk.PluginStudio.Core.Validation;

/// <summary>Lua 语法检查：仅用 MoonSharp 解析（LoadString），**绝不执行**用户脚本（沙箱安全）。</summary>
public static class LuaSyntaxChecker
{
    private static readonly Regex Position = new(@"\((\d+),(\d+)\)", RegexOptions.Compiled);

    public static IReadOnlyList<ValidationIssue> Check(string? source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<ValidationIssue>();
        try
        {
            var script = new Script();
            script.LoadString(source, null, "plugin.lua");
            return Array.Empty<ValidationIssue>();
        }
        catch (SyntaxErrorException ex)
        {
            var msg = ex.Message ?? "Lua 语法错误";
            var m = Position.Match(msg);
            var text = m.Success ? $"第 {m.Groups[1].Value} 行第 {m.Groups[2].Value} 列：{msg}" : msg;
            return new[] { new ValidationIssue(IssueLevel.Error, "lua", text) };
        }
        catch (Exception ex)
        {
            return new[] { new ValidationIssue(IssueLevel.Error, "lua", "Lua 解析失败：" + ex.Message) };
        }
    }
}