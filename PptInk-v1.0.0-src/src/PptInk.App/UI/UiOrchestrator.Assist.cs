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

    /// <summary>切换 AI Power（工具栏【AI 规整】按钮 = 开关）。</summary>
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

        // 识别范围：recent 合并最近 N 笔
        StrokeData scope = target;
        if (cfg.AiScope == "recent")
            scope = MergeRecentScope(engine, target, Math.Clamp(cfg.AiScopeStrokeCount, 2, 6));

        int timeoutMs = Math.Clamp(cfg.AiRecognizeTimeoutMs, 300, 10000);
        double threshold = Math.Clamp(cfg.AiConfidenceThreshold, 0.30, 0.98);
        var whitelist = ParseShapeWhitelist(cfg.AiShapeWhitelist);

        Task.Run(async () =>
        {
            PythonBridge.RecognizedShape? shape = null;
            bool bridgeOk = true;
            try
            {
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
                        Toast.Show(bridgeOk
                            ? "未识别出规整图形（支持：直线 / 虚线 / 点线 / 箭头 / 圆 / 椭圆 / 三角形 / 矩形 / 多边形 / 对勾 / 叉 / 波浪）"
                            : "AI 桥不可用（Python 子进程未就绪，稍后再试）");
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
                if (!manual && cfg.ShapeAssistMode == "bubble" && !_assistSessionOff)
                    ShowAssistBubble(engine, target, shape, ShapeName(shape.Shape));
                else
                    CommitRegularized(engine, target, shape, ShapeName(shape.Shape), manual);
            });
        });
    }

    /// <summary>bubble 模式：笔迹包围盒上方弹「规整为」气泡（6s 礼貌退场），确认才替换。
    /// 审查修复：target.Bounds 是墨迹层窗口本地坐标（引擎点原点=窗口左上角），
    /// 原实现直接当屏幕绝对坐标传给 ShowNearAnchor —— 双屏（墨迹层在扩展屏）时
    /// 气泡按主屏原点钳制，弹到完全无关的位置。现补上墨迹层窗口原点偏移。</summary>
    private void ShowAssistBubble(InkEngine engine, StrokeData target, PythonBridge.RecognizedShape shape, string name)
    {
        _shapeBubble?.Close();
        var b = target.Bounds;
        if (_overlay is { } ov) b.Offset(ov.Left, ov.Top);   // 本地 → 屏幕绝对 DIP
        _shapeBubble = new ShapeAssistBubble(name, shape.Confidence,
            onConfirm: () => CommitRegularized(engine, target, shape, name),
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

    private void CommitRegularized(InkEngine engine, StrokeData target, PythonBridge.RecognizedShape shape, string name, bool manual = false)
    {
        var regularized = new StrokeData(target.Pen);
        foreach (var p in shape.Points) regularized.Add(p);
        // 目标可能已被撤销/擦除（IndexOf<0）→ 按实际结果反馈，避免误报「已规整」
        if (engine.ReplaceStroke(target, regularized))
        {
            if (manual || _svc.Config.AiHighlightOnSuccess)
                Toast.Show($"已规整为「{name}」（置信度 {shape.Confidence:P0}）· 可撤销");
        }
        else if (manual)
            Toast.Show("这笔已被撤销或擦掉了。");
    }

    /// <summary>识别范围 recent：把页面上最近 N 笔的点合并成一笔（多笔组合图形用）。
    /// 合并后不足 4 点则退回仅识别最后一笔（避免拼出无意义的折线）。</summary>
    private StrokeData MergeRecentScope(InkEngine engine, StrokeData last, int count)
    {
        var strokes = engine.CurrentPage.Strokes;
        if (strokes.Count <= 1 || count <= 1) return last;
        var merged = new StrokeData(last.Pen);
        int start = Math.Max(0, strokes.Count - count);
        for (int i = start; i < strokes.Count; i++)
            foreach (var p in strokes[i].Points) merged.Add(p);
        return merged.Points.Count >= 4 ? merged : last;
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
