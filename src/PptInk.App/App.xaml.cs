using System.Windows;
using PptInk.Core;
using PptInk.Detection;
using PptInk.UI.Theme;

namespace PptInk;

/// <summary>
/// 应用生命周期：组装服务、注册自启、托盘驻留、放映检测接线。
/// S0（后台静默）→ S1（UI 唤起）→ S0 的循环由 <see cref="AppStateMachine"/> 驱动。
/// </summary>
public partial class App : Application
{
    private AppServices? _services;
    private readonly bool _silentStart;
    private readonly bool _portable;

    public App(bool silentStart = false, bool portable = false)
    {
        _silentStart = silentStart;
        _portable = portable;
        // 后台静默目标：关闭 WPF 默认的渲染线程忙等待功耗优化项干扰，
        // 并在异常时不弹窗（课堂场景）
        DispatcherUnhandledException += (_, e) =>
        {
            _services?.Log?.Error(e.Exception, "UI 线程未捕获异常");
            e.Handled = true;
        };
        // v1.1.0：补齐后台线程兜底 —— SetErrorMode 压掉了崩溃对话框，原先后台线程/
        // 未观察 Task 的异常会让进程无声消失（课堂上"程序突然没了"，零日志无法排障）。
        // 记录不能阻止进程退出（AppDomain 异常本就无法挽回），但能留下死因。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { AppLog.Instance.Error(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "非异常对象"), "非 UI 线程未处理异常（进程将终止）"); AppLog.Instance.Stop(); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { AppLog.Instance.Error(e.Exception, "未观察 Task 异常"); } catch { }
            e.SetObserved();   // 已记日志：不必在 GC 终结时再杀进程
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _services = AppServices.Compose(this);
            var svc = _services;

            // 0) 外观令牌先行：任何窗口创建之前写入 Application 资源
            //    （窗口 DynamicResource 首次解析即命中正确主题，零闪烁、零二次布局）
            ThemeManager.ApplyFromConfig();

            // 0.1) Auto 主题跟随系统明暗变化（SystemEvents 可能自非 UI 线程回调，
            //      ThemeManager.OnSystemThemeChanged 内部会切回 UI 线程再改资源）
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

            // 1) 注册当前用户自启（HKCU Run，无需管理员，静默 --minimized 参数）；
            //    --portable 便携试用（zip 解压即跑）：不往试用机写常驻项
            if (!_portable)
                svc.AutoStart.EnsureRegistered(silent: true);

            // 2) 低开销预热：提前创建墨迹窗口/渲染资源，保证唤起 <300ms 可书写、第一笔零延迟
            svc.PreWarm.WarmUp();

            // 3) 双通道放映检测接线 → 状态机。
            //    v0.8.1 时机修复（COM 死锁三明治）：事件在 COM STA 线程触发，若此处同步
            //    Dispatcher.Invoke —— UI 线程此刻可能正阻塞在 view.Next() 等同步 COM 调用上，
            //    而 PPT 处理该调用时会同步回发 SlideShowNextSlide 事件 → STA 线程等 UI、
            //    UI 等 PPT、PPT 等 STA —— 三方环死锁（表现为点翻页按钮偶发整卡）。
            //    异步投递即可破环：事件按序入 UI 队列，Next() 返回后照常处理，时序不变。
            svc.Detector.PresentationStarted   += (_, ev) => Dispatcher.BeginInvoke(() => svc.StateMachine.OnPresentationStart(ev));
            svc.Detector.SlideChanged          += (_, ev) => Dispatcher.BeginInvoke(() => svc.StateMachine.OnSlideChanged(ev));
            svc.Detector.PresentationEnded     += (_, _)   => Dispatcher.BeginInvoke(() => svc.StateMachine.OnPresentationEnd());
            svc.Detector.ForeignOverlayDetected+= (_, _)   => Dispatcher.BeginInvoke(() => svc.StateMachine.OnForeignOverlay()); // 希沃白板/EasiNote 互让 → S2

            // 4) 状态机 → UI 唤起/收回（同样异步：相位流转可能来自线程池恢复计时器）
            svc.StateMachine.PhaseChanged += (_, phase) => Dispatcher.BeginInvoke(() => svc.UiOrchestrator.ApplyPhase(phase));

            // 5) 托盘（可选隐藏）
            svc.Tray.Initialize(hideByDefault: _silentStart && AppConfig.Current.HideTrayIcon);

            // 6) 启动检测（COM 主通道 + WinEvent 兜底，零轮询）
            svc.Detector.Start();

            svc.Log.Info("PptInk 已驻留（S0 后台静默）");
        }
        catch (Exception ex)
        {
            // 启动阶段致命异常（COM/注册表/插件目录/Python 探测等）：记录后确定性退出，
            // 绝不半初始化留在消息泵里（原实现无保护 → 崩在事件回调、无日志、无痕迹）
            // 审查修复：Compose 失败时 _services 恰为 null，原 `_services?.Log?` 使日志语句
            // 整体空跳 —— 异常与已缓冲的启动日志全不落盘。AppLog 单例恒可用，直接记它。
            try { AppLog.Instance.Error(ex, "启动阶段致命异常"); } catch { }
            Shutdown(1);
        }
    }

    private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // 系统明暗切换（仅 Auto 主题模式响应；Dark/Light 用户显式指定优先）
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
            ThemeManager.OnSystemThemeChanged();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        try { _services?.Shutdown(); }
        finally
        {
            // 兜底刷盘：Compose 失败（_services 为 null，Shutdown 从未执行）或清理中途抛出时，
            // AppServices.Shutdown 末尾的 AppLog.Stop 不会跑 —— 任何退出路径都必须 Flush，
            // 否则启动阶段的日志（含致命异常记录）随进程一起消失。Stop 幂等，重复调用无害。
            AppLog.Instance.Stop();
        }
        base.OnExit(e);
    }
}
