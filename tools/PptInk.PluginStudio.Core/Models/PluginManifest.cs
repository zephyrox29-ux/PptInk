using System.Text.Json;
using System.Text.Json.Serialization;

namespace PptInk.PluginStudio.Core.Models;

/// <summary>
/// 插件清单（对应主程序 src/PptInk.App/Plugins/PluginContract.cs::PluginManifest）。
/// 同步来源：src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest / NormalizeManifest。
/// 主程序规则变更时，本模型与 ManifestValidator 须同步。
/// </summary>
public sealed class PluginManifest
{
    public int Magic { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>入口脚本（包内相对路径，如 main.lua）。</summary>
    public string Entry { get; set; } = "main.lua";

    /// <summary>PANEL 类：面板声明（宿主渲染）。</summary>
    public PanelDecl? Panel { get; set; }

    /// <summary>权限声明：file / network / clipboard。</summary>
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>预留字段：主程序当前不验签。</summary>
    public string? Signature { get; set; }

    [JsonIgnore]
    public PluginKind Kind => (PluginKind)Magic;
}

/// <summary>manifest.json 序列化（读大小写不敏感、写 camelCase，与主程序读取口径一致）。</summary>
public static class PluginManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize(PluginManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    public static PluginManifest? Deserialize(string json) => JsonSerializer.Deserialize<PluginManifest>(json, Options);
}