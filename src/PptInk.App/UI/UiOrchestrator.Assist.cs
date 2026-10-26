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

/// <summary>统一调度器 · AI 识别管线：停顿排程 / 统一识别入口（识别→气泡确认或直接规整）/ 回写与命名 / AI Power 开关。（v0.9.2 拆分，纯搬运）</summary>
public sealed partial class UiOrchestrator
{
    // ———————— AI 停顿自动规整（设置 → 行为：自动校正 / 气泡确认） ————————

    // ———————— AI Power 开关（v0.9.2） ————————

    /// <summary>AI Power 当前状态（工具栏按钮激活态查询）。</summary>
    public bool AiPowerOn => _svc.Config.AiEnabled;

    /// <summary>切换 AI Power（工具栏【AI 规整】按钮 = 开关；仅手动模式下改为“识别最近一笔”）。
    /// 手动模式此前把按钮当成纯电源：第一次点开顺带识别一笔，第二次点又把 AI 关掉，
    /// 无法连续手动识别。现在 manual 模式下按钮是幂等的“识别最近一笔”，电源开关留在设置页。</summary>
    public void OnToolbarAiPressed()
    {
        if (_svc.Config.AiTriggerTiming == "manual")
        {
            var last = _svc.InkEngine.LastDrawableStroke();
            if (last is null)
            {
                Toast.Show("还没有可识别的笔迹。");
                return;
            }
            if (!_svc.Config.AiEnabled)
            {
                SetAiPower(true, notify: false);
                Toast.Show("AI 正在启动，识别最近一笔…");
            }
            RunAssist(last, manual: true);
            return;
        }
        ToggleAiPower();
    }

    /// <summary>切换 AI Power（非手动模式保留原电源开关语义）。</summary>
    public void ToggleAiPower() => SetAiPower(!_svc.Config.AiEnabled, notify: true);

    /// <summary>
    /// 设置 AI Power（幂等；工具栏按钮与设置中心共用同一状态源 AppConfig.AiEnabled）：
    ///   开 → 拉起本地推理子进程 + 停顿自动规整生效；
    ///   关 → 取消待处理识别 + 释放子进程（内存归还；已画板书与撤销栈完全不受影响）。
    /// 两侧视觉实时同步（工具栏按钮激活态由 SetAiPower 主动刷新）。
    /// </summary>
    public void SetAiPower(bool on, bool notify = true)
    {
        _app.Dispatcher.Invoke(() =>
        {
            if (_svc.Config.AiEnabled != on)
            {
                _svc.Config.AiEnabled = on;
                _svc.Config.Save();
            }
            if (on)
            {
                _ = _svc.PythonBridge.EnsureStartedAsync();     // 池线程冷启动，不冻结界面
            }
            else
            {
                _assistTimer?.Stop();                            // 关：取消待处理停顿识别
                _assistTarget = null;
                if (_shapeBubble is { IsVisible: true }) _shapeBubble.HideAnimated();   // 清残留确认气泡
                _ = _svc.PythonBridge.StopAsync();               // 释放子进程（内存归还）
            }
            _toolbar?.RefreshAiBadge();                          // 按钮激活态同步
            if (notify)
            {
                string how = _svc.Config.AiTriggerTiming switch
                {
                    "lift" => "抬笔即识",
                    "manual" => "仅点【AI】时识别",
                    _ => "停笔后自动规整",
                };
                Toast.Show(on ? $"AI 已开启：{how}。" : "AI 已关闭，本地推理进程已释放。");
            }
        });
    }

