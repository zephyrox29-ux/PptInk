using System.Runtime.InteropServices;
using System.Windows;

namespace PptInk.Core;

/// <summary>
/// 放映屏感知（v0.1.3 试用反馈核心修复）：
/// 全部全屏 UI（墨迹层 / 工具栏 / 浮岛 / 新手引导）必须覆盖 <b>PPT 放映窗口所在的显示器</b>，
/// 而不是主屏 —— 教室典型部署是「笔记本（主屏）+ 大屏（扩展屏放映）」，锚定
/// SystemParameters.PrimaryScreen* / WorkArea（主屏 DIP）会让墨迹层盖在笔记本屏上、
/// 大屏书写全部穿透给 PPT（「画笔/荧光笔/擦除从未生效」的根因），工具栏/浮岛也
/// 停靠在主屏边缘（「大屏右缘看到错位且只有一半的面板」）。
///
/// 数据流：轮询/放映事件线程调 UpdateFromShowWindow(放映 HWND) → Win32
/// MonitorFromWindow + GetDpiForMonitor + GetMonitorInfo → 目标屏 DIP/物理边界快照；
/// 目标屏变化（首次定位 / 放映窗口移屏）触发 ScreenChanged（在调用线程触发，
/// 订阅方自行 Dispatcher 封送）。HWND 无效时保留上一块已知屏；退场复位走 ResetToPrimary。
/// 线程安全：后台线程写、UI 线程读，全部经 _gate。
/// </summary>
public static class ShowScreen
{
    private static readonly object _gate = new();
    private static Rect _bounds = new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    private static Rect _work = SystemParameters.WorkArea;
    private static Rect _phys = PhysicalOfPrimary();
    private static double _scale = SafeSystemScale();
    private static IntPtr _monitor = IntPtr.Zero;   // IntPtr.Zero = 未定位（主屏默认口径）

    /// <summary>放映屏 DIP 边界（窗口 Left/Top/Width/Height 直接使用）。</summary>
    public static Rect Bounds { get { lock (_gate) return _bounds; } }

    /// <summary>放映屏工作区 DIP：主屏=任务栏避让区（SystemParameters.WorkArea），扩展屏=整屏（无任务栏）。</summary>
    public static Rect WorkBounds { get { lock (_gate) return _work; } }

    /// <summary>放映屏物理像素边界（v1.1.1）：GetMonitorInfo 原始口径。混合 DPI 部署下
    /// WPF 的 Window.Left/Top 与目标监视器坐标系不一致（dotnet/wpf #4127），窗口跨屏
    /// 钉位必须用物理像素 —— 这是唯一不会错的传递方式。</summary>
    public static Rect PhysicalBounds { get { lock (_gate) return _phys; } }

    /// <summary>放映屏 DPI 缩放：物理 px → DIP 除以它；DIP → 物理 px 乘以它。</summary>
    public static double DpiScale { get { lock (_gate) return _scale; } }

    /// <summary>目标屏变化（含「未定位 → 已定位」的首次）。在调用线程触发，订阅方自行封送 UI。</summary>
    public static event Action? ScreenChanged;

    /// <summary>以放映窗口 HWND 更新目标屏（后台线程可调）。
    /// v1.1.1：HWND 无效/Zero 时**保留上一块已知屏**，不再立刻回主屏 —— COM 瞬时失败、
    /// WPS 类名不匹配、放映窗口过渡期都会取到 0，原实现此时把整套 UI 拉回主屏、下一拍
    /// 又拉回来（反复跳屏）。放映真正结束时由调用方（S0 相位）显式 ResetToPrimary。</summary>
    public static void UpdateFromShowWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return;
            UpdateFromMonitor(mon);
        }
        catch
        {
            // 保持上一块已知屏（同上）：探测异常不改变目标屏
        }
    }

    /// <summary>显式复位回主屏（S0 退场时调用：托盘 Toast / 设置窗不再出现在已关闭的投影屏）。</summary>
    public static void ResetToPrimary()
    {
        try
        {
            var origin = new POINT { x = 0, y = 0 };
            var mon = MonitorFromPoint(ref origin, MONITOR_DEFAULTTOPRIMARY);
            if (mon != IntPtr.Zero)
            {
                UpdateFromMonitor(mon, isPrimaryReset: true);
                return;
            }
        }
        catch { }
        FallbackToPrimaryDip();
    }

    private static void UpdateFromMonitor(IntPtr mon, bool isPrimaryReset = false)
    {
        double scale;
        try
        {
            uint dpiX;
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
            else
                scale = SafeSystemScale();
        }
        catch { scale = SafeSystemScale(); }   // Win7/8 无 shcore
        scale = Math.Max(1.0, scale);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) { FallbackToPrimaryDip(); return; }

        var rc = mi.rcMonitor;
        var bounds = new Rect(rc.left / scale, rc.top / scale,
            (rc.right - rc.left) / scale, (rc.bottom - rc.top) / scale);
        var phys = new Rect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
        var origin = new POINT { x = 0, y = 0 };
        bool isPrimary = MonitorFromPoint(ref origin, MONITOR_DEFAULTTOPRIMARY) == mon;
        var work = isPrimary ? SystemParameters.WorkArea : bounds;

        bool changed;
        lock (_gate)
        {
            // v0.4.0 健康修复：原判定只看监视器句柄 —— 同一监视器上改缩放（用户在放映中
            // 调显示设置 / RDP 重连）句柄不变 → DpiScale/Bounds 永不刷新，也不广播
            // ScreenChanged → 消费方（档案门禁、手势区、插件截图）拿到陈旧口径。
            // 改为「监视器 / 缩放 / 边界」任一变化即更新并广播（消费方幂等处理）。
            changed = _monitor != mon || _scale != scale || _bounds != bounds || _work != work || _phys != phys;
            if (changed) { _bounds = bounds; _work = work; _scale = scale; _monitor = mon; _phys = phys; }
        }
        if (changed)
        {
            AppLog.Instance.Info(
                $"放映屏{(isPrimaryReset ? "复位主屏" : "变化")}：物理[{rc.left},{rc.top} {rc.right - rc.left}×{rc.bottom - rc.top}] " +
                $"DPI {scale * 100:F0}% DIP[{bounds.Left:F0},{bounds.Top:F0} {bounds.Width:F0}×{bounds.Height:F0}]");
            ScreenChanged?.Invoke();
        }
    }

    /// <summary>无监视器信息时的主屏 DIP 兜底（保持旧行为：主屏工作区 + 系统缩放）。</summary>
    private static void FallbackToPrimaryDip()
    {
        bool changed;
        lock (_gate)
        {
            changed = _monitor != IntPtr.Zero;
            if (changed)
            {
                _bounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
                _work = SystemParameters.WorkArea;
                _scale = SafeSystemScale();
                _phys = PhysicalOfPrimary();
                _monitor = IntPtr.Zero;
            }
        }
        if (changed) ScreenChanged?.Invoke();
    }

    /// <summary>主屏物理矩形近似（SystemParameters 主屏 DIP × 系统缩放；未定位阶段钉位兜底用）。</summary>
    private static Rect PhysicalOfPrimary()
    {
        try
        {
            double k = SafeSystemScale();
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth * k, SystemParameters.PrimaryScreenHeight * k);
        }
        catch { return new Rect(0, 0, 1920, 1080); }
    }

    /// <summary>系统 DPI（主屏口径）。无 GetDpiForSystem 的旧系统回退 100%。</summary>
    private static double SafeSystemScale()
    {
        try { return Math.Max(1.0, GetDpiForSystem() / 96.0); }
        catch { return 1.0; }
    }

    #region Win32

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(ref POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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
