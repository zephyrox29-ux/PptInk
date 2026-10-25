using System.Windows;
using Microsoft.Win32;

namespace PptInk.PluginStudio.Services;

/// <summary>文件/目录选择对话框的薄封装（统一窗口标题与过滤器）。</summary>
internal static class FileDialogs
{
    public static string? OpenProject(Window owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开工程",
            Filter = "插件工作室工程 (project.ppstudio)|project.ppstudio|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? ImportZip(Window owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入插件包",
            Filter = "插件包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? SaveZip(Window owner, string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "打包插件",
            Filter = "插件包 (*.zip)|*.zip",
            FileName = suggestedName,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? PickFolder(Window owner, string title)
    {
        // .NET 8 WPF 自带 OpenFolderDialog
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    public static string? SaveText(Window owner, string suggestedName, string filter)
    {
        var dlg = new SaveFileDialog { Title = "导出", Filter = filter, FileName = suggestedName, OverwritePrompt = true };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }
}