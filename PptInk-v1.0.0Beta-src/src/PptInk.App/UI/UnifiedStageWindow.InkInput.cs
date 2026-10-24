using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;

namespace PptInk.UI;

/// <summary>统一舞台窗口 · 墨迹层输入：通道 A（原生 WM_POINTER）/ 通道 B（StylusPlugIn 墨迹线程）/ 通道 C（鼠标兜底）+ 会话锁与压感探测。（v0.9.2 拆分，纯搬运）</summary>
public sealed partial class UnifiedStageWindow
{
    // ———————— 通道 A：原生 WM_POINTER 输入（UI 线程） ————————
    // 坐标链：ptPixelLocation（屏幕物理 px）→ ÷DpiScale → 屏幕 DIP → −窗口 Left/Top → 窗口本地 DIP。
    // 会话仲裁：0=空闲，1=C 鼠标，2=B StylusPlugIn，3=A WM_POINTER，4=收笔提交中，5=舞台停用。
    // 收笔期间先占用 4，完成引擎 Up 后再释放；Deactivate 写入 5 后，任何迟到事件都不能复活会话。
    //
    // v0.8.2 现实认知（存档定论）：Win32 只向调用过 EnableMouseInPointer 的进程投递
    // WM_POINTER* —— 本进程从未调用（也绝不能：它会把鼠标一并转为 pointer 消息，
    // WPF 未开 EnablePointerSupport 收不到合成鼠标 → 工具栏全灭）。故通道 A 在标准
    // Windows 上收不到消息，实际兜底 = 通道 B（StylusPlugIn）+ 通道 C（合成鼠标）。
    // 保留 A：若现场环境因 UWP 宿主等原因全局启用了 pointer 路由，此通道零成本接管。

    private uint _pointerSessionId;      // 持有会话的指针 id（0=无）；多指场景只跟踪首指
    private uint _blockedPointerId;      // Down 命中 UI 的触点：整轮忽略到 Up，不可滑出 UI 后落墨
    private uint _rejectedPointerId;    // v0.4.0：被手掌抑制拒绝的指针 id（跟踪其接触轨迹供诊断可视化）
    private bool _sessionFirstFeed;     // 每会话首笔诊断（一次性，不刷屏）
    private readonly POINTER_INFO[] _histBuf = new POINTER_INFO[64];   // 历史点缓冲（复用零分配）

    // —— 输入能力探测（v0.4.0「从下往上兼容」核心）——
    // 保底链路 L0：坐标点流 + 恒 0.5 中性压感 + 速度笔锋 —— 传感能力最低的触摸屏
    // （无压感、无接触元数据）也始终流畅书写，这是<b>不可降级的底线</b>；
    // 升级链路 L2：探测到驱动报告<b>真实压感</b> → 压感参与 ±30% 宽度调制
    // （BuildWidths 的 0.7+0.6×p 公式天然从下往上：0.5 → ×1.0 中性，真压感即插即用）。
    // 证据门槛（保守，防垃圾数据升级）：同一会话内压感值跨度 >15%（/1024）且出现
    // ≥3 个显著档位 → 认定「设备有真实压感」；认定按 (指针类型, 设备句柄) 进程级记忆，
    // 每会话独立重验（新会话重新累积证据，已认定设备直接启用）。
    // 漏读/异常值一律回退 0.5 中性 —— 压感只是锦上添花的调制项，速度链路永远保底。
    private readonly HashSet<(int Type, long Device)> _pressureProven = new();
    private uint _sessPressureMin, _sessPressureMax;   // 本会话压力原始值范围（0~1024）
    private int _sessPressureSteps;                    // 显著档位计数
    private double _currentPressure = 0.5;             // 会话当前生效压感（归一化）

    // Win32 指针类型（v0.3.1 手写最优：PT_PEN 与 PT_TOUCH 同路处理 ——
    // 主动笔不在产品目标内；被动手写笔/手指在系统层面同为触摸点）
    private const int PT_PEN = 1, PT_TOUCH = 2, PT_MOUSE = 4;

