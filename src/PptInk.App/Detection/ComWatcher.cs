using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Threading;
using PptInk.Core;

namespace PptInk.Detection;

/// <summary>
/// COM 主通道：通过 PowerPoint 对象模型实时监听
/// SlideShowBegin / SlideShowNextSlide / SlideShowEnd，零轮询开销。
/// WPS 演示走其兼容 COM 接口（ProgID KWPP.Application，对象模型与 MS PPT 基本兼容）。
/// .NET 8 已移除 Marshal.GetActiveObject，此处手工 P/Invoke 获取运行中实例（绝不 New 一个 PPT）。
///
/// 线程纪律（v1.1.2 修复）：本前使用 BlockingCollection 承载工作，却没有为 STA 线程
/// 建立消息泵；COM ConnectionPoint 的 sink 是跨套间调用，必须由订阅线程的消息循环
/// 派发。结果 Begin/Next/End 事件可能永远收不到，且 PPT 侧还会等待一个永远不泵消息的
/// STA。现在专用线程运行 <see cref="Dispatcher"/>：队列投递、COM 事件派发、Unadvise
/// 与 RCW 释放全部在同一 STA 上串行完成。
/// </summary>
public sealed class ComWatcher : IPresentationChannel
{
    // PowerPoint 与 WPS 演示的 ProgID；按顺序探测（Office 与 WPS 并存场景两者都试）
    private static readonly string[] ProgIds = ["PowerPoint.Application", "KWPP.Application"];

    private readonly Thread _comThread;
    private readonly ManualResetEventSlim _dispatcherReady = new(false);
    private Dispatcher? _dispatcher;
    private dynamic? _app;
    private string _boundProgId = "";
    private volatile bool _running;
    private volatile bool _disposed;
    // dynamic COM 事件的 +=/-=：方法组不能直接作 dynamic 运算操作数（CS0019），
    // 缓存委托实例保证订阅/退订用同一引用（DLR 据此映射 IConnectionPoint Advise/Unadvise cookie）
    private Action<object>? _beginHandler;
    private Action<object>? _nextHandler;
    private Action<object>? _endHandler;

    public string Name => $"COM:{_boundProgId}";
    public bool IsHealthy => _running && !_disposed && _app is not null;

    public event EventHandler<ShowStartedEvent>? Started;
    public event EventHandler<SlideChangedEvent>? SlideChanged;
    public event EventHandler? Ended;
    /// <summary>连接点因异常/探活失败被释放（通知仲裁器允许兜底通道接管翻页事件）。</summary>
    public event EventHandler? Unbound;

    public ComWatcher()
    {
        // 专用 STA 线程：COM 事件回调要求消息泵，与 UI 线程隔离避免互相阻塞
        _comThread = new Thread(ComThreadLoop) { IsBackground = true, Name = "PptInk.ComWatcher" };
        _comThread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _comThread.Start();

        // 等待 Dispatcher 建立后再投递绑定，避免 Start 早期的 Post 丢在 null dispatcher 上。
        if (!_dispatcherReady.Wait(TimeSpan.FromSeconds(3)))
        {
            AppLog.Instance.Warn("COM 通道启动失败：STA 消息泵在 3 秒内未就绪");
            return;
        }
        Post(TryBindCore);
    }

    /// <summary>公共入口（兜底通道回调）：投递一次绑定尝试到 COM 线程。</summary>
    public void TryBind() => Post(TryBindCore);

    /// <summary>探活：在 STA 消息泵上做一次轻量 COM 调用，发现 RCW 断开则解绑重绑。</summary>
    public void ProbeHealth() => Post(ProbeHealthCore);

    private void ComThreadLoop()
    {
        // 必须先在本线程取 Dispatcher 并 Run，才能让 COM 的跨套间回调被派发。
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcherReady.Set();
        try
        {
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error(ex, "COM 通道消息泵异常退出");
        }
    }

