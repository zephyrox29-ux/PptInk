using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PptInk.Bridge;
using PptInk.Builtin;
using PptInk.Core;
using PptInk.Ink;
using PptInk.Seewo;
using PptInk.UI.Theme;


namespace PptInk.UI;

/// <summary>统一调度器 · 主控：服务字段与装配 / 舞台与相位（S0·S1·S2 应用）/ 万全看门狗 / 工具链接线 / 板书导出 / 设置中心与全量收场（CloseAll）。（v0.9.2 由 969 行单文件按职责拆分，纯搬运零逻辑改动）</summary>
public sealed partial class UiOrchestrator : IOnboardingTargets   // 接口列于主控部分（显式实现分散在其他 partial）
{
    private readonly Application _app;
    private readonly AppServices _svc;
    private UnifiedStageWindow? _overlay;
    /// <summary>墨迹层期望激活态：单窗口架构（v0.9.0）下窗口恒常、切档不再重建，
    /// 此标志保留给 RefreshInputMode/相位切换判断「切档后是否维持书写就绪」。</summary>
    private bool _overlayWanted;
    /// <summary>板书导出互斥（0 = 空闲）：池线程导出期间重复点击只提示，不排队复制渲染。</summary>
    private int _exporting;
    /// <summary>墨迹层渲染事件已接线（只挂一次：handler 读 _overlay 字段而非
    /// 捕获窗口实例 —— 事件永远流向当前窗口实例）。</summary>
    private bool _overlayWired;
    private Toolbar? _toolbar;
    private EraseModeIsland? _eraseIsland;
    private OnboardingGuideElement? _onboarding;   // v0.9.0 舞台 Guide 层元素
    private SettingsWindow? _settings;
    private DebugHudElement? _debugHud;                    // v0.4.0 调试模式诊断面板；v0.9.0 舞台 Hud 层元素
    private PageTurnIsland? _pageIslandR;            // v0.7.0 角落翻页岛（右下）；v0.9.0 舞台元素
    private PageTurnIsland? _pageIslandL;            // v0.7.0 角落翻页岛（左下）；v0.9.0 舞台元素
    private PageJumpPanel? _pageJump;                // v1.1.2 页码快捷跳页浮层
    private bool _onboardingDoneThisRun;   // 一次运行内自动触发一次，避免反复打扰课堂
    private bool _s0GuideSession;          // S0 托盘手动触发的引导会话：结束后收回桌面 UI
    private bool _chromeLayoutQueued;

    // —— AI 停顿自动规整调度（auto 即刻替换 / bubble 气泡确认） ——
    private ShapeAssistBubble? _shapeBubble;
    private DispatcherTimer? _assistTimer;
    private StrokeData? _assistTarget;
    private bool _assistSessionOff;        // 气泡「本次放映不再提示」（S1 重新唤起重置）

    // —— 页码轮询兜底：COM 事件（SlideShowNextSlide）在部分环境绑不上，
    //    页码徽标将永远停在初值。500ms 轮询读权威页码（COM 读取走后台线程，单次 <1ms）——
    //    事件通道可用时轮询只是静默校准；不可用时它是唯一更新源。
    //    v0.1.5：2s → 500ms（用户实测「页数显示不实时」，翻页后徽标近实时跟随）。 ——
    private readonly DispatcherTimer _pagePoll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _lastBadgePage = -1, _lastBadgeTotal = -1;
    private bool _polling;

    // —— 万全看门狗（v0.1.5）：前提假定一切外部状态不可信 ——
    //    PPT 会抢回置顶、WPF 会重写窗口样式、放映结束事件可能永远不来。
    //    每 600ms 主动校验并自愈（墨迹层可见性/穿透样式/Z 序 + 退映滞留回收）。
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int _wdStuckCount;      // 退映「双口径判死」连续确认拍数（3 拍才动手，防 COM 瞬断误杀）
    private bool _wdExitChecking;   // 退映复核防重入（fire-and-forget 异步任务并发保护）
    // COM 探测在飞任务（页码轮询同款，见 ProbeComAliveAsync）：仅在 UI 线程读写
    //（看门狗/页码轮询/翻页即时读全部经 Dispatcher 或 DispatcherTimer）
    private Task<bool>? _comProbeInFlight;
    private Task<(int Pos, int Total)>? _pageProbeInFlight;

    public Toolbar? Toolbar => _toolbar;
    public UnifiedStageWindow? Overlay => _overlay;
    public OnboardingGuideElement? Onboarding => _onboarding;

