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

/// <summary>统一调度器 · 新手引导与调试 HUD：引导生命周期（自动触发/续播/隐让/丢弃）、引导期工具切换（引导即实操）、调试模式诊断面板。（v0.9.2 拆分，纯搬运）</summary>
public sealed partial class UiOrchestrator
{
    /// <summary>
    /// 切换调试模式（v0.4.0）：写配置 + 持久化 + 诊断面板显隐。
    /// 面板懒创建（首次开启才建窗），放映屏切换时随 RelocateAllToScreen 重对位。
    /// 墨迹层的接触矩形/笔宽可视化由配置开关直接驱动（UnifiedStageWindow.Ink.cs 渲染层6）。
    /// </summary>
    public void SetDebugMode(bool enabled)
    {
        _app.Dispatcher.Invoke(() =>
        {
            // Debug HUD and the optional ink-layer status badge claim fixed UI lanes. If the
            // layout mode changes while a tool panel is open, close that panel before changing
            // lanes instead of allowing an already-open panel to remain under the diagnostics UI.
            StageUiLayout.PrepareForDebugModeChange(enabled);
            _svc.Config.DebugMode = enabled;
            _svc.Config.Save();
            // The ink-layer status badge is painted by OnRender; invalidate immediately so
            // turning debug mode off removes the already-rendered badge without waiting for ink input.
            _overlay?.InvalidateVisual();
            if (enabled)
            {
                if (_debugHud is null)
                {
                    _debugHud = new DebugHudElement(_svc);
                    _overlay?.AddToLayer(StageLayer.Hud, _debugHud, 0, 0);   // Hud 层（最顶）
                }
                if (StageUiLayout.HasActiveModal)
                    _debugHud.HidePanel(); // modal mode owns the stage until its exit
                else
                    _debugHud.ShowPanel();
                Toast.Show("调试模式已开：角落显示诊断信息，触摸能看到接触矩形。");
                _svc.Log.Info("调试模式开启（诊断面板 + 接触可视化）");
            }
            else
            {
                _debugHud?.HidePanel();
                Toast.Show("调试模式已关闭");
                _svc.Log.Info("调试模式关闭");
            }
        });
    }

    // ================= 新手引导 =================

    // ———————— 引导期工具切换（v0.9.0：引导即实操） ————————

    private InkTool _onboardingPrevTool = InkTool.Pen;
    private bool _onboardingToolSwitched;

    /// <summary>引导开始：自动切到「触摸操作」——老师可直接点洞内被讲解的按钮、
    /// 现场翻页演示（配合引导遮罩的「视觉/命中分离」，洞内命中直达下层 UI）。
    /// 老师若在引导中主动换工具（点【笔】试写），引导结束时尊重其选择不再改回。</summary>
    private void SwitchToTouchForOnboarding()
    {
        if (_svc.InkEngine.Tool == InkTool.TouchThrough) return;   // 已在触摸模式：不动
        _onboardingPrevTool = _svc.InkEngine.Tool;
        _onboardingToolSwitched = true;
        _svc.InkEngine.Tool = InkTool.TouchThrough;   // 触发穿透态（ToolChanged → SetPassthrough）
    }

    /// <summary>引导结束（完成/跳过/被强停）：归还引导期临时切换的工具。</summary>
    private void RestoreToolAfterOnboarding()
    {
        if (!_onboardingToolSwitched) return;
        _onboardingToolSwitched = false;
        if (_svc.InkEngine.Tool == InkTool.TouchThrough)
            _svc.InkEngine.Tool = _onboardingPrevTool;
    }

    /// <summary>
    /// 启动新手引导。force=true（托盘「立即播放」）无视勾选；否则要求「新手引导」处于勾选状态。
    /// S0 时先唤起 UI 再引导（未放映也能走一遍流程）。v1.1.2：S0 触发的会话记录标记 ——
    /// 引导结束后收回桌面 UI（原先工具栏/HUD 留在桌面上，S0 没有「结束放映」按钮可退）。
    /// </summary>
    public void StartOnboarding(bool force = false)
    {
        _app.Dispatcher.Invoke(() =>
        {
            if (_onboarding is null || _onboarding.IsActive) return;
            if (!force && !_svc.Config.EnableOnboarding) return;
            if (_svc.StateMachine.Phase == AppPhase.S0Background)
            {
                ShowChrome();
                _overlayWanted = true;   // v0.8.2：S0 唤起引导 = 墨迹层激活态
                _overlay?.ActivateStage();
                _watchdog.Start();
                _s0GuideSession = true;
            }
            _onboardingDoneThisRun = true;
            SwitchToTouchForOnboarding();   // 引导即实操：自动切触摸模式（点洞内按钮/现场翻页）
            _onboarding.Start(OnboardingCatalog.BuildDefault(this));
        });
    }

    /// <summary>v1.1.2：托盘手动触发的引导（S0 桌面会话）结束后收回唤起的 UI ——
    /// 桌面上没有放映，工具栏留着既挡内容又没有退出按钮。S1 放映会话不受影响。</summary>
    private void EndS0GuideSession()
    {
        if (!_s0GuideSession) return;
        _s0GuideSession = false;
        if (_svc.StateMachine.Phase != AppPhase.S0Background) return;
        _app.Dispatcher.Invoke(() =>
        {
            RestoreToolAfterOnboarding();
            HideChrome();
            _overlayWanted = false;
            _overlay?.DeactivateStage();
            _watchdog.Stop();
            _wdStuckCount = 0;
        });
    }

    /// <summary>S1 进入：引导未完成则续播；否则按勾选自动触发一次。</summary>
    private void ResumeOrStartOnboarding()
    {
        if (_onboarding is null) return;
        if (_onboarding.IsActive)
        {
            if (!_onboarding.IsVisible) _onboarding.Resume();   // S2 → S1 恢复
            return;
        }
        if (_onboardingDoneThisRun || !_svc.Config.EnableOnboarding) return;
        _onboardingDoneThisRun = true;
        SwitchToTouchForOnboarding();   // 同上：自动触发的引导同样切触摸模式
        _onboarding.Start(OnboardingCatalog.BuildDefault(this));
    }

    // —— IOnboardingTargets：引导高亮定位（v0.8.0 起传递【屏幕物理像素】矩形；
    //    引导覆盖窗口用自身 DPI 统一换算——混合 DPI 多屏部署零错位，见 OnboardingGuideElement.RenderStep） ——

    Rect? IOnboardingTargets.Tool(InkTool tool) => _toolbar?.GetToolPhysicalRect(tool);
    Rect? IOnboardingTargets.UndoRedo() => _toolbar?.GetUndoRedoPhysicalRect();
    Rect? IOnboardingTargets.AiPlugin() => _toolbar?.GetAiPluginPhysicalRect();
    Rect? IOnboardingTargets.Grip() => _toolbar?.GetGripPhysicalRect();
    Rect? IOnboardingTargets.NavBlock() =>
        _pageIslandR is { IsVisible: true } r ? r.GetIslandPhysicalRect() : _pageIslandL?.GetIslandPhysicalRect();

}
