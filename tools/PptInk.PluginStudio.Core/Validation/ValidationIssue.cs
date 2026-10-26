namespace PptInk.PluginStudio.Core.Validation;

/// <summary>问题级别：Error 阻断打包/安装；Warning 仅提示。</summary>
public enum IssueLevel
{
    Error,
    Warning,
}

/// <summary>校验问题：级别 + 定位字段 + 文案。
/// Field 取值示例：manifest 字段名（如 "id"）、面板定位（如 "panel.elements[2].onEvent"）、"lua"（脚本）。</summary>
public sealed record ValidationIssue(IssueLevel Level, string Field, string Message)
{
    public bool IsError => Level == IssueLevel.Error;
}