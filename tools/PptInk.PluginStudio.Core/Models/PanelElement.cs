namespace PptInk.PluginStudio.Core.Models;

/// <summary>面板元素（text/slider/button）；OnEvent 为脚本内回调函数名（标识符）。
/// 主程序规则：button/slider 的 OnEvent 必须为合法 Lua 标识符；text 可空缺省 noop。</summary>
public sealed class PanelElement
{
    public string Type { get; set; } = "text";
    public string? Id { get; set; }
    public string? Text { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Value { get; set; }
    public string OnEvent { get; set; } = "noop";
}