    /// <summary>抬笔后排程识别（v1.0.0 时机参数化）：
    ///   pause（默认）—— 停顿后识别（auto 0.7× / bubble 1.0×），连笔书写永不被打扰；
    ///   lift —— 抬笔即识（120ms），适合快速作图；
    ///   manual —— 不自动识别，仅工具栏【AI】按钮触发。
    /// 另外做最小尺寸过滤：笔迹包围盒短边小于 AiMinSizeMm 不识别（避开小勾、小点被误规整）。</summary>
    private void ScheduleAutoAssist(StrokeData stroke)
    {
        _assistTimer?.Stop();
        var cfg = _svc.Config;
        if (!cfg.AiEnabled || _assistSessionOff) return;
        if (stroke.Points.Count < 4) return;
        string timing = cfg.AiTriggerTiming is "pause" or "lift" or "manual" ? cfg.AiTriggerTiming : "pause";
        if (timing == "manual") return;   // 仅手动模式：不排程自动识别

        // 最小尺寸过滤：太小的一笔大概率是标点/勾画，不该被规整成图形
        double minMm = Math.Clamp(cfg.AiMinSizeMm, 0, 40);
        if (minMm > 0)
        {
            var bounds = stroke.Bounds;
            double minPx = _svc.InkEngine.Profile.MmToPx(minMm);
            if (Math.Min(bounds.Width, bounds.Height) < minPx) return;
        }

        _assistTarget = stroke;
        int delay;
        if (timing == "lift")
        {
            delay = 120;   // 抬笔即识：只留一点点去抖余量
        }
        else
        {
            bool instant = cfg.ShapeAssistMode != "bubble";
            delay = Math.Max(250, (int)(cfg.ShapeRecognizeDelayMs * (instant ? 0.7 : 1.0)));
        }
        if (_assistTimer is null)
        {
            _assistTimer = new DispatcherTimer();
            _assistTimer.Tick += (_, _) => OnAssistElapsed();
        }
        _assistTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _assistTimer.Start();
    }

    private void OnAssistElapsed()
    {
        _assistTimer?.Stop();
        if (_assistTarget is not { } target) return;
        _assistTarget = null;
        RunAssist(target, manual: false);
    }

