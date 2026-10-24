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

/// <summary>统一调度器 · 浮岛族与设置联动：工具栏/擦除浮岛/翻页岛/分割副岛的创建与重排、页码徽标轮询、设置项实时生效（布局/副岛/翻页笔/单手书写）。（v0.9.2 拆分，纯搬运）</summary>
public sealed partial class UiOrchestrator
{
    private void WireToolbar()
    {
        if (_toolbar is null) return;
        _toolbar.ToolSelected += (tool) => _svc.InkEngine.Tool = tool;
        _toolbar.GeometryChanged += ScheduleChromeLayout;
        _toolbar.UndoRequested += () => _svc.InkEngine.Undo();
        _toolbar.RedoRequested += () => _svc.InkEngine.Redo();
        _toolbar.ExitShowRequested += () => _svc.PptController.ExitShow();
        // v0.9.2：AI 按钮语义修正 —— 原「识别最近一笔」在实际课堂中没有价值（老师画完
        // 停顿即自动规整，再点一下等于重复触发）；现改为 AI Power 开关（开/关本地推理），
        // 与设置中心「AI 形状规整」同一状态源，两侧视觉实时同步。
        _toolbar.ShapeAssistRequested += ToggleAiPower;
        // v0.7.0 可分割：分割缝拉出 / 副岛合并 → 重建副岛
        _toolbar.SplitChanged += () => RebuildSegment();
        // 长按手柄收纳（v0.2.0）：工具栏自身收缩为屏上胶囊球（可拖动、吸附边缘、
        // 点按展开）——绝不能收进托盘（放映中托盘被全屏放映盖住，用户无法恢复）。
        // 编排器只负责让位弹出物：贴边画笔小球收起、擦除浮岛/气泡退场。
        _toolbar.Minimized += () =>
        {
            _eraseIsland?.HideAnimated();
            if (_shapeBubble is { IsVisible: true }) _shapeBubble.HideAnimated();
        };

        // 擦除：再次点按"擦除"（已在擦除模式）→ 按钮旁弹出三模式浮岛
        _toolbar.EraseRequested += () =>
        {
            var anchor = _toolbar?.GetEraseButtonRect();
            if (anchor is { } r)
            {
                // 弹出前与引擎当前工具对齐高亮（审查修复：原浮岛内部状态可能滞后于实际工具）
                _eraseIsland?.SetCurrentMode(ToolbarCatalog.ToolToEraseMode(_svc.InkEngine.Tool));
                _eraseIsland?.ShowNear(r);
            }
        };

        // v0.5.0：画笔/荧光笔的再次点按已内联为工具栏 TogglePalette（面板与按钮同窗口渲染），
        // 编排器不再中转调色弹出 —— 少一跳事件、少一个窗口、z 序天然正确。
    }

    private void WireEraseIsland()
    {
        if (_eraseIsland is null) return;
        _eraseIsland.EraseModeSelected += mode =>
        {
            // 模式即时生效：工具栏高亮同步（广播 ToolSelected → 引擎切工具）+ 默认模式记忆
            _toolbar?.SelectTool(ToolbarCatalog.EraseModeToTool(mode));
            _svc.Config.DefaultEraseMode = mode;
            _svc.Config.Save();
        };
    }

    private void AdvanceSlide(bool forward)
    {
        bool viaCom = forward ? _svc.PptController.NextSlide() : _svc.PptController.PrevSlide();
        if (viaCom)
            _ = PollPageBadgeAsync();          // COM 成功：立即读权威页码（不等 2s 轮询）
        else
            _svc.Detector.NotifyManualAdvance(forward); // 兜底通道页码推算
    }

    // ———————— v0.7.0 灵动岛家族（翻页岛 / 分割副岛） ————————

    /// <summary>页码分发：角落翻页岛 ×2 联动（主工具栏翻页组已移除）。</summary>
    private void UpdatePageIslands(int current, int total)
    {
        _pageIslandR?.UpdatePageBadge(current, total);
        _pageIslandL?.UpdatePageBadge(current, total);
    }

