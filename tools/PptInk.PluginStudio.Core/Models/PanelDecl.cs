namespace PptInk.PluginStudio.Core.Models;

/// <summary>面板声明（PANEL 类）。字段与取值范围与主程序 PluginManager.NormalizeManifest 一致：
/// Dock ∈ left/right/bottom/float；Width 160–600；Height 100–500；Alpha 0.3–1.0；Elements ≤64。</summary>
public sealed class PanelDecl
{
    public string Dock { get; set; } = "float";
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 160;
    public double Alpha { get; set; } = 0.88;
    public bool ShowInIsland { get; set; } = true;
    public PanelElement[] Elements { get; set; } = Array.Empty<PanelElement>();
}