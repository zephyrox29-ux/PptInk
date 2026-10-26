using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>
/// v0.9.0 统一舞台窗口 —— 多窗口冲突的结构性终结。
///
/// 【动机】历史架构下墨迹层/工具栏/翻页岛/副岛/浮岛/插件族/引导/调试 HUD 各自是
/// 独立 Topmost 窗口：Z 序竞争（PowerPoint 重申置顶压盖）、多窗口置顶重申看门狗、
/// 各自 EXSTYLE 守卫、跨窗口焦点翻转 —— 一切「冲突」问题都源于窗口数 &gt; 1。
/// 本类把全部界面合入<b>单窗口</b>，窗口冲突归零。
///
/// 【单窗口内分层】（Z 序自下而上，见 <see cref="StageLayer"/>）：
///   Ink    墨迹层（画布 + 底刷 + 状态牌 + 通道 A/B/C 输入）
///   Ui     工具栏 / 翻页岛 ×2 / 副岛 / 擦除岛 / 规整气泡 / 胶囊
///   Plugin 插件面板族（计时器/随机点名/聚光灯/板中板）+ Toast
///   Guide  首次引导高亮圈
///   Hud    调试 HUD
///
/// 【命中与穿透 —— 区域穿透机制（核心）】
/// WPF AllowsTransparency 窗口 = WS_EX_LAYERED + UpdateLayeredWindow（逐像素 alpha
/// 合成）。win7 起部分环境（希沃大屏定制系统，v0.8.2 定论）对 layered 窗口的输入
/// 命中按<b>逐像素 alpha</b>断言：alpha=0 像素输入直接穿透、alpha&gt;0 可命中。
/// 该规则正好被利用为「区域穿透」：
///   · 书写态：全屏底刷 alpha=1（命中资格最小值，v0.8.2 修复同源）→ 全屏拦截，
///     StylusPlugIn/鼠标 收到一切输入；
///   · 穿透态（「触摸操作」）：底刷 alpha=0 → 空白区像素无命中资格 → 触摸直达
///     PPT 翻页；UI 元素自身像素 alpha=255（其不透明视觉效果）→ 依然可点！
///     （多窗口时代「穿透时工具栏可点」靠独立窗口实现，单窗口时代由逐像素
///     alpha 实现 —— 冲突消解且能力保留。）
///   · 标准 DWM 整窗命中环境（alpha 不参与命中）：区域穿透不成立，穿透态退化为
///     WS_EX_TRANSPARENT 全窗放行（UI 暂不可点）。环境声明见
///     <see cref="AppConfig.WholeWindowHit"/>（默认 false=逐像素区域穿透）。
///
/// 【三档输入模式 —— 切档不再重建窗口】
/// v0.8.2 因 AllowsTransparency 运行时不可改而采用「销毁重建窗口」切档；单窗口
/// 化后窗口恒常，档位只是<b>墨迹层画布/底刷/通道门禁</b>的组合切换：
///   touch：底刷 alpha=1 透明层，A/B/C 三通道全开（默认）；
///   compat：同上画布，但通道 A/B 门禁关闭、只信通道 C（合成鼠标链路）；
///   super：墨迹层渲染全屏深色画布（像素 alpha=255，整窗命中 100% 保证）——
///     穿透以「画布隐藏」表达（PPT 重现），工具栏等 UI 层照常显示可点。
///
/// 【置顶与看门狗 —— 从「多窗口互相重申」收敛为「单窗口自保」】
/// Super/Topmost 只作用于本窗口：激活时 SetWindowPos 置顶一次，书写态看门狗
/// 每拍重申（不再需要把工具栏/浮岛逐窗拉回最顶）；被放映窗口压住 → 类名诊断
/// 保留（IsCoveredByShowWindow），修复动作就是重申本窗置顶。
/// </summary>
public sealed partial class UnifiedStageWindow : Window
{
    private readonly AppServices _svc;
    private HwndSource? _source;
    private WindowInputGuard? _guard;

    /// <summary>整窗命中环境声明（标准 DWM：alpha 不参与命中）。逐像素环境=false。
    /// 运行时可通过 <see cref="AppConfig.WholeWindowHit"/> 声明（默认逐像素）。</summary>
    private bool _wholeWindowHit;

    // ———————— 分层（单窗口内 Z 序） ————————

    /// <summary>墨迹层画布（最底，InkCanvasHost 子类承载自绘渲染管线）：底刷 alpha
    /// 即「区域穿透」的执行面；super 档深色画布同为此层。渲染/输入/状态牌逻辑见
    /// UnifiedStageWindow.Ink.cs（partial）。构造器创建（OnRender 需宿主引用）。</summary>
    private readonly InkCanvasHost _inkLayer;

