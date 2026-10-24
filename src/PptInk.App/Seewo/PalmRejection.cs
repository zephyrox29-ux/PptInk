using System.Windows;
using PptInk.Core;

namespace PptInk.Seewo;

/// <summary>
/// 手掌误触抑制（v0.3.1 手写最优重写）。
///
/// 产品目标（用户明确定义）：永远不考虑主动手写笔；被动手写笔与手指在系统层面
/// 同为触摸点，不做区分。<b>手写（希沃红外触摸 + 笔记本电容触摸）是唯一最优路径。</b>
///
/// 由此推论的架构纪律：
///  - 不存在「笔优先」策略（旧实现的 penInRange 判定与 StylusInAirMove 检测已删除，
///    后者在电容屏上被手指 hover 误触发，是华为笔记本触摸全灭的连环杀手之一）；
///  - 不存在按指针类型的分支（PT_PEN/PT_TOUCH 同路判定）；
///  - 唯一职责：从触摸 down 中识别「明显是手掌的巨物接触」并拒绝。
///
/// 判定原则（最坏前提工程）：判定不确定时一律放行 —— 漏画是课堂事故，误画可擦除。
///  - 手掌 = 面积显著超出本会话指尖分布（中位数 ×倍率）且绝对值巨大（>2500px²）；
///  - 严格模式（原「仅指尖书写」开关，语义更新）：倍率 4 → 2.5，手掌更早被识别；
///  - 面积下限只丢 &lt;9px² 的极端孤点（亚毫米级串扰，任何屏的指尖都远大于此）；
///  - 手势区只在红外框机型（IsInfraredFrame）启用；
///  - 无接触元数据（驱动不填 rcContact）→ 放行。
/// </summary>
public sealed class PalmRejection
{
    private readonly DeviceProfileStore _store;
    private DeviceProfile _profile;
    private readonly List<double> _areaWindow = new();   // 自适应窗口（最近 32 个被接受的接触面积）

    public PalmRejection(DeviceProfileStore store, string profileId)
    {
        _store = store;
        _profile = store.Get(profileId);
    }

    /// <summary>总开关（配置 PalmRejectionEnabled）。</summary>
    public bool Enabled { get; set; } = AppConfig.Current.PalmRejectionEnabled;

    /// <summary>严格手掌抑制（原「仅指尖书写」，v0.3.1 语义更新）：手掌判定倍率收紧
    /// （4 → 2.5），手掌更早被识别拒绝。手写最优架构下不再有「笔让位」语义。</summary>
    public bool OnlyFingerMode { get; set; } = AppConfig.Current.OnlyFingerInk;

    /// <summary>手势区避让开关（配置 RespectGestureZone）。实际生效还需档案 IsInfraredFrame。</summary>
    public bool RespectGestureZone { get; set; } = AppConfig.Current.RespectGestureZone;

    /// <summary>拦截到手掌误触时触发（UI 可轻提示，不打断上课节奏）。</summary>
    public event EventHandler? PalmBlocked;

    /// <summary>最近一次判定理由（v0.4.0 诊断模式：HUD / 首笔日志 / 拒绝跟踪显示）。
    /// 取值："接受" / "接受（未启用）" / "接受（无接触元数据）" / "拒绝：手掌（面积 N px² > 阈值 M）"
    /// / "拒绝：边缘手势区" / "拒绝：孤点串扰"。</summary>
    public string LastVerdict { get; private set; } = "";

    /// <summary>最近一次判定的结构化结果（v0.4.1 修复）：诊断层（墨迹层绿/红矩形）
    /// 直接读本布尔值，不再解析 LastVerdict 中文前缀 —— 判定理由的措辞/翻译变更
    /// 不再影响诊断显示。与 LastVerdict 同步更新。</summary>
    public bool LastAccepted { get; private set; } = true;

    public void ApplyProfile(DeviceProfile profile) => _profile = profile;

    /// <summary>实际放映屏物理矩形（px，虚拟桌面全局坐标）：优先 ShowScreen 实测（档案 ScreenWidthPx 在
    /// 非 1080p/非 4K 笔记本上失配 —— 2160 物理宽屏配 1920 档案会造成右侧 240px 触摸死区）。
    /// Bounds 是 DIP 且原点可为非 (0,0)（扩展屏部署），乘 DpiScale 还原为全局物理原点+尺寸；
    /// 异常回退档案宽高时原点取 (0,0)（单主屏口径）。</summary>
    private (double X, double Y, double W, double H) ScreenPhysicalRectPx()
    {
        try
        {
            var b = ShowScreen.Bounds;
            double s = ShowScreen.DpiScale;
            return (b.Left * s, b.Top * s, b.Width * s, b.Height * s);
        }
        catch { return (0, 0, _profile.ScreenWidthPx, _profile.ScreenHeightPx); }
    }