    /// <summary>统一识别管线（自动 / 工具栏手动共用）。v1.0.0 能力参数化：
    ///  - 识别范围：recent 时把最近 N 笔合并识别（多笔组合图形）；
    ///  - 置信度阈值：低于 AiConfidenceThreshold 不规整（越保守越不误识）；
    ///  - 形状白名单：只规整白名单内的形状（禁用常误识的形状）；
    ///  - 识别超时：超过 AiRecognizeTimeoutMs 放弃本次（板书不卡）；
    ///  - 成功提示：AiHighlightOnSuccess 控制是否弹确认。
    /// AiEnabled 全链路门禁；失败反馈仅手动触发给出（自动模式静默，不打扰课堂）。</summary>
    private void RunAssist(StrokeData target, bool manual)
    {
        var cfg = _svc.Config;
        if (!cfg.AiEnabled)
        {
            if (manual) Toast.Show("AI 未开启。去设置 → 行为里打开。");
            return;
        }
        var engine = _svc.InkEngine;

        // Beta 文字/公式防误触：汉字/公式由大量密集短笔画组成，常被单笔识别误认成
        // 箭头/直线。先取锁内快照，后台/自动识别期间老师可能仍在写下一笔。
        if (cfg.AdvancedCorrectionEnabled && cfg.TextGuardEnabled && engine.Profile.PxPerMm > 0
            && AdvancedCorrector.LooksLikeText(target, engine.SnapshotCurrentPageStrokes(),
                engine.Profile.PxPerMm, cfg.TextGuardSensitivity))
        {
            if (manual) Toast.Show("这笔看起来像写字/公式，没做图形规整。可在设置里关掉「文字防误触」。");
            return;
        }

        // 识别范围：recent 合并最近 N 笔
        StrokeData scope = target;
        List<StrokeData> scopeStrokes = new() { target };
        if (cfg.AiScope == "recent")
            scope = MergeRecentScope(engine, target, Math.Clamp(cfg.AiScopeStrokeCount, 2, 6), out scopeStrokes);

        int timeoutMs = Math.Clamp(cfg.AiRecognizeTimeoutMs, 300, 10000);
        double threshold = Math.Clamp(cfg.AiConfidenceThreshold, 0.30, 0.98);
        var whitelist = ParseShapeWhitelist(cfg.AiShapeWhitelist);

        Task.Run(async () =>
        {
            PythonBridge.RecognizedShape? shape = null;
            bool bridgeOk = true;
            try
            {
                // 手动第一次识别要覆盖 Python 冷启动（低配大屏可达 18~35s）。先单独
                // await 启动，再把用户设置的“识别超时”用于真正的识别阶段；否则 3s
                // 默认超时会在桥还没就绪时把结果误报成“未识别”。
                if (manual) await _svc.PythonBridge.EnsureStartedAsync();
                var recTask = _svc.PythonBridge.RecognizeAsync(scope);
                if (await Task.WhenAny(recTask, Task.Delay(timeoutMs)) == recTask)
                {
                    var r = await recTask;
                    shape = r.Shape;
                    bridgeOk = r.BridgeOk;
                }
                // 超时：桥在跑但没及时回 → 按"未识别"处理（不等待、不卡板书）
            }
            catch { bridgeOk = false; }

            _ = _app.Dispatcher.BeginInvoke(() =>
            {
                if (shape is null)
                {
                    if (manual)
                    {
                        // v1.1.2：桥不可用时给出具体原因（缺 NumPy / 识别模块 / 超时），
                        // 而不是笼统的"子进程未就绪"——用户才知道下一步该做什么。
                        string? reason = _svc.PythonBridge.LastUnavailableReason;
                        Toast.Show(bridgeOk
                            ? "未识别出规整图形（支持：直线 / 虚线 / 点线 / 箭头 / 圆 / 椭圆 / 三角形 / 矩形 / 多边形 / 对勾 / 叉 / 波浪）"
                            : string.IsNullOrWhiteSpace(reason)
                                ? "AI 桥不可用（Python 子进程未就绪，稍后再试）"
                                : "AI 桥不可用：" + reason);
                    }
                    return;   // 自动模式静默：识别失败不打扰课堂
                }
                // 置信度阈值门禁
                if (shape.Confidence < threshold)
                {
                    if (manual)
                        Toast.Show($"识别为「{ShapeName(shape.Shape)}」，但置信度 {shape.Confidence:P0} 低于阈值 {threshold:P0}，未规整。可在设置中调整阈值。");
                    return;
                }
                // 形状白名单门禁
                if (whitelist.Count > 0 && !whitelist.Contains(shape.Shape))
                {
                    if (manual)
                        Toast.Show($"识别为「{ShapeName(shape.Shape)}」，不在你启用的形状里，未规整。");
                    return;
                }
                // Beta 疑惑笔迹：置信度落在区间时保留候选，课后询问；不改变当前是否规整。
                if (cfg.AdvancedCorrectionEnabled && cfg.DoubtReviewEnabled
                    && shape.Confidence >= cfg.DoubtConfidenceLow && shape.Confidence < cfg.DoubtConfidenceHigh)
                    RecordDoubt(engine, target, shape);

                if (!manual && cfg.ShapeAssistMode == "bubble" && !_assistSessionOff)
                    ShowAssistBubble(engine, target, scopeStrokes, shape, ShapeName(shape.Shape));
                else
                    CommitRegularized(engine, target, scopeStrokes, shape, ShapeName(shape.Shape), manual);
            });
        });
    }

