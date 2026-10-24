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
                Toast.Show(on
                    ? "AI 增强已开启：落笔停顿自动规整手绘图形"
                    : "AI 增强已关闭：已释放本地推理进程（板书不受影响）");
        });
    }

    /// <summary>抬笔后排程停顿识别：auto 模式 0.7× 停顿（快，追求无感）；bubble 模式全额停顿后弹确认气泡。
    /// 期间再次落笔（PenDown）即取消——老师连笔书写永不被打扰。</summary>
    private void ScheduleAutoAssist(StrokeData stroke)
    {
        _assistTimer?.Stop();
        if (!_svc.Config.AiEnabled || _assistSessionOff) return;
        if (stroke.Points.Count < 4) return;
        _assistTarget = stroke;
        bool instant = _svc.Config.ShapeAssistMode != "bubble";
        int delay = Math.Max(250, (int)(_svc.Config.ShapeRecognizeDelayMs * (instant ? 0.7 : 1.0)));
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

    /// <summary>统一识别管线（自动停顿 / 工具栏手动共用）：AiEnabled 全链路门禁；失败反馈仅手动触发给出。</summary>
    private void RunAssist(StrokeData target, bool manual)
    {
        if (!_svc.Config.AiEnabled)
        {
            if (manual) Toast.Show("AI 形状规整未启用：设置 → 行为 中开启");
            return;
        }
        var engine = _svc.InkEngine;
        Task.Run(async () =>
        {
            var (shape, bridgeOk) = await _svc.PythonBridge.RecognizeAsync(target);
            _ = _app.Dispatcher.BeginInvoke(() => // fire-and-forget：UI 线程回调，无需等待
            {
                if (shape is null)
                {
                    if (manual)
                        Toast.Show(bridgeOk
                            ? "未识别出规整图形（支持：直线 / 虚线 / 点线 / 箭头 / 圆 / 椭圆 / 三角形 / 矩形 / 多边形 / 对勾 / 叉 / 波浪）"
                            : "AI 桥不可用（Python 子进程未就绪，稍后再试）");
                    return;   // 自动模式静默：识别失败不打扰课堂
                }
                if (!manual && _svc.Config.ShapeAssistMode == "bubble" && !_assistSessionOff)
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
            Toast.Show("AI 识别成功，但当前没有可用的独立提示区域；请先关闭其他浮动面板。");
        }
    }

    private void CommitRegularized(InkEngine engine, StrokeData target, PythonBridge.RecognizedShape shape, string name, bool manual = false)
    {
        var regularized = new StrokeData(target.Pen);
        foreach (var p in shape.Points) regularized.Add(p);
        // 目标可能已被撤销/擦除（IndexOf<0）→ 按实际结果反馈，避免误报「已规整」
        if (engine.ReplaceStroke(target, regularized))
            Toast.Show($"已规整为「{name}」（置信度 {shape.Confidence:P0}）· 可撤销");
        else if (manual)
            Toast.Show("目标笔画已被撤销或擦除，未规整");
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