    /// <summary>固定创建并挂载左右两份翻页岛。旧版配置开关只做迁移，不再决定控件是否存在。</summary>
    private void EnsurePageIslands()
    {
        bool migrateFlags = !_svc.Config.PageIslandRightEnabled || !_svc.Config.PageIslandLeftEnabled;
        _svc.Config.PageIslandRightEnabled = true;
        _svc.Config.PageIslandLeftEnabled = true;
        if (migrateFlags) _svc.Config.Save();

        if (_pageIslandR is null)
        {
            _pageIslandR = new PageTurnIsland(_svc, "R");
            _pageIslandR.PrevRequested += () => AdvanceSlide(forward: false);
            _pageIslandR.NextRequested += () => AdvanceSlide(forward: true);
        }
        if (_overlay is not null && _pageIslandR.Stage is null)
            _overlay.AddToLayer(StageLayer.Ui, _pageIslandR, 0, 0);

        if (_pageIslandL is null)
        {
            _pageIslandL = new PageTurnIsland(_svc, "L");
            _pageIslandL.PrevRequested += () => AdvanceSlide(forward: false);
            _pageIslandL.NextRequested += () => AdvanceSlide(forward: true);
        }
        if (_overlay is not null && _pageIslandL.Stage is null)
            _overlay.AddToLayer(StageLayer.Ui, _pageIslandL, 0, 0);

        // v0.9.9：翻页岛与工具栏共用同一闲置淡化设置（创建后应用一次；
        // 设置变更时由 RefreshIdleFade 统一刷新，三个元素观感一致）。
        RefreshIdleFade();
    }

    /// <summary>
    /// 重建分割副岛（SplitChanged 触发）：分割态 → 按主栏右侧 12px（或记忆位）创建/更新；
    /// 合并态 → 关闭旧岛。主栏与副岛按钮同走 ToolbarFactory（视觉/点击即有零割裂）。
    /// v0.9.7 自愈：分割点后无可见元素（撤销/AI/插件被旧配置全隐藏）时，自动清除
    /// 失效分割标记并重建主栏 —— 避免「主栏只剩 4 个按钮、副岛也不存在」的黑洞状态。
    /// </summary>
    private void RebuildSegment()
    {
        if (_toolbar is not { } tb) return;
        var old = _segment;
        _segment = null;
        old?.Close();   // 直接关闭：视觉焦点在主栏宽度变化上，副岛静默退场不抢戏
        if (tb is not { IsSplit: true }) return;

        var items = tb.SplitSegmentItems();
        if (items.Count == 0)
        {
            // v0.9.7 自愈：分割失效 → 清除标记 + 重建主栏（Ops 组回家），
            // 配置保存后下次启动不再复现；Toast 提示用户（说明而非静默改配置）。
            _svc.Config.ToolbarSplitAfterId = null;
            _svc.Config.Save();
            tb.RebuildLayout();
            tb.SelectTool(_svc.InkEngine.Tool);
            Toast.Show("分割失效，已还原：撤销/重做/AI/插件回到主工具栏。");
            return;
        }
        _segment = new SegmentIsland(_svc, items, OnSegmentCommand, () => _toolbar?.GetPanelRect());
        _segment.MergeRequested += () => _toolbar?.MergeOpsBack();   // → SplitChanged → 本方法关闭本岛
        _overlay?.AddToLayer(StageLayer.Ui, _segment, 0, 0);   // 舞台元素：即建即挂
        _segment.PlaceInitial(tb.Left + tb.ActualWidth + 12, tb.Top);
        if (tb.IsVisible)
        {
            // Set its fixed row before the first visible frame; no free-position flash next to the toolbar.
            ArrangeChromeSlots();
            _segment.ShowAnimated();
        }
        else ScheduleChromeLayout();
    }

    /// <summary>副岛命令分发（与主工具栏同一条真相：undo/redo/ai/plugin/exit）。</summary>
    private void OnSegmentCommand(string id)
    {
        switch (id)
        {
            case "undo": _svc.InkEngine.Undo(); break;
            case "redo": _svc.InkEngine.Redo(); break;
            case "plugin": _svc.Plugins.ToggleQuickPanel(); break;
            case "nav.exit": _svc.PptController.ExitShow(); break;
            case "ai":
                ToggleAiPower();   // v0.9.2：按钮语义 = AI Power 开关（原先"识别一次"没有实际价值）
                break;
        }
    }