    // WM_POINTER 消息族（Win8+ 统一指针输入，通道 A 主力；正确值自 InkOverlayWindow 定论）
    private const int WM_POINTERUPDATE = 0x0245;
    private const int WM_POINTERDOWN = 0x024B;
    private const int WM_POINTERUP = 0x024C;

    /// <summary>输入就绪硬保底（v0.5.0，每个书写会话首笔执行）：
    ///  1) 穿透位（WS_EX_TRANSPARENT）被外部改写残留 → 直接摘除（不依赖
    ///     WindowInputGuard 事件回滚——守卫对象创建失败/被绕过时依然自愈）；
    ///  2) 被放映窗口压住（PowerPoint 重申置顶后来居上）→ SetWindowPos 重申最顶。
    /// 任一修复都记日志，现场定位「为什么第一笔没生效」不再靠猜。</summary>
    private void EnsureInputReady()
    {
        try
        {
            if (_source is not { } src) return;
            IntPtr h = src.Handle;
            int ex = GetWindowLongW(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TRANSPARENT) != 0)
            {
                SetWindowLongW(h, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
                _svc.Log.Warn("输入保底：穿透位仍挂着（外部改写样式未及时回滚），已现场摘除");
            }
            if (IsCoveredByShowWindow(h))
            {
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _svc.Log.Warn("输入保底：统一舞台被放映窗口覆盖，已重申置顶");
            }
        }
        catch { /* 保底自检异常绝不阻断输入 */ }
    }

