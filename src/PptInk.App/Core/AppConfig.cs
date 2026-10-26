using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace PptInk.Core;

/// <summary>工具栏元素布局项（Id 见 UI.ToolbarCatalog；顺序即渲染顺序）。</summary>
public sealed class ToolbarItemConfig
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>用户配置（%LocalAppData%\PptInk\config.json 持久化）。</summary>
public sealed class AppConfig
{
    public bool HideTrayIcon { get; set; } = false;
    public bool AutoStart { get; set; } = true;

    /// <summary>希沃"仅指尖书写"模式：以触摸接触面积区分指尖/手掌。</summary>
    public bool OnlyFingerInk { get; set; } = false;
    public bool PalmRejectionEnabled { get; set; } = true;

    /// <summary>
    /// 新手引导开关（托盘菜单勾选状态持久化）：
    /// 勾选 = 每次进入放映（S1）自动提示一次引导；完成一次完整引导后自动取消勾选。
    /// </summary>
    public bool EnableOnboarding { get; set; } = true;

    /// <summary>UI 停靠位置避开系统手势区、可配置让出。</summary>
    public bool RespectGestureZone { get; set; } = true;

    /// <summary>
    /// 笔记本 / 普通触控屏试用模式（非希沃大屏环境）：
    ///  - 关闭手掌误触抑制（接触面积阈值按红外框调校，电容屏/鼠标会误杀）；
    ///  - 机型档案切换为笔记本屏（笔迹粗细毫米换算按 15.6 寸屏校准）；
    ///  - 鼠标书写兜底始终可用（不依赖本开关，带触摸防双发）。
    /// </summary>
    public bool LaptopTestMode { get; set; } = false;

    // ———————— 输入模式（v0.8.2 三档：设置中心实时切换，切档重建墨迹层） ————————

    /// <summary>
    /// 墨迹层书写链路三档（「笔写不出来」环境的按档排查路径）：
    ///  - "touch"（默认）：layered 透明层 + alpha=1 命中资格底刷 + 三通道全开
    ///    （WM_POINTER / StylusPlugIn / 合成鼠标），120Hz 低延迟标准体验；
    ///  - "compat"：同样 layered 层，但只信通道 C（触摸→鼠标提升链）—— 与工具栏
    ///    按钮完全相同的输入链路。工具栏可点而墨迹层写不出的环境选此项
    ///    （板中板可画 = 该链路活着的实测旁证）；代价：报点率/真实压感降级；
    ///  - "super"：全屏普通不透明窗口（非 layered，深色画布 #16201E）—— 整窗
    ///    命中 100% 保证（彻底绕开 layered 逐像素命中规则），任何环境的最后
    ///    手段。书写时遮挡幻灯片（应急白板）；切「触摸操作」或退出放映自动
    ///    收回（隐藏窗口），PPT 重现。
    /// 设计立场（与用户对齐）：程序无法区分「用户没摸屏」与「触摸未收到」，
    /// 故不做自动兜底 —— 状态牌亮出事实 + 用户手动选档。
    /// </summary>
    public string InputMode { get; set; } = "touch";

    /// <summary>整窗命中环境声明（v0.9.0 统一舞台用）：WPF layered 窗口的输入命中
    /// 在部分 Windows 版本/驱动/定制系统（希沃大屏）按逐像素 alpha 断言 —— 此时
    /// 单窗口可借「底刷 alpha」实现区域穿透（书写拦截 + 触摸操作时空白穿透给 PPT、
    /// UI 元素照常可点）。标准 DWM 环境按整窗矩形命中（alpha 不参与），区域穿透
    /// 不成立，穿透态退化为 WS_EX_TRANSPARENT 全窗放行（UI 暂不可点）。
    /// true = 标准整窗命中环境（区域穿透不可用）；false（默认）= 逐像素 alpha
    /// 命中环境（区域穿透启用）。真机实测书写/穿透表现异常时可手动切换本项。</summary>
    /// <summary>触摸操作（穿透）态是否隐藏板书：逐像素命中环境下墨迹像素 alpha&gt;0 会
    /// 截获输入（板书密布时无法操作 PPT 上的可交互项 —— 真实教学痛点）；隐藏后该像素
    /// alpha=0 → 触摸/鼠标天然穿透到 PPT，板书数据保留、切回书写立刻重现。默认开启（可靠）。
    /// 关闭时保留板书可见，由穿透态 WM_NCHITTEST 分流尽力放行（少数机型触摸提升不经
    /// 该判定时仍会被截获，故默认走隐藏）。</summary>
    public bool TouchThroughInkHidden { get; set; } = true;

