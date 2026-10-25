namespace PptInk.PluginStudio.Core;

/// <summary>工具用到的路径。插件根目录默认 <c>%LocalAppData%\PptInk\plugins</c>（与主程序一致），
/// 可被 <see cref="OverridePluginRoot"/> 覆盖以便测试。</summary>
public static class AppPaths
{
    private static string? _pluginRootOverride;

    public static string PptInkDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk");

    public static string PluginRoot => _pluginRootOverride ?? Path.Combine(PptInkDataRoot, "plugins");

    public static string LogRoot => Path.Combine(PptInkDataRoot, "PluginStudio", "logs");

    /// <summary>覆盖插件根目录（测试用）；传 null 恢复默认。</summary>
    public static void OverridePluginRoot(string? root) => _pluginRootOverride = root;
}