    public UiOrchestrator(Application app, AppServices svc)
    {
        _app = app;
        _svc = svc;

        // 页码轮询兜底（S1 启动/S0 停止，见 ApplyPhase）：COM 读数为权威值，
        // 与事件通道（可用时）静默校准，不可用时是唯一更新源
        _pagePoll.Tick += (_, _) => _ = PollPageBadgeAsync();

        // 万全看门狗（S1/手动唤起启动，S0 停止）：不信任任何一次性设置，只信任持续校验
        _watchdog.Tick += (_, _) => WatchdogTick();

        // 最近色落盘防抖到期冲刷（见 ScheduleRecentColorsSave）
        _recentColorSaveTimer.Tick += (_, _) => FlushRecentColorsSave();

        svc.StateMachine.SlideIndexChanged += (_, idx) =>
            _app.Dispatcher.Invoke(() =>
            {
                svc.InkEngine.SwitchPage(idx);
                HideAllPopovers();   // 翻页让路：弹出型浮岛绝不跨页赖屏
                UpdatePageIslands(idx, svc.StateMachine.Session?.TotalSlides ?? 0);
            });

        // 放映中途换了演示文稿文件（上一场 End 漏报也能识别）：清空旧板书，不跨文件串页。
        svc.StateMachine.DocumentChanged += (_, path) =>
            _app.Dispatcher.BeginInvoke(() => HandleDeckSwitch(path));

        // 引擎侧工具切换（清空后智能切回 / 插件请求）→ 工具栏高亮同步 + 收回浮岛
        svc.InkEngine.ToolChanged += tool => _app.Dispatcher.BeginInvoke(() =>
        {
            _toolbar?.SelectTool(tool);   // 幂等：同值赋值被引擎 setter 短路，不会成环
            HideAllPopovers();
            // 穿透态实时同步（v0.1.5）：切到「触摸操作」舞台整层放行给 PPT，
            // 切回书写/擦除恢复收输入。单窗口下穿透=底刷 alpha/画布隐藏/穿透位
            //（按命中环境与档位，见 SetPassthrough）。
            _overlay?.SetPassthrough(tool == InkTool.TouchThrough);
            // 切到触摸态且板书非空 + 隐藏生效 → 一次轻提示（老师须知板书暂时隐去、可恢复）
            if (tool == InkTool.TouchThrough && _svc.Config.TouchThroughInkHidden)
            {
                int strokes = 0;
                lock (_svc.InkEngine.Gate) strokes = _svc.InkEngine.CurrentPage.Strokes.Count;
                if (strokes > 0) Toast.Show("板书已暂时隐藏，切回【笔】恢复。");
            }
        });

        // 落笔即收回浮岛（书写优先，面板绝不遮挡笔迹）；同时打断上一笔的停顿识别计时
        svc.InkEngine.PenDown += () => _app.Dispatcher.BeginInvoke(() =>
        {
            _assistTimer?.Stop();
            HideAllPopovers();
        });

        // 笔迹提交 → 沉淀"最近使用颜色" + 排程停顿自动识别（AI 开启时）
        svc.InkEngine.StrokeCommitted += stroke => _app.Dispatcher.BeginInvoke(() =>
        {
            PushRecentColor(stroke.Pen.ColorHex);
            ScheduleAutoAssist(stroke);
        });

        // 主题热切换 → 代码层冻结 Brush 的高亮重建
        //（XAML 窗口 DynamicResource 自动跟随；浮岛基类 IslandWindow 自行监听，此处只补 Toolbar）
        ThemeManager.ThemeChanged += () =>
            _app.Dispatcher.BeginInvoke(() =>
            {
                _toolbar?.OnThemeApplied();
            });

        // 放映屏感知（v0.1.3 试用反馈核心修复）：目标屏变化 → 全部全屏 UI 重对位。
        // 事件由后台线程（轮询/放映事件）触发，封送 UI。
        // v0.4.0：放映屏切换同时触发 PxPerMm 重校准（数字化仪物理尺寸 × 新屏物理像素）。
        ShowScreen.ScreenChanged += () => _app.Dispatcher.BeginInvoke(() =>
        {
            _svc.RecalibrateProfileToScreen();
            RelocateAllToScreen();
        });
    }

    /// <summary>放映屏变化后的全量重对位（UI 线程）：墨迹层 / 工具栏 / 浮岛 / 引导。
    /// 仅 S1（放映可见态）执行 —— S0/S2 时窗口处于隐藏/避让态，重定位会误将其唤起。</summary>
    private void RelocateAllToScreen()
    {
        if (_svc.StateMachine.Phase != Core.AppPhase.S1Active) return;
        // v1.1.1：换屏前先收回弹出型浮层 —— 擦除浮岛/插件面板的锚点是旧屏坐标，换屏后
        // 会被钳到新屏边缘而非跟随触发按钮；弹出态本来就该一次交互一续期，直接收掉最稳。
        HideAllPopovers();
        _overlay?.RetractFloatingPanelsOnScreenChange();
        _overlay?.RelocateToScreen();
        _debugHud?.PositionTopLeft();   // v0.4.0：诊断面板跟随放映屏重对位（public 方法复用）
        _onboarding?.RelocateToScreen();   // v0.9.0：引导覆盖跟随放映屏重对位（幂等；全屏尺寸重设）
        // 工具栏：用户记忆位置仍在旧屏（可能已不可见）→ 钳制回放映屏；无记忆则用默认停靠
        if (_toolbar is { } tb)
        {
            // 位置偏好只保留 X；新屏坐标由中央槽布局器统一重算，旧屏的 Top 不再恢复。
            tb.RestoreDockPosition();
        }
        if (_onboarding is { IsActive: true }) _onboarding.Resume();   // 内部 RelocateToScreen
        // v0.7.0：灵动岛家族重对位（记忆位置可能在旧屏 → 钳制回放映屏；越界回默认位）
        _pageIslandR?.RestorePosition();
        _pageIslandL?.RestorePosition();
        ArrangeChromeSlots();
        ScheduleChromeLayout();
    }

