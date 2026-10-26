using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PptInk.Core;

namespace PptInk.Ppt;

/// <summary>
/// PPT 放映控制：翻页栏的上一页/下一页/跳页/退出放映。
/// 优先 COM（精确、无焦点副作用）；COM 不可用（兜底模式下）直接向放映窗口
/// PostMessage 键消息，不抢焦点（我们的墨迹层 WS_EX_NOACTIVATE，绝不能打断书写）。
///
/// v1.1.2 线程纪律：COM RCW 必须在其创建的 STA 上使用；此前 UI 线程同步调
/// view.Next()/ActivePresentation.FullName，PPT 假死时整个舞台冻结，看门狗也无法运行。
/// 现在本类自带专用 STA 消息泵，公开同步方法都带超时，UI 侧翻页/退出改用异步入口。
/// </summary>
public sealed class PptController : IDisposable
{
    private readonly Thread _comThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;
    private dynamic? _cachedApp;
    private volatile bool _disposed;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const short VK_RIGHT = 0x27, VK_LEFT = 0x25, VK_ESCAPE = 0x1B, VK_NEXT = 0x22, VK_PRIOR = 0x21;
    private const int DefaultComTimeoutMs = 1200;

    public PptController()
    {
        _comThread = new Thread(ComThreadLoop) { IsBackground = true, Name = "PptInk.PptController" };
        _comThread.SetApartmentState(ApartmentState.STA);
        _comThread.Start();
    }

