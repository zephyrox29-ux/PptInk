using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PptInk;

/// <summary>
/// 程序入口：S0 后台静默常驻。
/// 无窗口启动（不设 StartupUri），仅托盘小图标（可在设置中隐藏）。
/// 单实例互斥防止自启项 + 手动启动叠加。
/// </summary>
internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        // 注意（v0.3.0 教训存档）：绝不启用 Switch.System.Windows.Input.Stylus.EnablePointerSupport ——
        // 该实验性开关与 WPF 透明窗口（AllowsTransparency，墨迹层正是）不兼容，会让
        // StylusPlugIn 一颗触摸都收不到。WPF 触摸走 wisptis 稳定链路；原生 WM_POINTER
        // 通道由墨迹层 WndProc 自行处理（三通道冗余，见 InkOverlayWindow）。

        // 静默常驻进程：不给课堂弹任何系统级错误框
        SetErrorMode(ErrorModes.SEM_FAILCRITICALERRORS | ErrorModes.SEM_NOGPFAULTERRORBOX);

        // 单实例：学校机器常配置"自启 + 桌面快捷方式"，双开会引起双墨迹层、COM 双订阅
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\PptInk.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：转发唤醒请求后退出（例如用户双击图标想手动唤起 UI）
            TrySignalExistingInstance();
            return;
        }

        var silent = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)
                  || args.Contains("/silent", StringComparer.OrdinalIgnoreCase);
        // 便携试用：不注册开机自启（zip 解压即用，不往试用机留常驻项）
        var portable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);

        var app = new App(silentStart: silent, portable: portable);
        app.InitializeComponent();
        app.Run();
    }

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", "PptInk.ControlPipe", System.IO.Pipes.PipeDirection.Out);
            client.Connect(timeout: 500);
            using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
            writer.Write("""{"cmd":"show-tray-menu"}""");
        }
        catch (TimeoutException) { /* 主实例忙，忽略 */ }
        catch (System.IO.IOException) { /* 主实例管道未就绪，忽略 */ }
    }

    [DllImport("kernel32.dll")]
    private static extern ErrorModes SetErrorMode(ErrorModes modes);

    [Flags]
    private enum ErrorModes : uint
    {
        SEM_FAILCRITICALERRORS = 0x0001,
        SEM_NOGPFAULTERRORBOX = 0x0002, // 静默进程：不给课堂弹崩溃对话框
    }
}
