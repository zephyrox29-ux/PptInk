using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using MoonSharp.Interpreter;
using PptInk.Bridge;
using PptInk.Builtin;
using PptInk.Ink;
using PptInk.UI;

namespace PptInk.Plugins;

/// <summary>
/// Lua 脚本插件：MoonSharp 沙箱执行。
/// 安全与隔离：
///  - 沙箱禁用 IO/OS 危险子集（MoonSharp 默认不含 os/io，除非显式注册）；
///  - 仅注入 manifest 声明权限对应的 API；
///  - 通过 MoonSharp 自动让出协程 + 宿主指令/时间预算限制无限循环；超限时停止计时器并隔离脚本。
/// </summary>
public sealed class ScriptPlugin : IPlugin
{
    private readonly string _dir;
    private Script? _script;
    private IPluginHostApi? _api;
    private bool _faulted;

    // HardSandbox removes dangerous standard libraries, but does not itself stop an infinite loop.
    // MoonSharp's coroutine AutoYieldCounter lets the host enforce a bounded instruction/time budget.
    private const long InstructionsPerSlice = 10_000;
    private const int MaxExecutionSlices = 32;
    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromMilliseconds(160);

    public PluginManifest Manifest { get; }
    public bool IsFaulted => _faulted;

    public ScriptPlugin(PluginManifest manifest, string dir)
    {
        Manifest = manifest;
        _dir = dir;
    }

    public void Activate(IPluginHostApi api)
    {
        // v0.8.1 生命周期修复：重复激活（启停再启用）时，旧 HostApiBridge 的 DispatcherTimer
        // 仍持有旧脚本引用继续空转（PluginManager.Activate 每次 new 一个 bridge，旧的无人回收）。
        // 激活即接管：先停旧 API 计时器并收回其面板，再绑定新 API。
        try { _api?.StopAllTimers(); if (_api is HostApiBridge oldBridge) oldBridge.ClosePanelFromHost(); else _api?.ClosePanel(); } catch { }
        _faulted = false;
        _api = api;
        _script = new Script(CoreModules.Preset_HardSandbox); // 最严格预设：无 io/os/system（课堂沙箱）

        if (api is HostApiBridge bridge) bridge.SetOwner(this);

        // API 白名单注入：脚本里看到的全局函数只有这些
        _script.Globals["setPen"] = (Action<string, double, double>)api.SetPen;
        _script.Globals["drawPolyline"] = (Action<DynValue[]>)DrawPolylineShim;
        _script.Globals["drawText"] = (Action<string, double, double, double>)api.DrawText;
        _script.Globals["eraseBehavior"] = (Action<string>)api.RequestEraseBehavior;
        _script.Globals["startTimer"] = (Action<double, string>)api.StartTimer;
        _script.Globals["stopTimers"] = (Action)api.StopAllTimers;
        _script.Globals["showToast"] = (Action<string>)api.ShowToast;
        _script.Globals["closePanel"] = (Action)api.ClosePanel;
        // 审查修复：updatePanelElement 原先从未注入 —— 脚本一调用即 nil-call 崩溃，
        // timer 等面板插件激活即报错、显示永不更新。
        _script.Globals["updatePanelElement"] = (Action<string, string>)api.UpdatePanelElement;

        if (Manifest.Permissions.Contains("clipboard"))
        {
            _script.Globals["readClipboard"] = (Func<string?>)api.ReadClipboard;
            _script.Globals["writeClipboard"] = (Action<string>)api.WriteClipboard;
        }
        if (Manifest.Permissions.Contains("file"))
            _script.Globals["captureScreen"] = (Action<string>)api.CaptureScreen;

        // MoonSharp 2.0 无内建脚本大小限制选项：读入后显式校验（防超大脚本拖垮沙箱）
        var entry = File.ReadAllText(Path.Combine(_dir, Manifest.Entry));
        if (entry.Length > 256 * 1024) throw new InvalidOperationException($"插件脚本过大（>256KB）：{Manifest.Id}");

        _script.Options.DebugPrint = null;        // 沙箱内 print() 静默（课堂无控制台，防刷屏）
        _script.Options.CheckThreadAccess = true; // 仅允许创建线程进入（防跨线程滥用）
        // Compile first, then run the chunk through a host-controlled coroutine. Calling DoString
        // directly would let `while true do end` monopolize the UI thread indefinitely.
        ExecuteBounded(_script, _script.LoadString(entry, null, Manifest.Entry));

        // PANEL 类（审查修复）：manifest.panel.elements 原先无任何宿主渲染——面板从未出现。
        // 现按声明创建宿主面板窗口（先就位，onActivate 里的 updatePanelElement 才有落点）。
        if (Manifest.Panel is not null && api is HostApiBridge hostBridge)
            hostBridge.ShowPanel(this);

        // 按插件类型调用与契约一致的入口：面板类是 onActivate，自由/绘制类是 onStart。
        // 旧代码一律调用 onActivate，导致 FREE 插件的 onStart 永远不执行，插件看似启用却无行为。
        var startupCallback = _script.Globals.Get(Manifest.Kind == PluginKind.Panel ? "onActivate" : "onStart");
        if (startupCallback.Type == DataType.Function) ExecuteBounded(_script, startupCallback);
    }