    /// <summary>bubble 模式：笔迹包围盒上方弹「规整为」气泡（6s 礼貌退场），确认才替换。
    /// v1.0.0 修复：target.Bounds 是窗口本地坐标，补窗口原点偏移（双屏跑位）。
    /// v1.1.1 修复：本地坐标其实是 3840×2160 墨迹模型坐标 —— 直接当 DIP 用在 1080p
    /// 视口下放大 2 倍，锚点超屏后被钳到最右（气泡恒在右侧而非笔迹上方）。
    /// 现经 CanvasPointToDip 缩放到视口 DIP，再加放映屏原点得屏幕绝对 DIP。</summary>
    private void ShowAssistBubble(InkEngine engine, StrokeData target, List<StrokeData> scopeStrokes,
        PythonBridge.RecognizedShape shape, string name)
    {
        _shapeBubble?.Close();
        var model = target.Bounds;
        var b = _overlay is { } ov
            ? new Rect(ov.CanvasPointToDip(new Point(model.X, model.Y)),
                       ov.CanvasPointToDip(new Point(model.X + model.Width, model.Y + model.Height)))
            : model;
        var origin = Core.ShowScreen.Bounds;   // 舞台恒覆盖放映屏：原点用监视器口径（混合 DPI 下比 Window.Left 可靠）
        b.Offset(origin.Left, origin.Top);
        _shapeBubble = new ShapeAssistBubble(name, shape.Confidence,
            onConfirm: () => CommitRegularized(engine, target, scopeStrokes, shape, name),
            onSessionOff: () => _assistSessionOff = true);
        if (_overlay is { } ovb) ovb.AddToLayer(StageLayer.Ui, _shapeBubble, 0, 0);   // 短命元素：即建即挂
        if (_shapeBubble.ShowNearAnchor(b, ShapeAssistBubble.BubbleWidth, ShapeAssistBubble.BubbleHeight, 20))
            _shapeBubble.AutoDismissIn(6);
        else
        {
            _shapeBubble.Close();
            _shapeBubble = null;
            Toast.Show("识别成功，但没有空位弹提示，先关掉其他浮面板。");
        }
    }

    private StrokeData? CommitRegularized(InkEngine engine, StrokeData target, List<StrokeData> scopeStrokes,
        PythonBridge.RecognizedShape shape, string name, bool manual = false)
    {
        var regularized = new StrokeData(target.Pen);
        foreach (var p in shape.Points) regularized.Add(p);
        // 识别请求发的是合并 scope 的几何（AiScope=recent 多笔组合图形）→ 规整笔必须
        // 替换范围内全部原笔，否则前 N-1 笔手绘原迹留在页上与新图形叠成重影。
        // 单笔范围仍走 ReplaceStroke（保持原 z 位替换语义）。
        bool ok = scopeStrokes.Count > 1
            ? engine.ReplaceStrokeRange(scopeStrokes, target, regularized)
            : engine.ReplaceStroke(target, regularized);
        // 目标可能已被撤销/擦除 → 按实际结果反馈，避免误报「已规整」
        if (ok)
        {
            if (manual || _svc.Config.AiHighlightOnSuccess)
                Toast.Show($"已规整为「{name}」（置信度 {shape.Confidence:P0}）· 可撤销");
            // Beta 多笔几何后处理：在规整笔落页后再分析页面关系（后台，不打断书写）。
            if (_svc.Config.AdvancedCorrectionEnabled)
                RunAdvancedCorrection(engine, regularized);
            return regularized;
        }
        if (manual) Toast.Show("这笔已被撤销或擦掉了。");
        return null;
    }

    /// <summary>识别范围 recent：把页面上最近 N 笔的点合并成一笔（多笔组合图形用）。
    /// 合并后不足 4 点则退回仅识别最后一笔（避免拼出无意义的折线）。
    /// mergedStrokes：参与合并的原笔清单 —— 规整成功后须整批替换（教训：识别只发合并
    /// 几何、回写只换最后一笔，会造成手绘原笔 + 规整笔的重影）。</summary>
    private StrokeData MergeRecentScope(InkEngine engine, StrokeData last, int count, out List<StrokeData> mergedStrokes)
    {
        // 持锁快照（v1.1.0 修复）：停笔排程的识别触发时老师可能已开始写下一笔，
        // 直接枚举 CurrentPage.Strokes 与墨迹线程的抬笔提交并发会抛「集合已修改」。
        var strokes = engine.SnapshotRecentStrokes(count);
        mergedStrokes = new List<StrokeData> { last };
        if (strokes.Count <= 1 || count <= 1) return last;
        var merged = new StrokeData(last.Pen);
        var batch = new List<StrokeData>();
        foreach (var s in strokes)
        {
            batch.Add(s);
            foreach (var p in s.Points) merged.Add(p);
        }
        if (merged.Points.Count < 4) return last;   // 退回单笔：mergedStrokes 保持仅 target
        mergedStrokes = batch;
        return merged;
    }