    /// <summary>书写提交 → 最近使用颜色（最多 5 个，最近优先，去重持久化）。</summary>
    private void PushRecentColor(string hex)
    {
        var list = _svc.Config.RecentColors;
        // 去重短路：同色连写（课堂最常见情形）列表内容不变，跳过落盘排程
        if (list.Count > 0 && list[0].Equals(hex, StringComparison.OrdinalIgnoreCase)) return;
        list.RemoveAll(c => c.Equals(hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        if (list.Count > 5) list.RemoveRange(5, list.Count - 5);
        ScheduleRecentColorsSave();
        _toolbar?.RefreshPaletteRecent();   // v0.5.0：最近色沉淀到内嵌调色面板（面板未构建时内部静默忽略）
    }

    // —— 最近色落盘防抖（审查修复）：原实现每笔板书经 PushRecentColor 在 UI 线程同步
    //    全量配置序列化 + flushToDisk，连续板书课堂上每笔一次，机械盘/实时杀软环境
    //    可感知卡顿。现内存态即时生效，落盘延迟合并写（Save 的 flushToDisk 崩溃安全保留）；
    //    进程退出经 CloseAll 兜底冲刷，挂起未落盘的最近色不丢。——
    private readonly DispatcherTimer _recentColorSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _recentColorsDirty;

    /// <summary>排程（合并）一次最近色配置落盘，UI 线程调用。</summary>
    private void ScheduleRecentColorsSave()
    {
        _recentColorsDirty = true;
        _recentColorSaveTimer.Stop();
        _recentColorSaveTimer.Start();
    }

    /// <summary>冲刷挂起的最近色落盘（防抖到期 / 退出收场时调用，UI 线程）。</summary>
    private void FlushRecentColorsSave()
    {
        _recentColorSaveTimer.Stop();
        if (!_recentColorsDirty) return;
        _recentColorsDirty = false;
        _svc.Config.Save();
    }

    /// <summary>预热：预创建全部窗口（Hide 状态）。S0 阶段一次完成，唤起 &lt;300ms。</summary>
    public void PreCreateWindows()
    {
        _app.Dispatcher.Invoke(() =>
        {
            EnsureOverlay();
            if (_toolbar is null)
            {
                _toolbar = new Toolbar(_svc);
                _overlay?.AddToLayer(StageLayer.Ui, _toolbar, 0, 0);   // 预创建即挂舞台（Collapsed 状态）
                WireToolbar();
            }
            RefreshIdleFade();   // v0.9.9：工具栏与左右翻页岛共用同一闲置淡化设置
            if (_eraseIsland is null)
            {
                _eraseIsland = new EraseModeIsland(_svc);
                _overlay?.AddToLayer(StageLayer.Ui, _eraseIsland, 0, 0);   // 预创建即挂舞台（Collapsed 状态）
                WireEraseIsland();
            }
            if (_onboarding is null)
            {
                _onboarding = new OnboardingGuideElement();
                _overlay?.AddToLayer(StageLayer.Guide, _onboarding, 0, 0);   // Guide 层全屏覆盖（Collapsed 状态）
                // 一次完整引导走完 → 自动取消「新手引导」勾选（学过一次不必再提示）
                _onboarding.Completed += () => { _svc.Config.EnableOnboarding = false; _svc.Config.Save(); };
                // v0.9.9「我不想使用 PptInk「本次隐藏」」：明确不用 → 取消勾选，今后不再自动提示
                _onboarding.OptedOut += () => { _svc.Config.EnableOnboarding = false; _svc.Config.Save(); };
                // 跳过时（我先上课/超时）不改勾选状态（下次放映仍会提示）
                // 引导结束（完成/跳过任一）：归还引导期临时切换的工具（引导即实操，见 SwitchToTouchForOnboarding）
                _onboarding.Completed += RestoreToolAfterOnboarding;
                _onboarding.Dismissed += RestoreToolAfterOnboarding;
                _onboarding.OptedOut += RestoreToolAfterOnboarding;
                // v1.1.2：S0 桌面引导会话结束 → 收回唤起的 UI（工具栏不再滞留桌面无法退出）
                _onboarding.Completed += EndS0GuideSession;
                _onboarding.Dismissed += EndS0GuideSession;
                _onboarding.OptedOut += EndS0GuideSession;
            }
            // v0.7.0：角落翻页岛（右下/左下）预创建；页码快捷跳页浮层预创建
            EnsurePageIslands();
            EnsurePageJumpPanel();
        });
    }

    /// <summary>统一舞台窗口创建 + 渲染事件接线（幂等）。单窗口架构（v0.9.0）：
    /// 墨迹层/工具栏/浮岛/插件族全部合入这一个窗口（窗口恒常，切档不重建）。
    /// 事件订阅只挂一次 —— handler 读 _overlay <b>字段</b>而非捕获窗口实例。</summary>
    private void EnsureOverlay()
    {
        if (_overlay is not null) return;
        _overlay = new UnifiedStageWindow(_svc);
        _overlay.StageElementsVisibilityChanged += ScheduleChromeLayout;
        if (_overlayWired) return;
        _overlayWired = true;
        // 渲染失效接线（墨迹/擦除 → 重绘请求）
        _svc.InkEngine.VisualInvalidated += (_, e) =>
        {
            _overlay?.Invalidate();
            // 历史/换页变化 → 撤销/重做按钮可用性（事件可能来自墨迹线程，统一封送 UI）
            if (e.Reason is InvalidateReason.HistoryChanged or InvalidateReason.PageSwitched
                && !_app.Dispatcher.HasShutdownStarted)
                _app.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => _toolbar?.RefreshHistoryButtons()));
        };
        _svc.Eraser.GhostFrame += (_, _) => _overlay?.Invalidate();
        // 跨屏 DPI 变化 → 引导覆盖重对位（原独立引导窗口 DpiChanged 的等价承接）：
        // 舞台自身几何由其 DpiChanged 处理、元素 ScreenLeft/Top 经 LocationChanged 自动重同步，
        // 此处补的是引导全屏 Width/Height 的重设
        _overlay.DpiChanged += (_, _) => _onboarding?.RelocateToScreen();
    }

    /// <summary>三档输入模式切换（设置中心实时生效）：单窗口架构（v0.9.0）下
    /// <b>不再销毁重建窗口</b> —— 窗口恒常，档位只是墨迹层画布/底刷/通道门禁的
    /// 组合切换（SetInputMode）。板书/工具栏/浮岛/插件状态零丢失；书写中切档
    /// 由舞台生命周期门闩完成收笔和通道所有权复位，激活态维持不变。</summary>
    public void RefreshInputMode()
    {
        _app.Dispatcher.Invoke(() =>
        {
            if (_overlay is { } ov)
            {
                // SetInputMode 在输入生命周期门闩内统一收笔与复位通道所有权，避免这里先取消、
                // raw Stylus 随后补到一帧 Move/Down，再把半笔带入新模式。
                ov.SetInputMode(_svc.Config.InputMode);
                if (_overlayWanted)
                {
                    ov.PreWarmRender();          // 4K 位图预分配（口径变化时重建），首笔不卡
                    ov.SetPassthrough(_svc.InkEngine.Tool == InkTool.TouchThrough);   // 激活态穿透语义维持
                    ov.ShowSilent();
                    // v0.9.0 单窗口（阶段E 收敛）：工具栏/浮岛/引导与墨迹层同窗，Z 序由
                    // 分层 Canvas 恒定决定 —— 多窗口时代的「逐窗重申置顶」已无必要
                }
            }
            else
            {
                _svc.InkEngine.CancelLiveStroke();
            }
            string label = _svc.Config.InputMode switch
            {
                "compat" => "鼠标兼容",
                "super" => "超级兼容（全屏白板）",
                _ => "触摸优先",
            };
            Toast.Show($"输入模式已切换：{label}");
            _svc.Log.Info($"输入模式切换：{_svc.Config.InputMode}（单窗口内切换，激活态={_overlayWanted}）");
        });
    }

