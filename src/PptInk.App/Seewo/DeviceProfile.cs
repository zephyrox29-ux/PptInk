using System.IO;
using System.Text.Json;
using PptInk.Core;

namespace PptInk.Seewo;

/// <summary>
/// 希沃大屏机型档案：分辨率、物理尺寸、手势区、红外框报点特性。
/// 笔迹平滑参数按机型预设；支持 ProgramData 下 JSON 扩展（学校自购冷门机型可自行添加）。
/// 物理毫米与像素坐标精确映射 → 不同型号笔迹粗细视觉一致。
/// </summary>
public sealed class DeviceProfile
{
    public required string Id { get; init; }
    public string ModelName { get; init; } = "";
    public int ScreenWidthPx { get; init; }
    public int ScreenHeightPx { get; init; }
    public double PhysicalWidthMm { get; init; }
    public double PhysicalHeightMm { get; init; }
    public double DiagonalInch { get; init; }

    /// <summary>红外框标称报点率（Hz）。实测有效点受固件负载影响，供插值参考。</summary>
    public int ReportRateHz { get; init; } = 120;

    // —— 平滑参数（按机型红外框抖动模型预设）——
    public double EmaAlpha { get; init; } = 0.45;     // EMA 系数
    public double MaxJumpPx { get; init; } = 480;     // 飞点裁剪阈值
    public double DropoutMs { get; init; } = 100;     // 断触插值窗口

    // —— 误触模型（触摸接触面积，px²）——
    public double FingerAreaMinPx2 { get; init; } = 120;   // 指尖接触面积下限
    public double FingerAreaMaxPx2 { get; init; } = 2600;  // 指尖接触面积上限（超过视为手掌）
    public double PalmAreaThresholdPx2 { get; init; } = 4000; // 手掌判定阈值

    // —— 边缘手势区（希沃系统边框手势，UI 停靠需避让）——
    public double GestureZoneLeftPx { get; init; }
    public double GestureZoneRightPx { get; init; }
    public double GestureZoneTopPx { get; init; }
    public double GestureZoneBottomPx { get; init; }

    /// <summary>是否红外触摸框机型（v0.3.0）：手势区避让、面积阈值等红外专项逻辑
    /// 只在此类机型启用。笔记本电容屏（华为 MateBook 等）绝不套用红外参数 ——
    /// 否则边缘死区 + 面积误判 + PxPerMm 失配三连杀（触摸书写全灭/笔迹亚像素）。
    /// 用户 JSON 扩展档案缺省 false（安全默认：未知机型不启用红外专项）。</summary>
    public bool IsInfraredFrame { get; init; }

    /// <summary>
    /// 运行时校准 PxPerMm（v0.4.0「希沃优化不定死」）：TouchDigitizer 用数字化仪
    /// 权威物理尺寸（GetPointerDevices rectPhysical，0.01mm）实测换算，直接覆盖档案猜测。
    /// null = 未校准（回退档案换算）。internal set：仅组合根/设置页在注入点写入，
    /// 不参与 JSON 序列化（公共只读口径，扩展档案无法伪造校准值）。
    /// 见 <see cref="PxPerMm"/> getter 的优先级。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? CalibratedPxPerMm { get; internal set; }

    /// <summary>校准来源（"startup"=启动探测 / "screen-change"=放映屏切换后重算 / null）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CalibrationSource { get; internal set; }

    /// <summary>
    /// 当前屏幕物理像素/mm。运行时设备 API 校准优先；否则优先使用实际显示器物理宽度
    /// 与档案面板宽度，最后才回退档案自身分辨率。这个值用于真实物理尺寸与 UI DIP 换算。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double PhysicalPxPerMm =>
        CalibratedPxPerMm is > 0.5 and < 20 ? CalibratedPxPerMm.Value :
        RuntimePhysicalWidthPx > 100 && PhysicalWidthMm > 0 ? RuntimePhysicalWidthPx / PhysicalWidthMm :
        ScreenWidthPx > 0 && PhysicalWidthMm > 0 ? ScreenWidthPx / PhysicalWidthMm : 96.0 / 25.4;

