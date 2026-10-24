using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PptInk.Plugins;

/// <summary>插件类型魔数（manifest.magic）。</summary>
public enum PluginKind
{
    /// <summary>PANEL=1：声明面板位置/长宽/Alpha/面板元素（文字、滑动条、按钮）与脚本逻辑，宿主负责渲染。</summary>
    Panel = 1,
    /// <summary>DRAW=2：调用宿主绘制 SDK，可调整笔迹、Alpha、抗锯齿笔锋、图案与擦除行为，以 API 暴露给脚本。</summary>
    Draw = 2,
    /// <summary>FREE=3：不受面板/绘制框架约束，点按启用内置脚本，自行接管交互（截图、点名）。</summary>
    Free = 3,
}

/// <summary>插件清单（zip 内 manifest.json）。</summary>
public sealed class PluginManifest
{
    /// <summary>魔数：1=面板类 2=绘制类 3=自由类。</summary>
    public int Magic { get; set; }
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>入口脚本（zip 相对路径，如 main.lua）。</summary>
    public required string Entry { get; set; }

    /// <summary>PANEL 类：面板声明（宿主渲染）。</summary>
    public PanelDecl? Panel { get; set; }

    /// <summary>权限声明：file / network / clipboard，需用户确认后注入沙箱。</summary>
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>预留字段：真实验签（RSA/Ed25519）实现前不做任何校验——加载时忽略并记录告警。
    /// 摘要等值比较不能防篡改（改文件者可重算摘要），故未实现真验签前不提供伪校验（审查修复 #3）。</summary>
    public string? Signature { get; set; }

    public PluginKind Kind => (PluginKind)Magic;
}

public sealed class PanelDecl
{
    /// <summary>停靠位置：left / right / bottom / float。</summary>
    public string Dock { get; set; } = "float";
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 160;
    /// <summary>面板透明度 0~1。</summary>
    public double Alpha { get; set; } = 0.88;
    /// <summary>浮岛参数：是否挂到浮岛弧形区。</summary>
    public bool ShowInIsland { get; set; } = true;
    public PanelElement[] Elements { get; set; } = Array.Empty<PanelElement>();
}

public sealed class PanelElement
{
    // 支持的类型以 PluginManager.NormalizeManifest 白名单为准（text | slider | button）；
    // 注释曾虚列 input 导致第三方按契约声明的包必装失败 —— 契约文档不得先于实现承诺
    public string Type { get; set; } = "text";
    /// <summary>元素标识：脚本 updatePanelElement(id, value) 的定位键（text 元素建议填写）。</summary>
    public string? Id { get; set; }
    public string? Text { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Value { get; set; }
    /// <summary>脚本回调名（沙箱内函数）。</summary>
    public required string OnEvent { get; set; }
}

/// <summary>插件抽象：内置（C#）与脚本（Lua 沙箱）统一接口。</summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }
    void Activate(IPluginHostApi api);
    void Deactivate();
}

/// <summary>
/// 宿主 API（DRAW SDK）：以受控形式暴露给插件脚本，仅 manifest 声明的权限可用。
/// </summary>
public interface IPluginHostApi
{
    // 绘制 SDK（DRAW 类）
    void SetPen(string colorHex, double thicknessMm, double alpha);
    void DrawPolyline((double X, double Y)[] points, bool antialias, bool tapered);
    void DrawText(string text, double x, double y, double sizeMm);
    void RequestEraseBehavior(string mode); // "stroke" | "pixel" | "region"

    // 面板（PANEL 类）：宿主渲染的面板元素操作
    void UpdatePanelElement(string elementId, string value);
    void ClosePanel();

    // 通用
    void StartTimer(double intervalSeconds, string callback);
    void StopAllTimers();
    string? ReadClipboard();   // 需 clipboard 权限
    void WriteClipboard(string text); // 需 clipboard 权限
    void CaptureScreen(string savePath); // 需 file 权限
    void ShowToast(string message);
}
