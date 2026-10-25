using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace PptInk.Core;

/// <summary>
/// 诊断包导出（v1.1.1，真机验收配套）：把排障所需的环境快照打成 zip，
/// 存放 %LocalAppData%\PptInk\diagnostics\。内容只含环境与配置事实：
///   environment.txt —— 版本/OS/.NET/显示器物理矩形与 DPI/放映屏口径/机型档案/Office 与 Python 探测；
///   config.json     —— 当前配置副本；
///   logs/*.log      —— 最近的按天日志。
/// 明确不含：板书内容、屏幕截图（课堂隐私边界，导出前不需要用户判断能不能发）。
/// </summary>
public static class DiagnosticsExporter
{
    private static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk");

    /// <summary>导出诊断包（后台线程调用）；返回 zip 路径，失败抛异常由调用方提示。</summary>
    public static string Export()
    {
        var dir = Path.Combine(Root, "diagnostics");
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"PptInk-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        string tmp = zipPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddText(zip, "environment.txt", BuildEnvironmentText());

            var configPath = Path.Combine(Root, "config.json");
            if (File.Exists(configPath))
            {
                var entry = zip.CreateEntry("config.json");
                using var es = entry.Open();
                using var src = File.OpenRead(configPath);
                src.CopyTo(es);
            }

            var logDir = Path.Combine(Root, "logs");
            if (Directory.Exists(logDir))
            {
                foreach (var file in Directory.GetFiles(logDir, "*.log")
                             .OrderByDescending(f => f).Take(7))
                {
                    var entry = zip.CreateEntry("logs/" + Path.GetFileName(file));
                    using var es = entry.Open();
                    using var src = File.OpenRead(file);
                    src.CopyTo(es);
                }
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
        return zipPath;
    }

    /// <summary>启动环境摘要（一行式，直接进当日日志）。</summary>
    public static string EnvironmentSummary()
    {
        var sb = new StringBuilder();
        sb.Append($"PptInk {AssemblyVersion()} | ");
        try
        {
            var os = Environment.OSVersion;
            sb.Append($"OS {os.VersionString}");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) sb.Append(" (Win11)");
            else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0)) sb.Append(" (Win10+)");
        }
        catch { sb.Append("OS ?"); }
        sb.Append($" | .NET {Environment.Version}");
        sb.Append($" | 64bit={Environment.Is64BitProcess}");
        try
        {
            int n = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (_, _, _, _) => { n++; return true; }, IntPtr.Zero);
            sb.Append($" | 显示器×{n}");
        }
        catch { }
        sb.Append($" | 放映屏 DIP[{ShowScreen.Bounds.Left:F0},{ShowScreen.Bounds.Top:F0} " +
                  $"{ShowScreen.Bounds.Width:F0}×{ShowScreen.Bounds.Height:F0}] DPI {ShowScreen.DpiScale * 100:F0}%");
        sb.Append($" | 机型档案={AppConfig.Current.DeviceProfileId ?? "自动"}");
        sb.Append($" | 输入模式={AppConfig.Current.InputMode}");
        return sb.ToString();
    }

    private static string AssemblyVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    private static string BuildEnvironmentText()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"版本：{AssemblyVersion()}");
        sb.AppendLine($"OS：{Environment.OSVersion.VersionString}（64 位进程={Environment.Is64BitProcess}）");
        sb.AppendLine($".NET：{Environment.Version}；{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"机器名（自核对外）：{Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("—— 显示器（物理像素 + 有效 DPI）——");
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(hMon, ref mi))
                {
                    bool primary = (mi.dwFlags & 1) != 0;
                    double scale = 1.0;
                    try
                    {
                        if (GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                            scale = dpiX / 96.0;
                    }
                    catch { }
                    var rc = mi.rcMonitor;
                    sb.AppendLine($"  {(primary ? "主屏" : "扩展")} 物理[{rc.left},{rc.top} {rc.right - rc.left}×{rc.bottom - rc.top}] " +
                                  $"DPI {scale * 100:F0}% 工作区[{mi.rcWork.left},{mi.rcWork.top} {mi.rcWork.right - mi.rcWork.left}×{mi.rcWork.bottom - mi.rcWork.top}] 句柄 0x{hMon:X}");
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { sb.AppendLine($"  枚举失败：{ex.Message}"); }

        sb.AppendLine();
        sb.AppendLine("—— 放映屏口径（ShowScreen 运行时快照）——");
        sb.AppendLine($"  DIP 边界：[{ShowScreen.Bounds.Left:F1},{ShowScreen.Bounds.Top:F1} {ShowScreen.Bounds.Width:F1}×{ShowScreen.Bounds.Height:F1}]");
        sb.AppendLine($"  物理边界：[{ShowScreen.PhysicalBounds.Left:F0},{ShowScreen.PhysicalBounds.Top:F0} {ShowScreen.PhysicalBounds.Width:F0}×{ShowScreen.PhysicalBounds.Height:F0}]");
        sb.AppendLine($"  DPI 缩放：{ShowScreen.DpiScale * 100:F0}%");

        sb.AppendLine();
        sb.AppendLine("—— 配置要点 ——");
        sb.AppendLine($"  机型档案={AppConfig.Current.DeviceProfileId ?? "自动"}；笔记本试用={AppConfig.Current.LaptopTestMode}");
        sb.AppendLine($"  输入模式={AppConfig.Current.InputMode}；整窗命中环境={AppConfig.Current.WholeWindowHit}；穿透隐藏板书={AppConfig.Current.TouchThroughInkHidden}");
        sb.AppendLine($"  手掌抑制={AppConfig.Current.PalmRejectionEnabled}；严格={AppConfig.Current.OnlyFingerInk}；避让手势区={AppConfig.Current.RespectGestureZone}");
        sb.AppendLine($"  AI 开启={AppConfig.Current.AiEnabled}；插件开启={AppConfig.Current.PluginsEnabled}；调试模式={AppConfig.Current.DebugMode}");

        sb.AppendLine();
        sb.AppendLine("—— 环境探测（只读，不下载/不安装）——");
        sb.AppendLine($"  PowerPoint COM：{ProbeCom("PowerPoint.Application")}");
        sb.AppendLine($"  WPS COM：{ProbeCom("Kwpp.Application")}");
        sb.AppendLine($"  系统 Python：{ProbePythonOnPath()}");
        return sb.ToString();
    }

    private static string ProbeCom(string progId)
    {
        try
        {
            var t = Type.GetTypeFromProgID(progId);
            if (t is null) return "未注册";
            // 只查注册不激活实例（激活可能拉起进程/弹窗，诊断包不做）
            return "已注册";
        }
        catch { return "未注册"; }
    }

    private static string ProbePythonOnPath()
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), "python.exe"))) return $"PATH 命中 {dir.Trim()}";
                }
                catch { }
            }
        }
        catch { }
        return "PATH 未见 python.exe（AI 需内置运行时或手动安装）";
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var s = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        s.Write(content);
    }

    #region Win32

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    #endregion
}