    /// <summary>
    /// 运行时墨迹画布是 3840×2160 的稳定逻辑坐标。实际显示器像素宽度可为 1920/2560/3840，
    /// 所以将物理 px/mm 映射到逻辑画布后再参与笔宽、橡皮尺寸与笔迹模型计算。
    /// 防止在 1080p 或高 DPI 屏幕上，笔宽与触点坐标使用不同单位而出现抬笔跳变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double PxPerMm => PhysicalPxPerMm * Math.Clamp(
        double.IsFinite(CanvasScale) && CanvasScale > 0 ? CanvasScale : 1.0, 0.25, 8.0);

    /// <summary>当前物理屏幕像素映射到 3840 逻辑画布的比例。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double CanvasScale { get; private set; } = 1.0;
    /// <summary>当前显示器有效 DPI 缩放因子，用于仅以 DIP 为单位的工具栏笔样预览。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double RuntimeDpiScale { get; private set; } = 1.0;
    /// <summary>当前放映显示器物理像素宽度（运行时测量）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double RuntimePhysicalWidthPx { get; private set; }

    public double MmToPx(double mm) => mm * PxPerMm;
    [System.Text.Json.Serialization.JsonIgnore]
    public double MinimumVisibleStrokePx => 2.5 * Math.Clamp(CanvasScale, 0.25, 8.0) * Math.Clamp(RuntimeDpiScale, 1.0, 4.0);
    public double MmToDip(double mm) => mm * PhysicalPxPerMm / Math.Clamp(
        double.IsFinite(RuntimeDpiScale) && RuntimeDpiScale >= 1 ? RuntimeDpiScale : 1.0, 1.0, 4.0);

    internal void ConfigureRuntimeDisplay(double physicalWidthPx, double dpiScale)
    {
        RuntimePhysicalWidthPx = double.IsFinite(physicalWidthPx) && physicalWidthPx >= 320 ? physicalWidthPx : 0;
        RuntimeDpiScale = double.IsFinite(dpiScale) && dpiScale is >= 1 and <= 4 ? dpiScale : 1.0;
        CanvasScale = RuntimePhysicalWidthPx > 0
            ? Math.Clamp(3840.0 / RuntimePhysicalWidthPx, 0.25, 8.0)
            : (ScreenWidthPx > 0 ? Math.Clamp(3840.0 / ScreenWidthPx, 0.25, 8.0) : 1.0);
    }
}

/// <summary>机型档案库：内置主流 + 用户扩展。</summary>
public sealed class DeviceProfileStore
{
    private readonly Dictionary<string, DeviceProfile> _profiles = new();

    public DeviceProfile Fallback { get; private set; } = null!;