    /// <summary>面板 UI → 沙箱回调路由（宿主窗口元素事件触发脚本函数，UI 线程调用）。</summary>
    internal void InvokeCallback(string name, double? arg)
    {
        var script = _script;
        if (script is null || _faulted) return;
        try
        {
            var fn = script.Globals.Get(name);
            if (fn.Type != DataType.Function) return;
            ExecuteBounded(script, fn,
                arg is { } d ? MoonSharp.Interpreter.DynValue.NewNumber(d) : MoonSharp.Interpreter.DynValue.Nil);
        }
        catch (TimeoutException ex) { FaultPlugin(ex, $"回调 {name}"); }
        catch (Exception ex) { Core.AppLog.Instance.Warn($"插件 {Manifest.Id} 回调 {name} 异常：{ex.Message}"); }
    }

    internal bool InvokeTimerCallback(string name)
    {
        var script = _script;
        if (script is null || _faulted) return false;
        try
        {
            var fn = script.Globals.Get(name);
            if (fn.Type != DataType.Function) return false;
            ExecuteBounded(script, fn);
            return !_faulted;
        }
        catch (TimeoutException ex) { FaultPlugin(ex, $"计时器 {name}"); return false; }
        catch (Exception ex)
        {
            Core.AppLog.Instance.Warn($"插件 {Manifest.Id} 计时器 {name} 异常：{ex.Message}");
            return false;
        }
    }

    private static DynValue ExecuteBounded(Script script, DynValue function, params DynValue[] args)
    {
        var coroutine = script.CreateCoroutine(function);
        coroutine.Coroutine.AutoYieldCounter = InstructionsPerSlice;
        var stopwatch = Stopwatch.StartNew();
        var result = coroutine.Coroutine.Resume(args);
        var slices = 0;
        while (result.Type == DataType.YieldRequest)
        {
            if (++slices >= MaxExecutionSlices || stopwatch.Elapsed >= ExecutionBudget)
                throw new TimeoutException($"Lua 执行超过预算（{ExecutionBudget.TotalMilliseconds:0}ms / 最多 {MaxExecutionSlices * InstructionsPerSlice:N0} 条指令）");
            result = coroutine.Coroutine.Resume();
        }
        // Some host callbacks may be synchronous and non-yieldable; also check wall time after
        // completion. This is best-effort protection, not a substitute for isolating native APIs.
        if (stopwatch.Elapsed >= ExecutionBudget)
            throw new TimeoutException($"Lua 执行超过时间预算（{ExecutionBudget.TotalMilliseconds:0}ms）");
        return result;
    }

    private void FaultPlugin(Exception ex, string context)
    {
        if (_faulted) return;
        _faulted = true;
        // Null the script before closing the panel: closing invokes the user-close callback, which
        // must become a no-op rather than re-entering a runaway script during fault recovery.
        _script = null;
        try { _api?.StopAllTimers(); } catch { }
        try { _api?.ClosePanel(); } catch { }
        Core.AppLog.Instance.Error(ex, $"插件执行预算耗尽，已隔离：{Manifest.Id} / {context}");
        try { Toast.Show($"插件「{Manifest.Name}」执行超时，已自动停用"); } catch { }
    }

    private void DrawPolylineShim(DynValue[] pts)
    {
        if (pts is null || pts.Length == 0) return;
        // Script input is untrusted: malformed Lua tables and oversized arrays are skipped rather
        // than allowing a host delegate exception to disrupt the plugin event/UI pipeline.
        var list = new List<(double, double)>(Math.Min(pts.Length, StrokeData.MaxPoints));
        foreach (var item in pts.Take(StrokeData.MaxPoints))
        {
            if (item is null || item.Type != DataType.Table) continue;
            var x = item.Table.Get(1);
            var y = item.Table.Get(2);
            if (x.Type != DataType.Number || y.Type != DataType.Number) continue;
            if (!double.IsFinite(x.Number) || !double.IsFinite(y.Number) ||
                Math.Abs(x.Number) > 10_000_000 || Math.Abs(y.Number) > 10_000_000) continue;
            list.Add((x.Number, y.Number));
        }
        if (list.Count > 0) _api?.DrawPolyline(list.ToArray(), antialias: true, tapered: true);
    }