    /// <summary>投递到专用 STA。所有 COM 订阅/退订/RCW 释放必须走这里。</summary>
    private void Post(Action work)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _disposed || dispatcher.HasShutdownStarted) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_disposed) return;
                ExecuteSafely(work);
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 正在关闭：进程退出路径，忽略。
        }
    }

    private static void ExecuteSafely(Action work)
    {
        try { work(); }
        catch (COMException ex)
        {
            AppLog.Instance.Warn($"COM 通道异常（可能权限不一致/Office 未激活）：0x{ex.HResult:X8}");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error(ex, "COM 通道异常");
        }
    }

    private void TryBindCore()
    {
        if (_disposed || _app is not null || !_running) return;
        foreach (var progId in ProgIds)
        {
            dynamic? app = null;
            try
            {
                app = OleGetActiveObject(progId);
                if (app is null) continue;

                // 事件订阅：C# dynamic 对 COM 自动走 IConnectionPoint::Advise
                _beginHandler = OnSlideShowBegin;
                _nextHandler = OnSlideShowNextSlide;
                _endHandler = OnSlideShowEnd;
                app.SlideShowBegin += _beginHandler;
                app.SlideShowNextSlide += _nextHandler;
                app.SlideShowEnd += _endHandler;

                _app = app;
                _boundProgId = progId;
                AppLog.Instance.Info($"COM 主通道已绑定：{progId}");

                // 绑定成功时若放映已在进行（自启晚于放映），立即补报状态
                TryReportOngoingShow(app);
                return;
            }
            catch (COMException)
            {
                // 该 ProgID 无运行实例或事件签名不兼容：回滚后试下一个
                RollbackPartialBind(app);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // WPS 部分版本事件签名差异：回滚后交给 WinEvent 兜底。
                // 审查修复：原实现仅记日志后继续 —— 第二/三个 += 抛出时已 Advise 的
                // 连接点不释放、_app 保持 null 使 IsHealthy=false，5s 重绑定定时器每轮
                // 再 Advise 一个新委托实例，连接点/托管 sink 按每 5 秒 1 个无界累积。
                AppLog.Instance.Warn($"{progId} 事件接口不兼容（WPS 旧版本），主通道让位给兜底通道");
                RollbackPartialBind(app);
            }
        }
    }

    private void ProbeHealthCore()
    {
        if (_disposed || _app is null) return;
        try
        {
            // Count 是同步 COM 调用；这里在专用 STA 上执行，不占 UI 线程。
            _ = _app.SlideShowWindows.Count;
            _ = _app.Name;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"COM 通道探活失败，已解绑等待重连：{ex.GetType().Name}: {ex.Message}");
            Unbind();
        }
    }

    /// <summary>订阅中途失败（第二个/第三个 += 抛出）的回滚：按局部 app 解绑已成功
    /// Advise 的事件并释放 RCW。不能用 Unbind() —— 其以 _app 已赋值为前提，而中途失败
    /// 时 _app 尚未赋值。各步独立吞异常：单个解绑失败不得中断其余回滚与 ProgID 遍历。</summary>
    private void RollbackPartialBind(dynamic? app)
    {
        var begin = _beginHandler;
        var next = _nextHandler;
        var end = _endHandler;
        _beginHandler = _nextHandler = _endHandler = null;
        if (app is null) return;
        try { if (begin is not null) app.SlideShowBegin -= begin; } catch { }
        try { if (next is not null) app.SlideShowNextSlide -= next; } catch { }
        try { if (end is not null) app.SlideShowEnd -= end; } catch { }
        try { if (Marshal.IsComObject(app)) Marshal.ReleaseComObject(app); } catch { }
    }

    private void TryReportOngoingShow(dynamic app)
    {
        try
        {
            if (app.SlideShowWindows.Count > 0)
            {
                // 与 OnSlideShowBegin 同构：pos/total 分开 try，黑屏过渡（view.Slide 为 null）等
                // 单点异常不再吞掉整个补报（审查修复 #2：原先用 SlideIndex>0 判断取不取总页数，语义不通）
                dynamic view = app.SlideShowWindows[1].View;
                int pos = 1, total = 0;
                try { pos = (int)view.CurrentShowPosition; } catch { }
                try { total = (int)view.Presentation.Slides.Count; } catch { }
                string path = "";
                try { path = (string?)view.Presentation.FullName ?? ""; } catch { }
                Started?.Invoke(this, new ShowStartedEvent(pos, total, Name, path));
            }
        }
        catch { /* 补报失败不影响主流程 */ }
    }

    private void OnSlideShowBegin(dynamic window)
    {
        int pos = 1, total = 0;
        try { pos = (int)window.View.CurrentShowPosition; } catch { }
        try { total = (int)window.Presentation.Slides.Count; } catch { }
        string path = "";
        try { path = (string?)window.Presentation.FullName ?? ""; } catch { }
        Started?.Invoke(this, new ShowStartedEvent(pos, total, Name, path));
    }

    private void OnSlideShowNextSlide(dynamic window)
    {
        int pos = 1, total = 0;
        try { pos = (int)window.View.CurrentShowPosition; } catch { }
        try { total = (int)window.Presentation.Slides.Count; } catch { }
        SlideChanged?.Invoke(this, new SlideChangedEvent(pos, total, Name));
    }

    private void OnSlideShowEnd(dynamic presentation)
    {
        Ended?.Invoke(this, EventArgs.Empty);
        // WPS 在 End 后偶尔不再触发后续 Begin（同进程二次放映），主动解绑重订，
        // 规避"第二次放映不唤起"。不能在事件回调栈内同步 Unadvise：那会形成
        // RPC_E_CANTCALLOUT_ININPUTSYNCCALL，连接点残留。投递到消息泵空闲时执行。
        Post(() =>
        {
            Unbind();
            TryBindCore();
        });
    }

    private void Unbind()
    {
        var app = _app;
        if (app is null) return;
        try
        {
            if (_beginHandler is not null) app.SlideShowBegin -= _beginHandler;
            if (_nextHandler is not null) app.SlideShowNextSlide -= _nextHandler;
            if (_endHandler is not null) app.SlideShowEnd -= _endHandler;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"COM 事件退订异常（连接点可能残留）：{ex.GetType().Name}: {ex.Message}");
        }
        _beginHandler = _nextHandler = _endHandler = null;
        _app = null;
        _boundProgId = "unbound";
        try { if (Marshal.IsComObject(app)) Marshal.ReleaseComObject(app); } catch { }
        try { Unbound?.Invoke(this, EventArgs.Empty); } catch { }
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
                // 同步等 STA 完成 Unadvise/ReleaseComObject；即使 COM 卡住也只等 1s，
                // 之后仍强制关闭消息泵，不拖住进程退出。
                dispatcher.Invoke(new Action(Unbind), DispatcherPriority.Send,
                    CancellationToken.None, TimeSpan.FromSeconds(1));
            }
            catch { }
            try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
        }
        _dispatcherReady.Dispose();
        _comThread.Join(1500);   // 最多等 1.5s：进程退出时 COM 兜底释放连接点
    }

    #region GetActiveObject P/Invoke（.NET 8 无内置实现）

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static object? OleGetActiveObject(string progId)
    {
        try
        {
            var hr = CLSIDFromProgIDEx(progId, out var clsid);
            if (hr != 0) return null;
            GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return obj;
        }
        catch { return null; }
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgIDEx([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    #endregion
}