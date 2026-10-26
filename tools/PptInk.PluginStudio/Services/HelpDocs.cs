using System.Diagnostics;
using System.IO;
using System.Windows;
using PptInk.PluginStudio.Core;

namespace PptInk.PluginStudio.Services;

/// <summary>打开随程序分发的使用说明（USAGE.md）。找不到时给出仓库路径提示。</summary>
internal static class HelpDocs
{
    public static void OpenUsageDoc(Window owner)
    {
        var path = FindUsageDoc();
        if (path is null)
        {
            MessageBox.Show(
                "未找到 USAGE.md（应随程序一起分发在程序目录）。\n" +
                "仓库位置：tools/PptInk.PluginStudio/USAGE.md",
                "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FilePluginStudioLog.Instance.Warn("打开使用说明失败：" + ex.Message);
            MessageBox.Show("无法打开使用说明：" + ex.Message, "使用说明", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindUsageDoc()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "USAGE.md", "使用说明.md", "USAGE.zh.md" })
        {
            var p = Path.Combine(baseDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}