    public bool IsInGestureZone(Point p)
    {
        // 红外框机型门禁（v0.3.0）：手势区是希沃系统边框手势的避让区，
        // 笔记本/普通电容屏上不存在该交互，启用只会制造边缘死区
        if (!RespectGestureZone || !_profile.IsInfraredFrame) return false;
        // p 是虚拟桌面全局物理 px（WM_POINTER rcContact 原始值），须先减放映屏物理原点
        // 平移到板面本地坐标再比四边手势区 —— 否则扩展屏部署（放映屏原点非 (0,0)）下
        // 判定区间整体错位：板在主屏右侧时右半块被误拒，板在主屏左侧时整板被误拒
        var (x, y, w, h) = ScreenPhysicalRectPx();
        p = new Point(p.X - x, p.Y - y);
        return p.X < _profile.GestureZoneLeftPx ||
               p.X > w - _profile.GestureZoneRightPx ||
               p.Y < _profile.GestureZoneTopPx ||
               p.Y > h - _profile.GestureZoneBottomPx;
    }

    /// <summary>
    /// 判定一次触摸 down 是否允许起笔（v0.3.1：手写最优，无指针类型分支）。
    /// 手指与被动手写笔在系统层面同为触摸点，本方法对其一视同仁。
    /// </summary>
    /// <param name="contactRectPx">rcContact 接触矩形（屏幕物理 px）；无效（宽高≤0）表示驱动未提供。</param>
    public bool ShouldAcceptInk(Rect contactRectPx)
    {
        if (!Enabled) { LastVerdict = "接受（未启用）"; LastAccepted = true; return true; }

        // 无有效接触元数据：不能因缺数据拒绝书写
        //（原实现 area=0 < 下限恒拒绝 —— rcContact 不填的触摸屏上手指书写全灭）
        if (contactRectPx.Width <= 0 || contactRectPx.Height <= 0)
        {
            LastVerdict = "接受（无接触元数据）";
            LastAccepted = true;
            return true;
        }

        // 边缘手势区：仅红外框机型（希沃系统边框手势让出）
        if (IsInGestureZone(new Point(contactRectPx.X, contactRectPx.Y)) ||
            IsInGestureZone(new Point(contactRectPx.Right, contactRectPx.Bottom)))
        {
            LastVerdict = "拒绝：边缘手势区";
            LastAccepted = false;
            return false;
        }

        double area = contactRectPx.Width * contactRectPx.Height;

        // 极端孤点（<9px²，即 3×3px 以下）：任何屏的指尖都远大于此，只可能是串扰/鬼点
        if (area < 9) { LastVerdict = "拒绝：孤点串扰"; LastAccepted = false; return false; }

        // 手掌判定（自适应）：面积显著超出本会话指尖分布（中位数 × 倍率）且绝对值巨大
        //（>2500px²，即 50×50px 以上的接触斑块）才判手掌。自适应使红外大屏（接触面积
        // 天然大）与笔记本电容屏（面积天然小）都无需按机型校准。
        double ratio = OnlyFingerMode ? 2.5 : 4.0;
        double threshold = Math.Max(2500, MedianArea() * ratio);
        if (area > threshold)
        {
            LastVerdict = $"拒绝：手掌（{area:F0}px² > 阈值{threshold:F0}）";
            LastAccepted = false;
            PalmBlocked?.Invoke(this, EventArgs.Empty);
            return false;
        }

        // 通过：沉淀进自适应窗口（供后续手掌判定参考）
        LastVerdict = "接受";
        LastAccepted = true;
        _areaWindow.Add(area);
        if (_areaWindow.Count > 32) _areaWindow.RemoveAt(0);
        return true;
    }

    /// <summary>会话内接触面积中位数（窗口为空返回 0 → 手掌判定退化为绝对阈值）。</summary>
    private double MedianArea()
    {
        if (_areaWindow.Count == 0) return 0;
        var sorted = _areaWindow.OrderBy(a => a).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
