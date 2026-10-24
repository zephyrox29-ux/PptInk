using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PptInk.UI.Theme;

/// <summary>主题（"Auto" 跟随系统明暗）。</summary>
public enum AppTheme { Dark, Light, Auto }

/// <summary>
/// 面板材质：
///  - Solid        实体色（最强对比，投影仪弱光环境可读性最好）
///  - Translucent  半透明 82%（默认：幻灯片主体透出，课堂不割裂）
///  - Acrylic      亚克力模糊（DWM 实时高斯模糊 + 着色；Win10 1803+，旧系统自动回退半透明）
///  - Minimal      极简融合（无描边、无阴影、近实底、小圆角：面板"融"进画面）
/// </summary>
public enum PanelMaterial { Solid, Translucent, Acrylic, Minimal }

/// <summary>
/// 主题管理器：主题 / 强调色 / 材质 的运行时热切换。
///  - 覆盖 Application 级资源键（各窗口以 DynamicResource 引用，改键即全局生效，零窗口重建）；
///  - 亚克力通过 DWM AccentPolicy 附加到注册窗口（工具栏/浮岛/设置窗），
///    WPF 内容背景置为近全透明让模糊透出 —— 与系统资源管理器同源观感；
///  - ThemeChanged 供纯代码构建的窗口（浮岛等）重建本地冻结的 Brush。
/// </summary>
public static class ThemeManager
{
    // ———————— 预设 ————————