    /// <summary>「触摸操作时隐藏板书」开关变更：立即按当前工具重应用穿透语义
    ///（无需切档/重启；隐藏只在穿透态生效，书写态不受影响）。</summary>
    public void RefreshTouchThroughInkMode()
    {
        _app.Dispatcher.Invoke(() =>
            _overlay?.SetPassthrough(_svc.InkEngine.Tool == InkTool.TouchThrough));
    }

    /// <summary>设置中心切换墨迹层状态牌后立即请求重绘，不等待下一笔输入或下一帧诊断数据。</summary>
    public void RefreshInkStatusBadge()
    {
        _app.Dispatcher.Invoke(() =>
        {
            if (_svc.Config.DebugMode && _svc.Config.InkStatusBadgeEnabled)
                StageUiLayout.PrepareForDebugBadgeChange();
            _overlay?.InvalidateVisual();
        });
    }

    /// <summary>渲染目标预热：首次合成位图与字体光栅化在后台提前完成。</summary>
    public void PreWarmRenderTarget()
    {
        _app.Dispatcher.Invoke(() =>
        {
            _overlay?.PreWarmRender();
            _svc.InkEngine.SwitchPage(_svc.StateMachine.Session?.CurrentSlide ?? 1);
        }, DispatcherPriority.Background);
    }

    // ———————— 万全看门狗（v0.1.5 工程学底线：假定目标不可信） ————————

    /// <summary>看门狗单拍（UI 线程，600ms 周期）：
    ///  1) 墨迹层三项自愈（可见性 / 穿透样式 / Z 序），修复过则把工具栏/浮岛拉回最顶；
    ///  2) 退映滞留检测：放映已死而状态机仍停在 S1/S2（WinEvent End 链路失效）——
    ///     v0.1.4「退出放映后黑屏滞留、任务栏不出现」的根因即 End 永不触发、
    ///     全套 UI 赖屏。此处双口径判死 + 连续 3 拍确认后强制 OnPresentationEnd 收回。</summary>
    private void WatchdogTick()
    {
        _overlay?.VerifyAndRepair();   // 舞台级自愈（EXSTYLE 回滚/可见性/书写态置顶重申）
        // v0.9.0 单窗口（阶段E 收敛）：原「逐窗重申置顶」整段删除 —— 工具栏/翻页岛/副岛/
        // 引导与墨迹层同窗，Z 序由分层 Canvas 恒定决定，不存在 Topmost 互压问题；
        // 唯一需要自保的是舞台窗口本身，已由 VerifyAndRepair 全权承担。

        // 退映滞留检测只对「状态机认为在放映」的相位生效（S0 手动唤起的
        // Session 为 null，不受影响 —— 否则未放映书写会被看门狗误杀回收）
        if (_svc.StateMachine.Phase is not (AppPhase.S1Active or AppPhase.S2Yield) ||
            _svc.StateMachine.Session is null || _wdExitChecking)
            return;
        _ = WatchdogExitCheckAsync();
    }

    /// <summary>退映滞留复核（COM 查询走后台线程，不卡 UI）。三口径判死（v0.2.0）：
    ///  a) COM 与窗口枚举双判死（WinEvent End 链路失效的经典滞留）；
    ///  b) 窗口在但<strong>挂起</strong>（SendMessageTimeout 超时）—— PowerPoint 退出放映时
    ///     进程假死：全屏放映残骸窗口挡住任务栏与桌面、点击无响应，正是用户
    ///     「黑屏、任务栏不出现、PPT 全无响应、只能 CTRL+ALT+DEL」的实测案例。
    ///     此时放映窗口类还在、COM 也可能还活着，旧双口径判活永远不回收。
    /// 保守项：仅 COM 判死而窗口可见且响应正常（RDP 切换瞬断 COM 的场景）不计拍。</summary>
    private async Task WatchdogExitCheckAsync()
    {
        _wdExitChecking = true;
        try
        {
            // 审查修复（探测顺序）：窗口枚举 + 响应性探测先行（纯 Win32，SendMessageTimeout
            // 自带 800ms 上限，永不挂死），窗口在且响应正常才碰 COM。原实现首条语句即
            // HasActiveShow() —— PPT 主 STA 完全假死（不泵消息）时跨套间同步 COM 调用永久
            // 挂起（MTA 线程池调用方无忙碌重试对话框、无默认超时），b 口径窗口探测永远执行
            // 不到：_wdExitChecking 卡死 true、此后每拍在门禁处直接 return，看门狗在它为之
            // 设计的假死场景下自锁。
            bool comAlive = false, winVisible = false, winResponsive = false;
            try
            {
                var h = await Task.Run(() => Ppt.PptController.FindSlideShowWindow());
                winVisible = h is not null;
                winResponsive = winVisible && Ppt.PptController.IsWindowResponsive(h!.Value);
                if (winVisible && !winResponsive)
                {
                    // 窗口挂起即 b 口径目标形态：此拍不碰 COM（同上必挂），
                    // 挂起本身已足以判死，COM 口径按 false 参与即可。
                }
                else
                {
                    bool? alive = await ProbeComAliveAsync();
                    if (alive is null) return;   // COM 探测超时：状态不明，本拍不计（保守防误杀）
                    comAlive = alive.Value;
                }
            }
            catch { return; }

            bool dead = (!comAlive && !winVisible)   // a) 双口径判死
                     || (winVisible && !winResponsive); // b) 窗口挂起判死
            if (!dead) { _wdStuckCount = 0; return; }

            await _app.Dispatcher.InvokeAsync(() =>
            {
                if (_svc.StateMachine.Phase is not (AppPhase.S1Active or AppPhase.S2Yield)) return;
                if (++_wdStuckCount < 3) return;
                _wdStuckCount = 0;
                _svc.Log.Warn($"看门狗：放映已判死回收（COM={comAlive}，窗口可见={winVisible}，窗口响应={winResponsive}）");
                // 审查修复：同步复位检测器去重状态 —— 看门狗回收绕过检测器（其 End 事件链
                // 失效正是走到这里的前提），不复位则开场 300ms 内被吞的 End 使 _showActive
                // 滞留 true，此后重启放映的 Start 被双通道去重丢弃，整场放映无批注 UI
                _svc.Detector.ForceReset();
                _svc.StateMachine.OnPresentationEnd();
                Toast.Show(winVisible && !winResponsive
                    ? "检测到放映程序无响应，批注工具已收回（可用任务管理器结束 PowerPoint）"
                    : "放映已结束，批注工具已收回");
            });
        }
        finally { _wdExitChecking = false; }
    }

