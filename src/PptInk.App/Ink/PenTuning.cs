namespace PptInk.Ink;

/// <summary>
/// 笔墨精调参数统一入口（v1.0.0 高级模式）。
/// 把「高级模式」下的用户精调值与机型档案值汇成"生效值"，供书写、渲染、导出、
/// 几何重建等所有路径共用 —— 单一真源，避免同一支笔在不同路径用不同参数导致
/// 手感/外观不一致（例如实时渲染与抬笔精修、导出件之间出现差异）。
/// </summary>
internal static class PenTuning
{
    /// <summary>生效的平滑强度：高级模式启用时用用户值（0.20~0.80，越大越跟手），
    /// 否则沿用机型档案的 EmaAlpha（0.40~0.55）。</summary>
    public static double EffectiveAlpha(double profileAlpha)
    {
        var cfg = Core.AppConfig.Current;
        return cfg.AdvancedMode && double.IsFinite(cfg.PenSmoothing)
            ? System.Math.Clamp(cfg.PenSmoothing, 0.20, 0.80)
            : profileAlpha;
    }
}