using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PptInk.Core;
using PptInk.Ink;
using PptInk.Seewo;

namespace PptInk.UI;

/// <summary>
/// 调试模式诊断面板（v0.4.0 → v0.9.0 单窗口架构：原独立 Topmost 窗口降维为舞台
/// Hud 层元素）：放映屏角落常驻，100ms 轮询 <see cref="InputDiagnostics"/> 与引擎实况 ——
/// 输入通道、接触面积/接触点、压感探测分层、报点率、触摸数字化仪物理探测、机型档案
/// 与运行时校准、渲染 FPS 与位图口径、舞台窗口状态，一屏尽览。
///
/// 窗口纪律等价物：舞台本体 NOACTIVATE（不抢焦点，不打断 PPT 键盘消息）+ TOOLWINDOW
/// （Alt+Tab 隐藏）+ Topmost；元素 Focusable=false。可拖动（DragKit 三源定源，位移
/// &gt; 8px 才入拖 —— 比原 DragMove 更防误触），双击面板收起/展开（handledEventsToo
/// 直订：DragKit 隧道段已 Handled 的按下事件依然可达）。诊断链路永不抛出
/// （InputDiagnostics 全防御 + 读取全部 try-catch）。
/// 停用态（S0/未放映）显示沿用 Toast「借窗」纪律：舞台透明穿透窗上屏零干扰，收起还原。
/// </summary>
internal sealed class DebugHudElement : StageElement
{
    private readonly AppServices _svc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = 12.5,
        LineHeight = 19,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly DragKit _drag;
    private bool _collapsed;
    private bool _borrowed;   // 停用态借窗显示（HidePanel 时还原）

    // FPS 差分（HUD 侧计算，不动渲染线程）
    private long _lastFrameCount;
    private long _lastFrameTick;
    private double _fps;