    /// <summary>读 COM 权威页码并刷新徽标（值变化才刷）。COM 读取放后台线程，不卡 UI。</summary>
    private async Task PollPageBadgeAsync()
    {
        if (_polling) return;                  // 防重入（轮询 Tick 与翻页即时读并发）
        _polling = true;
        try
        {
            // 审查修复：COM 读数加 800ms 超时护栏 + 在飞任务复用（与看门狗 COM 探测同策）
            // —— PPT 主 STA 完全假死时同步 COM 读永久挂起，原实现 finally 永不执行、
            // _polling 卡死 true，此后页码轮询每拍在防重入处 return，整体停摆。
            // 复用而非每次新建：挂起的调用无法取消，反复新建会按 500ms 周期无界堆积挂死
            // 线程；PPT 进程死亡时挂起调用以 RPC 失败落地、任务自然完成，轮询自愈。
            var probe = _pageProbeInFlight ??= Task.Run(() =>
            {
                // 放映屏感知：趁读页码的同一后台段刷新目标屏（EnumWindows+Monitor 查询廉价；
                // 幂等：目标屏未变时无副作用，变化时 ScreenChanged → RelocateAllToScreen）
                ShowScreen.UpdateFromShowWindow(_svc.PptController.TryGetShowHwnd());
                int pos = _svc.PptController.TryGetSlidePosition(out var p) ? p : 0;
                int total = _svc.PptController.TryGetTotalSlides(out var t) ? t : 0;
                return (Pos: pos, Total: total);
            });
            if (await Task.WhenAny(probe, Task.Delay(800)) != probe)
                return;                        // 超时：本拍放弃（徽标保留旧值），在飞任务留待下拍
            _pageProbeInFlight = null;
            int page, pages;
            try { (page, pages) = await probe; }
            catch { return; }
            if (page <= 0) return;             // COM 不可用：不动徽标（保留事件/推算通道的值）
            if (page == _lastBadgePage && pages == _lastBadgeTotal) return;
            _lastBadgePage = page; _lastBadgeTotal = pages;
            await _app.Dispatcher.InvokeAsync(() => UpdatePageIslands(page, pages));
            // 审查修复：轮询兜底原先只刷徽标、不切墨迹页 —— COM 事件订阅失效但查询可用的环境下，
            // 老师翻页后页码显示变了、墨迹却留在旧页。权威读数送状态机统一驱动切页/徽标/收浮岛
            // （幂等：与事件通道同值时 ApplySlideIndex 静默返回，不重复触发）。
            _svc.StateMachine.OnSlideChanged(new Detection.SlideChangedEvent(page, pages, "COM:poll"));
        }
        finally { _polling = false; }
    }

    // ———————— 设置中心联动（实时生效，无需重启/重新放映） ————————

    /// <summary>工具栏布局变更后调用：按新配置重建并保持当前工具选中态。</summary>
    public void RefreshToolbarLayout()
    {
        _app.Dispatcher.Invoke(() =>
        {
            _toolbar?.RebuildLayout();
            // RebuildLayout 清空按钮表后需恢复高亮；SelectTool 同值广播幂等无害
            _toolbar?.SelectTool(_svc.InkEngine.Tool);
            // 页码指示器随重建丢失 → 按当前会话回填（v0.7.0：翻页岛承载页码）
            UpdatePageIslands(
                _svc.StateMachine.Session?.CurrentSlide ?? _svc.InkEngine.CurrentSlideIndex,
                _svc.StateMachine.Session?.TotalSlides ?? 0);
        });
    }

    /// <summary>刷新左右固定翻页岛（不再提供隐藏/拖动开关）；放映中立即确保两个角落都可用。</summary>
    public void RefreshPageIslands()
    {
        _app.Dispatcher.Invoke(() =>
        {
            EnsurePageIslands();
            // 放映中（工具栏可见）→ 两侧都恢复固定角落并上屏；非放映期由 ShowChrome 统一唤起
            if (_toolbar is { IsVisible: true })
            {
                if (_pageIslandR is { IsVisible: false } pr) { pr.RestorePosition(); pr.ShowAnimated(); }
                if (_pageIslandL is { IsVisible: false } pl) { pl.RestorePosition(); pl.ShowAnimated(); }
            }
        });
    }

    /// <summary>翻页笔开关变更后调用：按当前相位与配置收敛钩子（S1 才装，其余立即卸）。</summary>
    public void RefreshPresenterForward()
    {
        _app.Dispatcher.Invoke(() =>
        {
            bool want = _svc.Config.PresenterForwardEnabled &&
                        _svc.StateMachine.Phase == AppPhase.S1Active;
            if (want) PresenterForwarder.Install(AdvanceSlide);
            else PresenterForwarder.UninstallAll();
        });
    }

    private void InstallPresenterForward()
    {
        if (_svc.Config.PresenterForwardEnabled)
            PresenterForwarder.Install(AdvanceSlide);
    }

    /// <summary>切换"严格手掌抑制"（托盘菜单，v0.3.1 语义更新：原「仅指尖书写」）：
    /// 写配置 + 持久化 + 实时同步手掌抑制实例。
    /// 审查修复：原托盘菜单只改 AppConfig（PalmRejection.OnlyFingerMode 为构造期快照），
    /// 既不持久化也不生效 —— 属死功能。</summary>
    public void SetOnlyFingerInk(bool enabled)
    {
        _svc.Config.OnlyFingerInk = enabled;
        _svc.Config.Save();
        _svc.PalmRejection.OnlyFingerMode = enabled;
    }

}
