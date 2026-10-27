using System.Windows;
using PptInk.Bridge;
using PptInk.Detection;
using PptInk.Ink;
using PptInk.Plugins;
using PptInk.Ppt;
using PptInk.Seewo;
using PptInk.UI;

namespace PptInk.Core;

/// <summary>
/// 组合根：手工依赖注入（不引第三方容器，控制体积与启动耗时）。
/// 常驻内存目标 &lt;30MB：所有重量级资源（渲染位图、Python 子进程、插件）均为懒加载。
/// </summary>
public sealed class AppServices
{
    public AppConfig Config { get; }
    /// <summary>便携模式：不写开机自启（程序目录删除即停止驻留，不做系统级安装）。</summary>
    public bool IsPortable { get; }
    public AppLog Log => AppLog.Instance;
    public AutoStartRegistrar AutoStart { get; } = new();
    public PreWarmer PreWarm { get; }
    public PresentationDetector Detector { get; }
    public AppStateMachine StateMachine { get; }
    public InkDocument InkDocument { get; }
    public EraseService Eraser { get; }
    public InkEngine InkEngine { get; }
    public PptController PptController { get; }
    public DeviceProfileStore DeviceProfiles { get; }
    public PalmRejection PalmRejection { get; }
    public PluginManager Plugins { get; }
    public PythonBridge PythonBridge { get; }
    public UiOrchestrator UiOrchestrator { get; }
    public TrayIconController Tray { get; }
    /// <summary>触摸数字化仪运行时探测（v0.4.0）：权威物理数据（PxPerMm 校准 / 大面板特征）。</summary>
    public TouchDigitizer TouchDigitizer { get; }
    private readonly CancellationTokenSource _controlPipeCts = new();

    private AppServices(App app, AppConfig config, bool portable)
    {
        Config = config;
        IsPortable = portable;

        DeviceProfiles = new DeviceProfileStore();
        DeviceProfiles.LoadDefaults();

        // v0.4.1 配置迁移：旧版本默认 "seewo-86-generic" 不在档案库中（恒回退 generic，
        // 4K 大屏笔迹粗细失真）。无效 ID 一律重置为自动选择，让 ResolveBest 按实际
        // 屏幕匹配；用户显式配置的有效 ID（含 JSON 扩展档案）不受影响。
        if (config.DeviceProfileId is { } cfgId && DeviceProfiles.Get(cfgId).Id != cfgId)
        {
            config.DeviceProfileId = null;
            config.Save();
            AppLog.Instance.Info($"配置迁移：无效机型档案 ID「{cfgId}」已重置为自动选择");
        }

        // v0.4.0「希沃优化不定死」：先探测触摸数字化仪物理事实（GetPointerDevices：
        // 物理尺寸 0.01mm / 大面板特征），再解析档案 —— 大面板存在时高缩放门禁软化
        // （PxPerMm 将来自物理实测，与系统缩放无关）；解析后注入权威校准。
        // 探测全防御：RDP/无触摸/数据异常 → 静默回退纯档案逻辑（从下往上兼容）。
        TouchDigitizer = TouchDigitizer.Probe(AppLog.Instance);
        var profile = DeviceProfiles.ResolveBest(config.DeviceProfileId, TouchDigitizer);
        bool calibrated = TouchDigitizer.ApplyTo(profile, "startup");
        AppLog.Instance.Info($"启动坐标映射：档案={profile.Id}，逻辑画布比例={profile.CanvasScale:F3}，" +
            $"物理px/mm={profile.PhysicalPxPerMm:F3}，逻辑px/mm={profile.PxPerMm:F3}，" +
            $"红外实测校准={(calibrated ? "有效" : "未确认（使用机型档案）")}");
        ReportDeviceDiagnostics(profile);

        InkDocument = new InkDocument();
        Eraser = new EraseService(InkDocument, profile);
        PalmRejection = new PalmRejection(DeviceProfiles, profile.Id);
        // 笔记本试用模式接线（v1.1.0 修复）：设置页开启时只改了运行态 + DeviceProfileId，
        // PalmRejectionEnabled 没动 —— 重启后 Enabled 又从配置默认 true 恢复，电容屏上
        // 误杀手掌判定回来了，UI 承诺的"关掉手掌抑制"只在当次运行成立。启动时读
        // LaptopTestMode 补上这一半（机型本身经 DeviceProfileId 持久化，无需重复处理）。
        if (config.LaptopTestMode)
        {
            PalmRejection.Enabled = false;
            AppLog.Instance.Info("笔记本试用模式（配置）：手掌抑制保持关闭");
        }
        InkEngine = new InkEngine(InkDocument, DeviceProfiles, profile.Id, Eraser);
        InkDocument.RenderProfile = InkEngine.Profile;   // 精修重绘/导出与活体渲染同 EMA 口径（审查修复 #1）

        PptController = new PptController();

        StateMachine = new AppStateMachine(config.HysteresisMs);
        Detector = new PresentationDetector();

        PythonBridge = new PythonBridge(); // 懒拉起：S0 不启动 Python 子进程
        PythonBridge.StatusSink = Toast.Show;   // 静默自愈成功等状态用轻提示告知，不弹错误框
        Plugins = new PluginManager(InkEngine, PythonBridge);

        UiOrchestrator = new UiOrchestrator(app, this);
        PreWarm = new PreWarmer(UiOrchestrator);
        Tray = new TrayIconController(app, StateMachine, UiOrchestrator);
        StartControlPipe();
    }

