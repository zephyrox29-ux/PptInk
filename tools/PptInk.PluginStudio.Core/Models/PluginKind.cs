namespace PptInk.PluginStudio.Core.Models;

/// <summary>插件类型魔数（与主程序 PluginContract.PluginKind 对齐）。
///   Panel=1：宿主渲染面板；Draw=2：调用绘制 SDK；Free=3：自由接管交互。</summary>
public enum PluginKind
{
    Panel = 1,
    Draw = 2,
    Free = 3,
}