    private readonly Canvas _uiLayer = new();       // 工具栏/翻页岛/副岛/浮岛
    private readonly Canvas _pluginLayer = new();   // 插件面板族/Toast
    private readonly Canvas _guideLayer = new();    // 首次引导高亮圈
    private readonly Canvas _hudLayer = new();      // 调试 HUD

    private readonly SolidColorBrush _surfaceBrush; // 墨迹层底刷（受档位/穿透态控制）
    private readonly SolidColorBrush _boardBrush;   // super 档深色画布（#16201E 主题面板同源）

    /// <summary>窗口当前是否处于激活态（放映进行中且本窗应覆盖放映屏）。</summary>
    private bool _active;

    /// <summary>可见悬浮元素变化后通知编排器重检工具栏避碰。</summary>
    internal event Action? StageElementsVisibilityChanged;
    internal void NotifyStageElementsChanged() => StageElementsVisibilityChanged?.Invoke();

    /// <summary>期望穿透态（「触摸操作」工具）。穿透的物理表达随环境与档位而异：
    /// 逐像素环境 = 底刷 alpha=0（区域穿透）；整窗环境 = WS_EX_TRANSPARENT。
    /// super 档 = 深色画布隐藏（PPT 重现）。</summary>
    private bool _passthroughWanted;

    /// <summary>v0.8.2 三档输入模式快照（AppConfig.InputMode；切档 = SetInputMode，
    /// 不重建窗口 —— 构造期读一次 + SetInputMode 同步刷新）。</summary>
    private string _inputMode = "touch";
    private bool SuperCompat => _inputMode == "super";
    private bool ChannelCOnly => _inputMode == "compat";

    /// <summary>Z 序诊断日志节流（5s）。</summary>
    private long _lastCoveredWarnTick;

    /// <summary>穿透态跳过笔迹绘制（v0.9.1「触摸操作时隐藏板书」）：与
    /// <see cref="AppConfig.TouchThroughInkHidden"/> 联动，由 ApplySurface 维护。
    /// 只跳笔迹层绘制、不隐藏整层 —— 底刷 alpha=0（穿透）、状态牌/诊断照常可读。</summary>
    private bool _hideInkWhilePassthrough;

