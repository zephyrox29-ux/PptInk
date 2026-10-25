using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PptInk.UI;

/// <summary>
/// 新手引导覆盖元素（v0.9.0 单窗口架构：原独立 Topmost OnboardingGuideWindow 降维为
/// 舞台 Guide 层全屏元素）：暗化挖洞（目标高亮）+ 白色步骤卡片 + ~30s 自动推进。
///
/// 人体工学设计：
///  - 退出两路：点击暗区（等同次级按钮）/ 卡片跳过按钮，任何一步都能随时抽身；
///  - v0.9.0 挖洞「视觉 / 命中」分离（引导可操作性核心）：视觉层保留原有圆角挖洞，
///    命中层只在暗区放透明块（点暗区=跳过）、洞区不放任何元素 —— 命中测试直达下层：
///    · 洞内是工具栏按钮 → 直接可点（引导中即可实操被讲解的功能，配编排器的
///      「引导期间自动切触摸模式」，翻页/点按钮现场就能试）；
///    · 洞内是空白（触摸模式，底刷 alpha=0）→ 按像素穿透直达 PPT（可真翻页演示）；
///    · 洞内是空白（书写模式，底刷 alpha=1）→ 命中墨迹层，可当场试写一笔；
///  - v0.9.0 行为变化：原独立窗口的 Esc/Enter 键盘路径移除 —— 单窗口舞台
///    NOACTIVATE 纪律优先，绝不为了引导抢焦点打断 PPT 键盘消息；
///  - 卡片默认停靠屏幕下部板书间隙，与高亮目标重叠时自动上移让位；
///  - 欢迎步 12s 无操作自动按"先跳过"处理，绝不无限占用课堂时间。
///
/// 状态机协作：S2 隐让调用 Pause()（隐藏但保留进度），恢复 S1 时 Resume() 无缝续播；
/// S0 结束调用 Stop() 丢弃。完成一次完整流程 → Completed（由编排器取消托盘勾选）。
/// </summary>
public sealed class OnboardingGuideElement : StageElement
{
    // —— 视觉令牌（与 Tokens.xaml 同源，此处内联避免对外部资源加载的依赖；
    //    v0.8.0 岱山衢青：引导徽标/主按钮/高亮环全部衢青，与全局主题一致） ——
    private static readonly Color Accent = Color.FromRgb(0x78, 0xAF, 0xA5);        // 衢青
    private static readonly Color AccentSoft = Color.FromArgb(0x0F, 0x78, 0xAF, 0xA5);
    private static readonly Brush ScrimBrush = new SolidColorBrush(Color.FromArgb(0xC2, 0x0A, 0x10, 0x0F));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xF8));   // 青白卡
    private static readonly Color InkColor = Color.FromRgb(0x1A, 0x21, 0x1F);      // 墨青主文字
    private static readonly Color SubColor = Color.FromRgb(0x56, 0x65, 0x5F);      // 青灰辅文字
    private static readonly Brush DotIdleBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xD2));

    private readonly DispatcherTimer _advance;
    private readonly Grid _scrimVisual;   // 视觉：挖洞遮罩（不参与命中）
    private readonly Canvas _scrimHit;    // 命中：暗区分块（透明块拦截=跳过；洞区留空=命中穿透到下层）
    private readonly Canvas _highlight;
    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly Button _tertiary;
    private readonly StackPanel _dots;
    private readonly TranslateTransform _cardSlide = new();
    private readonly Dictionary<string, Geometry> _icons = new();
    private Path? _badgeIcon;

    private IReadOnlyList<OnboardingStep>? _steps;
    private int _index = -1;
    private double _elapsedMs;
    private bool _isActive;

    /// <summary>走完全流程（含收尾步）。编排器据此取消「新手引导」勾选。</summary>
    public event Action? Completed;

    /// <summary>被跳过（点暗区/跳过/我先上课/欢迎步超时）。保持勾选状态。</summary>
    public event Action? Dismissed;

    /// <summary>欢迎步「我不想使用 PptInk「本次隐藏」」：编排器据此取消勾选，今后不再自动提示。</summary>
    public event Action? OptedOut;

    /// <summary>引导进行中。</summary>
    public bool IsActive => _isActive;

    public OnboardingGuideElement()
    {
        BuildIcons();

        // —— 暗化层（v0.9.0 视觉/命中分离，见 RenderStep）——
        //   视觉层：OpacityMask 挖洞（保留圆角洞视觉），IsHitTestVisible=false 不参与命中；
        //   命中层：洞外分块透明矩形（点暗区=跳过），洞区不放任何元素 ——
        //   命中测试直达下层 UI（引导中即可直接操作被讲解的按钮），
        //   触摸模式下洞内空白则按像素穿透直达 PPT（翻页演示可现场做）。
        _scrimVisual = new Grid { Background = ScrimBrush, IsHitTestVisible = false };
        _scrimHit = new Canvas();

        // —— 高亮呼吸层 ——
        _highlight = new Canvas { IsHitTestVisible = false };

        // —— 步骤卡片 ——
        _card = BuildCard(out _title, out _body, out _primary, out _secondary, out _tertiary, out _dots);

        // 卡片必须置于 Canvas 上，PositionCard 的 Canvas.SetLeft/Top 才生效
        //（审查修复传承：把 _card 直接加入 Grid 时 Canvas 附加属性无效，卡片定位失效）
        var cardLayer = new Canvas();
        cardLayer.Children.Add(_card);
        var root = new Grid();
        root.Children.Add(_scrimVisual);
        root.Children.Add(_scrimHit);
        root.Children.Add(_highlight);
        root.Children.Add(cardLayer);
        Content = root;

        // —— 自动推进（250ms 心跳；仅讲解步） ——
        _advance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _advance.Tick += (_, _) =>
        {
            if (!_isActive || _steps is null || _index < 0) return;
            var step = _steps[_index];
            _elapsedMs += 250;
            if (step.StayMs > 0 && _elapsedMs >= step.StayMs)
            {
                if (step.AwaitChoice) Dismiss();   // 欢迎步超时 → 视作「先上课」
                else Advance();
            }
        };

        // —— 键盘 Esc/Enter 路径已随窗口语义移除（见类注释 v0.9.0 行为变化） ——
    }

    // ================= 对外控制 =================

    /// <summary>元素几何对位放映屏：遮罩只盖放映屏（引导是给放映中的老师看的）。
    /// Start/Resume + 舞台 DpiChanged/ScreenChanged 时重跑，幂等。public：
    /// 编排器在放映屏变化重对位时调用（原独立窗口 DpiChanged 的等价承接）。
    /// v1.1.1：换屏时当前步骤的挖洞/高亮/卡片仍指向旧屏位置 —— 几何重设后重渲染
    /// 当前步骤（可见时），引导不因换屏而指错地方。</summary>
    public void RelocateToScreen()
    {
        var b = Core.ShowScreen.Bounds;
        ScreenLeft = b.Left;
        ScreenTop = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (_isActive && IsVisible && _steps is not null && _index >= 0 && _index < _steps.Count)
            RenderStep();
    }

    public void Start(IReadOnlyList<OnboardingStep> steps)
    {
        if (_isActive) return;
        // Explicit hole-punch modal: transient islands/HUD are suspended, while permanent
        // toolbar/navigation targets remain visible and operable through the guide's holes.
        if (!StageUiLayout.EnterModal(this, preserveChromeUnderHole: true)) return;
        _steps = steps;
        _index = -1;
        _elapsedMs = 0;
        _isActive = true;
        RelocateToScreen();   // 此时 ShowScreen 已由放映事件更新（Start 仅在 S1 后调用）
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, Fade(0, 1, 220, EaseOut.Power3));
        Advance();              // 进第 0 步（欢迎）
        _advance.Start();
    }

    /// <summary>S2 隐让：隐藏但保留步骤进度，恢复后无缝续播。</summary>
    public void Pause()
    {
        if (!_isActive) return;
        _advance.Stop();
        var fade = Fade(Opacity, 0, 140, EaseIn.Power3);
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>S1 恢复：从暂停的步骤继续（不重置索引）。</summary>
    public void Resume()
    {
        if (!_isActive || IsVisible) return;
        if (!StageUiLayout.EnterModal(this, preserveChromeUnderHole: true)) return;
        RelocateToScreen();   // 放映屏可能已变（换屏重放）
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, Fade(0, 1, 160, EaseOut.Power3));
        _advance.Start();
    }

    /// <summary>会话结束强制终止（S0）。v1.1.2：必须归还模态租约 —— Start 的 EnterModal
    /// 若不配对 ExitModal，租约永久滞留，此后插件/擦除/画笔/聚光灯全部静默失效
    /// （AllocateUtilityPanel/AllocatePopover 在模态占用下一律拒绝，「插件按钮没作用」根因）。</summary>
    public void Stop()
    {
        if (!_isActive && !IsVisible) return;
        _advance.Stop();
        _isActive = false;
        StageUiLayout.ExitModal(this);
        Visibility = Visibility.Collapsed;
    }

    /// <summary>友好跳过：不触发任何回调之外的行为由编排器决定（保持托盘勾选）。</summary>
    public void Dismiss()
    {
        if (!_isActive) return;
        _advance.Stop();
        _isActive = false;
        StageUiLayout.ExitModal(this);
        var fade = Fade(Opacity, 0, 170, EaseIn.Power3);
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
        Dismissed?.Invoke();
    }

    // ================= 步骤渲染 =================

    private void Advance()
    {
        if (_steps is null || !_isActive) return;
        if (_index + 1 >= _steps.Count) { Finish(); return; }
        _index++;
        _elapsedMs = 0;
        RenderStep();
    }

    private void Finish()
    {
        _advance.Stop();
        _isActive = false;
        StageUiLayout.ExitModal(this);   // 与 Start 的 EnterModal 配对（租约泄漏 = 全部浮层静默失效）
        var fade = Fade(Opacity, 0, 200, EaseIn.Power3);
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
        Completed?.Invoke();
    }

    private void SecondaryAction() => Dismiss();

    private void RenderStep()
    {
        var step = _steps![_index];

        // 1) 卡片文案与按钮态
        _title.Text = step.Title;
        _body.Text = step.Body;
        bool welcome = step.AwaitChoice;
        _tertiary.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;   // 「本次隐藏」只在欢迎步
        _secondary.Visibility = Visibility.Visible;
        if (welcome)
        {
            _primary.Content = "来看看怎么使用";
            _secondary.Content = "我先上课";
        }
        else if (_index == _steps.Count - 1)
        {
            _primary.Content = "开始板书";
            _secondary.Content = "跳过";
        }
        else
        {
            _primary.Content = "下一步";
            _secondary.Content = "跳过";
        }
        RenderIconBadge(step.Icon);

        // 2) 挖洞遮罩 + 高亮呼吸框
        // v0.8.0 定位保险（单窗口传承）：Targets 传递【物理像素】矩形，此处用引导元素
        // 【自身】DPI 统一换算为本地 DIP —— 混合 DPI 多屏（教师屏 100% + 大屏 150%）
        // 部署下，单一换算源是唯一不会错的坐标链路（元素 ScreenLeft/Top 为 DIP，
        // ×dpi 即物理像素原点；元素挂在舞台 hwnd 上，同屏等价于旧独立窗口口径）。
        double dpi = 1.0;
        if (PresentationSource.FromVisual(this) is { CompositionTarget: { } ct })
            dpi = ct.TransformToDevice.M11;
        var holes = step.Targets?.Invoke();
        var holeRects = new List<Rect>();
        _highlight.Children.Clear();
        if (holes is { Count: > 0 })
        {
            foreach (var h in holes)
            {
                if (h.IsEmpty || h.Width < 4 || h.Height < 4) continue;
                // 物理 → 本地 DIP：减元素屏幕物理原点后整体除 dpi；±10 为高亮外扩留白
                var infl = new Rect(
                    (h.X - ScreenLeft * dpi) / dpi - 10,
                    (h.Y - ScreenTop * dpi) / dpi - 10,
                    h.Width / dpi + 20,
                    h.Height / dpi + 20);
                holeRects.Add(infl);
                _highlight.Children.Add(BuildHighlightRing(infl, gap: 14, stroke: SoftSolid(Accent, 0.35), thickness: 10));
                _highlight.Children.Add(BuildHighlightRing(infl, gap: 5, stroke: Solid(Accent), thickness: 2.5));
            }
        }
        _scrimVisual.OpacityMask = BuildHoleMask(holeRects);   // 视觉：精确多洞（圆角，观感不变）
        RebuildScrimHit(holeRects);                            // 命中：联合洞区外分块（洞区可操作）

        // 3) 卡片定位（底部居中；与高亮重叠则上移让位）
        PositionCard(holeRects);

        // 4) 进度点
        RenderDots();

        // 5) 进入动画：卡片上浮 + 淡入
        var slide = new DoubleAnimation(18, 0, new Duration(TimeSpan.FromMilliseconds(320)))
        { EasingFunction = PowerEase3Out };
        _cardSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        _card.BeginAnimation(OpacityProperty, Fade(0, 1, 280, EaseOut.Power3));
    }

    private void RenderIconBadge(string iconName)
    {
        if (_badgeIcon is { } p && _icons.TryGetValue(iconName, out var g))
            p.Data = g;
    }

    private void RenderDots()
    {
        _dots.Children.Clear();
        if (_steps is null) return;
        for (int i = 0; i < _steps.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = i == _index ? Solid(Accent) : DotIdleBrush,
            };
            _dots.Children.Add(dot);
        }
    }

    /// <summary>卡片停靠：默认屏幕下部（板书间隙），水平对齐目标联合中心（视线连贯）；
    /// 无目标步（欢迎/收尾）停靠放映屏底部居中；与高亮目标重叠时让位到目标上方。</summary>
    private void PositionCard(IReadOnlyList<Rect> holeRects)
    {
        const double bottomMargin = 96;      // 避开底部手势区/工具栏
        const double reservedZone = 260;     // 卡片所在"预占带"高度
        double cardW = Math.Min(460, Width - 56);
        _card.Width = cardW;
        _card.Measure(new Size(cardW, Height));
        double cardH = _card.DesiredSize.Height;

        // 水平锚点：有目标 → 目标联合中心（卡片贴近被讲解元素，减少视线往返）；
        // 无目标 → 元素中心（元素即覆盖放映屏，此处中心即放映屏中心）。
        double anchorCx;
        if (holeRects.Count > 0)
        {
            var u = holeRects[0];
            for (int i = 1; i < holeRects.Count; i++) u = Rect.Union(u, holeRects[i]);
            anchorCx = u.Left + u.Width / 2;
        }
        else
        {
            // 无目标步（欢迎/收尾）：卡片居中在放映屏（元素即覆盖放映屏，此处中心即元素中心）
            anchorCx = Width / 2;
        }
        double x = Math.Max(28, Math.Min(Width - cardW - 28, anchorCx - cardW / 2));
        double y = Math.Max(20, Height - cardH - bottomMargin);

        double overlapTop = Height - cardH - bottomMargin - reservedZone;
        foreach (var h in holeRects)
        {
            if (h.Bottom > overlapTop && h.Top < Height - bottomMargin)
            {
                double cand = h.Top - cardH - 32;
                if (cand > 20) { y = cand; break; }
            }
        }
        Canvas.SetLeft(_card, x);
        Canvas.SetTop(_card, y);
        _card.UpdateLayout();
    }

    // ================= 挖洞遮罩 / 高亮框 =================

    private Brush BuildHoleMask(IReadOnlyList<Rect> holes)
    {
        // 全屏矩形 = 元素本地 [0,0,W,H]（元素已覆盖放映屏，holes 亦为本地坐标）
        // v0.8.0：洞做 10px 圆角——与高亮环（CornerRadius 18）圆角观感对齐，
        // 消除「直角洞 + 圆角环」在目标四角的露边缝隙
        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        Geometry maskGeo = full;
        foreach (var h in holes)
            maskGeo = new CombinedGeometry(GeometryCombineMode.Exclude, maskGeo,
                new RectangleGeometry(h, 10, 10));
        var db = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, maskGeo)) { Stretch = Stretch.None };
        db.Freeze();
        return db;
    }

    /// <summary>
    /// 重建命中层（引导可操作性核心）：洞区（多洞的联合矩形）之外放置透明命中块 ——
    /// 暗区按下=跳过引导；洞区不放任何元素 → 命中测试直达下层 UI 元件
    /// （老师引导中即可直接点被讲解的工具栏按钮/翻页），触摸模式下洞内空白
    /// 则按像素穿透直达 PPT。视觉层另有独立挖洞遮罩，两层互不影响。
    /// </summary>
    private void RebuildScrimHit(IReadOnlyList<Rect> holeRects)
    {
        _scrimHit.Children.Clear();
        Rect hole = Rect.Empty;
        if (holeRects.Count > 0)
        {
            hole = holeRects[0];
            for (int i = 1; i < holeRects.Count; i++) hole = Rect.Union(hole, holeRects[i]);
            hole.Intersect(new Rect(0, 0, Width, Height));   // 钳制到元素范围（出屏保护）
        }
        if (hole.IsEmpty || hole.Width <= 0 || hole.Height <= 0)
        {
            AddHitBlock(0, 0, Width, Height);   // 无目标步（欢迎/收尾）：整屏暗区可点=跳过
            return;
        }
        AddHitBlock(0, 0, Width, hole.Top);                                  // 上带
        AddHitBlock(0, hole.Bottom, Width, Height - hole.Bottom);           // 下带
        AddHitBlock(0, hole.Top, hole.Left, hole.Height);                    // 左带
        AddHitBlock(hole.Right, hole.Top, Width - hole.Right, hole.Height);  // 右带
    }

    private void AddHitBlock(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        var b = new Border { Width = w, Height = h, Background = Brushes.Transparent };
        Canvas.SetLeft(b, x);
        Canvas.SetTop(b, y);
        ToolbarFactory.PressWire(b, () => SecondaryAction());
        _scrimHit.Children.Add(b);
    }

    private static Border BuildHighlightRing(Rect r, double gap, Brush stroke, double thickness)
    {
        var b = new Border
        {
            BorderBrush = stroke,
            BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(18),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(b, r.X - gap);
        Canvas.SetTop(b, r.Y - gap);
        b.Width = r.Width + gap * 2;
        b.Height = r.Height + gap * 2;
        // 呼吸脉动（v0.8.0 放缓收敛：1400ms / 幅度 1.03——引导期更安静，
        // 12Hz 微颤会与大屏 60Hz 刷新拍频产生不舒适感）
        var breath = new DoubleAnimation(1.0, 1.03, new Duration(TimeSpan.FromMilliseconds(1400)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        b.RenderTransform = new ScaleTransform(1, 1, b.Width / 2, b.Height / 2);
        b.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        b.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, breath.Clone());
        return b;
    }

    // ================= 卡片构建 =================

    private Border BuildCard(out TextBlock title, out TextBlock body,
        out Button primary, out Button secondary, out Button tertiary, out StackPanel dots)
    {
        // —— 顶部：小线性图标（v0.9.9 极简现代：去掉大圆底徽标，改用 22px 强调色线性图标） ——
        var badgeIcon = new Path
        {
            Name = "BadgeIcon",
            Width = 22, Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        badgeIcon.SetResourceReference(Path.FillProperty, "AccentBrush");
        badgeIcon.Data = _icons["IconSparkle"];
        _badgeIcon = badgeIcon;

        title = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(InkColor),
            Margin = new Thickness(0, 16, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        body = new TextBlock
        {
            FontSize = 15,
            Foreground = Solid(SubColor),
            LineHeight = 25,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 22),
        };

        // 主按钮：强调色填充 + 深字（≥50px 触控目标）
        primary = new Button
        {
            Content = "来看看怎么使用",
            Height = 50,
            MinWidth = 128,
            Padding = new Thickness(22, 0, 22, 0),
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(InkColor),
            Background = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        primary.RenderTransform = new TranslateTransform(0, 1);
        primary.Template = RoundedButtonTemplate(25);

        // 次按钮：细描边幽灵风
        secondary = new Button
        {
            Content = "我先上课",
            Height = 50,
            MinWidth = 104,
            Padding = new Thickness(22, 0, 22, 0),
            FontSize = 15,
            Foreground = Solid(SubColor),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0xDD, 0xD9)),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        secondary.Template = RoundedButtonTemplate(25);

        // 三级按钮：纯文字（「本次隐藏」，只在欢迎步出现）
        tertiary = new Button
        {
            Content = "我不想使用 PptInk「本次隐藏」",
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
            Foreground = Solid(Color.FromRgb(0x8E, 0x9C, 0x98)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        tertiary.Template = RoundedButtonTemplate(18);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(secondary);
        buttonRow.Children.Add(new Rectangle { Width = 10 });
        buttonRow.Children.Add(primary);

        dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };

        var stack = new StackPanel();
        stack.Children.Add(badgeIcon);
        stack.Children.Add(title);
        stack.Children.Add(body);
        stack.Children.Add(buttonRow);
        stack.Children.Add(tertiary);
        stack.Children.Add(dots);

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(26, 22, 26, 16),
            Opacity = 0,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 30, ShadowDepth = 10 },
            RenderTransform = _cardSlide,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = stack,
        };
        // Copy out parameters to locals before capturing them in lambdas (CS1628).
        var primaryButton = primary;
        var secondaryButton = secondary;
        var tertiaryButton = tertiary;
        ToolbarFactory.PressWire(primaryButton, () => { AnimationKit.PressPulse(primaryButton); _advance.Stop(); Advance(); });
        ToolbarFactory.PressWire(secondaryButton, () => { AnimationKit.PressPulse(secondaryButton); SecondaryAction(); });
        ToolbarFactory.PressWire(tertiaryButton, () =>
        {
            AnimationKit.PressPulse(tertiaryButton);
            OptedOut?.Invoke();   // 「本次隐藏」：编排器取消勾选，下次不再自动提示
            Dismiss();
        });

        return card;
    }

    private static ControlTemplate RoundedButtonTemplate(double radius)
    {
        var tf = new FrameworkElementFactory(typeof(Border));
        tf.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        tf.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        tf.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        tf.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        tf.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = tf };
    }

    // ================= 图标几何（Icons.xaml 同源，TryFindResource 兜底零外部依赖） =================

    private void BuildIcons()
    {
        // v0.8.1 归一：图标统一来源 Icons.xaml（Application 级资源，App 启动即加载）——
        // 原先此处内联 8 个与 Icons.xaml 重复的 path data（DRY 违例：改一处漏一处）。
        // TryFindResource 兜底：极端情况下资源缺失也不崩，仅该图标留空。
        var keys = new[]
        {
            "IconPen", "IconHighlighter", "IconLasso", "IconUndo", "IconRedo",
            "IconAi", "IconPlugin", "IconNext", "IconPalette", "IconSparkle", "IconCheck",
            "IconGrip",
        };
        foreach (var key in keys)
            if (Application.Current.TryFindResource(key) is Geometry g)
                _icons[key] = g;
    }

    // ================= 工具方法 =================

    private static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static SolidColorBrush SoftSolid(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static DoubleAnimation Fade(double from, double to, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
        return a;
    }

    private static readonly IEasingFunction PowerEase3Out = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction PowerEase3In = new PowerEase { Power = 3, EasingMode = EasingMode.EaseIn };

    // 简化 easing 选择
    private static class EaseOut { public static readonly IEasingFunction Power3 = PowerEase3Out; }
    private static class EaseIn { public static readonly IEasingFunction Power3 = PowerEase3In; }
}