    /// <summary>标准 DWM 整窗命中环境声明（alpha 不参与命中）。逐像素环境=false。</summary>
    public bool WholeWindowHit { get; set; }

    /// <summary>墨迹层状态牌（仅调试模式下可选显示）：正常放映默认关闭，避免左上角常驻信息干扰课堂。</summary>
    public bool InkStatusBadgeEnabled { get; set; } = false;

    /// <summary>希沃机型档案 ID（86/75 寸等），见 Seewo.DeviceProfileStore。
    /// null = 自动选择（v0.4.1 修复：旧默认 "seewo-86-generic" 不存在于档案库，
    /// 恒回退 generic —— 4K 大屏按 1080p 口径算笔迹粗细失真约一半。现在默认走
    /// ResolveBest 按放映屏物理宽度匹配最接近内置档，与「希沃优化不定死」一致；
    /// 设置中心切换档案后写入具体 ID，尊重用户显式选择）。</summary>
    public string? DeviceProfileId { get; set; }

    public int HysteresisMs { get; set; } = 500;

    // ———————— 外观（设置中心实时调整） ————————

    /// <summary>主题："Dark"（默认，课堂暗环境舒适）/"Light"/"Auto"（跟随系统）。</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>强调色（#RRGGBB）。预设见 ThemeManager.AccentPresets，支持自定义。</summary>
    public string AccentColor { get; set; } = "#78AFA5";   // v0.8.0 衢青（岱山衢青主题默认强调）

    /// <summary>面板材质："Solid" 实体 / "Translucent" 半透明（默认）/"Acrylic" 亚克力模糊 / "Minimal" 极简融合。</summary>
    public string Material { get; set; } = "Translucent";

    // ———————— 插件（设置只负责启用/禁用；实际组件通过放映工具栏面板唤起） ————————

    /// <summary>被用户禁用的插件 ID；不在此列表中的新插件默认可用。</summary>
    public List<string> DisabledPluginIds { get; set; } = new();

    // ———————— 工具栏布局（设置中心可调：排序 + 显隐） ————————

    /// <summary>工具栏元素顺序与可见性（Id 对应 UI.ToolbarCatalog.All）。</summary>
    public List<ToolbarItemConfig> ToolbarLayout { get; set; } = CreateDefaultToolbarLayout();

    // ———————— 擦除 ————————

    /// <summary>点按"擦除"后进入的默认模式："stroke" 涂抹 / "lasso" 圈画（浮岛可随时换）。</summary>
    public string DefaultEraseMode { get; set; } = "stroke";

    // ———————— 书写体验（第四轮打磨） ————————

    /// <summary>
    /// 整页清空后的工具焦点："smart"（默认，清空即重新板书 → 切回上次书写工具）/
    /// "pen"（书写笔）/"touch"（触摸操作）/"keep"（保持清空手势前的工具）。
    /// </summary>
    public string AfterClearTool { get; set; } = "smart";

    /// <summary>
    /// 识别灵敏度系数 0.75~1.3：越大越宽松（手绘随意也能规整），越小越严格（只有画得准才触发）。
    /// 调制直线容差 / 闭合阈值 / 圆度 / 圆残差 / 角点转向角等全部识别阈值。
    /// </summary>
    public double RecognitionSensitivity { get; set; } = 1.0;

    /// <summary>最近使用颜色（最近优先，最多 5 个；画笔浮岛"最近使用"区数据源，书写提交时更新）。</summary>
    public List<string> RecentColors { get; set; } = new();

    // ———————— 翻页笔 ————————

    /// <summary>
    /// 翻页笔转发：焦点落在 PptInk（刚点过工具栏）时，拦截 PageUp/PageDown/←/→ 并转发给 PPT。
    /// 教师"点工具栏 → 按翻页笔"零切换成本。
    /// </summary>
    public bool PresenterForwardEnabled { get; set; } = true;

