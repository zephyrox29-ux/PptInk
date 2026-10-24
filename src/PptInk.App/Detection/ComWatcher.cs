using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using PptInk.Core;

namespace PptInk.Detection;

/// <summary>
/// COM 主通道：通过 PowerPoint 对象模型实时监听
/// SlideShowBegin / SlideShowNextSlide / SlideShowEnd，零轮询开销。
/// WPS 演示走其兼容 COM 接口（ProgID KWPP.Application，对象模型与 MS PPT 基本兼容）。
/// .NET 8 已移除 Marshal.GetActiveObject，此处手工 P/Invoke 获取运行中实例（绝不 New 一个 PPT）。
/// </summary>
public sealed class ComWatcher : IPresentationChannel
{
    // PowerPoint 与 WPS 演示的 ProgID；按顺序探测（Office 与 WPS 并存场景两者都试）
    private static readonly string[] ProgIds = ["PowerPoint.Application", "KWPP.Application"];

    private readonly Thread _comThread;
    private readonly BlockingCollection<Action> _comQueue = new(boundedCapacity: 64);
    private dynamic? _app;
    private string _boundProgId = "";
    private volatile bool _running;
    // dynamic COM 事件的 +=/-=：方法组不能直接作 dynamic 运算操作数（CS0019），
    // 缓存委托实例保证订阅/退订用同一引用（DLR 据此映射 IConnectionPoint Advise/Unadvise cookie）
    private Action<object>? _beginHandler;
    private Action<object>? _nextHandler;
    private Action<object>? _endHandler;

    public string Name => $"COM:{_boundProgId}";
    public bool IsHealthy => _app is not null;

    public event EventHandler<ShowStartedEvent>? Started;
    public event EventHandler<SlideChangedEvent>? SlideChanged;
    public event EventHandler? Ended;

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

        // PPT 可能先于本程序启动（自启顺序），启动即尝试绑定一次；
        // 之后由兜底通道在发现新 PPT 进程前台化时回调 TryBind（见 PresentationDetector）
        Post(TryBindCore);
    }

    /// <summary>公共入口（兜底通道回调）：投递一次绑定尝试到 COM 线程。</summary>
    public void TryBind() => Post(TryBindCore);

    private void ComThreadLoop()
    {
        while (_comQueue.TryTake(out var work, Timeout.Infinite))
        {
            try { work(); }
            catch (COMException ex) { AppLog.Instance.Warn($"COM 通道异常（可能权限不一致/Office 未激活）：0x{ex.HResult:X8}"); Unbind(); }
            catch (Exception ex) { AppLog.Instance.Error(ex, "COM 通道异常"); }
        }
    }

    private void Post(Action work)
    {
        try { _comQueue.TryAdd(work); } catch { /* 队列满：丢弃旧绑定尝试 */ }
    }

    private void TryBindCore()
    {
        if (_app is not null) return;
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
                Started?.Invoke(this, new ShowStartedEvent(pos, total, Name));
            }
        }
        catch { /* 补报失败不影响主流程 */ }
    }

    private void OnSlideShowBegin(dynamic window)
    {
        int pos = 1, total = 0;
        try { pos = (int)window.View.CurrentShowPosition; } catch { }
        try { total = (int)window.Presentation.Slides.Count; } catch { }
        Started?.Invoke(this, new ShowStartedEvent(pos, total, Name));
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
        // WPS 在 End 后偶尔不再触发后续 Begin（同进程二次放映），
        // 主动解绑重订，规避"第二次放映不唤起"
        Unbind();
        Post(TryBind);
    }

    private void Unbind()
    {
        if (_app is null) return;
        try
        {
            if (_beginHandler is not null) _app.SlideShowBegin -= _beginHandler;
            if (_nextHandler is not null) _app.SlideShowNextSlide -= _nextHandler;
            if (_endHandler is not null) _app.SlideShowEnd -= _endHandler;
        }
        catch { }
        _beginHandler = _nextHandler = _endHandler = null;
        _app = null;
        _boundProgId = "unbound";
    }

    public void Dispose()
    {
        _running = false;
        // v0.8.1 关闭时序修复：Unbind 的 dynamic -= 是 COM Unadvise，经 RCW 封送到
        // 订阅发生的 STA 线程执行。原实现先 CompleteAdding（STA 线程随即退出循环）再在
        // UI 线程调 Unbind —— 封送目标已死，抛 RemotingException 被 catch 吞掉，
        // Unadvise 从未真正执行、连接点泄漏；ReleaseComObject 在 UI 线程与之竞争还有
        // ObjectDisconnected 风险。正确时序：解绑与 RCW 释放都投递进 COM 线程队列
        //（先 Post 再 CompleteAdding，队列内串行执行），UI 线程短等干净收尾。
        var app = _app;
        Post(Unbind);
        if (app is not null && Marshal.IsComObject(app))
            Post(() => Marshal.ReleaseComObject(app));
        _comQueue.CompleteAdding();
        _comThread.Join(800);   // 最多等 800ms：进程退出时 COM 兜底释放连接点，此等待只为干净关闭
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
