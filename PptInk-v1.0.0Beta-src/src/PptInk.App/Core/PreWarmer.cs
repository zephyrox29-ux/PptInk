using System.Windows.Threading;
using PptInk.UI;

namespace PptInk.Core;

/// <summary>
/// 唤起预热：UI 唤起前墨迹引擎与窗口资源已低开销完成预热，
/// 保证 S0→S1 切换 &lt;300ms 即可书写、第一笔零延迟。
/// </summary>
public sealed class PreWarmer
{
    private readonly UiOrchestrator _ui;
    private bool _warmed;

    public PreWarmer(UiOrchestrator ui) => _ui = ui;

    /// <summary>后台线程执行，避免拖慢启动。WPF 元素创建封送回 UI 线程。</summary>
    public void WarmUp()
    {
        if (_warmed) return;
        _warmed = true;

        var uiThread = Task.Factory.StartNew(() =>
        {
            try
            {
                _ui.PreCreateWindows();            // 置顶透明窗口、工具栏、浮岛：Hide 状态预创建
                _ui.PreWarmRenderTarget();         // 4K 分层缓存位图 + 脏矩形管线
                JitWarmRenderingPath();            // 预热平滑/贝塞尔数学路径，触发 JIT
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error(ex, "预热失败（不影响功能，仅首次唤起稍慢）");
            }
        }, TaskCreationOptions.LongRunning);
    }

    /// <summary>预执行一次纯托管数学路径，让 JIT 产出本机代码（首次笔迹不再卡 50~100ms 编译尖刺）。</summary>
    private static void JitWarmRenderingPath()
    {
        var f = new Ink.SmoothingFilter(alpha: 0.45);
        Span<Ink.InkPoint> pts = stackalloc Ink.InkPoint[4];
        for (int i = 0; i < 4; i++) pts[i] = new Ink.InkPoint(i * 12.0, i * 7.0, 0.5, Environment.TickCount64);
        foreach (var p in pts) f.Feed(p);
        _ = f.EmitSmoothed();
    }
}