    // 墨迹默认值
    public string DefaultColor { get; set; } = "#E8442E"; // 朱红：教学批注醒目
    public double DefaultThicknessMm { get; set; } = 2.2;
    public string HighlightColor { get; set; } = "#F5D000";
    public double EraserRadiusMm { get; set; } = 18;

    /// <summary>擦除模式（v0.9.6）："pixel"=像素级（只擦轨迹覆盖的笔迹局部，默认）；"stroke"=整笔擦除。</summary>
    public string EraseMode { get; set; } = "pixel";

    // AI（v1.0.0：识别时机 / 范围 / 能力全参数化）
    public bool AiEnabled { get; set; } = false; // 默认关闭：AI 未启用时 Python 子进程不驻留内存
    public int ShapeRecognizeDelayMs { get; set; } = 1000; // 落笔停顿约 1s 弹"规整为"

    /// <summary>自动识别呈现模式：auto = 停顿后即刻替换（快，0.7× 停顿即出手）；bubble = 弹「规整为」气泡点按确认。</summary>
    public string ShapeAssistMode { get; set; } = "auto";

    /// <summary>识别触发时机（v1.0.0）："pause"=抬笔停顿后识别（默认，防连笔误识）；
    /// "lift"=抬笔即识（最快，适合快速作图）；"manual"=只在工具栏点【AI】时识别（最不打扰）。</summary>
    public string AiTriggerTiming { get; set; } = "pause";

    /// <summary>识别范围（v1.0.0）："last"=只认刚抬笔的那一笔（默认）；
    /// "recent"=合并最近若干笔一起认（多笔组合图形，如"圆+箭头"）。</summary>
    public string AiScope { get; set; } = "last";

    /// <summary>范围为 recent 时合并的笔数上限 2~6。</summary>
    public int AiScopeStrokeCount { get; set; } = 2;

    /// <summary>置信度阈值 0.30~0.98：低于此值不规整（越高越保守，越不易误识）。</summary>
    public double AiConfidenceThreshold { get; set; } = 0.60;

    /// <summary>识别超时（毫秒）300~10000：超过则放弃本次识别（板书不卡）。</summary>
    public int AiRecognizeTimeoutMs { get; set; } = 3000;

    /// <summary>最小识别尺寸（毫米）0~40：笔迹包围盒任一边小于此值不识别（避开小勾、小点误触）。</summary>
    public double AiMinSizeMm { get; set; } = 6;

    /// <summary>形状白名单（分号分隔，如 "circle;line;arrow"）；空 = 全部形状均可识别。
    /// 用于禁用课堂上常被误识的形状（如写字被认成箭头）。</summary>
    public string AiShapeWhitelist { get; set; } = "";

    /// <summary>识别成功后高亮提示笔迹（短暂强调，让老师知道"刚才那笔画被规整了"）。</summary>
    public bool AiHighlightOnSuccess { get; set; } = true;

    // ———————— 笔墨高级参数（v1.0.0 高级模式：精调手感） ————————

    /// <summary>平滑笔迹总开关（默认关 = 实时笔迹）：实时模式用笔尖原始报点即时描边、
    /// 0 位置滞后、落笔即所见；开启后走 OneEuro 自适应平滑（红外框抗抖，但慢写略滞后）。
    /// 每笔落笔时读取，实时生效，两方向都可回退。</summary>
    public bool SmoothInk { get; set; } = false;

    /// <summary>平滑强度 0.20~0.80（越大越跟手，越小越稳）：映射 OneEuro 截止频率（仅平滑模式生效）。</summary>
    public double PenSmoothing { get; set; } = 0.45;

    /// <summary>速度笔锋幅度 0.0~1.0：0 = 等宽（无笔锋），1 = 强笔锋（快写明显收细）。</summary>
    public double PenTaperStrength { get; set; } = 0.5;

    /// <summary>起笔渐入长度（点）0~20：0 = 无渐入；越大落笔由细到粗的过渡越长。</summary>
    public int PenLeadInPoints { get; set; } = 8;