    private void ComThreadLoop()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();
        try { Dispatcher.Run(); }
        catch (Exception ex) { AppLog.Instance.Error(ex, "PowerPoint 控制消息泵异常退出"); }
    }

    /// <summary>在专用 STA 上同步执行 COM 工作；超时/异常返回 fallback，绝不阻塞 UI 超过 timeout。</summary>
    private T InvokeCom<T>(Func<T> work, T fallback, int timeoutMs = DefaultComTimeoutMs)
    {
        if (_disposed || !_ready.Wait(2000) || _dispatcher is null) return fallback;
        try
        {
            var op = _dispatcher.InvokeAsync(work, DispatcherPriority.Normal);
            if (!op.Task.Wait(timeoutMs))
            {
                AppLog.Instance.Warn($"COM 调用超时（>{timeoutMs}ms），本次按不可用处理");
                return fallback;
            }
            return op.Task.Result;
        }
        catch (Exception ex)
        {
            _cachedApp = null;
            AppLog.Instance.Warn($"COM 调用失败：{ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>异步 COM 入口（UI 线程使用）：调用超时只影响本次结果，不冻结界面。</summary>
    private async Task<T> InvokeComAsync<T>(Func<T> work, T fallback, int timeoutMs = 5000)
    {
        if (_disposed || !_ready.Wait(2000) || _dispatcher is null) return fallback;
        try
        {
            var op = _dispatcher.InvokeAsync(work, DispatcherPriority.Normal);
            if (await Task.WhenAny(op.Task, Task.Delay(timeoutMs)).ConfigureAwait(false) != op.Task)
            {
                AppLog.Instance.Warn($"异步 COM 调用超时（>{timeoutMs}ms），本次按不可用处理");
                return fallback;
            }
            return await op.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _cachedApp = null;
            AppLog.Instance.Warn($"异步 COM 调用失败：{ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>下一页。返回是否经 COM 完成（false = 走了键消息兜底，需上层推算页码）。</summary>
    public bool NextSlide() => InvokeCom(NextSlideCore, false, 2500);

    public Task<bool> NextSlideAsync() => InvokeComAsync(NextSlideCore, false);

    private bool NextSlideCore()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.Next();
                return true;
            }
        }
        catch { _cachedApp = null; }
        PostShowKey(VK_NEXT);
        return false;
    }

    /// <summary>上一页。</summary>
    public bool PrevSlide() => InvokeCom(PrevSlideCore, false, 2500);

    public Task<bool> PrevSlideAsync() => InvokeComAsync(PrevSlideCore, false);

    private bool PrevSlideCore()
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

    /// <summary>跳转到指定页（v1.1.2 页码快捷选择）：COM GotoSlide 直跳。
    /// 兜底按键通道无法表达任意页码，COM 不可用时返回 false（调用方提示）。</summary>
    public bool GotoSlide(int slideNumber)
    {
        if (slideNumber <= 0) return false;
        return InvokeCom(() => GotoSlideCore(slideNumber), false);
    }

    public Task<bool> GotoSlideAsync(int slideNumber)
    {
        if (slideNumber <= 0) return Task.FromResult(false);
        return InvokeComAsync(() => GotoSlideCore(slideNumber), false);
    }

    private bool GotoSlideCore(int slideNumber)
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.GotoSlide(slideNumber);
                return true;
            }
        }
        catch { _cachedApp = null; }
        return false;
    }

    /// <summary>退出放映（工具栏"停止播放"）。v1.1.2：COM 在 STA 上执行，UI 不冻结。</summary>
    public void ExitShow() => _ = ExitShowAsync();

    public async Task ExitShowAsync()
    {
        bool viaCom = await InvokeComAsync(ExitShowCore, false, 2500).ConfigureAwait(false);
        if (!viaCom)
        {
            AppLog.Instance.Warn("退出放映：COM 不可用，PostMessage ESC 兜底");
            PostShowKey(VK_ESCAPE);
            return;
        }
        // Exit() 返回成功不等于放映真的结束：300ms 后复核，仍存活则补 ESC。
        await Task.Delay(300).ConfigureAwait(false);
        if (HasActiveShow() && FindSlideShowWindow() is { } h)
        {
            AppLog.Instance.Warn("退出放映复核：放映仍存活，PostMessage ESC 追击");
            PostEscToWindow(h);
        }
    }

    private bool ExitShowCore()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                view.Exit();
                AppLog.Instance.Info("退出放映：COM view.Exit()");
                return true;
            }
        }
        catch (Exception ex) { _cachedApp = null; AppLog.Instance.Warn($"退出放映 COM 失败：{ex.Message}"); }
        return false;
    }

    /// <summary>COM 通道读取精确页码：SlideShowWindow.View.CurrentShowPosition。</summary>
    public bool TryGetSlidePosition(out int position)
    {
        int p = InvokeCom(GetSlidePositionCore, -1);
        position = p;
        return p > 0;
    }

    private int GetSlidePositionCore()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                int p = (int)view.CurrentShowPosition;
                return p > 0 ? p : -1;
            }
        }
        catch { _cachedApp = null; }
        return -1;
    }

    /// <summary>COM 通道读取总页数：ActivePresentation.Slides.Count（页码徽标 "3 / 18" 右侧）。</summary>
    public bool TryGetTotalSlides(out int total)
    {
        int t = InvokeCom(GetTotalSlidesCore, -1);
        total = t;
        return t > 0;
    }

    private int GetTotalSlidesCore()
    {
        try
        {
            var app = GetApp();
            if (app is null) return -1;
            int t = (int)app.ActivePresentation.Slides.Count;
            return t > 0 ? t : -1;
        }
        catch { _cachedApp = null; return -1; }
    }

    /// <summary>放映是否仍在进行（COM 口径）。供看门狗存活判定（与窗口枚举双口径互为兜底）。</summary>
    public bool HasActiveShow() => InvokeCom(HasActiveShowCore, false);

    private bool HasActiveShowCore()
    {
        try
        {
            var app = GetApp();
            return app is not null && app.SlideShowWindows.Count > 0;
        }
        catch { _cachedApp = null; return false; }
    }

    /// <summary>取当前演示文稿身份（ActivePresentation.FullName）：换课件检测用。</summary>
    public bool TryGetPresentationIdentity(out string identity)
    {
        string id = InvokeCom(GetPresentationIdentityCore, "");
        identity = id;
        return !string.IsNullOrWhiteSpace(id);
    }

    private string GetPresentationIdentityCore()
    {
        try
        {
            var app = GetApp();
            if (app is null) return "";
            return (string?)app.ActivePresentation.FullName ?? "";
        }
        catch { _cachedApp = null; return ""; }
    }

    /// <summary>向指定放映窗口投递 ESC（COM view.Exit() 在个别版本/放映状态下不生效时的强力兜底）。</summary>
    public static void PostEscToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_KEYDOWN, VK_ESCAPE, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, VK_ESCAPE, IntPtr.Zero);
    }

    /// <summary>窗口是否响应消息（挂起探测）：SendMessageTimeout(WM_NULL, 800ms)。</summary>
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
        catch { _cachedApp = null; return false; }
    }

    /// <summary>仅允许在专用 STA 线程调用。</summary>
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

    /// <summary>向放映窗口投递键消息（兜底翻页）。放映窗口丢失时不动作，避免把键发给前台错误窗口。</summary>
    private static void PostShowKey(short vk)
    {
        IntPtr hwnd = FindSlideShowWindow() ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_KEYDOWN, (nint)vk, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, (nint)vk, IntPtr.Zero);
    }

    /// <summary>取放映窗口 HWND：优先 COM（SlideShowWindow.HWND，精确），失败回退窗口类名枚举。</summary>
    public IntPtr TryGetShowHwnd()
    {
        IntPtr h = InvokeCom(TryGetShowHwndCore, IntPtr.Zero);
        return h != IntPtr.Zero ? h : FindSlideShowWindow() ?? IntPtr.Zero;
    }

    private IntPtr TryGetShowHwndCore()
    {
        try
        {
            if (TryGetSlideShowView(out var view))
            {
                int hwnd = (int)view.Parent.HWND;
                if (hwnd != 0) return new IntPtr(hwnd);
            }
        }
        catch { _cachedApp = null; }
        return IntPtr.Zero;
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
        if (_disposed) return;
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is not null)
        {
            try
            {
                dispatcher.Invoke(new Action(() =>
                {
                    if (_cachedApp is not null && Marshal.IsComObject(_cachedApp))
                        Marshal.ReleaseComObject(_cachedApp);
                    _cachedApp = null;
                }), DispatcherPriority.Send, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(1));
            }
            catch { }
            try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
        }
        _ready.Dispose();
        _comThread.Join(1500);
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
    private static extern bool PostMessage(IntPtr hwnd, uint msg, nint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    #endregion
}