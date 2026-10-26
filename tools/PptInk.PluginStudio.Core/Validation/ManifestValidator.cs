using System.Text;
using System.Text.RegularExpressions;
using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Validation;

/// <summary>
/// manifest 校验（对齐主程序 src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest / NormalizeManifest）。
/// 与主程序不同的取向：主程序遇错即抛（安装短路），本工具**收集全部问题**以便界面逐条定位；
/// 打包/安装前若存在 Error 级问题则拒绝。
/// </summary>
public static class ManifestValidator
{
    public const int MaxElements = 64;
    public const double PanelWidthMin = 160, PanelWidthMax = 600;
    public const double PanelHeightMin = 100, PanelHeightMax = 500;
    public const double PanelAlphaMin = 0.3, PanelAlphaMax = 1.0;
    public const double SliderBound = 100_000;
    public const int ScriptMaxBytes = 256 * 1024;
    public const int PackageMaxEntries = 128;
    public const long PackageMaxBytes = 20L * 1024 * 1024;

    private static readonly Regex IdRegex = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierRegex = new(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownPermissions = new(StringComparer.OrdinalIgnoreCase) { "file", "network", "clipboard" };
    private static readonly string[] Docks = { "left", "right", "bottom", "float" };
    private static readonly string[] ElementTypes = { "text", "slider", "button" };

    /// <summary>校验 manifest。entryExists 用于判断入口脚本是否存在于工程/包内。</summary>
    public static IReadOnlyList<ValidationIssue> Validate(PluginManifest m, Func<string, bool> entryExists)
    {
        var issues = new List<ValidationIssue>();

        if (m.Magic is < 1 or > 3)
            issues.Add(Err("magic", "魔数非法：应为 1（面板）/ 2（绘制）/ 3（自由）"));

        if (string.IsNullOrWhiteSpace(m.Id) || !IdRegex.IsMatch(m.Id))
            issues.Add(Err("id", "Id 必须以字母或数字开头，且仅含字母、数字、点、下划线、连字符，长度 ≤100"));

        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 120)
            issues.Add(Err("name", "插件名称不能为空且长度 ≤120"));

        if ((m.Author?.Length ?? 0) > 200) issues.Add(Err("author", "作者字段长度 ≤200"));
        if ((m.Description?.Length ?? 0) > 1000) issues.Add(Err("description", "描述长度 ≤1000"));
        if ((m.Version?.Length ?? 0) > 64) issues.Add(Err("version", "版本号长度 ≤64"));

        if (string.IsNullOrWhiteSpace(m.Entry) || m.Entry.Length > 240 || PathSafety.IsUnsafeRelative(m.Entry))
            issues.Add(Err("entry", "入口脚本路径非法（不能为空、不能是绝对路径或含 .. ，长度 ≤240）"));
        else if (!entryExists(m.Entry))
            issues.Add(Err("entry", $"入口脚本不存在：{m.Entry}"));

        m.Permissions ??= Array.Empty<string>();
        foreach (var p in m.Permissions)
            if (string.IsNullOrWhiteSpace(p) || !KnownPermissions.Contains(p))
                issues.Add(Err("permissions", $"未知权限：{p}（仅支持 file / clipboard；network 当前不生效）"));

        ValidatePanel(m.Panel, issues);
        return issues;
    }

    private static void ValidatePanel(PanelDecl? panel, List<ValidationIssue> issues)
    {
        if (panel is null) return;

        var dock = string.IsNullOrWhiteSpace(panel.Dock) ? "float" : panel.Dock.Trim().ToLowerInvariant();
        if (Array.IndexOf(Docks, dock) < 0)
            issues.Add(Err("panel.dock", "Dock 只支持 left / right / bottom / float"));

        if (!InRange(panel.Width, PanelWidthMin, PanelWidthMax))
            issues.Add(Warn("panel.width", $"宽度超出 {PanelWidthMin:0}–{PanelWidthMax:0}，打包时将收敛到区间内"));
        if (!InRange(panel.Height, PanelHeightMin, PanelHeightMax))
            issues.Add(Warn("panel.height", $"高度超出 {PanelHeightMin:0}–{PanelHeightMax:0}，打包时将收敛到区间内"));
        if (!InRange(panel.Alpha, PanelAlphaMin, PanelAlphaMax))
            issues.Add(Warn("panel.alpha", $"透明度应在 {PanelAlphaMin:0.0}–{PanelAlphaMax:0.0}，打包时将收敛到区间内"));

        panel.Elements ??= Array.Empty<PanelElement>();
        if (panel.Elements.Length > MaxElements)
            issues.Add(Err("panel.elements", $"面板元素过多（最多 {MaxElements} 个）"));

        for (int i = 0; i < panel.Elements.Length; i++)
        {
            var el = panel.Elements[i];
            var f = $"panel.elements[{i}]";
            if (el is null) { issues.Add(Err(f, "面板包含空元素")); continue; }

            var type = (el.Type ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(ElementTypes, type) < 0)
                issues.Add(Err(f + ".type", $"不支持的元素类型：{el.Type}（仅 text / slider / button）"));

            if ((el.Id?.Length ?? 0) > 80) issues.Add(Err(f + ".id", "元素 Id 长度 ≤80"));
            if ((el.Text?.Length ?? 0) > 500) issues.Add(Err(f + ".text", "元素文本长度 ≤500"));

            bool identOk = !string.IsNullOrEmpty(el.OnEvent) && IdentifierRegex.IsMatch(el.OnEvent);
            if (!identOk)
            {
                if (type is "button" or "slider")
                    issues.Add(Err(f + ".onEvent", "按钮/滑条的 OnEvent 必须是合法的 Lua 函数名（字母或下划线开头）"));
                else
                    issues.Add(Warn(f + ".onEvent", "OnEvent 非法，将按 noop 处理"));
            }

            if (type == "slider")
            {
                double min = double.IsFinite(el.Min ?? 0) ? el.Min ?? 0 : 0;
                double max = double.IsFinite(el.Max ?? 100) ? el.Max ?? 100 : 100;
                if (max <= min) issues.Add(Err(f + ".max", "滑条最大值必须大于最小值"));
                else if (min < -SliderBound || max > SliderBound)
                    issues.Add(Warn(f, $"滑条范围建议在 ±{SliderBound:0} 内"));
                double val = double.IsFinite(el.Value ?? min) ? el.Value ?? min : min;
                if (max > min && (val < min || val > max))
                    issues.Add(Warn(f + ".value", "滑条默认值不在 [min, max] 区间，将收敛"));
            }
        }
    }