    /// <summary>强调色预设（v0.8.0 岱山衢青主题：青系领衔，饱和度全部 ≤60%，课堂克制不刺眼；
    /// 去鸢尾紫——紫色渐变是通用 AI 观感的重灾区）。</summary>
    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("衢青", "#78AFA5"), ("黛青", "#3E6B64"), ("青碧", "#2EB8B8"), ("板书蓝", "#3D9BE8"),
        ("板书绿", "#34B37A"), ("暖琥珀", "#D9A05B"), ("玫红", "#E85D8A"), ("石墨", "#5C6370"),
    };

    // ———————— 状态 ————————

    private static AppTheme _theme = AppTheme.Dark;
    private static PanelMaterial _material = PanelMaterial.Translucent;
    private static Color _accent = Color.FromRgb(0x78, 0xAF, 0xA5);   // 衢青（v0.8.0 默认）
    private static readonly HashSet<Window> _acrylicWindows = new();

    /// <summary>主题/材质/强调色任一变化（UI 线程触发）。参数=是否需要重建亚克力。</summary>
    public static event Action? ThemeChanged;

    public static AppTheme Theme => _theme;
    public static PanelMaterial Material => _material;
    public static Color Accent => _accent;
    /// <summary>当前生效的明暗（Auto 已解析为 Dark/Light）。</summary>
    public static bool IsLight => ResolveLight();

    // ———————— 应用 ————————

    /// <summary>按配置应用全部外观（启动时调用一次）。</summary>
    public static void ApplyFromConfig()
    {
        var cfg = Core.AppConfig.Current;
        Enum.TryParse(cfg.Theme, ignoreCase: true, out _theme);
        Enum.TryParse(cfg.Material, ignoreCase: true, out _material);
        try { _accent = (Color)ColorConverter.ConvertFromString(cfg.AccentColor); }
        catch { _accent = Color.FromRgb(0x78, 0xAF, 0xA5); }   // 衢青兜底
        ApplyTokens();
    }

    /// <summary>切换主题（立即生效并持久化）。</summary>
    public static void SetTheme(AppTheme theme)
    {
        _theme = theme;
        Core.AppConfig.Current.Theme = theme.ToString();
        Core.AppConfig.Current.Save();
        ApplyTokens();
    }

    /// <summary>切换强调色。</summary>
    public static void SetAccent(Color c)
    {
        _accent = c;
        Core.AppConfig.Current.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Core.AppConfig.Current.Save();
        ApplyTokens();
    }

    /// <summary>切换材质（亚克力变化需逐窗口刷新 DWM 属性）。</summary>
    public static void SetMaterial(PanelMaterial material)
    {
        _material = material;
        Core.AppConfig.Current.Material = material.ToString();
        Core.AppConfig.Current.Save();
        ApplyTokens();
    }

    /// <summary>
    /// 系统明暗变化时（Auto 模式）重算。
    /// SystemEvents 可能自非 UI 线程回调 → BeginInvoke 切回 UI 线程再写资源。
    /// </summary>
    public static void OnSystemThemeChanged()
    {
        if (_theme != AppTheme.Auto) return;
        if (Application.Current is null) return;
        Application.Current.Dispatcher.BeginInvoke(ApplyTokens);
    }

    // ———————— 令牌计算与写入 ————————

    private static void ApplyTokens()
    {
        if (Application.Current is null) return;
        var res = Application.Current.Resources;
        bool light = IsLight;

        // —— 面板底色（v0.8.0 岱青体系：深色为山岚墨青 #16201E，浅色为青白 #F5F7F6，
        //    整体色温统一到青调——弃中性蓝黑，主题气质连贯） ——
        // Minimal：近实底（96%）+ 无描边（描边键写透明）+ 小圆角观感由各窗口 CornerRadius 决定
        // Acrylic（审查修复 v0.1.3）：DWM AccentPolicy 在 AllowsTransparency=true 的 layered
        // 窗口上【不生效】（微软已知限制，WPF 透明窗口不参与 AccentPolicy 合成）——
        // 原实现把 WPF 前景置 0x01 alpha 赌 DWM 承担底色，结果就是既无模糊、面板又近乎
        // 全透明（「设置页没有任何材质效果」）。现 Acrylic 用 80% 不透明前景 + 仍尝试 DWM：
        // DWM 生效（非 layered 窗口）→ 模糊透出；不生效（layered）→ 半透明青调面板，观感可用。
        Color panel = (_material, light) switch
        {
            (PanelMaterial.Solid, false)       => Color.FromRgb(0x16, 0x20, 0x1E),
            (PanelMaterial.Solid, true)        => Color.FromRgb(0xF5, 0xF7, 0xF6),
            (PanelMaterial.Minimal, false)     => Color.FromRgb(0x1A, 0x25, 0x23),
            (PanelMaterial.Minimal, true)      => Color.FromRgb(0xF1, 0xF4, 0xF3),
            (PanelMaterial.Acrylic, false)     => Color.FromArgb(0xCC, 0x16, 0x20, 0x1E),
            (PanelMaterial.Acrylic, true)      => Color.FromArgb(0xCC, 0xF5, 0xF7, 0xF6),
            _ when light                       => Color.FromArgb(0xF2, 0xF5, 0xF7, 0xF6), // Translucent-Light
            _                                  => Color.FromArgb(0xD2, 0x16, 0x20, 0x1E), // Translucent-Dark（默认）
        };
        res["PanelColor"] = panel;
        res["PanelBrush"] = MakeBrush(panel);

        Color panelSubtle = light
            ? Color.FromArgb(0xE6, 0xF5, 0xF7, 0xF6)
            : Color.FromArgb(0xCC, 0x16, 0x20, 0x1E);
        res["PanelColorSubtle"] = panelSubtle;
        res["PanelBrushSubtle"] = MakeBrush(panelSubtle);

        // 描边：Minimal 融合模式无描边；亚克力用极淡描边勾勒边界
        Color border = _material switch
        {
            PanelMaterial.Minimal => Colors.Transparent,
            PanelMaterial.Acrylic when light => Color.FromArgb(0x2E, 0x00, 0x00, 0x00),
            PanelMaterial.Acrylic => Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
            _ when light => Color.FromArgb(0x24, 0x00, 0x00, 0x00),
            _ => Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF),
        };
        res["PanelBorderBrush"] = MakeBrush(border);

        // —— 选中/悬停（浅色下用墨色淡块，深色下用白色淡块，均非高饱和） ——
        res["ItemSelectedBrush"] = MakeBrush(light ? Color.FromArgb(0x1E, 0x00, 0x00, 0x00) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        res["ItemHoverBrush"] = MakeBrush(light ? Color.FromArgb(0x12, 0x00, 0x00, 0x00) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

        // —— 文字（青调：深色青白/青灰，浅色墨青/青灰） ——
        res["TextPrimaryBrush"] = MakeBrush(light ? Color.FromRgb(0x1A, 0x21, 0x1F) : Color.FromRgb(0xEE, 0xF2, 0xF0));
        res["TextSecondaryBrush"] = MakeBrush(light ? Color.FromRgb(0x56, 0x65, 0x5F) : Color.FromRgb(0xA3, 0xB5, 0xB0));
        res["TextDisabledBrush"] = MakeBrush(light ? Color.FromRgb(0x8A, 0x9B, 0x95) : Color.FromRgb(0x5E, 0x71, 0x6C));

        // —— 强调色（及柔和版：衢青 40% 柔光） ——
        res["AccentBrush"] = MakeBrush(_accent);
        res["AccentSoftBrush"] = MakeBrush(Color.FromArgb(0x66, _accent.R, _accent.G, _accent.B));
        // 警示琥珀（v0.8.0：仅高危操作图标用——「结束放映」一眼可辨，饱和度收敛不刺眼）
        res["AccentAmberBrush"] = MakeBrush(Color.FromRgb(0xD9, 0xA0, 0x5B));

        // —— 卡片（设置中心/引导卡）：深色下为"比面板略亮"的墨青（近白卡+浅字=对比度事故） ——
        res["CardBrush"] = MakeBrush(light ? Colors.White : Color.FromRgb(0x1E, 0x2B, 0x28));
        res["ScrimBrush"] = MakeBrush(light
            ? Color.FromArgb(0xC2, 0xF1, 0xF4, 0xF2)   // 浅色暗化幕用"青白雾"而非黑幕
            : Color.FromArgb(0xC2, 0x0A, 0x10, 0x0F));

        // —— 阴影（Minimal 去阴影，其余保留柔和投影） ——
        res["PanelHasShadow"] = _material != PanelMaterial.Minimal;

        RefreshAcrylicAllWindows();
        ThemeChanged?.Invoke();
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static bool ResolveLight()
    {
        if (_theme == AppTheme.Dark) return false;
        if (_theme == AppTheme.Light) return true;
        // Auto：读注册表 AppsUseLightTheme（无键按深色，教室环境保守）
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return false;
    }

    // ———————— 亚克力（DWM AccentPolicy） ————————

    /// <summary>
    /// 注册窗口参与材质系统：Acrylic 时启用 DWM 模糊，其余材质自动撤销。
    /// 窗口关闭前调用 Unregister。
    /// </summary>
    public static void Register(Window w)
    {
        _acrylicWindows.Add(w);
        if (w.IsLoaded) ApplyAcrylicToWindow(w);
        else w.Loaded += (_, _) => ApplyAcrylicToWindow(w);
    }

    public static void Unregister(Window w) => _acrylicWindows.Remove(w);

    private static void RefreshAcrylicAllWindows()
    {
        // ApplyAcrylicToWindow 内部已兜底异常并降级；此处不再 catch 即 Unregister
        // （原实现会把窗口永久移出材质系统，之后主题/材质切换再也不更新该窗口）
        foreach (var w in _acrylicWindows.ToArray())
        {
            if (w.IsLoaded) ApplyAcrylicToWindow(w);
        }
    }

    private static bool _acrylicUnsupportedLogged;

    private static void ApplyAcrylicToWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 亚克力：DWM 高斯模糊 + 着色底（AARRGGBB，alpha=0x99 → 着色可见且模糊透出）。
        // Data 是指针（指向 AccentPolicy）：结构体在栈上，同步 P/Invoke 调用期间有效。
        // 着色 = 面板色的深/浅变体（岱青调）+ 约 60% 覆盖。
        var accent = _material == PanelMaterial.Acrylic
            ? new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = IsLight
                    ? (0x99u << 24) | (0xF5u << 16) | (0xF7u << 8) | 0xF6u
                    : (0x99u << 24) | (0x16u << 16) | (0x20u << 8) | 0x1Eu,
            }
            : new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED }; // 撤销：恢复默认合成

        // 兼容兜底（审查修复）：SetWindowCompositionAttribute 是未文档 API，
        // Win7/8 与部分早期 Win10 无此 user32 导出 → EntryPointNotFoundException。
        // Register 的 Loaded 回调此前无捕获，装载即崩溃；现静默降级为半透明（内容背景已按材质着色）。
        try
        {
            unsafe
            {
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = (nint)(&accent),
                    SizeOfData = sizeof(AccentPolicy),
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
        }
        catch (Exception ex)
        {
            if (!_acrylicUnsupportedLogged)
            {
                _acrylicUnsupportedLogged = true;
                Core.AppLog.Instance.Warn($"亚克力不可用（{ex.GetType().Name}），已降级为半透明材质");
            }
        }
    }

    /// <summary>该材质下 WPF 内容 Border 应使用的背景（与 PanelBrush 同源；Acrylic 时
    /// 含 80% 不透明前景，DWM 模糊不可用时面板依然可读 —— layered window 限制见 ApplyTokens 注释）。</summary>
    public static Brush ContentBackground => (Brush)Application.Current.Resources["PanelBrush"];


    #region Win32（亚克力）

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    private enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    #endregion
}