    /// <summary>放映屏切换后的重校准（v0.4.0）：数字化仪物理尺寸不变，但放映屏物理像素
    /// 变了（笔记本主屏 → 大屏扩展屏）→ PxPerMm 实测值随之重算。幂等：仅大屏红外档案
    /// 且实测有效时注入。由 UiOrchestrator 在 ScreenChanged 重对位时调用。</summary>
    public void RecalibrateProfileToScreen()
    {
        try
        {
            // Refresh device-to-monitor mapping as well as profile choice: classroom setups
            // commonly start on a laptop, attach a Seewo board later, or reconnect through RDP.
            TouchDigitizer.Refresh(Log);
            var profile = DeviceProfiles.ResolveBest(Config.DeviceProfileId, TouchDigitizer);
            string previousProfileId = InkEngine.Profile.Id;
            bool profileChanged = profile.Id != previousProfileId;
            if (profileChanged)
            {
                InkEngine.ApplyProfile(profile.Id);
                PalmRejection.ApplyProfile(profile);
                InkDocument.RenderProfile = profile;
                Log.Info($"放映屏机型档案切换：{previousProfileId} → {profile.Id}");
                InputDiagnostics.Note($"机型档案已按放映屏重新解析：{profile.ModelName} ({profile.Id})");
            }

            bool calibrated = TouchDigitizer.ApplyTo(profile, "screen-change");
            InkDocument.RenderProfile = profile;
            Log.Info($"放映屏坐标重映射：档案={profile.Id}，逻辑px/mm={profile.PxPerMm:F3}，" +
                $"画布比例={profile.CanvasScale:F3}，实测校准={(calibrated ? "有效" : "使用档案")}");
            InputDiagnostics.Note($"放映屏坐标映射：{profile.ModelName}，逻辑px/mm={profile.PxPerMm:F3}，" +
                $"校准={(calibrated ? profile.CalibrationSource : "档案回退")}");
            ReportDeviceDiagnostics(profile);
        }
        catch (Exception ex) { Log.Warn($"放映屏坐标重映射失败：{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>数字化仪结论同步到诊断总线（HUD「触摸数字化仪」区数据源）。</summary>
    private void ReportDeviceDiagnostics(Seewo.DeviceProfile profile)
    {
        try
        {
            InputDiagnostics.SetDevice(
                TouchDigitizer.Summary,
                TouchDigitizer.IsLargeTouchPanel,
                profile.CalibratedPxPerMm ?? 0,
                profile.ScreenWidthPx > 0 && profile.PhysicalWidthMm > 0 ? profile.ScreenWidthPx / profile.PhysicalWidthMm : 0,
                profile.Id,
                profile.CalibrationSource ?? "");
        }
        catch { }
    }

    /// <summary>控制管道服务端：第二实例（双击图标/快捷方式）经命名管道请求唤起 UI。
    /// 审查修复：Program 此前只写了客户端、无服务端 —— 双击图标唤醒主实例的功能从未生效。</summary>
    private void StartControlPipe()
    {
        var token = _controlPipeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        "PptInk.ControlPipe", System.IO.Pipes.PipeDirection.In, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new System.IO.StreamReader(server);
                    // v1.1.1：读行加 5s 超时 —— 连接后不发数据的客户端（探测/半开连接）原先
                    // 让本循环永久停在 ReadLineAsync，唤起管道自此停摆。
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    string? line;
                    try { line = await reader.ReadLineAsync(readCts.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                    if (line is not null && !token.IsCancellationRequested)
                    {
                        // v1.1.2：托盘被隐藏后，再次双击程序图标是唯一的恢复入口；
                        // --settings 则直接打开设置，彻底不依赖托盘。
                        if (line.Contains("open-settings", StringComparison.OrdinalIgnoreCase))
                            UiOrchestrator.OpenSettings();
                        else if (line.Contains("show-tray-menu", StringComparison.OrdinalIgnoreCase))
                            UiOrchestrator.ShowTrayAndActivate();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) Log.Warn($"控制管道重试：{ex.GetType().Name}: {ex.Message}");
                    try { await Task.Delay(500, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                }
            }
        }, token);
    }

    public static AppServices Compose(App app, bool portable = false)
    {
        AppLog.Instance.Start();
        return new AppServices(app, AppConfig.Current, portable);
    }

    /// <summary>有序释放：先停检测避免半关闭状态下收到事件，再收 UI，然后先落会话墨迹
    /// 快照、最后收子进程 —— 快照必须排在 AI 桥释放之前：任何后续清理被拖住/强杀时，
    /// 板书数据都已在盘上（原顺序 AI 桥在前，Dispose 一旦被冷启动握手拖住即丢快照）。</summary>
    public void Shutdown()
    {
        // A failure in one disposer must never prevent later services (especially the AI process,
        // tray icon, or session snapshot) from being released.
        _controlPipeCts.Cancel();
        RunShutdownStep("放映检测", Detector.Dispose);
        RunShutdownStep("PowerPoint 控制器", PptController.Dispose);
        RunShutdownStep("统一舞台", UiOrchestrator.CloseAll);
        RunShutdownStep("插件", Plugins.UnloadAll);
        RunShutdownStep("会话墨迹快照", InkEngine.TrySaveSessionSafe);
        RunShutdownStep("AI 桥", PythonBridge.Dispose);
        RunShutdownStep("系统托盘", Tray.Dispose);
        _controlPipeCts.Dispose();
        Log.Info("PptInk 服务清理完成");
        AppLog.Instance.Stop();
    }

    private void RunShutdownStep(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error(ex, $"关闭清理阶段异常：{name}"); }
    }
}