    /// <summary>按主程序口径收敛各字段（trim / clamp / 去重小写）。就地修改并返回同一实例。</summary>
    public static PluginManifest Normalize(PluginManifest m)
    {
        m.Name = (m.Name ?? "").Trim();
        m.Author ??= "";
        m.Description ??= "";
        m.Version ??= "1.0.0";
        m.Permissions ??= Array.Empty<string>();
        m.Permissions = m.Permissions.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();

        if (m.Panel is { } panel)
        {
            panel.Dock = string.IsNullOrWhiteSpace(panel.Dock) ? "float" : panel.Dock.Trim().ToLowerInvariant();
            if (Array.IndexOf(Docks, panel.Dock) < 0) panel.Dock = "float";
            panel.Width = double.IsFinite(panel.Width) ? Math.Clamp(panel.Width, PanelWidthMin, PanelWidthMax) : 240;
            panel.Height = double.IsFinite(panel.Height) ? Math.Clamp(panel.Height, PanelHeightMin, PanelHeightMax) : 180;
            panel.Alpha = double.IsFinite(panel.Alpha) ? Math.Clamp(panel.Alpha, PanelAlphaMin, PanelAlphaMax) : 0.88;
            panel.Elements ??= Array.Empty<PanelElement>();
            foreach (var el in panel.Elements)
            {
                if (el is null) continue;
                el.Type = (el.Type ?? "text").Trim().ToLowerInvariant();
                if (Array.IndexOf(ElementTypes, el.Type) < 0) el.Type = "text";
                if (string.IsNullOrEmpty(el.OnEvent) || !IdentifierRegex.IsMatch(el.OnEvent)) el.OnEvent = "noop";
                if (el.Type == "slider")
                {
                    double min = double.IsFinite(el.Min ?? 0) ? Math.Clamp(el.Min ?? 0, -SliderBound, SliderBound) : 0;
                    double max = double.IsFinite(el.Max ?? 100) ? Math.Clamp(el.Max ?? 100, -SliderBound, SliderBound) : 100;
                    if (max <= min) max = min + 1;
                    double val = el.Value is { } v && double.IsFinite(v) ? v : min;
                    el.Min = min; el.Max = max; el.Value = Math.Clamp(val, min, max);
                }
            }
        }
        return m;
    }

    /// <summary>工程内容限制：与主程序 InstallFromZip 的 256KB 脚本、128 条目、20MB
    /// 实际解压字节保持一致，避免 Studio 校验全绿而主程序拒绝，或“一键安装成功、
    /// 重启扫描时插件消失”。</summary>
    public static IReadOnlyList<ValidationIssue> ValidateContent(string? luaSource, IReadOnlyList<ResourceFile>? resources)
    {
        var issues = new List<ValidationIssue>();
        int scriptBytes = Encoding.UTF8.GetByteCount(luaSource ?? "");
        if (scriptBytes > ScriptMaxBytes)
            issues.Add(Err("lua", $"入口脚本 {scriptBytes} 字节，超过 256 KB 限制"));

        var res = resources ?? Array.Empty<ResourceFile>();
        if (res.Count + 2 > PackageMaxEntries)
            issues.Add(Err("resources", $"包内文件数 {res.Count + 2}，超过 128 个限制"));
        long total = scriptBytes;
        foreach (var r in res)
        {
            if (r is null || PathSafety.IsUnsafeRelative(r.RelativePath))
                issues.Add(Err("resources", $"资源路径非法：{r?.RelativePath ?? "空资源"}"));
            else
                total += r.Content?.LongLength ?? 0;
        }
        if (total > PackageMaxBytes)
            issues.Add(Err("resources", $"包内实际内容 {total} 字节，超过 20 MB 限制"));
        return issues;
    }

    private static bool InRange(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;
    private static ValidationIssue Err(string field, string msg) => new(IssueLevel.Error, field, msg);
    private static ValidationIssue Warn(string field, string msg) => new(IssueLevel.Warning, field, msg);
}