    /// <summary>收笔收锋长度（点）0~24：0 = 无收锋。</summary>
    public int PenLiftPoints { get; set; } = 12;

    /// <summary>宽度低通系数 0.10~0.90：越大宽度跟随速度越快（越活），越小越平滑稳重。</summary>
    public double PenWidthLowpass { get; set; } = 0.30;

    /// <summary>压感调制强度 0.0~1.0：0 = 忽略压感（无压感设备恒等宽），1 = 强调制。</summary>
    public double PenPressureGain { get; set; } = 0.5;

    /// <summary>荧光笔笔尖横竖比 0.30~1.00：1.0 = 圆形笔尖（无方向）；0.5 = 竖矩形（横画粗、竖画细）。</summary>
    public double HighlighterTipRatio { get; set; } = 0.5;

    /// <summary>荧光笔透明度 0.20~0.80（越低越透，越不遮文字）。</summary>
    public double HighlighterAlpha { get; set; } = 0.43;

    /// <summary>高级模式总开关：开启后设置中心显示高级页，可用上述精调参数。</summary>
    public bool AdvancedMode { get; set; } = false;

    // 插件
    public bool PluginsEnabled { get; set; } = true;

    /// <summary>
    /// 调试模式（v0.4.0）：放映屏角落常驻诊断面板 —— 输入通道、接触面积、压感探测、
    /// 报点率、机型档案与运行时校准、渲染 FPS；墨迹层实时绘制接触矩形（绿=接受/红=拒绝）。
    /// 面向售后定位与开发调参，默认关闭（诊断上报在关闭时近乎零开销）。
    /// </summary>
    public bool DebugMode { get; set; } = false;

    /// <summary>工具栏整体缩放（0.7~1.6）：大屏远距操作时调大更易按。</summary>
    public double ToolbarScale { get; set; } = 1.0;

    /// <summary>是否在无交互一段时间后将工具栏淡化。触摸优先默认关闭，避免控件看似消失。</summary>
    public bool ToolbarIdleFadeEnabled { get; set; } = false;

    /// <summary>工具栏闲置淡化等待秒数（0.5–15）。</summary>
    public double ToolbarIdleFadeSeconds { get; set; } = 2.5;

    /// <summary>工具栏闲置后的不透明度（0.2–1.0）；数值越高越清晰。</summary>
    public double ToolbarIdleOpacity { get; set; } = 0.45;

    /// <summary>减少非必要界面动效：保留状态和回调，但跳过淡入、缩放与吸附动画。</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>轻量渲染：关闭纯装饰性的落笔反馈圈、消散粒子与清空扫光，保留墨迹和擦除结果。</summary>
    public bool LightweightRendering { get; set; } = false;

    /// <summary>退出/换课件时自动把板书导出到 桌面\PptInk导出\。v1.1.1 隐私默认关闭：
    /// 共用教室电脑上，默认把整节课板书写到桌面（任何登录者可见）不合适。需要留存用
    /// 托盘/设置的手动导出；开启本项恢复 1.1.0 及之前的自动行为。</summary>
    public bool AutoExportOnExit { get; set; } = false;

    // 上次停靠位置（重启还原）
    public double? ToolbarDockX { get; set; }
    public double ToolbarDockY { get; set; } = double.NaN;

    // ———————— 灵动岛布局（v0.7.0） ————————

    /// <summary>旧版兼容字段：左右翻页岛现在始终存在，初始化时会迁移为 true。</summary>
    public bool PageIslandRightEnabled { get; set; } = true;
    /// <summary>旧版兼容字段：左右翻页岛现在始终存在，初始化时会迁移为 true。</summary>
    public bool PageIslandLeftEnabled { get; set; } = true;

    /// <summary>旧版兼容字段：固定角落版本不再读取可移动位置记忆。</summary>
    public double PageIslandRightX { get; set; } = double.NaN;
    public double PageIslandRightY { get; set; } = double.NaN;
    public double PageIslandLeftX { get; set; } = double.NaN;
    public double PageIslandLeftY { get; set; } = double.NaN;

    /// <summary>
    /// 工具栏分割点（v0.7.0 可分割）：值为「主栏最后一个元素 Id」（如 "erase"），
    /// 其后的 Ops 段元素（撤销/重做/AI/插件/结束放映）由独立副岛承载。
    /// null = 一体不分割。分割/合并实时写回。
    /// </summary>
    public string? ToolbarSplitAfterId { get; set; }

