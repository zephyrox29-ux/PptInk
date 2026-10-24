using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PptInk.Core;

/// <summary>
/// 翻页笔转发器（教学刚需）：
///  教师刚在工具栏上点过按钮 → 焦点落在 PptInk → 此时按翻页笔（PageUp/PageDown/←/→），
///  键盘消息发给的是 PptInk 而不是 PPT → 翻页"失灵"，教师被迫找鼠标点回幻灯片。
///
///  解决：WH_KEYBOARD_LL 低级键盘钩子（仅 S1 期间安装，S0/S2 卸载）。
///  规则精确、克制：
///   - 仅当前台窗口属于本进程时拦截（焦点在 PPT / 其他程序时完全放行，不产生双翻页）；
///   - 仅拦 PageUp/PageDown/←/→ 四个翻页笔标准键（空格/回车/Esc 不碰 —— 表单与引导窗语义优先）；
///   - 拦截后异步转发（Dispatcher.BeginInvoke）：钩子回调 300ms 超时硬限制，COM 翻页绝不在回调内同步做；
///   - 放行注入键（LLKHF_INJECTED）：我们自己 PostMessage 的兜底翻页不会被二次钩住。
/// </summary>
public sealed class PresenterForwarder : IDisposable
{
    private readonly Action<bool> _advance;       // true = 下一页
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private readonly HookProc _proc;              // 防 GC（低级钩子回调委托被回收 = 进程级事故）

    private static PresenterForwarder? _active;   // 低级钩子进程内只装一条

    private PresenterForwarder(Action<bool> advance, Dispatcher dispatcher)
    {
        _advance = advance;
        _dispatcher = dispatcher;
        _proc = LowLevelHookProc;
    }

    /// <summary>安装（幂等）。返回转发器实例；S1 进入时调用。</summary>
    public static PresenterForwarder Install(Action<bool> advance)
    {
        if (_active is not null) return _active;
        var f = new PresenterForwarder(advance, Dispatcher.CurrentDispatcher);
        _active = f;
        f._hook = SetWindowsHookExW(WH_KEYBOARD_LL, f._proc, GetModuleHandleW(null), 0);
        return f;
    }

    /// <summary>卸载（幂等）。S0/S2 退出时调用；钩子常驻会成为全局输入税，绝不留。</summary>
    public static void UninstallAll()
    {
        if (_active is null) return;
        _active.Dispose();
        _active = null;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (ReferenceEquals(_active, this)) _active = null;
    }

    // ———————— 回调 ————————

    private IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION || _hook == IntPtr.Zero)
            return CallNextHookEx(_hook, code, wParam, lParam);

        // 仅 KEYDOWN（含 SYSKEYDOWN）；KEYUP 放行
        int msg = wParam.ToInt32();
        if (msg is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            return CallNextHookEx(_hook, code, wParam, lParam);

        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // 放行注入键（我们自己的兜底 PostMessage / 远程注入均不算"教师手按"）
        if ((k.Flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hook, code, wParam, lParam);

        bool? forward = k.VkCode switch
        {
            VK_NEXT or VK_RIGHT => true,   // PageDown / → ：下一页
            VK_PRIOR or VK_LEFT => false,  // PageUp / ← ：上一页
            _ => null,
        };
        if (forward is null)
            return CallNextHookEx(_hook, code, wParam, lParam);

        // 核心判定：焦点是否在本进程（痛点场景）。在 PPT / 其他程序 → 完全放行。
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return CallNextHookEx(_hook, code, wParam, lParam);
        _ = GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid != GetCurrentProcessId())
            return CallNextHookEx(_hook, code, wParam, lParam);

        // 焦点在本进程 → 吞掉本键，异步转发翻页（回调内不做 COM/耗时操作，300ms 硬限制）
        bool fwd = forward.Value;
        _dispatcher.BeginInvoke(() => _advance(fwd), DispatcherPriority.Input);
        return (IntPtr)1;
    }

    #region Win32

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint VK_PRIOR = 0x21;  // PageUp
    private const uint VK_NEXT = 0x22;   // PageDown
    private const uint VK_LEFT = 0x25;
    private const uint VK_RIGHT = 0x27;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    #endregion
}
