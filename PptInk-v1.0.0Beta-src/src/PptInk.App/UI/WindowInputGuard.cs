using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PptInk.UI;

/// <summary>墨迹层输入模式。</summary>
public enum InkInputMode
{
    /// <summary>接收笔/触摸/鼠标（书写与擦除）。</summary>
    Ink,

    /// <summary>整层命中测试穿透（「触摸操作」模式：输入直达 PPT）。</summary>
    PassThrough,
}

/// <summary>
/// 窗口输入样式守卫（v0.2.0 输入层重构核心）。
///
/// 背景：墨迹层的 Win32 扩展样式（WS_EX_TRANSPARENT 穿透位）此前散落三处直写
/// （HookHwnd / ApplyPassthroughStyle / VerifyAndRepair），而 WPF 内部会在
/// Show/Hide、DPI 变化、渲染状态切换时改写 EXSTYLE 覆盖我们的设置 ——
/// 一旦穿透位被意外残留，鼠标/触摸/笔全部直达下层窗口（用户实测「画笔模式下
/// 鼠标照样操作下层应用，一直如此」的根因路径）。
///
/// 本类是 EXSTYLE 的<strong>唯一写入点</strong>，三层防御：
///  1) <see cref="Apply"/>：写入后立即回读验证，失败记日志；
///  2) WM_STYLECHANGED 钩子：外部（WPF 内部/系统/第三方）改写样式且与期望不符，
///     <strong>消息级零延迟回滚</strong>（不再等 600ms 看门狗轮询）；
///  3) <see cref="Verify"/>：看门狗轮询二道防线（事件钩子被绕过时兜底）。
///
/// 恒设位：WS_EX_NOACTIVATE（不抢焦点）+ WS_EX_TOOLWINDOW（Alt+Tab 隐藏）。
/// </summary>
public sealed class WindowInputGuard
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_STYLECHANGED = 0x007C;

    private readonly HwndSource _source;
    private readonly Action<string>? _log;
    private InkInputMode _mode;
    private bool _applying;   // 自身写入也会触发 WM_STYLECHANGED：标记防反馈环

    public WindowInputGuard(HwndSource source, InkInputMode initial, Action<string>? log = null)
    {
        _source = source;
        _log = log;
        _mode = initial;
        _source.AddHook(WndProc);
        Apply("attach");
    }

    public InkInputMode Mode => _mode;

    /// <summary>切换输入模式（幂等；同值重入仅重申一次写入，用于自愈后校准）。</summary>
    public void SetMode(InkInputMode mode)
    {
        bool changed = _mode != mode;
        _mode = mode;
        if (changed) Apply("set");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // wParam==0 表示 GWL_EXSTYLE 变更（==1 是 GWL_STYLE，与输入无关）
        if (msg == WM_STYLECHANGED && wParam.ToInt64() == 0 && !_applying)
        {
            int ex = GetWindowLongW(_source.Handle, GWL_EXSTYLE);
            if (Mismatch(ex))
            {
                _log?.Invoke($"[InputGuard] 外部改写输入样式（transparent 位={Has(ex, WS_EX_TRANSPARENT)}，期望模式={_mode}），已回滚");
                Apply("heal");
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>按期望模式写入 EXSTYLE（写后回读验证）。</summary>
    private void Apply(string cause)
    {
        try
        {
            _applying = true;
            IntPtr h = _source.Handle;
            int ex = GetWindowLongW(h, GWL_EXSTYLE);
            ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (_mode == InkInputMode.PassThrough) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLongW(h, GWL_EXSTYLE, ex);
            int now = GetWindowLongW(h, GWL_EXSTYLE);
            if (Mismatch(now))
                _log?.Invoke($"[InputGuard] 写入后验证失败（cause={cause}，mode={_mode}，transparent 位={Has(now, WS_EX_TRANSPARENT)}）");
        }
        catch { }
        finally { _applying = false; }
    }

    /// <summary>看门狗二道防线：校验并修复。返回是否发生过修复。</summary>
    public bool Verify()
    {
        try
        {
            if (!Mismatch(GetWindowLongW(_source.Handle, GWL_EXSTYLE))) return false;
            Apply("watchdog");
            return true;
        }
        catch { return false; }
    }

    private bool Mismatch(int ex) => Has(ex, WS_EX_TRANSPARENT) != (_mode == InkInputMode.PassThrough);

    private static bool Has(int ex, int bit) => (ex & bit) != 0;

    /// <summary>辅助浮窗统一标记：加 WS_EX_TOOLWINDOW（从 Alt+Tab 隐藏）。
    /// v0.2.0：此前全部辅助窗口只设 ShowInTaskbar=false，Alt+Tab 中「浮岛」「工具栏」
    /// 各自显示为独立条目 —— 用户误以为浮岛是单独进程。只加 TOOLWINDOW，
    /// 不动 NOACTIVATE（设置中心等正常窗口需要激活能力）。</summary>
    public static void ApplyToolWindowStyle(Window w)
    {
        try
        {
            if (PresentationSource.FromVisual(w) is HwndSource s)
            {
                IntPtr h = s.Handle;
                int ex = GetWindowLongW(h, GWL_EXSTYLE);
                SetWindowLongW(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            }
        }
        catch { }
    }

    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

    #endregion
}