    public UnifiedStageWindow(AppServices svc)
    {
        _svc = svc;
        _inputMode = AppConfig.Current.InputMode;
        _wholeWindowHit = AppConfig.Current.WholeWindowHit;

        // 单窗口恒常 layered（v0.8.2 命中资格底刷机制同源）；super 档不重建窗口，
        // 由 _inkLayer 画布承担「不透明深色画布」语义（见 SetInputMode）。
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;             // 永不抢焦点（放映/书写节奏不被打断）
        ResizeMode = ResizeMode.NoResize;

        // 大屏清晰度：像素对齐 + ClearType（4K/86 寸半像素模糊不可接受）
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // 底刷不可 Freeze：alpha 在书写(1)↔穿透(0)间动态切换（Freeze 后赋值抛异常）。
        _surfaceBrush = new SolidColorBrush(Color.FromArgb(1, 0xFF, 0xFF, 0xFF));
        _boardBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x1E));
        _boardBrush.Freeze();

        _inkLayer = new InkCanvasHost(this);
        ApplySurface();                    // 初始档位/穿透态 → 底刷/画布初值

        var stageRoot = new Grid();
        // stageRoot 背景保持 null：窗口逐像素命中资格由 _inkLayer 底刷（铺满全屏）与
        // UI 元素自身像素共同承担 —— 单一执行面；super 档画布隐藏时空白区天然穿透。
        stageRoot.Children.Add(_inkLayer);
        stageRoot.Children.Add(_uiLayer);
        stageRoot.Children.Add(_pluginLayer);
        stageRoot.Children.Add(_guideLayer);
        stageRoot.Children.Add(_hudLayer);
        // Canvas 不自拉伸：随宿主同步全窗尺寸（分层画布必须覆盖整屏）。
        stageRoot.SizeChanged += (_, _) => SyncLayerSizes(stageRoot);
        Content = stageRoot;

        // 跨屏 DPI 变化（笔记本 150% ↔ 大屏 100%）：先用本窗口 HWND 刷新 ShowScreen 口径
        //（缩放已变而 500ms 轮询未到，直接按旧口径重设会差一个缩放比），再重设几何 + 物理钉位。
        DpiChanged += (_, _) =>
        {
            if (_source is { } s) ShowScreen.UpdateFromShowWindow(s.Handle);
            RelocateToScreen();
        };

        InitializeInk();                   // 墨迹层输入/渲染接线（partial：UnifiedStageWindow.Ink.cs）

        // Toast 宿主注册（全局唯一舞台）：轻提示元素挂 Plugin 层，停用态借窗显示见 Toast
        StageHost.Register(this);   // 全局唯一舞台注册（Toast/插件族挂载入口）

        Loaded += (_, _) =>
        {
            HookHwnd();
            if (Content is Grid root) SyncLayerSizes(root);
            ResyncStageElements();
        };
    }

    /// <summary>分层画布尺寸与宿主同步（Canvas 默认 0×0，须显式撑满）。</summary>
    private void SyncLayerSizes(Grid host)
    {
        var s = new Size(host.ActualWidth, host.ActualHeight);
        foreach (var layer in new[] { _inkLayer, _uiLayer, _pluginLayer, _guideLayer, _hudLayer })
        {
            layer.Width = s.Width;
            layer.Height = s.Height;
        }
    }

    // ———————— 生命周期（Stage 窗口级，替代原 InkOverlayWindow 对应方法） ————————

    /// <summary>激活（放映开始/进入书写阶段）：对位放映屏 → 显示 → 按工具初始化
    /// 穿透态 → 置顶重申 → 状态快照日志。替代原 ActivateInkLayer + ShowSilent。</summary>
    public void ActivateStage()
    {
        bool wasActive = _active; // ForceActivate / 状态机通知可能重复到达；重复激活不应抢占正在书写的会话
        _active = true;
        RelocateToScreen();
        // 恢复 0 空闲态与显示/穿透切换保持原子：B 通道只能在舞台真正激活后开始写。
        lock (_inputLifecycleGate)
        {
            ShowSilent();
            SetPassthrough(_svc.InkEngine.Tool == InkTool.TouchThrough);
            if (!wasActive)
                InkOnStageActivated(); // 仅由停用→激活转换重置会话，重复激活不截断当前笔划
        }
        Invalidate();            // 首帧渲染（页面缓存预热后即绘）
        if (_source is { } s)
            SetWindowPos(s.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        LogStageState("激活");
    }

    /// <summary>停用（退出放映/希沃前台避让）：强制收笔 + 释放鼠标捕获 + 隐藏。
    /// 捕获残留会让全局鼠标事件被吞（比穿透更严重的输入劫持）。</summary>
    public void DeactivateStage()
    {
        // 与 StylusPlugIn 首个 Down 共用门闩：绝不允许“ForceLift 后迟到 Down”在隐藏舞台
        // 上新建 LiveStroke。UI 线程持锁期间不会泵入新的 UI 输入，B 通道需在放锁后复核哨兵。
        lock (_inputLifecycleGate)
        {
            InkOnStageDeactivating();      // 会话先切到 5 哨兵，拒绝新输入
            ForceLiftStroke();             // 收笔保数据；随后隐藏舞台
        }
        InputDiagnostics.EndSession();
        _active = false;
        _passthroughWanted = true;
        SetPassthrough(true);
        HideSilent();
        // 停止后延迟复核隐藏（v0.2.0 黑屏滞留防御）：Hide() 被外部吞掉时一张全屏
        // Topmost 窗口残留会遮蔽整个桌面。300ms 后仍可见 → 强制再 Hide。
        var src = _source;
        if (src is not null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_active && IsWindowVisible(src.Handle))
                {
                    HideSilent();
                    _svc.Log.Warn("统一舞台停用复核：窗口仍可见，已强制隐藏");
                }
            };
            timer.Start();
        }
    }

    /// <summary>书写收笔（通道 C 鼠标链路 + 引擎会话锁正确归还）。</summary>
    public void ForceLiftStroke()
    {
        // v0.4.2 修复（同 InkOverlayWindow）：强制收笔用最后有效点而非 NaN ——
        // NaN 经 SmoothingFilter EMA 传播进历史，导出 SVG/JSON 含 NaN 致文件损坏。
        StrokeData? live;
        lock (_svc.InkEngine.Gate) live = _svc.InkEngine.LiveStroke;
        if (live is { Points: { Count: > 0 } pts })
            _svc.InkEngine.OnStylusUp(pts[^1] with { TimestampTicks = InkClock.NowMs() });
        else
            _svc.InkEngine.CancelLiveStroke();
    }

    /// <summary>显示（不激活焦点）。单窗口恒显：super 档穿透以「画布隐藏」表达
    ///（原版隐藏整个窗口的语义已由 ApplySurface 承担），窗口本体保持可见 ——
    /// UI 层（工具栏等）在穿透期照常可点。</summary>
    public void ShowSilent()
    {
        if (!IsVisible)
        {
            // Set a concrete screen rectangle before first Show; relying on WPF's automatic
            // placement can leave Window.Left/Top as NaN while child Canvas coordinates are set.
            RelocateToScreen();
            Show();
        }
        if (Content is Grid root) SyncLayerSizes(root);
        UpdateLayout();
        ResyncStageElements();
    }

    private void ResyncStageElements()
    {
        foreach (var layer in new[] { _uiLayer, _pluginLayer, _guideLayer, _hudLayer })
            foreach (var element in layer.Children.OfType<StageElement>())
                element.SyncCanvasPosition();
    }

    internal IEnumerable<StageElement> EnumerateStageElements()
    {
        foreach (var layer in new[] { _uiLayer, _pluginLayer, _guideLayer, _hudLayer })
            foreach (var element in layer.Children.OfType<StageElement>())
                yield return element;
    }

    public void HideSilent() => Hide();

    /// <summary>换屏前收回插件层浮面（v1.1.1）：计时器/点名/聚光灯/快捷插件面板等的
    /// 屏幕坐标来自旧屏，换屏后会被钳到新屏边缘；随下次触发重新弹出即可。</summary>
    public void RetractFloatingPanelsOnScreenChange()
    {
        foreach (var element in _pluginLayer.Children.OfType<StageElement>().ToArray())
            if (element.IsVisible)
                element.HideForExclusiveSlotReplacement(preserveChromeHost: false);
    }

    /// <summary>舞台当前是否处于放映激活态（Toast/HUD 停用态借窗显示的还原判据）。</summary>
    public bool StageActive => _active;

    /// <summary>窗口几何对位放映屏（双屏：必须盖在放映屏而非主屏 —— 「画笔从未
    /// 生效」的双屏根因之一）。ShowScreen.ScreenChanged / DpiChanged 时重跑，幂等。
    /// v1.1.1：WPF 赋值后用物理像素把窗口钉到放映屏 —— 混合 DPI 部署（笔记本 150% +
    /// 大屏 100%）下 Window.Left/Top 的跨屏落点是 WPF 已知缺陷（dotnet/wpf #4127），
    /// DIP 赋值会落到错误显示器/差一个缩放比；GetMonitorInfo 的物理矩形不会错。</summary>
    public void RelocateToScreen()
    {
        var b = ShowScreen.Bounds;
        if (!double.IsFinite(b.Left) || !double.IsFinite(b.Top) || b.Width <= 0 || b.Height <= 0) return;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (IsLoaded)
        {
            if (Content is Grid root) SyncLayerSizes(root);
            ResyncStageElements();
        }
        PinToScreenPhysical();
    }

    /// <summary>物理像素钉位（v1.1.1）：把窗口精确盖到放映屏矩形上。窗口 DIP 尺寸 ×
    /// 新屏 DPI = 监视器物理尺寸，故尺寸本就吻合，钉位主要修正原点；跨屏迁移时 WPF
    /// 会按 WM_DPICHANGED 重排内容，之后的 DpiChanged → RelocateToScreen 再钉一次收敛。</summary>
    private void PinToScreenPhysical()
    {
        var src = _source;
        if (src is null) return;
        var p = ShowScreen.PhysicalBounds;
        if (!double.IsFinite(p.Left) || !double.IsFinite(p.Top) || p.Width <= 0 || p.Height <= 0) return;
        SetWindowPos(src.Handle, IntPtr.Zero,
            (int)Math.Round(p.Left), (int)Math.Round(p.Top),
            (int)Math.Round(p.Width), (int)Math.Round(p.Height),
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ———————— 穿透与档位 ————————

    /// <summary>穿透开关（工具驱动）：书写/擦除 = 收输入；「触摸操作」= 放行给 PPT。
    /// 物理表达三选一（按环境与档位）：
    ///   逐像素环境（默认）：墨迹层底刷 alpha=0 → 空白区天然穿透 + UI 区天然可点；
    ///   整窗命中环境：WS_EX_TRANSPARENT 全窗放行（UI 暂不可点，退出通道 =
    ///     翻页笔/键盘/结束放映，见试用必读）；
    ///   super 档：深色画布隐藏（墨迹与遮挡一并让出，PPT 重现）。</summary>
    public void SetPassthrough(bool passThrough)
    {
        lock (_inputLifecycleGate)
        {
            bool enteringPassThrough = passThrough && !_passthroughWanted;
            if (enteringPassThrough)
            {
                // 切换到触摸操作/穿透前结束正在进行的笔划并释放所有输入所有权；否则
                // 视觉层隐藏后，迟到的 Stylus Move 仍可能修改擦除路径或留下半笔。
                if (_mouseInking)
                {
                    _mouseInking = false;
                    Mouse.Capture(null);
                }
                _pointerSessionId = 0;
                _rejectedPointerId = 0;
                _touchOwnedUntil = 0;
                ForceLiftStroke();
                InputDiagnostics.EndSession();
                // 若正处舞台停用/激活切换，保留 5 哨兵；由 InkOnStageActivated 统一开放。
                if (Volatile.Read(ref _inputSession) != 5)
                    Interlocked.Exchange(ref _inputSession, _active ? 0 : 5);
            }
            _passthroughWanted = passThrough;
            ApplySurface();
            _guard?.SetMode(GuardModeFor(passThrough));
        }
        // 立即重绘：穿透态切换会改变「是否跳过笔迹绘制」与底刷 alpha，不能等 250ms 状态牌泵。
        Invalidate();
    }

    /// <summary>守卫模式决策：穿透位（WS_EX_TRANSPARENT，整窗放行）只在「整窗命中
    /// 环境 + 非 super 档」的穿透态挂 —— 逐像素环境的区域穿透由底刷 alpha 承担
    ///（此时挂穿透位反而整窗放行、UI 不可点，丧失区域穿透能力）；super 档穿透以
    /// 画布隐藏表达（窗口恒 Ink 模式，UI 照常可点）。</summary>
    private InkInputMode GuardModeFor(bool passThrough) =>
        passThrough && _wholeWindowHit && !SuperCompat
            ? InkInputMode.PassThrough
            : InkInputMode.Ink;

    /// <summary>三档输入模式切换（v0.8.2 需销毁重建窗口；单窗口下仅切换
    /// 墨迹层画布/底刷/通道门禁组合，板书与 UI 状态零丢失）。仅激活态可见 ——
    /// 隐藏态（S0/避让）切档不唤起窗口，显示职责归 ActivateStage。</summary>
    public void SetInputMode(string mode)
    {
        var nextMode = mode is "compat" or "super" ? mode : "touch";
        lock (_inputLifecycleGate)
        {
            if (!string.Equals(_inputMode, nextMode, StringComparison.Ordinal))
            {
                // 切换命中/穿透策略前，结束当前交互并清掉所有权：若先改画布再收笔，
                // StylusPlugIn 迟到的 Move 可能继续修改旧工具的擦除路径或隐藏半笔。
                if (_mouseInking)
                {
                    _mouseInking = false;
                    Mouse.Capture(null);
                }
                _pointerSessionId = 0;
                _rejectedPointerId = 0;
                _touchOwnedUntil = 0;
                ForceLiftStroke();
                InputDiagnostics.EndSession();
                Interlocked.Exchange(ref _inputSession, _active ? 0 : 5);
                _inputMode = nextMode;
            }
            ApplySurface();
            // 仅激活态允许本方法触达显示：未放映（S0/希沃避让）时切档绝不唤起全屏 Topmost
            // 舞台 —— 否则 super 档是一整屏无控件的不透明画布、touch 档（逐像素环境）
            // 是 alpha=1 的隐形输入黑洞，工具栏全 Collapsed、看门狗因 !_active 不自愈，
            // 用户只能强杀进程。显示职责归 ActivateStage / 编排器 _overlayWanted 分支。
            if (_active) ShowSilent();
            _forceRecompose = true;   // 画布组合已变：强制重合成页面缓存（原切档=重建窗口、缓存全新）
        }
        Invalidate();
        _svc.Log.Info($"统一舞台输入模式：{_inputMode}（画布={SurfaceLabel()} 穿透期望={_passthroughWanted}）");
    }

    /// <summary>按档位 + 穿透期望刷新墨迹层画布（区域穿透执行面）。_surfaceBrush 挂在
    /// _inkLayer.Background —— 墨迹层自己的命中资格（铁律：Background 必须非 null，
    /// 否则元素不参与 hit-test、StylusPlugIn 收不到输入）；alpha 1（书写拦截）↔
    /// 0（区域穿透，UI 元素自身像素 alpha=255 照常可点）。</summary>
    private void ApplySurface()
    {
        if (SuperCompat)
        {
            // super：不透明深色画布（像素 alpha=255 → 逐像素环境全屏命中；整窗环境
            // layered 窗口本就整窗命中 —— 双环境皆 100% 拦截）。
            // 穿透期望 → 画布隐藏（不渲染=像素让位，PPT 重现）；底刷同步降为 alpha=0 ——
            // 画布隐藏后的空白区穿透由窗口剩余像素表达，UI 层照常显示可点。
            _inkLayer.Visibility = _passthroughWanted ? Visibility.Hidden : Visibility.Visible;
            _inkLayer.Background = _boardBrush;
            _surfaceBrush.Color = Color.FromArgb(
                _passthroughWanted ? (byte)0 : (byte)1, 0xFF, 0xFF, 0xFF);
            return;
        }
        // 穿透态「隐藏板书」（v0.9.1，默认开启）：逐像素命中环境下墨迹像素 alpha>0 会
        // 把触摸截留在本窗口（板书密布时无法操作 PPT 可交互项）。跳过笔迹绘制后该像素
        // alpha=0 → 触摸天然穿透直达 PPT；板书数据无损，切回书写态立刻重现。
        // 注意只跳「笔迹绘制」而不隐藏整层：底刷(alpha=0)/状态牌/诊断照常 —— 老师
        // 排障时看到的「穿透中」实况不因该优化而消失。
        // 关闭该选项时保留板书可见，由穿透态 WM_NCHITTEST 分流尽力放行（见 WndProc）。
        _hideInkWhilePassthrough = _passthroughWanted && _svc.Config.TouchThroughInkHidden;
        _inkLayer.Visibility = Visibility.Visible;
        _inkLayer.Background = _surfaceBrush;
        // 逐像素环境：alpha 1（书写拦截）↔ 0（区域穿透）；整窗环境由
        // WS_EX_TRANSPARENT 兜底（GuardModeFor），alpha 切换无副作用。
        _surfaceBrush.Color = Color.FromArgb(
            _passthroughWanted ? (byte)0 : (byte)1, 0xFF, 0xFF, 0xFF);
    }

    private string SurfaceLabel()
    {
        if (SuperCompat) return _passthroughWanted ? "深层画布（穿透，PPT重现）" : "深色画布（阻挡）";
        return _passthroughWanted ? "透明（区域穿透）" : "命中底刷 alpha=1";
    }

    // ———————— UI 元素注册（阶段C/D 迁入各窗口内容时使用） ————————

    /// <summary>把 UI 元素挂到指定层（单窗口内定位）。
    /// StageElement 的定位唯一真相 = ScreenLeft/ScreenTop（屏幕 DIP，与旧独立窗口
    /// Left/Top 同语义）—— 本方法自动接线舞台（AttachStage），x/y 参数对其无效；
    /// 普通 FrameworkElement 仍用 x/y 直接 Canvas 定位。</summary>
    public T AddToLayer<T>(StageLayer layer, T element, double x, double y) where T : FrameworkElement
    {
        LayerOf(layer).Children.Add(element);
        if (element is StageElement se)
            se.AttachStage(this, layer);
        else
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }
        RefreshUiInputBounds();
        return element;
    }

    /// <summary>从舞台移除元素（收容/回收）。</summary>
    public void RemoveFromLayer(StageLayer layer, FrameworkElement element)
    {
        var host = LayerOf(layer);
        if (host.Children.Contains(element)) host.Children.Remove(element);
        RefreshUiInputBounds();
    }

    public Canvas LayerOf(StageLayer layer) => layer switch
    {
        StageLayer.Ink => _inkLayer,
        StageLayer.Ui => _uiLayer,
        StageLayer.Plugin => _pluginLayer,
        StageLayer.Guide => _guideLayer,
        _ => _hudLayer,
    };

    /// <summary>已迁入元素的屏幕矩形枚举（v0.9.1 用于穿透态 WM_NCHITTEST 分流：
    /// UI 区可点 / 其余放行给 PPT）。**统一为屏幕物理像素口径**（与 NCHITTEST 的
    /// lParam 一致）：PointToScreen 为物理像素、TranslatePoint 为窗口本地 DIP，
    /// 故 DIP 位移需乘 DevicePerDip。仅枚举可见（含祖先可见）且有实际尺寸的元素。
    /// v1.1.1：尺寸经 TransformBounds 映射到窗口坐标 —— 工具栏带 LayoutTransform 时
    /// RenderSize 是缩放前尺寸，原实现命中矩形比视觉小（按钮边缘穿透失效）。</summary>
    public void EnumerateScreenRects(Action<Rect> visit)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource) return;
        double k = DevicePerDip;
        Point tl = PointToScreen(new Point(0, 0));   // 物理像素
        foreach (var layer in new[] { _uiLayer, _pluginLayer, _guideLayer, _hudLayer })
            foreach (UIElement el in layer.Children)
            {
                if (el is not FrameworkElement fe || !fe.IsVisible) continue;   // 隐藏元素不占命中区
                Rect local;
                try
                {
                    local = el.TransformToVisual(this) is { } t
                        ? t.TransformBounds(new Rect(new Point(0, 0), el.RenderSize))
                        : new Rect(el.TranslatePoint(new Point(0, 0), this), el.RenderSize);
                }
                catch
                {
                    local = new Rect(el.TranslatePoint(new Point(0, 0), this), el.RenderSize);
                }
                if (local.Width <= 0 || local.Height <= 0) continue;
                visit(new Rect(tl.X + local.X * k, tl.Y + local.Y * k, local.Width * k, local.Height * k));
            }
    }

    // ———————— 看门狗自愈（编排器 600ms 周期调用） ————————

    /// <summary>单窗口自保：EXSTYLE 校验回滚 + 应激活却不可见则重显 +
    /// 书写态每拍重申置顶（被放映窗口压住的自愈）。返回是否发生过修复。</summary>
    public bool VerifyAndRepair()
    {
        if (!_active) return false;
        if (_source is not { } src) return false;
        bool repaired = _guard?.Verify() ?? false;
        try
        {
            IntPtr h = src.Handle;
            // super 档穿透期望下的画布隐藏是正常态；窗口本身仍可见。
            if (!IsWindowVisible(h)) { ShowSilent(); repaired = true; }
            if (_svc.InkEngine.Tool != InkTool.TouchThrough)
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (IsCoveredByShowWindow(h) &&
                Environment.TickCount64 - _lastCoveredWarnTick > 5000)
            {
                _lastCoveredWarnTick = Environment.TickCount64;
                _svc.Log.Warn("统一舞台 Z 序诊断：上方存在放映类窗口（本拍已重申置顶）");
            }
        }
        catch { }
        return repaired;
    }

    /// <summary>Z 序上方是否存在可见的放映窗口（本窗口被压在其下）。
    /// GW_HWNDPREV 沿 Z 序向上遍历（topmost 链内），遇放映类窗口即命中。</summary>
    private static bool IsCoveredByShowWindow(IntPtr hwnd)
    {
        var cur = GetWindow(hwnd, GW_HWNDPREV);
        int guard = 0;
        var sb = new StringBuilder(64);
        while (cur != IntPtr.Zero && guard++ < 64)
        {
            sb.Clear();
            if (GetClassNameW(cur, sb, 64) > 0 &&
                sb.ToString() is "screenClass" or "KPptScreenClass" or "WPPScreenClass" &&
                IsWindowVisible(cur))
                return true;
            cur = GetWindow(cur, GW_HWNDPREV);
        }
        return false;
    }

    /// <summary>状态快照日志（v0.6.0 语义继承）：HWND 未挂钩时记 Warn 线索（竞态回补）。</summary>
    private void LogStageState(string cause)
    {
        try
        {
            if (_source is not { } src)
            {
                _svc.Log.Warn($"统一舞台{cause}：HWND 尚未挂钩（首次激活竞态，HookHwnd 将回补）");
                return;
            }
            int ex = GetWindowLongW(src.Handle, GWL_EXSTYLE);
            _svc.Log.Info($"统一舞台{cause}：bounds=({Left:F0},{Top:F0},{Width:F0}x{Height:F0}) " +
                          $"档位={_inputMode} mode={(_passthroughWanted ? "穿透" : "接收")} " +
                          $"EXSTYLE=0x{ex:X8} 穿透位={((ex & WS_EX_TRANSPARENT) != 0 ? "挂" : "无")} " +
                          $"visible={IsWindowVisible(src.Handle)} 守卫={(_guard is null ? "缺位" : "就位")} " +
                          $"画布={SurfaceLabel()}");
        }
        catch { }
    }

    // ———————— Win32 挂钩与通道 A 转发 ————————

    private void HookHwnd()
    {
        _source = (HwndSource)PresentationSource.FromVisual(this);
        // EXSTYLE 集中管理（NOACTIVATE/TOOLWINDOW/穿透位），外部改写零延迟回滚。
        // 初始守卫模式：未激活或穿透期望 → 穿透；穿透位只在整窗环境挂
        //（逐像素环境由底刷 alpha 承担，见 GuardModeFor）。
        _guard = new WindowInputGuard(_source, GuardModeFor(!_active || _passthroughWanted),
            msg => _svc.Log.Info(msg));
        _source.AddHook(WndProc);
        // 亮度键不是标准虚拟键，用 Raw Input 的 Consumer Control 兜底；舞台停用/关闭时
        // 回调里的 enabled 会返回 false，不会在桌面/设置场景误翻页。
        BrightnessHotkeyRouter.Register(_source.Handle,
            forward => _svc.UiOrchestrator.AdvanceSlidePage(forward),
            () => _active && _svc.Config.PresenterMediaKeyPageTurn && _svc.Config.PresenterForwardEnabled);
        Closed += (_, _) => BrightnessHotkeyRouter.Unregister();
        // 竞态回补（v0.6.0 三项空跑修复）：预创建窗口首次 Show 后 Loaded 异步晚于
        // Activate 同步序列 —— _guard/_source 均 null 时穿透/置顶/日志空跑，此处补齐。
        if (_active)
        {
            SetPassthrough(_svc.InkEngine.Tool == InkTool.TouchThrough);
            SetWindowPos(_source.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            LogStageState("挂钩后补");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 亮度键 Raw Input：只消费 HID Consumer Control 的 0x006F/0x0070。
        const int WM_INPUT = 0x00FF;
        if (msg == WM_INPUT && BrightnessHotkeyRouter.HandleRawInput(lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        // —— 通道 A（v0.3.0 主力）：WndProc 原生 WM_POINTER，不依赖 WPF 触摸栈 ——
        // 消息不置 handled（WPF 未启用 pointer 模式时不消费；万一消费，StylusPlugIn
        // 通道被会话锁幂等挡住，无双发）。硬件环境全局启用 pointer 路由时零成本接管。
        // compat 档屏蔽通道 A（只信通道 C）；可见性判据用 _inkLayer（super 档穿透=
        // 画布隐藏，不收输入）。处理逻辑见 partial：HandlePointer / EnsureInputReady。
        if (!ChannelCOnly &&
            msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP &&
            _inkLayer.IsVisible && _svc.InkEngine.Tool != InkTool.TouchThrough)
        {
            if (msg == WM_POINTERDOWN) EnsureInputReady();
            HandlePointer(LOWORD(wParam), msg);
        }

        // —— 穿透态 WM_NCHITTEST 分流（v0.9.1，仅「保留板书可见」模式启用）——
        // 背景：逐像素命中环境下墨迹像素 alpha>0 会让 OS 把输入判给本窗口（板书密布
        // 时点不到 PPT 可交互项）。此处显式介入窗口命中判定：
        //   · 落点在可见 UI 元素矩形内 → 返回 HTCLIENT（UI 照常可点）；
        //   · 其余（含墨迹像素/空白）     → 返回 HTTRANSPARENT（系统把该次鼠标类命中
        //     交给下层窗口 → 直达 PPT 翻页/交互项）。
        // 说明：触摸经提升链产生的合成鼠标消息同样经过本判定；若个别机型触摸提升
        // 不经 NCHITTEST，则用默认的「穿透态隐藏板书」兜底（两种模式二选一）。
        if (msg == WM_NCHITTEST && _active && _passthroughWanted
            && !_svc.Config.TouchThroughInkHidden)
        {
            handled = true;
            return new IntPtr(HitUiChrome(lParam) ? HTCLIENT : HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    /// <summary>穿透态命中分流判据：屏幕物理像素点是否落在任一可见 UI 元素矩形内
    ///（WM_NCHITTEST 的 lParam 即屏幕物理像素坐标）。</summary>
    private bool HitUiChrome(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        int x = (short)(v & 0xFFFF);          // 低 16 位含符号
        int y = (short)((v >> 16) & 0xFFFF);  // 高 16 位含符号
        bool hit = false;
        EnumerateScreenRects(r => { if (r.Contains(x, y)) hit = true; });
        return hit;
    }

    private static uint LOWORD(IntPtr v) => (uint)(v.ToInt64() & 0xFFFF);

    // ———————— Win32 P/Invoke ————————

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int GW_HWNDPREV = 3;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
}

/// <summary>统一舞台分层（Z 序自下而上）。</summary>
public enum StageLayer
{
    /// <summary>墨迹层（最底）：画布/底刷/状态牌/通道 A/B/C 输入。</summary>
    Ink = 0,

    /// <summary>工具栏/翻页岛 ×2/副岛/浮岛/胶囊。</summary>
    Ui = 1,

    /// <summary>插件面板族（计时器/点名/聚光灯/板中板）+ Toast。</summary>
    Plugin = 2,

    /// <summary>首次引导高亮圈。</summary>
    Guide = 3,

    /// <summary>调试 HUD（最顶）。</summary>
    Hud = 4,
}