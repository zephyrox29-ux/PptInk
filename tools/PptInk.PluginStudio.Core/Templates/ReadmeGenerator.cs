using System.Text;
using PptInk.PluginStudio.Core.Lua;
using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Templates;

/// <summary>生成可直接分享的安装说明（Markdown）：安装步骤 + 权限含义 + 注入 API 参考。</summary>
public static class ReadmeGenerator
{
    public static string Generate(PluginManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {m.Name}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(m.Description)) { sb.AppendLine(m.Description); sb.AppendLine(); }

        sb.AppendLine("## 安装");
        sb.AppendLine();
        sb.AppendLine("1. 打开 PptInk → 托盘/设置中心 → 插件 → 安装插件包（选择本插件的 .zip）。");
        sb.AppendLine("2. 安装后可到设置中心「插件」页启用；工具栏「插件」按钮弹出快捷面板。");
        sb.AppendLine("3. 首次启用若插件声明了 file/clipboard 权限，会弹一次确认。");
        sb.AppendLine();
        sb.AppendLine($"> 插件 Id：`{m.Id}`　版本：`{m.Version}`　类型：`{KindName(m.Kind)}`　作者：`{m.Author}`");
        sb.AppendLine();

        sb.AppendLine("## 权限");
        sb.AppendLine();
        var perms = m.Permissions ?? Array.Empty<string>();
        if (perms.Length == 0) sb.AppendLine("- 本插件**不申请任何权限**。");
        foreach (var p in perms)
            sb.AppendLine("- `" + p + "`：" + PermissionText(p));
        sb.AppendLine();

        sb.AppendLine("## 可用脚本 API");
        sb.AppendLine();
        sb.AppendLine("| 函数 | 签名 | 所需权限 |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var api in LuaApiCatalog.ForPermissions(perms))
            sb.AppendLine($"| `{api.Name}` | `{api.Signature}` | {api.RequiredPermission ?? "—"} |");
        sb.AppendLine();
        sb.AppendLine($"入口回调：`{(m.Kind == PluginKind.Panel ? "onActivate" : "onStart")}`；停止回调：`onDeactivate`。");
        return sb.ToString();
    }

    private static string KindName(PluginKind kind) => kind switch
    {
        PluginKind.Panel => "面板类",
        PluginKind.Draw => "绘制类",
        _ => "自由类",
    };

    private static string PermissionText(string p) => p.ToLowerInvariant() switch
    {
        "file" => "允许把截图等文件写入插件专属目录（沙箱内，不能写到任意路径）。",
        "clipboard" => "允许读写剪贴板。",
        "network" => "当前版本未注入网络能力，声明不生效。",
        _ => "未知权限。",
    };
}