    /// <summary>COM 存活探测（800ms 超时护栏 + 在飞任务复用）。返回 null = 本拍超时放弃。
    /// 复用原因：挂起的 COM 调用无法取消，若每次超时后新建探测，会按看门狗周期无界堆积
    /// 挂死线程；复用同一在飞任务则至多占用一个线程，且 PPT 进程死亡时该调用以 RPC 失败
    /// 落地、任务自然完成，下一拍取到真实值。仅 UI 线程调用（看门狗 tick）。</summary>
    private async Task<bool?> ProbeComAliveAsync()
    {
        var probe = _comProbeInFlight ??= Task.Run(() => _svc.PptController.HasActiveShow());
        if (await Task.WhenAny(probe, Task.Delay(800)) != probe) return null;   // 超时：保留在飞任务
        _comProbeInFlight = null;
        try { return await probe; }
        catch { return false; }
    }

    /// <summary>工具栏缩放变更（设置中心调用）。</summary>
    public void RefreshToolbarScale() => _toolbar?.RefreshScale();

    /// <summary>笔墨高级参数/荧光笔透明度变化：强制重合成当前页缓存，立即反映到已有板书。</summary>
    public void RefreshInkAppearance()
    {
        _app.Dispatcher.Invoke(() => _overlay?.InvalidateInkComposition());
    }

    /// <summary>影响底部槽位的设置项（手势区避让等）变更后立即重排一次（v1.1.1：
    /// 原先只写配置，工具栏/翻页岛要等下次显隐或几何变化才挪位，设置看似无效）。</summary>
    public void RefreshChromeLayout() => ScheduleChromeLayout();

    /// <summary>导出诊断包（v1.1.1 真机验收配套）：环境快照 + 配置 + 最近日志打 zip，
    /// 不含板书与截图。IO 放后台线程，结果 Toast 告知路径。</summary>
    public void ExportDiagnostics()
    {
        _ = Task.Run(() =>
        {
            try
            {
                string zip = Core.DiagnosticsExporter.Export();
                _svc.Log.Info($"诊断包已导出：{zip}");
                _app.Dispatcher.BeginInvoke(() => Toast.Show($"诊断包已导出：{zip}"));
            }
            catch (Exception ex)
            {
                _svc.Log.Error(ex, "诊断包导出失败");
                _app.Dispatcher.BeginInvoke(() => Toast.Show("诊断包导出失败，详见日志。"));
            }
        });
    }

    /// <summary>v0.9.9：闲置淡出设置应用到工具栏与左右翻页岛（同一套开关/时间/透明度，
    /// 三个元素观感一致 —— 用户期望翻页岛与工具栏有相同的闲置半透明能力）。
    /// 创建时与设置变更时各调一次。</summary>
    public void RefreshIdleFade()
    {
        var cfg = _svc.Config;
        _toolbar?.SetIdleFade(cfg.ToolbarIdleFadeEnabled, cfg.ToolbarIdleFadeSeconds, cfg.ToolbarIdleOpacity);
        _pageIslandR?.SetIdleFade(cfg.ToolbarIdleFadeEnabled, cfg.ToolbarIdleFadeSeconds, cfg.ToolbarIdleOpacity);
        _pageIslandL?.SetIdleFade(cfg.ToolbarIdleFadeEnabled, cfg.ToolbarIdleFadeSeconds, cfg.ToolbarIdleOpacity);
    }

    private bool _doubtReviewing;

    /// <summary>下课后疑惑笔迹询问（Beta 可选）：只在放映结束且确有疑问时出现，不打断课堂。</summary>
    private async Task ShowDoubtReviewAsync()
    {
        var cfg = _svc.Config;
        if (!cfg.AdvancedCorrectionEnabled || !cfg.DoubtReviewEnabled || _doubtReviewing || _doubts.Count == 0)
            return;
        _doubtReviewing = true;
        try
        {
            await Task.Delay(900).ConfigureAwait(true);   // 等下课收场动画结束
            var items = TakeDoubts();
            if (items.Count == 0) return;
            var win = new DoubtReviewWindow(items);
            win.ShowDialog();
            foreach (var shape in win.RejectedShapes) ExcludeShapeFromAi(shape);
            if (win.RejectedShapes.Count > 0)
                Toast.Show($"已按你的反馈调整：今后不再自动规整 {win.RejectedShapes.Count} 类图形。");
        }
        catch (Exception ex) { AppLog.Instance.Warn($"疑惑笔迹询问失败：{ex.GetType().Name}: {ex.Message}"); }
        finally { _doubtReviewing = false; }
    }