    private void HandlePointer(uint pid, int msg)
    {
        try
        {
            if (!GetPointerInfo(pid, out var pi)) return;
            if (pi.pointerType == PT_MOUSE) return;   // 鼠标走通道 C

            // UI 命中快照与 B 通道共用同一判据。被 UI 接收的 pointer 整轮被屏蔽，
            // 即使系统随后继续把 Move/Up 发给全屏舞台，也不会给引擎创建或续写笔划。
            if (_blockedPointerId == pid)
            {
                if (msg == WM_POINTERUP) _blockedPointerId = 0;
                return;
            }
            if (msg == WM_POINTERDOWN)
            {
                var local = ScreenPxToWindowDip(pi.ptPixelLocationX, pi.ptPixelLocationY);
                if (IsInkStartBlockedByUi(local.X, local.Y))
                {
                    _blockedPointerId = pid;
                    return;
                }
            }

            switch (msg)
            {
                case WM_POINTERDOWN:
                {
                    if (_pointerSessionId != 0) return;          // 已有指针会话（多指第二点忽略）
                    // v0.4.0 从下往上：读取驱动实际报告的原始压感与接触矩形 ——
                    // PT_PEN（含带真实压感的手写笔）与 PT_TOUCH 同路判定，
                    // 谁有真实压感谁升级 L2 调制；无压感设备恒 0 → 探测自然回落 L0 保底
                    // v0.4.1：PEN/TOUCH 查询改 else-if（Win32 语义保证 GetPointerTouchInfo
                    // 只对 PT_TOUCH 成功，原顺序写法对 PEN 多一次必失败的调用）
                    uint rawPressure = 0;
                    var contact = Rect.Empty;                     // 无效矩形 = 无元数据 → 判定放行
                    if (pi.pointerType == PT_PEN)
                    {
                        if (GetPointerPenInfo(pid, out var pen))
                            rawPressure = pen.pressure;           // PEN 压感 0~1024（无压感笔恒 0）
                    }
                    else if (GetPointerTouchInfo(pid, out var ti))
                    {
                        rawPressure = ti.pressure;                // TOUCH 压感（多数屏恒 0=未提供）
                        var r = ti.rcContact;
                        if (r.right > r.left && r.bottom > r.top)
                            contact = new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
                    }
                    bool accept = _svc.PalmRejection.ShouldAcceptInk(contact);
                    ResetSessionProbe();
                    LogFirstFeed("A:WM_POINTER", pi.pointerType, contact, accept, rawPressure);
                    if (!accept)
                    {
                        // v0.4.0 拒绝跟踪：手掌指针的接触轨迹继续可视化（诊断红框），
                        // 不喂引擎 —— 手掌抑制的「误画可擦」纪律不受影响
                        _rejectedPointerId = pid;
                        // v0.6.0：同步锁死触摸所有权 —— 该触摸提升出的合成鼠标（通道 C）
                        // 一并拒绝（见 OnMouseInkDown 注释），手掌判定不得被鼠标形态绕过
                        _touchOwnedUntil = Environment.TickCount64 + 800;
                        return;
                    }
                    if (Interlocked.CompareExchange(ref _inputSession, 3, 0) != 0) return;   // B/C 持有
                    _pointerSessionId = pid;
                    _rejectedPointerId = 0;
                    double pressure = ResolvePressure(pi.pointerType, pi.sourceDevice, rawPressure);
                    ReportSessionBegin(pi, contact, rawPressure, pressure, accept);
                    Feed(pi, pressure, FeedKind.Down);
                    break;
                }
                case WM_POINTERUPDATE:
                {
                    // 手掌拒绝指针：只更新接触实况（红框跟随），不产生墨迹
                    if (_rejectedPointerId == pid)
                    {
                        var (rc, _) = ReadCurrentContact(pid, pi.pointerType);
                        InputDiagnostics.UpdateRejected(!rc.IsEmpty, rc.X, rc.Y, rc.Width, rc.Height,
                            _svc.PalmRejection.LastVerdict, _svc.PalmRejection.LastAccepted);
                        return;
                    }
                    if (_pointerSessionId != pid) return;        // 只跟踪本会话指针
                    // 当前接触/压感实况（历史点无独立压感，用当前值近似连续 —— 压感仅
                    // ±30% 调制，近似误差对笔锋无感知影响）
                    var (curContact, curRaw) = ReadCurrentContact(pid, pi.pointerType);
                    double pressure = ResolvePressure(pi.pointerType, pi.sourceDevice, curRaw);
                    // 历史点批量补发：UI 消息泵频率低于报点率时，一条 UPDATE 里带多个历史点
                    uint count = (uint)_histBuf.Length;
                    if (pi.historyCount > 1 && GetPointerInfoHistory(pid, ref count, _histBuf))
                    {
                        InputDiagnostics.NoteHistory((int)pi.historyCount);
                        for (int i = 0; i < (int)count; i++)
                            Feed(_histBuf[i], pressure, FeedKind.Move);
                    }
                    else
                        Feed(pi, pressure, FeedKind.Move);
                    bool pActive = curRaw > 0 && _pressureProven.Contains((pi.pointerType, pi.sourceDevice.ToInt64()));
                    InputDiagnostics.UpdateSession((int)Math.Max(1, pi.historyCount), !curContact.IsEmpty,
                        curContact.X, curContact.Y, curContact.Width, curContact.Height, curRaw, pressure, pActive);
                    break;
                }
                default: // WM_POINTERUP
                {
                    if (_rejectedPointerId == pid) { _rejectedPointerId = 0; InputDiagnostics.EndSession(); return; }
                    if (_pointerSessionId != pid) return;
                    _pointerSessionId = 0;
                    // 先进入收笔临界区，待引擎完成 Up 后才允许下一会话抢占。
                    // 若舞台已停用，5 哨兵会令此 CAS 失败，绝不重新喂入隐藏画布。
                    if (Interlocked.CompareExchange(ref _inputSession, 4, 3) != 3)
                    {
                        InputDiagnostics.EndSession();
                        return;
                    }
                    try
                    {
                        Feed(pi, _currentPressure, FeedKind.Up); // 会话最后压感（连续性优于硬 0.5）
                        InputDiagnostics.EndSession();
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _inputSession, 0, 4);
                    }
                    break;
                }
            }
        }
        catch { /* 单条消息异常不中断输入泵 */ }
    }

    /// <summary>读指针当前接触矩形与原始压感（UPDATE 时刻实况；驱动不提供则空/0）。
    /// v0.4.1：PEN/TOUCH 查询改 else-if（同 HandlePointer，省一次必失败调用）。</summary>
    private (Rect Contact, uint RawPressure) ReadCurrentContact(uint pid, int pointerType)
    {
        var contact = Rect.Empty;
        uint raw = 0;
        if (pointerType == PT_PEN)
        {
            if (GetPointerPenInfo(pid, out var pen)) raw = pen.pressure;
        }
        else if (GetPointerTouchInfo(pid, out var ti))
        {
            raw = ti.pressure;
            var r = ti.rcContact;
            if (r.right > r.left && r.bottom > r.top)
                contact = new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }
        return (contact, raw);
    }

    /// <summary>新会话重置探测统计（跨会话保留「已认定设备」记忆）。</summary>
    private void ResetSessionProbe()
    {
        _sessPressureMin = _sessPressureMax = 0;
        _sessPressureSteps = 0;
        _currentPressure = 0.5;
    }

    /// <summary>
    /// 压感解析（从下往上兼容核心）：驱动原始压感（0~1024，0=未提供）→ 归一化压感。
    /// 未认定设备恒 0.5 中性（速度笔锋完全主导 —— L0 保底）；会话内证据凑足
    /// （跨度 &gt;15% 且 ≥3 档）→ 认定并即时启用；已认定设备直接用原始值。
    /// 任何异常输入回退中性，绝不因压感数据质量问题断笔。
    /// </summary>
    private double ResolvePressure(int pointerType, IntPtr device, uint raw)
    {
        if (raw == 0 || raw > 1024) return 0.5;                    // 未提供/越界：中性保底
        var key = (pointerType, device.ToInt64());
        if (!_pressureProven.Contains(key))
        {
            if (_sessPressureMin == 0)
            {
                _sessPressureMin = raw; _sessPressureMax = raw;
                return 0.5;                                        // 首个样本：仅建基线
            }
            uint newMin = Math.Min(_sessPressureMin, raw);
            uint newMax = Math.Max(_sessPressureMax, raw);
            bool significant = (newMax - newMin) / 1024.0 > 0.15;  // 显著档位（跨度 >15%）
            if (newMin < _sessPressureMin || newMax > _sessPressureMax) _sessPressureSteps++;
            _sessPressureMin = newMin; _sessPressureMax = newMax;
            if (significant && _sessPressureSteps >= 3)
            {
                _pressureProven.Add(key);
                _svc.Log.Info($"压感探测：{(pointerType == PT_PEN ? "笔" : "触摸")}设备报告真实压感" +
                              $"（本会话范围 {_sessPressureMin}~{_sessPressureMax}），已启用 L2 压感调制");
                InputDiagnostics.Note($"压感能力升级：{(pointerType == PT_PEN ? "PT_PEN" : "PT_TOUCH")} 真实压感确认，L2 调制生效");
            }
            else return 0.5;                                       // 证据未凑足：维持中性
        }
        _currentPressure = Math.Clamp(raw / 1024.0, 0.05, 1.0);
        return _currentPressure;
    }

    /// <summary>通道 A 会话诊断上报（占位成功后）。</summary>
    private void ReportSessionBegin(in POINTER_INFO pi, Rect contact, uint rawPressure, double pressureNorm, bool accepted)
    {
        bool pActive = rawPressure > 0 && _pressureProven.Contains((pi.pointerType, pi.sourceDevice.ToInt64()));
        InputDiagnostics.BeginSession("A:WM_POINTER", pi.pointerType, (uint)pi.pointerId,
            !contact.IsEmpty, contact.X, contact.Y, contact.Width, contact.Height,
            _svc.PalmRejection.LastVerdict, accepted, rawPressure, pressureNorm, pActive);
    }

    private enum FeedKind { Down, Move, Up }

    /// <summary>窗口自身 device→dip 变换系数（PerMonitorV2 实时值，DPI 变化即随 WPF 重排更新）。
    /// 窗口未挂源（关闭中）回退 ShowScreen 口径。</summary>
    private double DevicePerDip =>
        _source?.CompositionTarget?.TransformToDevice.M11 is double m and > 0.1 ? m : Core.ShowScreen.DpiScale;

    /// <summary>屏幕物理 px → 窗口本地 DIP（ptPixelLocation 是 PerMonitorV2 物理 px）。
    /// v0.4.0 健康修复：原用 ShowScreen.DpiScale（500ms 轮询驱动，存在陈旧窗口期）→
    /// 墨迹与指尖错位。改用窗口自身 TransformToDevice：与 WPF 窗口坐标系永远一致 ——
    /// RDP 重连 / 放映中改缩放 / 跨屏拖动全部即时刻度自洽。_inkLayer 铺满窗口且
    /// 无偏移，窗口本地 DIP == 墨迹层本地 DIP。</summary>
    private Point ScreenPxToWindowDip(int px, int py)
    {
        // Let WPF apply its current per-monitor transform. Manual (px / dpi - Window.Left)
        // arithmetic is subtly wrong when the virtual desktop spans monitors with different DPI.
        try
        {
            var local = _inkLayer.PointFromScreen(new Point(px, py));
            if (double.IsFinite(local.X) && double.IsFinite(local.Y)) return local;
        }
        catch { /* during HWND teardown, use the last known transform below */ }
        double s = DevicePerDip;
        return new Point(px / s - Left, py / s - Top);
    }

    private void Feed(in POINTER_INFO pi, double pressure, FeedKind kind)
    {
        var dip = ScreenPxToWindowDip(pi.ptPixelLocationX, pi.ptPixelLocationY);
        NotePointer(dip.X, dip.Y);   // 状态牌始终使用窗口 DIP
        var p = ToCanvasPoint(dip); // 通道 A 同样落到 3840×2160 逻辑画布
        // v0.4.1：时间戳用 InkClock（QPC 毫秒）—— TickCount64 分辨率 10~16ms 低于
        // 120Hz 报点间隔，相邻点时间戳相同导致速度笔锋计算失真（快写过度收细）
        var ink = new InkPoint(p.X, p.Y, pressure, InkClock.NowMs());
        switch (kind)
        {
            case FeedKind.Down:
                _svc.InkEngine.OnStylusDown(ink);
                if (_svc.InkEngine.Tool is InkTool.Pen or InkTool.Highlighter) PumpFeedback();
                break;
            case FeedKind.Move:
                _svc.InkEngine.OnStylusMove(ink);
                break;
            default:
                _svc.InkEngine.OnStylusUp(ink);
                break;
        }
        Invalidate();
    }

    /// <summary>会话首笔诊断（A/B/C 通道共用）：通道来源、指针类型、接触面积、
    /// 判定结果、压感状态、机型档案与运行时校准 —— 书写异常时日志直接给出
    /// "到底谁收到了什么、卡在哪一环"。</summary>
    private void LogFirstFeed(string channel, int type, Rect contact, bool accepted, uint rawPressure)
    {
        if (_sessionFirstFeed) return;
        _sessionFirstFeed = true;
        try
        {
            string typeStr = type == PT_PEN ? "笔" :
                             type == PT_TOUCH ? "触摸" : type == 0 ? "B通道" : $"type{type}";
            string areaStr = contact.Width > 0 ? $"{contact.Width * contact.Height:F0}px²" : "无元数据";
            string pressureStr = rawPressure > 0
                ? $"压感raw={rawPressure}{(_pressureProven.Count > 0 ? "(已启用调制)" : "(探测中)")}"
                : "压感=无";
            var prof = _svc.InkEngine.Profile;
            string calib = prof.CalibratedPxPerMm is { } c
                ? $" 运行时校准={c:F3}({prof.CalibrationSource})" : "";
            _svc.Log.Info($"首笔（通道{channel}）：{typeStr} area={areaStr} 判定={(accepted ? "接受" : "拒绝")} " +
                          $"{pressureStr} 档案={prof.Id} PxPerMm={prof.PxPerMm:F2}{calib} " +
                          $"手掌抑制={( _svc.PalmRejection.Enabled ? "开" : "关")}");
        }
        catch { }
    }

    // ———————— 通道 B：墨迹线程报点 → 引擎（直接调用，引擎内部自锁） ————————

    private void OnRawDown(InkPoint p)
    {
        if (ChannelCOnly) return;   // compat 档：只信通道 C（A/B 全屏蔽，见 WndProc 注释）
        // 快速前置过滤；关键的可见性/模式/会话复核在生命周期门闩内再做一次。
        if (!_inkLayer.IsVisible || _svc.InkEngine.Tool == InkTool.TouchThrough || IsInkStartBlockedByUi(p.X, p.Y)) return;
        // 通道 B（StylusPlugIn 墨迹线程）：会话占位失败（通道 A/C 持有）则忽略。
        // 手掌判定由通道 A 独占执行；B 若先到说明 A 未收到该 down，直接放行（宽容原则）。
        if (Interlocked.CompareExchange(ref _inputSession, 2, 0) != 0) return;
        lock (_inputLifecycleGate)
        {
            // DeactivateStage 可能在 CAS 后先拿到门闩并设置 5 哨兵；此处复核防止隐藏舞台
            // 上创建新 LiveStroke。若成功拿锁则 Down 和停用收笔严格串行。
            if (Volatile.Read(ref _inputSession) != 2 || !_inkLayer.IsVisible ||
                _svc.InkEngine.Tool == InkTool.TouchThrough || IsInkStartBlockedByUi(p.X, p.Y))
            {
                Interlocked.CompareExchange(ref _inputSession, 0, 2);
                return;
            }
            NotePointer(p.X, p.Y);   // 通道 B 报点进状态牌坐标缓存（墨迹线程；double x64 原子写）
            LogFirstFeed("B:StylusPlugIn", 0, Rect.Empty, accepted: true, (uint)Math.Round(p.Pressure * 1024));
            // WPF 已把压感归一化（无压感设备恒 0.5 —— 通道 B 天然从下往上）
            uint raw = (uint)Math.Round(p.Pressure * 1024);
            bool pActive = p.Pressure is (< 0.45 or > 0.55);
            InputDiagnostics.BeginSession("B:StylusPlugIn", 0, 0, false, 0, 0, 0, 0, "接受（B通道宽容）", true, raw, p.Pressure, pActive);
            _svc.InkEngine.OnStylusDown(ToCanvasPoint(p));
            Invalidate();
            // 落笔微反馈：淡圈 180ms，落笔后即使静止也要有动画推进 → 共享帧泵
            if (_svc.InkEngine.Tool is InkTool.Pen or InkTool.Highlighter)
                PumpFeedback();
        }
    }

    private DispatcherTimer? _feedbackPump;
    private long _feedbackUntil;
    private int _feedbackStartPending;

    /// <summary>反馈帧泵：220ms 内以渲染优先级持续重绘（淡圈扩张消散），到点自停。
    /// 共享单例定时器 + 截止时间戳（审查修复 #7）：高频落笔只顺延截止时间，不重复分配短命对象。</summary>
    private void PumpFeedback()
    {
        Interlocked.Exchange(ref _feedbackUntil, Environment.TickCount64 + 220);
        // StylusPlugIn 回调运行在专用墨迹线程；DispatcherTimer 必须由窗口 Dispatcher
        // 创建/启动，否则可能绑定到没有消息泵的工作线程，导致反馈动画从未 Tick。
        if (!Dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _feedbackStartPending, 1) == 0)
            {
                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
                    {
                        Interlocked.Exchange(ref _feedbackStartPending, 0);
                        PumpFeedback();
                    }));
                }
                catch (InvalidOperationException) { Interlocked.Exchange(ref _feedbackStartPending, 0); }
            }
            return;
        }

        if (_feedbackPump is null)
        {
            _feedbackPump = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _feedbackPump.Tick += (_, _) =>
            {
                if (Environment.TickCount64 > Interlocked.Read(ref _feedbackUntil))
                {
                    _feedbackPump!.Stop();
                    Invalidate();
                    return;
                }
                Invalidate();
            };
        }
        // 在 UI Dispatcher 上刷新截止时间；若线程请求刚到达、已在运行，则合并到同一帧泵。
        Interlocked.Exchange(ref _feedbackUntil, Environment.TickCount64 + 220);
        _feedbackPump.Start();
    }

    private void OnRawMove(InkPoint p)
    {
        if (ChannelCOnly) return;   // compat 档：只信通道 C
        lock (_inputLifecycleGate)
        {
            if (Volatile.Read(ref _inputSession) != 2) return; // 忽略迟到 move 与收笔临界区中的点
            if (!_inkLayer.IsVisible) return;   // 层已收回时不再修改擦除路径/笔划
            if (_svc.InkEngine.Tool == InkTool.TouchThrough) return;   // 单窗口适配门禁（同 OnRawDown）
            NotePointer(p.X, p.Y);       // 通道 B 报点进状态牌坐标缓存
            _svc.InkEngine.OnStylusMove(ToCanvasPoint(p));
            InputDiagnostics.UpdateSession(1, false, 0, 0, 0, 0, (uint)Math.Round(p.Pressure * 1024), p.Pressure,
                p.Pressure is (< 0.45 or > 0.55));
            Invalidate();
        }
    }

    private void OnRawUp(InkPoint p)
    {
        if (ChannelCOnly) return;   // compat 档：只信通道 C（与 Down/Move 对称）
        lock (_inputLifecycleGate)
        {
            // 收笔期间仍独占会话，防止另一条输入通道在引擎提交 Up 前开始下一笔；
            // 生命周期同锁确保停用时要么完整提交 Up，要么先落 5 哨兵并收尾。
            if (Interlocked.CompareExchange(ref _inputSession, 4, 2) != 2) return;
            try
            {
                if (!_inkLayer.IsVisible)
                    ForceLiftStroke(); // 层在书写中被收回：结束当前笔划，不留半笔状态
                else
                    _svc.InkEngine.OnStylusUp(ToCanvasPoint(p));
                InputDiagnostics.EndSession();
                Invalidate();
            }
            finally
            {
                Interlocked.CompareExchange(ref _inputSession, 0, 4);
            }
        }
    }

    // ———————— 通道 C：鼠标兜底（笔记本 / 无触摸设备 / 触摸→鼠标提升链） ————————
    // v0.6.0 触摸合成鼠标兜底（用户实测「从未捕捉到触摸和鼠标」的正面攻坚）：
    // 板中板（非透明普通窗口 + InkCanvas）流畅可画、工具栏按钮可点（触摸→鼠标提升链活）
    // —— 证明本机触摸与进程内 WPF 输入栈都活着；而墨迹层三通道全灭的候选路径：
    // 通道 B（StylusPlugIn）在 AllowsTransparency 透明窗口上失效（铁律1 的同族问题）、
    // 通道 A（WM_POINTER）依赖消息投递资格/窗口未被压未被穿透。此情此景下，
    // 触摸合成的鼠标事件是<b>唯一</b>到达本窗口的输入形态 —— 而旧版双保险
    // `if (e.StylusDevice is { } sd && !sd.InAir) return;` 把它无条件封死。
    // 新仲裁纪律：
    //  - 手掌时限挡「已被通道 A 判死」的触摸（防手掌以鼠标形态复活）；
    //  - 会话锁挡「通道 A/B 已接管」的触摸（A/B 活的环境零双发 —— B 在墨迹线程
    //    先占 _inputSession=2/3，合成鼠标 down 后到时 CompareExchange 必失败）；
    //  - 其余放行 —— A/B 全灭时合成鼠标直达引擎，书写复活（HUD 通道名
    //    「C:Mouse(触摸提升)」即为该形态的铁证标识）。

    private void OnMouseInkDown(object sender, MouseButtonEventArgs e)
    {
        // v0.5.0：与通道 A/B 同款朴素判据（墨迹层可见即可写）；TouchThrough 门禁
        //（单窗口适配，见 OnRawDown 注释 —— 穿透态不误画）。
        if (!_inkLayer.IsVisible || _mouseInking) return;
        if (_svc.InkEngine.Tool == InkTool.TouchThrough) return;
        var p = e.GetPosition(_inkLayer);
        if (IsInkStartBlockedByUi(p.X, p.Y)) { e.Handled = true; return; }
        if (Environment.TickCount64 < _touchOwnedUntil) return;   // 手掌判死的触摸：鼠标形态一并拒绝
        if (Interlocked.CompareExchange(ref _inputSession, 1, 0) != 0) return;   // A/B 已接管本触摸
        bool fromTouch = e.StylusDevice is { } sd && !sd.InAir;   // 仅作日志/诊断通道名区分
        string channel = fromTouch ? "C:Mouse(触摸提升)" : "C:Mouse";
        LogFirstFeed(channel, 0, Rect.Empty, accepted: true, 512);
        NotePointer(p.X, p.Y);
        _mouseInking = true;
        InputDiagnostics.BeginSession(channel, 0, 0, false, 0, 0, 0, 0, "接受", true, 512, 0.5, false);
        Mouse.Capture(_inkLayer);                                          // 移出墨迹层也能收 move/up
        _svc.InkEngine.OnStylusDown(ToCanvasPoint(new InkPoint(p.X, p.Y, 0.5, InkClock.NowMs())));
        Invalidate();
        if (_svc.InkEngine.Tool is InkTool.Pen or InkTool.Highlighter) PumpFeedback();
        e.Handled = true;
    }

    private void OnMouseInkMove(object sender, MouseEventArgs e)
    {
        if (!_mouseInking || !_inkLayer.IsVisible) return;   // v0.5.0：书写中层被收回（画布隐藏）时停止喂点
        var p = e.GetPosition(_inkLayer);
        _svc.InkEngine.OnStylusMove(ToCanvasPoint(new InkPoint(p.X, p.Y, 0.5, InkClock.NowMs())));
        Invalidate();
        e.Handled = true;
    }

    private void OnMouseInkUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseInking) return;
        _mouseInking = false;
        Mouse.Capture(null);
        if (Interlocked.CompareExchange(ref _inputSession, 4, 1) != 1)
        {
            e.Handled = true; // 会话已被舞台停用/回收，不再向引擎补发迟到 Up
            return;
        }
        try
        {
            if (!_inkLayer.IsVisible)
            {
                // 与 WM_POINTER / Stylus 收笔路径一致：关闭当前笔划，避免留下半笔。
                ForceLiftStroke();
                InputDiagnostics.EndSession();
                Invalidate();
                e.Handled = true;
                return;
            }
            var p = e.GetPosition(_inkLayer);
            _svc.InkEngine.OnStylusUp(ToCanvasPoint(new InkPoint(p.X, p.Y, 0.5, InkClock.NowMs())));
            InputDiagnostics.EndSession();
            Invalidate();
            e.Handled = true;
        }
        finally
        {
            Interlocked.CompareExchange(ref _inputSession, 0, 4);
        }
    }

}
