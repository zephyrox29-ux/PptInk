namespace PptInk.PluginStudio.Core.Packaging;

/// <summary>插件包的固定结构与限制（对齐主程序 PluginManager.InstallFromZip）。</summary>
public static class PackageLayout
{
    public const string ManifestFile = "manifest.json";
    public const int MaxEntries = 128;
    public const long MaxUncompressedBytes = 20L * 1024 * 1024;
}