    /// <summary>用户否决的形状从白名单排除；空白名单先展开为全部已知形状。</summary>
    private void ExcludeShapeFromAi(string shape)
    {
        var cfg = _svc.Config;
        var all = new[] { "line", "arrow", "dashed", "dotted", "check", "cross", "triangle",
                          "quad", "polygon", "circle", "ellipse", "wave", "heart", "star", "cloud" };
        var set = ParseShapeWhitelist(cfg.AiShapeWhitelist);
        if (set.Count == 0) foreach (var a in all) set.Add(a);
        set.Remove(shape);
        cfg.AiShapeWhitelist = set.Count == 0 || set.Count >= all.Length ? "" : string.Join(";", set);
        cfg.Save();
    }

    /// <summary>状态机相位 → UI 呈现。</summary>
    /// <summary>换课件：先给导出机会，再清空旧板书。S1 进入与「放映中途换文件」共用同一路径，
    /// 保证两种入口都不会把上一份 PPT 的板书/页码带到新文件。</summary>
    private void HandleDeckSwitch(string? deckId)
    {
        if (string.IsNullOrEmpty(deckId)) return;
        try
        {
            // 换课件会在 BeginPresentationSession 中清空旧页。自动导出关闭时
            // 必须先给用户一个明确的导出机会，不能清完再发“请提前导出”的提示。
            if (!_svc.Config.AutoExportOnExit &&
                _svc.InkEngine.HasInk &&
                !string.Equals(_svc.InkEngine.SessionIdentity, deckId, StringComparison.OrdinalIgnoreCase))
            {
                var choice = MessageBox.Show(
                    "检测到换了课件。旧板书的批注会被清空，是否先导出旧板书？\n\n" +
                    "选「是」：先导出到桌面「PptInk导出」再切换；选「否」：直接清空。",
                    "融绘 · 换课件", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes)
                {
                    var dir = _svc.InkEngine.ExportSessionSafe();
                    if (dir is not null) Toast.Show($"旧板书已导出：{dir}");
                }
            }
            bool discardedInk = _svc.InkEngine.BeginPresentationSession(deckId);
            if (discardedInk && !_svc.Config.AutoExportOnExit)
                Toast.Show("已切换课件：旧板书已清空。");
        }
        catch (Exception ex)
        {
            _svc.Log.Warn($"换课件处理失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void ApplyPhase(AppPhase phase)
    {
        _app.Dispatcher.Invoke(() =>
        {
            switch (phase)
            {
                case AppPhase.S1Active:
                    _svc.Tray.RestoreIfHiddenByMenu();   // 兑现托盘菜单「下次放映自动恢复」承诺（图标因菜单隐藏时恢复）
                    _assistSessionOff = false;   // 新放映会话：气泡「本次放映不再提示」重新生效
                    // 换课件检测（v1.1.0）：身份变化时存档并清空旧课件批注，防串页。
                    // 身份读取失败（兜底通道/WPS）时引擎不动，保持既有行为。
                    // v1.1.1：自动导出默认关闭 —— 换课件丢弃旧板书时提醒留存，避免
                    // 「以为有自动备份，实际退出即清」的静默数据丢失。
                    if (_svc.PptController.TryGetPresentationIdentity(out var deckId))
                        HandleDeckSwitch(deckId);
                    // 重要：放映开始/换课件/S2 恢复时，session.CurrentSlide 在 OnPresentationStart
                    // 中已经被赋成起始页，ApplySlideIndex 不会再产生“页码变化”事件。若不在这里
                    // 无条件对齐引擎页，引擎会停留在上一场/上次手动书写的页，板书写到不可见的
                    // 旧页；页码轮询同样因为 session 已是当前页而无法纠正。必须显式同步一次。
                    _svc.InkEngine.SwitchPage(_svc.StateMachine.Session?.CurrentSlide ?? 1);
                    _lastBadgePage = -1;
                    _lastBadgeTotal = -1;
                    // v1.1.1：首帧前同步刷新目标屏 —— 原先只有 500ms 页码轮询更新 ShowScreen，
                    // 首帧工具栏/浮岛按旧屏（常为主屏）出现，探测完成后整套 UI 再跳一次屏
                    //（「浮窗先出现在错误屏幕再乱跳」）。FindSlideShowWindow 纯 EnumWindows
                    // 无 COM 风险；COM 路径的精确定位仍由页码轮询兜底。
                    if (Ppt.PptController.FindSlideShowWindow() is { } showHwnd)
                        ShowScreen.UpdateFromShowWindow(showHwnd);
                    ShowChrome();
                    _overlayWanted = true;        // v0.8.2：切档重建后据此恢复激活态
                    _overlay?.ActivateStage();
                    InstallPresenterForward();          // 翻页笔：焦点在本进程时仍可翻页
                    UpdatePageIslands(                      // 页码指示器随放映唤起联动（翻页岛）
                        _svc.StateMachine.Session?.CurrentSlide ?? 1,
                        _svc.StateMachine.Session?.TotalSlides ?? 0);
                    _pagePoll.Start();           // 页码轮询兜底（COM 事件不可用环境）
                    _watchdog.Start();           // 万全看门狗：墨迹层自愈 + 退映滞留回收
                    _ = PollPageBadgeAsync();    // 唤起即校准一次
                    ResumeOrStartOnboarding();
                    break;
                case AppPhase.S2Yield:
                    // 希沃白板/EasiNote 前台：立即静默收回（不打扰切换），墨迹层穿透；引导暂停保留进度
                    HideChrome();
                    _overlayWanted = false;       // v0.8.2：切档重建后不误唤起
                    _overlay?.DeactivateStage();
                    PresenterForwarder.UninstallAll();  // 希沃前台：全部按键放行（绝不产生双翻页）
                    _onboarding?.Pause();
                    break;
                case AppPhase.S0Background:
                    HideChrome();
                    _overlayWanted = false;       // v0.8.2：同上
                    _overlay?.DeactivateStage();
                    PresenterForwarder.UninstallAll();  // 钩子常驻是全局输入税，S0 必卸
                    _svc.InkEngine.CancelLiveStroke();
                    _onboarding?.Stop();
                    RestoreToolAfterOnboarding();     // 引导被强停（退出放映）：临时切换的工具归还
                    _onboardingDoneThisRun = false;   // 新的放映会话可再次自动触发
                    _pagePoll.Stop();
                    _watchdog.Stop();           // 全套 UI 已收回，看门狗无守卫对象
                    _wdStuckCount = 0;
                    _lastBadgePage = -1;
                    _lastBadgeTotal = -1;
                    // v1.0.0Beta：下课（放映结束、UI 已收回）后询问“把握不大”的识别结果。
                    _ = ShowDoubtReviewAsync();
                    // v1.1.1：退场显式复位主屏 —— ShowScreen 停留在上一块放映屏时，托盘
                    // 唤起的设置窗/Toast/HUD 会出现在可能已关闭的投影屏上（「设置像没打开」）。
                    ShowScreen.ResetToPrimary();
                    break;
            }
        });
    }

    /// <summary>手动唤起（托盘双击：未放映也可书写）。不装翻页笔钩子：未放映时翻页无意义。</summary>
    public void ForceActivate()
    {
        _app.Dispatcher.Invoke(() =>
        {
            // v1.1.1：手动唤起也刷新一次目标屏 —— 可能有放映在兜底通道下进行（COM 未绑定），
            // 或上一场把 ShowScreen 留在了已关闭的投影屏。
            if (Ppt.PptController.FindSlideShowWindow() is { } manualHwnd)
                ShowScreen.UpdateFromShowWindow(manualHwnd);
            else
                ShowScreen.ResetToPrimary();
            ShowChrome();
            _overlayWanted = true;       // v0.8.2：手动唤起同为期望激活态
            _overlay?.ActivateStage();
            _watchdog.Start();         // 手动唤起同样受看门狗保护（Z 序被抢自愈）；
                                        // 退映检测有 Session 非空门禁，未放映书写不受影响
if (_svc.StateMachine.Phase == AppPhase.S0Background)
                    {
                        // 手动唤起没有放映页语义：从上一场残页回到第 1 页，避免“桌面书写”
                        // 落在上一场最后一页的缓存里，随后新放映完全看不见。
                        _svc.InkEngine.SwitchPage(1);
                        _lastBadgePage = -1;
                        _lastBadgeTotal = -1;
                        // 显式把页码岛置回「第 1 页 / 无放映」：COM 此时读不到页码，
                        // 轮询不会更新徽标 → 原先会一直显示上一场 PPT 的最后页码。
                        UpdatePageIslands(1, 0);
                        _ = PollPageBadgeAsync();   // 手动唤起也校准一次页码（可能已有放映在进行）
                    }
        });
    }

    /// <summary>单实例管道 show-tray-menu：恢复托盘图标 + 唤起工具栏。
    /// 托盘被「隐藏托盘图标」收起后，再次双击程序图标是唯一能拿回控制面的路径。</summary>
    public void ShowTrayAndActivate()
    {
        _app.Dispatcher.Invoke(() =>
        {
            _svc.Tray.Show();
            ForceActivate();   // 同线程 Invoke 直接执行，不会排队死锁
        });
    }

    /// <summary>工具栏“结束放映”：S1 交给 PPT；S0 手动唤起时没有放映可退，直接收回桌面 UI。</summary>
    public void RequestExitShow()
    {
        if (_svc.StateMachine.Phase == AppPhase.S0Background && _svc.StateMachine.Session is null)
        {
            _app.Dispatcher.Invoke(() =>
            {
                HideChrome();
                _overlayWanted = false;
                _overlay?.DeactivateStage();
                _watchdog.Stop();
                _wdStuckCount = 0;
            });
            return;
        }
        _svc.PptController.ExitShow();
    }

    /// <summary>导出当前会话全部板书（PNG 逐页 + SVG 矢量 + JSON 点序列）到桌面「PptInk导出」。
    /// 功能补齐：导出此前仅在退出时自动触发，课堂中无法手动留存板书。</summary>
    public void ExportInk()
    {
        // 4K 逐页 PNG 渲染是秒级 CPU 工作：放到池线程，舞台/书写完全不受影响。
        // ExportSessionSafe 本身就是「持锁快照 + 锁外渲染」，线程安全。
        if (Interlocked.Exchange(ref _exporting, 1) != 0)
        {
            Toast.Show("正在导出，请稍候…");
            return;
        }
        Toast.Show("正在导出板书（PNG / SVG / JSON）…");
        _ = Task.Run(() =>
        {
            try
            {
                var dir = _svc.InkEngine.ExportSessionSafe();
                Toast.Show(dir is null ? "暂无板书可导出" : $"板书已导出：{dir}");
            }
            catch (Exception ex)
            {
                _svc.Log.Error(ex, "导出板书失败");
                Toast.Show("导出失败，详见日志");
            }
            finally { Interlocked.Exchange(ref _exporting, 0); }
        });
    }

    /// <summary>打开设置中心（单例：已开则置前，绝不叠窗）。</summary>
    public void OpenSettings()
    {
        _app.Dispatcher.Invoke(() =>
        {
            // 设置窗构造涉及整页 XAML/字体/主题资源：任何一步异常都不能静默吞掉
            //（否则表现就是"点了设置没反应"，用户完全无从判断）。
            try
            {
                HideAllPopovers();   // 弹出型浮岛让位给模态感更强的设置中心
                if (_settings is null || !_settings.IsLoaded)
                {
                    _settings = new SettingsWindow(_svc);
                    _settings.Closed += (_, _) => _settings = null;
                    _settings.Show();
                }
                else
                {
                    _settings.Activate();
                }
            }
            catch (Exception ex)
            {
                _svc.Log.Error(ex, "打开设置中心失败");
                Toast.Show("设置窗口打开失败，详情已写入日志（托盘 → 导出诊断包）。");
            }
        });
    }

    /// <summary>
    /// 统一收回全部弹出型浮岛（触发点：落笔书写 / 翻页 / 切工具 / 相位离开 S1 / 打开设置）。
    /// v0.5.0：内嵌调色面板 → 工具栏窗口缩回单行；擦除浮岛 → 吮吸隐藏。
    /// </summary>
    public void HideAllPopovers()
    {
        if (_eraseIsland is { IsVisible: true })
            _eraseIsland.HideAnimated();
        _toolbar?.HidePalette(immediate: true);
        if (_shapeBubble is { IsVisible: true })
            _shapeBubble.HideAnimated();
        if (_pageJump is { IsVisible: true })
            _pageJump.HideAnimated();
    }

    private void ShowChrome()
    {
        EnsurePageIslands(); // 双岛是固定界面的一部分，不依赖旧版开关或预创建时序
        _overlay?.ShowSilent(); // 先让舞台坐标系统完成初始化，再分配槽位，最后才显示 UI 元素
        _toolbar?.ExpandFromCapsule();
        ArrangeChromeSlots();  // 同步分配独占槽；不能等 ContextIdle 后才修正已显示的元素
        _toolbar?.ShowAnimated(fromBottomEdge: !double.IsFinite(_svc.Config.ToolbarDockY));
        if (_pageIslandR is { } r) { r.RestorePosition(); r.ShowAnimated(); }
        if (_pageIslandL is { } l) { l.RestorePosition(); l.ShowAnimated(); }
        ScheduleChromeLayout();
    }

    /// <summary>将底部 chrome 与浮岛放进固定槽位。这里只分配区域，不扫描矩形相交。</summary>
    private void ScheduleChromeLayout()
    {
        if (_toolbar is null || _chromeLayoutQueued) return;
        _chromeLayoutQueued = true;
        _app.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _chromeLayoutQueued = false;
            ArrangeChromeSlots();
        }));
    }

