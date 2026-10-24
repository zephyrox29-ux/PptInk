using System.Runtime.InteropServices;
using System.Text;
using PptInk.Core;

namespace PptInk.Ppt;

/// <summary>
/// PPT 放映控制：翻页栏的上一页/下一页/退出放映。
/// 优先 COM（精确、无焦点副作用）；COM 不可用（兜底模式下）直接向放映窗口
/// PostMessage 键消息，不抢焦点（我们的墨迹层 WS_EX_NOACTIVATE，绝不能打断书写）。
/// </summary>
public sealed class PptController : IDisposable
{
    private dynamic? _cachedApp;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const short VK_RIGHT = 0x27, VK_LEFT = 0x25, VK_ESCAPE = 0x1B, VK_NEXT = 0x22, VK_PRIOR = 0x21;

    /// <summary>下一页。返回是否经 COM 完成（false = 走了键消息兜底，需上层推算页码）。</summary>
    public bool NextSlide()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.Next();
                return true;
            }
        }
        catch { _cachedApp = null; }   // v0.8.1：RCW 失效即弃缓存（PPT 关闭重开后下次调用重绑）
        PostShowKey(VK_NEXT);
        return false;
    }

    /// <summary>上一页。</summary>
    public bool PrevSlide()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.Previous();
                return true;
            }
        }
        catch { _cachedApp = null; }
        PostShowKey(VK_PRIOR);
        return false;
    }

    /// <summary>退出放映（工具栏"停止播放"）。
    /// v0.1.5 强化：view.Exit()「返回成功」≠ 放映真的结束（个别版本/放映状态静默吞掉）——
    /// 300ms 后复核，仍存活则向放映窗口追加 ESC；UI 滞留由编排器看门狗最终兜底回收。</summary>
    public void ExitShow()
    {
        bool viaCom = false;
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.Exit();
                viaCom = true;
                AppLog.Instance.Info("退出放映：COM view.Exit()");
            }
        }
        catch (System.Exception ex) { _cachedApp = null; AppLog.Instance.Warn($"退出放映 COM 失败：{ex.Message}"); }

        if (!viaCom)
        {
            AppLog.Instance.Warn("退出放映：COM 不可用，PostMessage ESC 兜底");
            PostShowKey(VK_ESCAPE);
            return;
        }
        // Exit() 成功路径复核（假定目标不可信）：静默失败时向放映窗口追发 ESC
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300);
                if (HasActiveShow() && FindSlideShowWindow() is { } h)
                {
                    AppLog.Instance.Warn("退出放映复核：放映仍存活，PostMessage ESC 追击");
                    PostEscToWindow(h);
                }
            }
            catch { }
        });
    }

    /// <summary>COM 通道读取精确页码：SlideShowWindow.View.CurrentShowPosition。</summary>
    public bool TryGetSlidePosition(out int position)
    {
        position = -1;
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                position = (int)view.CurrentShowPosition;
                return position > 0;
            }
        }
        catch { _cachedApp = null; }
        return false;
    }

    /// <summary>COM 通道读取总页数：ActivePresentation.Slides.Count（页码徽标 "3 / 18" 右侧）。</summary>
    public bool TryGetTotalSlides(out int total)
    {
        total = -1;
        try
        {
            var app = GetApp();
            if (app is null) return false;
            total = (int)app.ActivePresentation.Slides.Count;
            return total > 0;
        }
        catch { _cachedApp = null; return false; }
    }

    /// <summary>放映是否仍在进行（COM 口径）：SlideShowWindows.Count &gt; 0。
    /// 供看门狗存活判定（与窗口枚举双口径互为兜底）。</summary>
    public bool HasActiveShow()
    {
        try
        {
            var app = GetApp();
            return app is not null && app.SlideShowWindows.Count > 0;
        }
        catch { _cachedApp = null; return false; }
    }

    /// <summary>向指定放映窗口投递 ESC（COM view.Exit() 在个别版本/放映状态下不生效时的强力兜底）。</summary>
    public static void PostEscToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_KEYDOWN, VK_ESCAPE, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, VK_ESCAPE, IntPtr.Zero);
    }

    /// <summary>窗口是否响应消息（挂起探测）：SendMessageTimeout(WM_NULL, 800ms)。
    /// v0.2.0：PowerPoint 退出放映时进程假死（窗口在但完全不响应）是用户
    /// 「黑屏、任务栏不见、PPT 全无响应、只能 CTRL+ALT+DEL」实测案例的根因 ——
    /// 此状态放映窗口类仍存在、COM 也可能存活，传统判活口径永远不回收，
    /// 必须用消息响应性探测识别。看门狗据此判死并收回全套批注 UI。</summary>
    public static bool IsWindowResponsive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            const uint SMTO_ABORTIFHUNG = 0x0002;
            const uint WM_NULL = 0x0000;
            return SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 800, out _) != IntPtr.Zero;
        }
        catch { return false; }
    }

    private bool TryGetSlideShowView(out dynamic view)
    {
        view = null!;
        var app = GetApp();
        if (app is null) return false;
        try
        {
            if (app.SlideShowWindows.Count == 0) return false;
            view = app.SlideShowWindows[1].View;
            return true;
        }
        catch { _cachedApp = null; return false; }   // v0.8.1：缓存的 RCW 失联（PPT 已退出）→ 弃缓存重绑
    }

    private dynamic? GetApp()
    {
        if (_cachedApp is not null) return _cachedApp;
        foreach (var progId in new[] { "PowerPoint.Application", "KWPP.Application" })
        {
            try
            {
                var app = ComGetActiveObject(progId);
                if (app is not null) { _cachedApp = app; return app; }
            }
            catch { }
        }
        return null;
    }

    /// <summary>向放映窗口投递键消息（兜底翻页）。若放映窗口已丢失则广播前台。</summary>
    private static void PostShowKey(short vk)
    {
        IntPtr hwnd = FindSlideShowWindow() ?? GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_KEYDOWN, (nint)vk, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, (nint)vk, IntPtr.Zero);
    }

    /// <summary>取放映窗口 HWND：优先 COM（SlideShowWindow.HWND，精确），失败回退窗口类名枚举。
    /// 供 ShowScreen 放映屏定位（后台线程调用安全：COM 经 marshaling，枚举纯 GDI 查询）。</summary>
    public IntPtr TryGetShowHwnd()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                int hwnd = (int)view.Parent.HWND;   // SlideShowWindow.HWND
                if (hwnd != 0) return new IntPtr(hwnd);
            }
        }
        catch { _cachedApp = null; }
        return FindSlideShowWindow() ?? IntPtr.Zero;
    }

    /// <summary>枚举放映窗口（PPT screenClass / WPS 双类名）。公开供 ShowScreen 兜底。</summary>
    public static IntPtr? FindSlideShowWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            if (GetClassNameW(hwnd, sb, 256) > 0 &&
                sb.ToString() is "screenClass" or "KPptScreenClass" or "WPPScreenClass" &&
                IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    public void Dispose()
    {
        if (_cachedApp is not null && Marshal.IsComObject(_cachedApp))
            Marshal.ReleaseComObject(_cachedApp);
        _cachedApp = null;
    }

    #region P/Invoke

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static object? ComGetActiveObject(string progId)
    {
        try
        {
            if (CLSIDFromProgIDEx(progId, out var clsid) != 0) return null;
            GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return obj;
        }
        catch { return null; }
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgIDEx([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder cls, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, nint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    #endregion
}