    public void LoadDefaults()
    {
        // 希沃 86 寸 4K（如 MAXHUB/希沃 BM-86 系列）：1927×1102mm 物理幅面
        Add(new DeviceProfile
        {
            Id = "seewo-86-4k", ModelName = "希沃 86寸 4K", IsInfraredFrame = true,
            ScreenWidthPx = 3840, ScreenHeightPx = 2160,
            PhysicalWidthMm = 1896, PhysicalHeightMm = 1066, DiagonalInch = 86,
            ReportRateHz = 120, EmaAlpha = 0.46, MaxJumpPx = 520, DropoutMs = 100,
            FingerAreaMinPx2 = 500, FingerAreaMaxPx2 = 9000, PalmAreaThresholdPx2 = 16000,
            GestureZoneLeftPx = 64, GestureZoneRightPx = 64, GestureZoneTopPx = 48, GestureZoneBottomPx = 48,
        });

        // 希沃 86 寸 1080p（存量教室大量部署）
        Add(new DeviceProfile
        {
            Id = "seewo-86-1080p", ModelName = "希沃 86寸 1080p", IsInfraredFrame = true,
            ScreenWidthPx = 1920, ScreenHeightPx = 1080,
            PhysicalWidthMm = 1896, PhysicalHeightMm = 1066, DiagonalInch = 86,
            ReportRateHz = 100, EmaAlpha = 0.42, MaxJumpPx = 260, DropoutMs = 110,
            FingerAreaMinPx2 = 125, FingerAreaMaxPx2 = 2250, PalmAreaThresholdPx2 = 4000,
            GestureZoneLeftPx = 32, GestureZoneRightPx = 32, GestureZoneTopPx = 24, GestureZoneBottomPx = 24,
        });

        // 希沃 75 寸 4K
        Add(new DeviceProfile
        {
            Id = "seewo-75-4k", ModelName = "希沃 75寸 4K", IsInfraredFrame = true,
            ScreenWidthPx = 3840, ScreenHeightPx = 2160,
            PhysicalWidthMm = 1650, PhysicalHeightMm = 928, DiagonalInch = 75,
            ReportRateHz = 120, EmaAlpha = 0.44, MaxJumpPx = 460, DropoutMs = 100,
            FingerAreaMinPx2 = 440, FingerAreaMaxPx2 = 8000, PalmAreaThresholdPx2 = 14000,
            GestureZoneLeftPx = 56, GestureZoneRightPx = 56, GestureZoneTopPx = 44, GestureZoneBottomPx = 44,
        });

        // 笔记本 / 普通触控屏（试用与备课场景：无红外框；鼠标/电容触摸轨迹本身无抖动，高 EMA 直通保跟手）
        Add(new DeviceProfile
        {
            Id = "laptop-1080p", ModelName = "笔记本 15.6寸 1080p",
            ScreenWidthPx = 1920, ScreenHeightPx = 1080,
            PhysicalWidthMm = 344, PhysicalHeightMm = 194, DiagonalInch = 15.6,
            ReportRateHz = 60, EmaAlpha = 0.55, MaxJumpPx = 200, DropoutMs = 40,
            FingerAreaMinPx2 = 40, FingerAreaMaxPx2 = 3200, PalmAreaThresholdPx2 = 6000,
        });

        // 通用兜底（未知机型）
        Add(new DeviceProfile
        {
            Id = "generic", ModelName = "通用 1080p 兜底",
            ScreenWidthPx = 1920, ScreenHeightPx = 1080,
            PhysicalWidthMm = 1209, PhysicalHeightMm = 680, DiagonalInch = 55,
            ReportRateHz = 60, EmaAlpha = 0.40, MaxJumpPx = 240, DropoutMs = 120,
        });

        Fallback = _profiles["generic"];

        // 用户扩展：C:\ProgramData\PptInk\profiles\*.json
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PptInk", "profiles");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            // 逐文件容错：try/catch 收进循环体 —— 单个坏档案（非法 JSON / 缺 required Id）
            // 只跳过自身，不得终止整个循环连坐其余扩展档案；Deserialize 对 JSON null
            // 文件返回 null，Add 前判空（否则 _profiles[p.Id] 抛 NRE 同样打断循环）
            try
            {
                if (JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(f)) is { } p)
                    Add(p);
            }
            catch { /* 单个扩展档案损坏不阻塞启动 */ }
        }
    }

    private void Add(DeviceProfile p) => _profiles[p.Id] = p;

    public DeviceProfile Get(string? id) =>
        id is not null && _profiles.TryGetValue(id, out var p) ? p : Fallback;

    /// <summary>
    /// 按实际放映屏物理分辨率解析最佳档案（审查修复）：
    /// 配置档案命中且分辨率与实际相近（±15%）→ 尊重配置；否则按屏幕宽度选最接近内置档。
    /// 解决默认 DeviceProfileId 与内置档案集不匹配时静默回退 generic ——
    /// 4K 大屏会因此按 1080p 算 PxPerMm（笔迹粗细错约一半）与误触阈值。
    /// 双屏部署（笔记本+大屏）时以放映屏为准，主屏分辨率与机型档案无关。
    /// v0.3.0：高 DPI 缩放屏（>125%）直接选笔记本档案 —— 希沃红外大屏恒 100% 缩放，
    /// 高缩放必是笔记本/桌面显示器；华为 2160 物理宽屏若按宽度匹配会命中 86 寸希沃档案
    /// （PxPerMm≈1.0 vs 真实≈3.1 → 2.2mm 笔只剩亚像素宽，"写了看不见"），此门禁杜绝。
    /// v0.4.0 门禁软化（希沃优化不定死）：探测到<b>大尺寸触摸面板</b>（digitizer 物理对角
    /// &gt; 40 寸）时不再因高缩放强制笔记本档案 —— 大屏被用户改了缩放的场景，物理事实
    /// （面板尺寸）比启发式规则（缩放值）更可信；PxPerMm 由运行时校准权威覆盖。
    /// </summary>
    public DeviceProfile ResolveBest(string? configuredId, TouchDigitizer? digitizer = null)
    {
        // 优先使用显示分辨率与真实触摸硬件事实。旧的「DPI > 125% 就视为笔记本」
        // 在 4K 希沃大屏上会误选 15.6 寸档案（尤其是设备 API 未暴露红外框时），
        // 让 2.2mm 笔宽被放大数倍。只有分辨率不像大屏、也未探测到大触摸面板时，
        // 才用高 DPI 作为笔记本的保守启发式。
        bool largePanel = digitizer?.IsLargeTouchPanel ?? false;
        int screenW = DetectShowScreenWidthPx();
        bool highResolutionDisplay = screenW >= 3000;
        if (!largePanel && !highResolutionDisplay)
        {
            try
            {
                if (ShowScreen.DpiScale > 1.25)
                {
                    var laptop = _profiles.TryGetValue("laptop-1080p", out var lp) ? lp : Fallback;
                    if (configuredId is not null && _profiles.TryGetValue(configuredId, out var cfg) && !cfg.IsInfraredFrame)
                        return cfg;   // 用户显式配置了非红外档案则尊重
                    return laptop;
                }
            }
            catch { /* DPI 查询异常走原逻辑 */ }
        }

        if (configuredId is not null && _profiles.TryGetValue(configuredId, out var configured))
        {
            if (screenW <= 0 || Math.Abs(configured.ScreenWidthPx - screenW) <= screenW * 0.15)
                return configured;
        }
        if (screenW <= 0) return Fallback;
        DeviceProfile? best = null;
        int bestDiff = int.MaxValue;
        foreach (var p in _profiles.Values)
        {
            int diff = Math.Abs(p.ScreenWidthPx - screenW);
            if (diff < bestDiff) { bestDiff = diff; best = p; }
        }
        return best ?? Fallback;
    }

    /// <summary>放映屏物理宽度（px）：双屏部署时以放映屏（大屏）分辨率匹配机型档案；
    /// ShowScreen 未定位（无放映窗口）时回退主屏口径，异常回退 0。</summary>
    private static int DetectShowScreenWidthPx()
    {
        try
        {
            var b = ShowScreen.Bounds;
            if (b.Width <= 0) return 0;
            return (int)Math.Round(b.Width * ShowScreen.DpiScale);
        }
        catch { return 0; }
    }

    public IReadOnlyCollection<DeviceProfile> All => _profiles.Values;
}
