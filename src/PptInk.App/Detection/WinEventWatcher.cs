using System.Runtime.InteropServices;
using System.Text;
using PptInk.Core;

namespace PptInk.Detection;

/// <summary>
/// 兜底通道：WinEvent 钩子监听前台窗口变化，识别 PPT 放映特征窗口类名
/// （MS Office: screenClass；WPS: 可配置列表），覆盖 COM 注册失败/权限不一致/
/// Office 未激活/保护模式等场景。同时识别希沃白板/EasiNote 前台 → 触发 S2 避让。
/// </summary>
public sealed class WinEventWatcher : IPresentationChannel
{
    // 放映特征窗口类名（大小写不敏感；WPS 历代版本类名有差异，全部列出）
    private static readonly HashSet<string> SlideShowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenClass",          // MS PowerPoint 放映窗口
        "KPptScreenClass",      // WPS 演示放映（新版）
        "WPPScreenClass",       // WPS 演示放映（旧版）
    };

    // 希沃自家工具前台类名前缀：检测到即触发 ForeignOverlay → S2 隐让，互不打架
    private static readonly string[] ForeignOverlayClassPrefixes =
    {
        "Seewo.",               // 希沃白板 EasiNote / 授课助手
        "EasiNote",
        "SeewoEasiNote",
    };

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private IntPtr _hookFg;
    private IntPtr _hookShow;
    private WinEventProc? _procFg;
    private WinEventProc? _procShow;
    private IntPtr _currentShowHwnd;
    private int _estimatedSlide; // 兜底通道页码推算计数

    public string Name => "WinEvent:screenClass";
    public bool IsHealthy => _hookFg != IntPtr.Zero;

    public event EventHandler<ShowStartedEvent>? Started;
    public event EventHandler<SlideChangedEvent>? SlideChanged;
    public event EventHandler? Ended;
    /// <summary>希沃白板/EasiNote 等前台覆盖：主程序应执行 S2 隐藏逻辑。</summary>
    public event EventHandler? ForeignOverlay;

    public void Start()
    {
        _procFg = OnForegroundChanged;
        _procShow = OnObjectShow;
        _hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _procFg, 0, 0, WINEVENT_OUTOFCONTEXT);
        _hookShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _procShow, 0, 0, WINEVENT_OUTOFCONTEXT);
        AppLog.Instance.Info($"WinEvent 兜底通道已挂载（fg={_hookFg != IntPtr.Zero}, show={_hookShow != IntPtr.Zero}）");
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 /*OBJID_WINDOW*/) return;
        HandleWindowEvent(hwnd);
    }

    private void OnObjectShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return;
        // SHOW 事件只关心放映类窗口的出现（前台切换可能尚未发生）
        var cls = GetClassName(hwnd);
        if (SlideShowClasses.Contains(cls)) HandleWindowEvent(hwnd);
    }

    private void HandleWindowEvent(IntPtr hwnd)
    {
        var cls = GetClassName(hwnd);
        if (string.IsNullOrEmpty(cls)) return;

        if (SlideShowClasses.Contains(cls))
        {
            if (_currentShowHwnd == hwnd) return; // 放映中窗口内部焦点抖动，忽略
            _currentShowHwnd = hwnd;
            _estimatedSlide = 1;
            // 兜底通道无 COM 页码信息：页码从 1 起算，后续翻页由
            // PptController/PageNumberTracker 在发送翻页指令时推算校正
            Started?.Invoke(this, new ShowStartedEvent(_estimatedSlide, 0, Name));
        }
        else if (_currentShowHwnd != IntPtr.Zero)
        {
            if (IsForeignOverlay(cls))
            {
                ForeignOverlay?.Invoke(this, EventArgs.Empty); // → S2 避让（放映会话保留）
                return;
            }
            if (!IsSlideShowStillAlive())
            {
                // 前台已切到普通窗口且原放映窗口销毁 → 放映结束
                _currentShowHwnd = IntPtr.Zero;
                Ended?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>由 PageNumberTracker 回调：兜底模式下我们主动发送翻页指令后的计数推算。</summary>
    public void NotifyManualAdvance(bool forward)
    {
        if (_currentShowHwnd == IntPtr.Zero) return;
        _estimatedSlide = Math.Max(1, _estimatedSlide + (forward ? 1 : -1));
        SlideChanged?.Invoke(this, new SlideChangedEvent(_estimatedSlide, 0, Name + "+count"));
    }

    // 存活判定加可见性（审查修复，v0.1.4「退出放映后黑屏滞留」根因）：
    // PowerPoint 退出放映后放映窗口可能仅隐藏而句柄存活 —— 只查 IsWindow 会永远
    // 判活 → End 永不触发 → 状态机卡 S1 → 墨迹层/引导遮罩/工具栏全套滞留。
    private bool IsSlideShowStillAlive() => _currentShowHwnd != IntPtr.Zero && IsWindow(_currentShowHwnd) && IsWindowVisible(_currentShowHwnd);

    /// <summary>类名是否命中外来覆盖前缀（希沃白板/EasiNote）。公开给状态机的恢复探测
    /// 复核前台 —— 两处判定必须同源（同一前缀表），各自维护会漂移。</summary>
    public static bool IsForeignOverlay(string cls) =>
        ForeignOverlayClassPrefixes.Any(p => cls.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public void Dispose()
    {
        if (_hookFg != IntPtr.Zero) UnhookWinEvent(_hookFg);
        if (_hookShow != IntPtr.Zero) UnhookWinEvent(_hookShow);
        _hookFg = _hookShow = IntPtr.Zero;
        _procFg = _procShow = null; // 防 GC 回收委托导致野回调
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder lpClassName, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