    /// <summary>副岛（Ops 段拉出后的独立岛）停靠位置记忆。</summary>
    public double? SegmentDockX { get; set; }
    public double SegmentDockY { get; set; } = double.NaN;

    /// <summary>默认工具栏布局：书写工具 | 操作 | 翻页。设置中心可任意重排/隐藏。</summary>
    public static List<ToolbarItemConfig> CreateDefaultToolbarLayout() => new()
    {
        new ToolbarItemConfig { Id = "touch",       Visible = true },
        new ToolbarItemConfig { Id = "pen",         Visible = true },
        new ToolbarItemConfig { Id = "highlighter", Visible = true },
        new ToolbarItemConfig { Id = "erase",       Visible = true },
        new ToolbarItemConfig { Id = "undo",        Visible = true },
        new ToolbarItemConfig { Id = "redo",        Visible = true },
        new ToolbarItemConfig { Id = "ai",          Visible = true },
        new ToolbarItemConfig { Id = "plugin",      Visible = true },
        new ToolbarItemConfig { Id = "nav",         Visible = true },
    };

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk");
    private static readonly string Path_ = Path.Combine(Dir, "config.json");
    private static readonly object SaveGate = new();
    // Positions use NaN as an intentional "not placed yet" sentinel. The default
    // System.Text.Json settings reject NaN/Infinity and previously made every Save
    // fail silently on a fresh profile. Read and write with the same explicit policy.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppConfig Current { get; } = Load();

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_, Encoding.UTF8), ConfigJsonOptions) ?? new AppConfig();
                Normalize(cfg);
                return cfg;
            }
        }
        catch (Exception ex)
        {
            // 配置损坏：回退默认（绝不阻塞启动），但不再静默 —— 备份坏文件并记日志。
            // 原先 catch{} 直接吞：手滑改错一个逗号，全部设置无痕丢失，用户和售后都无从知晓。
            try
            {
                if (File.Exists(Path_))
                    File.Copy(Path_, Path_ + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
            }
            catch { }
            try { AppLog.Instance.Warn($"config.json 损坏，已备份并回退默认：{ex.Message}"); } catch { }
        }
        return new AppConfig();
    }

    /// <summary>布局修补：剔除未知 Id、补齐新增项（追加在末位）、保底书写工具可见。
    /// v0.9.7 自愈：旧配置把 Ops 组（undo/redo/ai/plugin）全部隐藏、或分割点后无可见元素时，
    /// 工具栏会只剩「笔/荧光笔/触摸/擦除」四个按钮且无任何入口恢复 —— 此类坏配置
    /// 自动修复：Ops 组至少保留撤销/重做/AI/插件可见，失效分割标记自动清除。</summary>
    private static void NormalizeToolbarLayout(AppConfig cfg)
    {
        var known = new HashSet<string> { "touch", "pen", "highlighter", "erase", "undo", "redo", "ai", "plugin", "nav" };
        cfg.ToolbarLayout ??= CreateDefaultToolbarLayout();
        cfg.ToolbarLayout.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.Id) || !known.Contains(t.Id));
        var present = cfg.ToolbarLayout.Select(t => t.Id).ToHashSet();
        foreach (var def in CreateDefaultToolbarLayout())
            if (!present.Contains(def.Id))
                cfg.ToolbarLayout.Add(def);
        // 保底：笔必须可见（任何情况下教师都能立即书写）
        if (cfg.ToolbarLayout.FirstOrDefault(t => t.Id == "pen") is { } pen)
            pen.Visible = true;
        if (cfg.ToolbarSplitAfterId is not null && !known.Contains(cfg.ToolbarSplitAfterId))
            cfg.ToolbarSplitAfterId = null;

        // v0.9.7 自愈 1：Ops 组全隐藏 = 坏配置（用户无从恢复撤销/AI/插件入口），重置 Ops 组可见。
        // 仅当四项全部不可见时触发 —— 用户显式隐藏其中一两项的偏好仍被尊重。
        var opsIds = new[] { "undo", "redo", "ai", "plugin" };
        bool anyOpsVisible = cfg.ToolbarLayout.Any(t => opsIds.Contains(t.Id) && t.Visible);
        if (!anyOpsVisible)
            foreach (var t in cfg.ToolbarLayout.Where(t => opsIds.Contains(t.Id)))
                t.Visible = true;

        // v1.1.2：分割副岛特性已移除（分割缝误触导致 Ops 段进入常不可见的副岛，
        // 「工具栏只剩基础工具」的报告根因）。旧配置一律清除标记，主栏恢复全量。
        cfg.ToolbarSplitAfterId = null;
    }

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static string SafeColor(string? value, string fallback) =>
        IsHexColor(value) ? value!.ToUpperInvariant() : fallback;

    /// <summary>修复旧配置/人工编辑配置中的越界值、非有限数与未知枚举；配置错误不能击穿输入/渲染链路。</summary>
    private static void Normalize(AppConfig cfg)
    {
        NormalizeToolbarLayout(cfg);
        cfg.DisabledPluginIds ??= new List<string>();
        cfg.DisabledPluginIds = cfg.DisabledPluginIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        cfg.RecentColors ??= new List<string>();
        cfg.RecentColors.RemoveAll(c => !IsHexColor(c));
        cfg.RecentColors = cfg.RecentColors.Select(c => c.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Take(5).ToList();

        cfg.RecognitionSensitivity = double.IsFinite(cfg.RecognitionSensitivity)
            ? Math.Clamp(cfg.RecognitionSensitivity, 0.75, 1.3) : 1.0;
        cfg.DefaultThicknessMm = double.IsFinite(cfg.DefaultThicknessMm)
            ? Math.Clamp(cfg.DefaultThicknessMm, 0.6, 6.0) : 2.2;
        cfg.EraserRadiusMm = double.IsFinite(cfg.EraserRadiusMm)
            ? Math.Clamp(cfg.EraserRadiusMm, 2.0, 120.0) : 18.0;
        cfg.ShapeRecognizeDelayMs = Math.Clamp(cfg.ShapeRecognizeDelayMs, 150, 5000);
        cfg.ToolbarScale = double.IsFinite(cfg.ToolbarScale) ? Math.Clamp(cfg.ToolbarScale, 0.7, 1.6) : 1.0;
        cfg.ToolbarIdleFadeSeconds = double.IsFinite(cfg.ToolbarIdleFadeSeconds)
            ? Math.Clamp(cfg.ToolbarIdleFadeSeconds, 0.5, 15.0) : 2.5;
        cfg.ToolbarIdleOpacity = double.IsFinite(cfg.ToolbarIdleOpacity)
            ? Math.Clamp(cfg.ToolbarIdleOpacity, 0.2, 1.0) : 0.45;
        cfg.HysteresisMs = Math.Clamp(cfg.HysteresisMs, 100, 5000);

        cfg.InputMode = cfg.InputMode is "touch" or "compat" or "super" ? cfg.InputMode : "touch";
        cfg.Theme = cfg.Theme is "Dark" or "Light" or "Auto" ? cfg.Theme : "Dark";
        // v1.1.2：亚克力材质选项移除 —— 全部窗口均为 WPF 透明分层窗口，DWM 亚克力
        // 在其上不生效且实测渲染异常。旧配置的 Acrylic 映射回半透明（观感最接近）。
        cfg.Material = cfg.Material is "Solid" or "Translucent" or "Minimal" ? cfg.Material : "Translucent";
        cfg.ShapeAssistMode = cfg.ShapeAssistMode is "auto" or "bubble" ? cfg.ShapeAssistMode : "auto";
        // v1.0.0：AI 时机/范围/能力参数 + 笔墨高级参数钳制（防手改配置越界）
        cfg.AiTriggerTiming = cfg.AiTriggerTiming is "pause" or "lift" or "manual" ? cfg.AiTriggerTiming : "pause";
        cfg.AiScope = cfg.AiScope is "last" or "recent" ? cfg.AiScope : "last";
        cfg.AiScopeStrokeCount = Math.Clamp(cfg.AiScopeStrokeCount, 2, 6);
        cfg.AiConfidenceThreshold = double.IsFinite(cfg.AiConfidenceThreshold)
            ? Math.Clamp(cfg.AiConfidenceThreshold, 0.30, 0.98) : 0.60;
        cfg.AiRecognizeTimeoutMs = Math.Clamp(cfg.AiRecognizeTimeoutMs, 300, 10000);
        cfg.AiMinSizeMm = double.IsFinite(cfg.AiMinSizeMm) ? Math.Clamp(cfg.AiMinSizeMm, 0, 40) : 6;
        cfg.AiShapeWhitelist ??= "";
        cfg.PenSmoothing = double.IsFinite(cfg.PenSmoothing) ? Math.Clamp(cfg.PenSmoothing, 0.20, 0.80) : 0.45;
        cfg.PenTaperStrength = double.IsFinite(cfg.PenTaperStrength) ? Math.Clamp(cfg.PenTaperStrength, 0.0, 1.0) : 0.5;
        cfg.PenLeadInPoints = Math.Clamp(cfg.PenLeadInPoints, 0, 20);
        cfg.PenLiftPoints = Math.Clamp(cfg.PenLiftPoints, 0, 24);
        cfg.PenWidthLowpass = double.IsFinite(cfg.PenWidthLowpass) ? Math.Clamp(cfg.PenWidthLowpass, 0.10, 0.90) : 0.30;
        cfg.PenPressureGain = double.IsFinite(cfg.PenPressureGain) ? Math.Clamp(cfg.PenPressureGain, 0.0, 1.0) : 0.5;
        cfg.HighlighterTipRatio = double.IsFinite(cfg.HighlighterTipRatio) ? Math.Clamp(cfg.HighlighterTipRatio, 0.30, 1.0) : 0.5;
        cfg.HighlighterAlpha = double.IsFinite(cfg.HighlighterAlpha) ? Math.Clamp(cfg.HighlighterAlpha, 0.20, 0.80) : 0.43;
        cfg.DefaultEraseMode = cfg.DefaultEraseMode is "stroke" or "lasso" or "clear" ? cfg.DefaultEraseMode : "stroke";
        cfg.AfterClearTool = cfg.AfterClearTool is "smart" or "pen" or "touch" or "keep" ? cfg.AfterClearTool : "smart";
        cfg.AccentColor = SafeColor(cfg.AccentColor, "#78AFA5");
        cfg.DefaultColor = SafeColor(cfg.DefaultColor, "#E8442E");
        cfg.HighlightColor = SafeColor(cfg.HighlightColor, "#F5D000");

        if (cfg.ToolbarDockX is { } tx && !double.IsFinite(tx)) cfg.ToolbarDockX = null;
        if (cfg.SegmentDockX is { } sx && !double.IsFinite(sx)) cfg.SegmentDockX = null;
        if (double.IsInfinity(cfg.ToolbarDockY)) cfg.ToolbarDockY = double.NaN;
        if (double.IsInfinity(cfg.SegmentDockY)) cfg.SegmentDockY = double.NaN;
        if (double.IsInfinity(cfg.PageIslandRightX)) cfg.PageIslandRightX = double.NaN;
        if (double.IsInfinity(cfg.PageIslandRightY)) cfg.PageIslandRightY = double.NaN;
        if (double.IsInfinity(cfg.PageIslandLeftX)) cfg.PageIslandLeftX = double.NaN;
        if (double.IsInfinity(cfg.PageIslandLeftY)) cfg.PageIslandLeftY = double.NaN;
    }

    public void Save()
    {
        lock (SaveGate)
        {
            string? tempPath = null;
            try
            {
                Normalize(this);
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(this, ConfigJsonOptions);
                // Write beside the destination and rename only after a complete write. A power
                // loss/exception can no longer leave config.json half-written or truncate the
                // last known-good configuration.
                tempPath = Path_ + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, Path_, overwrite: true);
                tempPath = null;
            }
            catch (Exception ex)
            {
                // Persist failures are observable in diagnostics rather than silently ignored.
                try { AppLog.Instance.Warn($"配置保存失败：{ex.GetType().Name}: {ex.Message}"); } catch { }
            }
            finally
            {
                if (tempPath is not null)
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }
}