    /// <summary>解析形状白名单（分号/逗号分隔）。空 = 全部允许。</summary>
    private static HashSet<string> ParseShapeWhitelist(string raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var s in raw.Split(new[] { ';', ',', '；', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            set.Add(s.Trim());
        return set;
    }

    // ═══════════ v1.0.0Beta 高级矫正 / 疑惑笔迹（稳定可选） ═══════════

    private readonly List<(int Slide, StrokeData Stroke, string Shape, double Confidence)> _doubts = new();

    /// <summary>当前待课后确认的疑惑笔迹数。</summary>
    public int DoubtCount => _doubts.Count;

    /// <summary>取出并清空疑惑笔迹（课后询问）。</summary>
    public List<(int Slide, StrokeData Stroke, string Shape, double Confidence)> TakeDoubts()
    {
        var copy = new List<(int, StrokeData, string, double)>(_doubts);
        _doubts.Clear();
        return copy;
    }

    private void RecordDoubt(InkEngine engine, StrokeData stroke, PythonBridge.RecognizedShape shape)
    {
        if (_doubts.Count >= 50) _doubts.RemoveAt(0);
        _doubts.Add((engine.CurrentSlideIndex, stroke, shape.Shape, shape.Confidence));
    }

    /// <summary>后台几何后处理：只读快照，结果回 UI 线程提交到撤销栈。</summary>
    private void RunAdvancedCorrection(InkEngine engine, StrokeData trigger)
    {
        var cfg = _svc.Config;
        double pxPerMm = engine.Profile.PxPerMm;
        if (pxPerMm <= 0 || trigger.Points.Count < 2) return;
        double gapPx = pxPerMm * Math.Clamp(cfg.AdvGapToleranceMm, 0.5, 20);
        double snapPx = pxPerMm * Math.Clamp(cfg.AdvSnapToleranceMm, 1, 30);
        bool doGap = cfg.AdvCloseGapEnabled, doCont = cfg.AdvContinueStrokeEnabled, doHatch = cfg.AdvHatchAlignEnabled;
        int slide = engine.CurrentSlideIndex;
        bool notify = cfg.AiHighlightOnSuccess;

        Task.Run(() =>
        {
            try
            {
            StrokeData[] snapshot = engine.SnapshotCurrentPageStrokes();
            List<AdvancedCorrector.Correction> corrs;
            if (doCont) corrs = AdvancedCorrector.TryContinueStroke(trigger, snapshot, snapPx);
            else corrs = new List<AdvancedCorrector.Correction>();
            if (corrs.Count == 0 && doGap) corrs = AdvancedCorrector.TryCloseGap(trigger, snapshot, gapPx);
            if (corrs.Count == 0 && doHatch) corrs = AdvancedCorrector.TryAlignHatch(trigger, snapshot, gapPx);
            if (corrs.Count == 0) return;

            _ = _app.Dispatcher.BeginInvoke(() =>
            {
                if (engine.CurrentSlideIndex != slide) return;   // 已翻页/换页
                string? desc = null;
                foreach (var c in corrs)
                {
                    if (c.Replacement.Points.Count == 0)
                        engine.RemoveStroke(c.Target);
                    else if (engine.ReplaceStroke(c.Target, c.Replacement) && !string.IsNullOrEmpty(c.Description))
                        desc = c.Description;
                }
                if (notify && desc is not null) Toast.Show(desc + " · 可撤销");
            });
            }
            catch (Exception ex) { AppLog.Instance.Warn($"高级识别矫正失败：{ex.GetType().Name}: {ex.Message}"); }
        });
    }

    /// <summary>形状中文名（与识别引擎 v2.1 的形状集合一一对应）。</summary>
    private static string ShapeName(string? s) => s switch
    {
        "line" => "直线", "dashed" => "虚线", "dotted" => "点线", "arrow" => "箭头",
        "circle" => "圆", "ellipse" => "椭圆", "triangle" => "三角形",
        "quad" => "四边形", "polygon" => "多边形",
        "check" => "对勾", "cross" => "叉", "wave" => "波浪线",
        "heart" => "心形", "star" => "星形", "cloud" => "云朵", "curve" => "自由曲线",
        _ => s ?? "图形",
    };

}