    private void ArrangeChromeSlots()
    {
        var tb = _toolbar;
        if (tb is null) return;
        var wa = ShowScreen.Bounds;
        if (wa.Width < 100 || wa.Height < 100) return;

        _overlay?.UpdateLayout();
        tb.Measure(new Size(wa.Width, wa.Height));
        tb.UpdateLayout();

        double safeBottom = _svc.Config.RespectGestureZone ? 48 : 8;
        double dockBottom = wa.Bottom - safeBottom;

        // Reserve the center width first; corner islands are then assigned disjoint left/right
        // lanes. On narrow screens they become internally horizontally scrollable rather than
        // extending their visuals or hit targets into the toolbar's lane.
        const double edgeMargin = 8;
        const double laneGap = 8;
        double usableWidth = Math.Max(0, wa.Width - 2 * edgeMargin - 2 * laneGap);
        double minimumCenterLane = Math.Min(360, usableWidth);
        double edgeBudget = Math.Max(0, (usableWidth - minimumCenterLane) / 2);
        double leftPreferred = _pageIslandL?.PreferredWidth ?? 220;
        double rightPreferred = _pageIslandR?.PreferredWidth ?? 220;
        if (!double.IsFinite(leftPreferred) || leftPreferred < 1) leftPreferred = 220;
        if (!double.IsFinite(rightPreferred) || rightPreferred < 1) rightPreferred = 220;
        double leftWidth = Math.Min(leftPreferred, edgeBudget);
        double rightWidth = Math.Min(rightPreferred, edgeBudget);

        double leftSlotStart = wa.Left + edgeMargin;
        double leftSlotEnd = leftSlotStart + leftWidth;
        double rightSlotStart = wa.Right - edgeMargin - rightWidth;
        double rightSlotEnd = wa.Right - edgeMargin;
        double laneLeft = leftSlotEnd + laneGap;
        double laneRight = rightSlotStart - laneGap;
        // The exact partition, including zero-width extreme fallback, is used by both rendering
        // size and pointer clamps. Never inflate a lane to an arbitrary minimum that steals space.
        laneLeft = Math.Clamp(laneLeft, wa.Left, wa.Right);
        laneRight = Math.Clamp(laneRight, laneLeft, wa.Right);

        _pageIslandL?.SetChromeSlot(leftSlotStart, leftSlotEnd, dockBottom);
        _pageIslandR?.SetChromeSlot(rightSlotStart, rightSlotEnd, dockBottom);

        tb.SetDockLane(laneLeft, laneRight, dockBottom);
        tb.UpdateLayout();

        StageUiLayout.Configure(wa, laneLeft, laneRight, dockBottom, tb);
    }

    private void HideChrome()
    {
        _toolbar?.HidePalette(immediate: true); // 隐藏舞台时直接复位同层面板，防止关闭动画抢后续位置
        _toolbar?.HideAnimated();
        _eraseIsland?.HideAnimated();   // 弹出型浮岛绝不赖屏
        _pageIslandR?.HideAnimated();   // v0.7.0：翻页岛同步收回
        _pageIslandL?.HideAnimated();
        _pageJump?.HideAnimated();
        if (_shapeBubble is { IsVisible: true }) _shapeBubble.HideAnimated();
        _overlay?.HideSilent();
    }

    public void CloseAll()
    {
        _app.Dispatcher.Invoke(() =>
        {
            FlushRecentColorsSave();   // 兜底冲刷挂起的最近色落盘（此后防抖定时器不再有机会触发）
            _toolbar?.Close();
            _eraseIsland?.Close();
            _overlay?.Close();
            _settings?.Close();
            _shapeBubble?.Close();
            _debugHud?.Close();
            _pageIslandR?.Close();
            _pageIslandL?.Close();
            _pageJump?.Close();
            _onboarding?.Stop();
        });
    }
}