    public void Deactivate()
    {
        var script = _script;
        try
        {
            _api?.StopAllTimers();
            // Host deactivation must not trigger the user-close callback and call onDeactivate a
            // second time. User-initiated close still uses ClosePanel() and keeps its own semantics.
            if (_api is HostApiBridge bridge) bridge.ClosePanelFromHost();
            else _api?.ClosePanel();
            var onDeactivate = script?.Globals.Get("onDeactivate");
            if (script is not null && !_faulted && onDeactivate is { Type: DataType.Function })
                ExecuteBounded(script, onDeactivate);
        }
        catch (TimeoutException ex) { Core.AppLog.Instance.Warn($"插件 {Manifest.Id} 停用回调超时：{ex.Message}"); }
        catch (Exception ex) { Core.AppLog.Instance.Warn($"插件 {Manifest.Id} 停用异常：{ex.Message}"); }
        finally { _script = null; _api = null; }
    }
}

/// <summary>
/// 宿主 API 实现：绘制 SDK 直通 InkEngine，计时器在 UI 线程 Dispatcher 上调度。
/// </summary>
internal sealed class HostApiBridge : IPluginHostApi
{
    private readonly InkEngine _ink;
    private readonly PythonBridge _ai;
    private readonly PluginManifest _manifest;
    private readonly List<DispatcherTimer> _timers = new();
    private ScriptPlugin? _ownerPlugin;

    public HostApiBridge(InkEngine ink, PythonBridge ai, PluginManifest manifest)
    {
        _ink = ink;
        _ai = ai;
        _manifest = manifest;
    }

    /// <summary>脚本插件激活后回填宿主引用（计时器回调要重新进入沙箱）。</summary>
    public void SetOwner(ScriptPlugin plugin) => _ownerPlugin = plugin;

