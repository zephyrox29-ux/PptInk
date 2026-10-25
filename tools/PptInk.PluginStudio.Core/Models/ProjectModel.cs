namespace PptInk.PluginStudio.Core.Models;

/// <summary>包内额外资源（入口脚本以外的文件）。</summary>
public sealed record ResourceFile(string RelativePath, byte[] Content);

/// <summary>工具内的编辑单元：manifest + Lua 源码 + 资源（可另存为 .ppstudio 工程）。</summary>
public sealed class ProjectModel
{
    public PluginManifest Manifest { get; set; } = new();

    /// <summary>入口脚本源码（与 Manifest.Entry 对应）。</summary>
    public string LuaSource { get; set; } = "";

    public List<ResourceFile> Resources { get; set; } = new();

    /// <summary>工程文件路径（.ppstudio），未保存过为 null。</summary>
    public string? ProjectPath { get; set; }
}