    public DebugHudElement(AppServices svc)
    {
        _svc = svc;
        MinWidth = 360;
        MaxWidth = 430;
        SnapsToDevicePixels = true;

        // 圆角深色卡 + 内边距（两层：拖动标题语义赋予整个面板 + 内容区）
        var textScroller = new ScrollViewer
        {
            Content = _text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            CanContentScroll = false,
            ClipToBounds = true,
        };
        var dragHeader = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x58, 0xB8, 0xFF)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = "输入诊断 · 拖动此处",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            },
        };
        var panelContent = new DockPanel();
        DockPanel.SetDock(dragHeader, Dock.Top);
        panelContent.Children.Add(dragHeader);
        panelContent.Children.Add(textScroller);
        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x18, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x58, 0xB8, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 12),
            Child = panelContent,
            ClipToBounds = true,
        };
        Content = root;

        // 仅标题行负责拖动；诊断文本保留自己的纵向触摸滚动，不和拖动手势争用输入。
        _drag = new DragKit(this);
        _drag.Wire(dragHeader);

        // 双击收起/展开：DragKit 的隧道段订阅会抢先 Handled —— 本检测必须 handledEventsToo
        // 直订（同节点后续 handler 默认被 Handled 短路），否则双击永远收不到
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPanelPreviewDown), true);

        Loaded += (_, _) => PositionTopLeft();
        _timer.Tick += (_, _) => Refresh();
    }

    private void OnPanelPreviewDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        _collapsed = !_collapsed;
        _text.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _drag.EndSession();   // 双击不是拖动：终结 DragKit 会话防 600ms 后误报长按
        e.Handled = true;
    }

    // ———————— 显隐（编排器 SetDebugMode 调用） ————————

    /// <summary>面板显示（幂等）。停用态借窗：舞台透明穿透窗上屏零干扰，HidePanel 还原。</summary>
    public void ShowPanel()
    {
        if (Stage is { } st && !st.IsVisible)
        {
            st.SetPassthrough(true);   // 借窗前提：穿透态（同 Toast 借窗纪律）
            st.ShowSilent();
            _borrowed = true;
        }
        StageUiLayout.ApplyDebugHudSlot(this);
        PositionTopLeft();
        Visibility = Visibility.Visible;
        _timer.Start();
        Refresh();
    }

    public void HidePanel()
    {
        _timer.Stop();
        Visibility = Visibility.Collapsed;
        if (_borrowed && Stage is { } st && !st.StageActive)
        {
            st.HideSilent();   // 借窗还原（显示期间已进入放映激活态则不还原）
            _borrowed = false;
        }
    }

    /// <summary>停靠在左侧独占诊断列。横向不会进入工作面板区域；放映屏切换后由编排器重调。</summary>
    public void PositionTopLeft()
    {
        try
        {
            var b = ShowScreen.Bounds;
            ScreenLeft = b.Left + 24;
            ScreenTop = b.Top + 24;
        }
        catch { }
    }

    // ———————— 轮询刷新（全部 try-catch：诊断链路永不抛出） ————————

    private void Refresh()
    {
        try
        {
            // FPS 差分
            long now = Environment.TickCount64;
            long count = InputDiagnostics.FrameCount;
            if (now - _lastFrameTick >= 900)
            {
                if (_lastFrameTick > 0 && now > _lastFrameTick)
                    _fps = (count - _lastFrameCount) * 1000.0 / (now - _lastFrameTick);
                _lastFrameCount = count;
                _lastFrameTick = now;
            }

            var sb = new System.Text.StringBuilder(1024);
            var s = InputDiagnostics.SessionSnapshot();
            var dev = InputDiagnostics.Device;
            var ren = InputDiagnostics.Render;

            sb.AppendLine("── 输入会话 ──────────────────");
            if (s is null || string.IsNullOrEmpty(s.Channel))
            {
                sb.AppendLine("  （空闲，等待下一次触摸）");
            }
            else
            {
                string type = s.PointerType == 1 ? "笔(PT_PEN)" : s.PointerType == 2 ? "触摸(PT_TOUCH)" : "—";
                double age = (Environment.TickCount64 - s.LastUpdateTick) / 1000.0;
                string state = age < 0.35 ? "进行中" : $"已结束 {age:F1}s";
                sb.AppendLine($"  通道 {s.Channel} · {type} #{s.PointerId} · {state}");
                if (s.HasContact)
                    sb.AppendLine($"  接触 {s.ContactW:F0}×{s.ContactH:F0}px（{s.AreaPx2:F0}px²）");
                else
                    sb.AppendLine("  接触 无元数据（驱动未提供 rcContact）");
                sb.AppendLine($"  判定 {s.Verdict}");
                string tier = s.PressureActive ? "L2 压感调制" : s.HasContact ? "L1 面积" : "L0 坐标保底";
                sb.AppendLine($"  压感 raw={s.PressureRaw} → {s.PressureNorm:F2} · 能力 {tier}");
                double hz = s.Points > 0 && s.LastUpdateTick > s.StartTick
                    ? s.Points * 1000.0 / (s.LastUpdateTick - s.StartTick) : 0;
                sb.AppendLine($"  报点 {s.Points} 点 · 实测 {hz:F0}Hz · 历史深≤{s.HistoryMax}");
            }

            sb.AppendLine("── 触摸数字化仪（权威物理探测）──");
            sb.AppendLine($"  {dev.Summary}");
            string calib = dev.CalibratedPxPerMm > 0
                ? $"{dev.CalibratedPxPerMm:F3}（{dev.CalibrationSource}）" : "未校准（档案口径）";
            sb.AppendLine($"  档案 {dev.ProfileId} · PxPerMm {calib}");

            // 统一舞台窗口状态区（v0.6.0 墨迹层状态继承）：可见性/穿透位/守卫/几何/被谁压 ——
            // 现场「透明层是否真实出现」不再靠猜（配合墨迹层调试描边肉眼互证）。
            sb.AppendLine("── 统一舞台窗口 ─────────────");
            try
            {
                var ov = _svc.UiOrchestrator.Overlay;
                if (ov is null) sb.AppendLine("  （未创建）");
                else sb.AppendLine(ov.DebugStateText().TrimEnd());
            }
            catch { sb.AppendLine("  （状态读取失败）"); }

            sb.AppendLine("── 显示与渲染 ────────────────");
            sb.AppendLine($"  位图 {ren.CacheW}×{ren.CacheH}px · 画布 {ren.RenderDipW:F0}×{ren.RenderDipH:F0} DIP");
            string scaleNote = Math.Abs(ren.WindowScale - ren.ShowScreenScale) > 0.01
                ? $"  ⚠ 窗口DPI {ren.WindowScale:F2} ≠ ShowScreen {ren.ShowScreenScale:F2}（瞬时不一致）"
                : $"  DPI {ren.WindowScale:F2}（窗口=感知，一致）";
            sb.AppendLine(scaleNote);
            try
            {
                var eng = _svc.InkEngine;
                int livePts = eng.LiveStroke?.Points.Count ?? 0;
                int strokes = 0;
                lock (eng.Gate) strokes = eng.CurrentPage.Strokes.Count;
                sb.AppendLine($"  页 {eng.CurrentSlideIndex} · 笔画 {strokes} · live {livePts} 点 · 工具 {eng.Tool}");
            }
            catch { }
            sb.AppendLine($"  渲染 FPS {_fps:F0}");

            sb.AppendLine("── 最近事件 ──────────────────");
            var events = InputDiagnostics.EventsSnapshot();
            if (events.Length == 0) sb.AppendLine("  （暂无）");
            foreach (var (time, msg) in events[^Math.Min(events.Length, 6)..])
                sb.AppendLine($"  {time} {msg}");

            sb.Append("提示：触摸屏幕 → 接触矩形绿=接受/红=手掌拒绝；蓝色圆=当前笔宽");
            _text.Text = sb.ToString();
        }
        catch { }
    }
}
