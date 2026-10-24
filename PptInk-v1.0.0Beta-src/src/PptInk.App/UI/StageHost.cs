namespace PptInk.UI;

/// <summary>
/// 舞台宿主注册表（v0.9.0 单窗口架构）：全局唯一舞台的静态访问点 —— Toast/插件族
/// （无编排器依赖的模块）通过它挂载元素到 Plugin 层。
/// 单注册点纪律：任何「静态找窗口」的旧模式都收敛到这里，禁止再开第二个全局注册表。
/// 注册者：UnifiedStageWindow 构造器（窗口唯一，先于一切元素挂载）。
/// </summary>
internal static class StageHost
{
    private static UnifiedStageWindow? _stage;

    /// <summary>舞台注册（UnifiedStageWindow 构造器调用，全局唯一）。</summary>
    internal static void Register(UnifiedStageWindow stage) => _stage = stage;

    /// <summary>当前舞台（未创建时 null）。</summary>
    internal static UnifiedStageWindow? Stage => _stage;

    /// <summary>把元素挂到 Plugin 层并返回。无舞台时返回未挂载元素（其 ShowPanel 内部
    /// 判 Stage null 静默降级不显示）—— 调用方无需空引用分支；理论不可达（插件激活
    /// 前舞台必已由 PreCreateWindows 建好），防御式兜底。</summary>
    internal static T Mount<T>(T el) where T : StageElement
    {
        _stage?.AddToLayer(StageLayer.Plugin, el, 0, 0);
        return el;
    }
}