    public void SetPen(string colorHex, double thicknessMm, double alpha)
    {
        // Plugin input is untrusted. Normalize color and clamp dimensions before these values
        // reach WPF geometry/rendering; malformed script arguments must not break the stage.
        string safeColor = _ink.Pen.ColorHex;
        try
        {
            if (!string.IsNullOrWhiteSpace(colorHex))
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
                safeColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }
        catch { Core.AppLog.Instance.Warn($"插件 {_manifest.Id} 提供了非法颜色，已保留当前颜色"); }

        double safeThickness = double.IsFinite(thicknessMm)
            ? Math.Clamp(thicknessMm, 0.1, 40.0)
            : _ink.Pen.ThicknessMm;
        double safeAlpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0.0, 1.0) : 1.0;
        _ink.Pen = _ink.Pen with
        {
            ColorHex = safeColor,
            ThicknessMm = safeThickness,
            Highlighter = false,
            AlphaOverride = (byte)Math.Round(safeAlpha * 255.0),
        };
    }

    public void DrawPolyline((double X, double Y)[] points, bool antialias, bool tapered)
    {
        // DRAW SDK：以插件笔属性直接落一笔矢量墨迹（入历史栈、可撤销、可导出）。
        // 经引擎提交（持锁 + 通知重绘），而非直接操作历史树。
        if (points is null || points.Length == 0) return;
        var finite = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
                                       Math.Abs(p.X) <= 10_000_000 && Math.Abs(p.Y) <= 10_000_000)
                           .Take(StrokeData.MaxPoints).ToArray();
        if (finite.Length == 0) return;
        var stroke = new StrokeData(_ink.Pen with { Tapered = tapered });
        long t = InkClock.NowMs();
        for (int i = 0; i < finite.Length; i++)
            stroke.Add(new InkPoint(finite[i].X, finite[i].Y, 0.6, t + i * 4L));
        _ink.CommitExternalStroke(stroke);
    }

    public void DrawText(string text, double x, double y, double sizeMm)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(sizeMm)) return;
        var safeText = string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(text.Length, 300)];
        ShowToast($"[DrawText] {safeText} @({x:F0},{y:F0}) {Math.Clamp(sizeMm, 0.1, 100.0):F1}mm"); // 文本层绘制走 WPF GlyphRun，M5 扩展
    }

    public void RequestEraseBehavior(string mode) => _ink.Tool = mode switch
    {
        "stroke" => InkTool.EraseStroke,
        "region" or "lasso" => InkTool.EraseLasso,
        "clear" => InkTool.EraseClearSwipe,   // 插件可直接请求"滑动清空"手势模式（一挥清页、可撤销）
        _ => _ink.Tool,
    };

    // ———————— 面板（PANEL 类，审查修复：原先 Toast 占位 + 从不创建窗口） ————————

    private PluginPanelElement? _panel;   // v0.9.0 舞台 Plugin 层元素

    /// <summary>按 manifest.panel 声明创建宿主面板（ScriptPlugin.Activate 在 DoString 后调用）。
    /// 用户点面板「×」关闭 → 停计时器 + 回调脚本 onDeactivate（面板与脚本生命周期同步收口）。</summary>
    public void ShowPanel(ScriptPlugin owner)
    {
        if (_manifest.Panel is not { } decl) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            // 重复激活接管：先收旧面板（Dismiss 走 onClosedByUser 收口，v0.8.1 语义）
            _panel?.Dismiss();
            _panel = StageHost.Mount(new PluginPanelElement(_manifest.Name, decl, owner.InvokeCallback, OnPanelClosedByUser));
            _panel.ShowPanel();
        });
    }

    private void OnPanelClosedByUser()
    {
        // Detach first: onDeactivate may call closePanel again; it must become an idempotent no-op.
        _panel = null;
        StopAllTimers();
        // Route through ScriptPlugin's bounded executor. Calling Script.Call directly here
        // bypassed the execution budget and could hang the UI when a panel was closed.
        try { _ownerPlugin?.InvokeCallback("onDeactivate", null); } catch { }
    }

    public void UpdatePanelElement(string elementId, string value)
    {
        var w = _panel;
        if (w is null) { ShowToast($"{elementId}: {value}"); return; }   // 无面板（DRAW/FREE 类脚本误用）→ 降级提示
        w.UpdateText(elementId, value);
    }

    public void ClosePanel()
    {
        var w = _panel;
        _panel = null;
        if (w is null) return;
        if (w.Dispatcher.CheckAccess()) w.Dismiss();
        else w.Dispatcher.Invoke(w.Dismiss);
    }

    internal void ClosePanelFromHost()
    {
        var w = _panel;
        _panel = null;
        if (w is null) return;
        if (w.Dispatcher.CheckAccess()) w.Close();
        else w.Dispatcher.Invoke(w.Close);
    }

    public void StartTimer(double intervalSeconds, string callback)
    {
        if (!double.IsFinite(intervalSeconds) || string.IsNullOrWhiteSpace(callback)) return;
        if (_timers.Count >= 32)
        {
            Core.AppLog.Instance.Warn($"插件 {_manifest.Id} 定时器数量达到上限（32）");
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 0.05, 86_400.0)) };
        timer.Tick += (_, _) =>
        {
            var owner = _ownerPlugin;
            if (owner is null || !owner.InvokeTimerCallback(callback))
            {
                timer.Stop();
                _timers.Remove(timer);
            }
        };
        timer.Start();
        _timers.Add(timer);
    }

    public void StopAllTimers()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
    }

    public string? ReadClipboard() =>
        TryClipboard(() => System.Windows.Clipboard.GetText());

    public void WriteClipboard(string text) =>
        TryClipboard(() => { System.Windows.Clipboard.SetText(text); return 0; });

    public void CaptureScreen(string savePath)
    {
        // The file capability grants screenshot export, not arbitrary file overwrite. Keep
        // all plugin output inside its own LocalAppData sandbox even if a script supplies an
        // absolute path or a traversal sequence.
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PptInk", "plugin-data", _manifest.Id);
            Directory.CreateDirectory(root);
            var name = Path.GetFileName(string.IsNullOrWhiteSpace(savePath) ? "capture.png" : savePath);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "capture.png";
            if (string.IsNullOrEmpty(Path.GetExtension(name))) name += ".png";
            var fullPath = Path.GetFullPath(Path.Combine(root, name));
            var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            ScreenCapture.CaptureActiveShow(fullPath);
        }
        catch (Exception ex) { Core.AppLog.Instance.Warn($"插件 {_manifest.Id} 截图失败：{ex.Message}"); }
    }

    public void ShowToast(string message) =>
        Toast.Show(message);

    private static T? TryClipboard<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }
}
