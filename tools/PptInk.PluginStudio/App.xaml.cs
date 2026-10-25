using System.Windows;
using PptInk.PluginStudio.Core;

namespace PptInk.PluginStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            FilePluginStudioLog.Instance.Error(args.Exception, "未处理异常");
            MessageBox.Show("发生未预期的错误，详情已写入日志。\n" + args.Exception.Message,
                "PptInk 插件工作室", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}