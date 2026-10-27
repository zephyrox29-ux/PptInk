namespace PptInk.Detection;

/// <summary>放映开始事件。</summary>
/// <param name="SlideIndex">1-based 幻灯片索引。</param>
/// <param name="TotalSlides">总页数（兜底通道可为 0，表示未知）。</param>
/// <param name="Source">来源："COM:PowerPoint" / "COM:WPS" / "WinEvent:screenClass"。</param>
/// <param name="DocumentPath">当前演示文稿文件全路径（COM 通道提供；兜底通道为空）。
/// 用于「换 PPT 必须换会话」的身份判定 —— 页码/板书绝不跨文件串。</param>
public sealed record ShowStartedEvent(int SlideIndex, int TotalSlides, string Source, string DocumentPath = "");

/// <summary>翻页事件。</summary>
public sealed record SlideChangedEvent(int SlideIndex, int TotalSlides, string Source);

/// <summary>
/// 放映探测通道抽象：主通道（COM）与兜底通道（WinEvent）共同实现。
/// 事件可能重复/乱序，由 <c>PresentationDetector</c> 做仲裁与去重。
/// </summary>
public interface IPresentationChannel : IDisposable
{
    string Name { get; }
    bool IsHealthy { get; }
    event EventHandler<ShowStartedEvent>? Started;
    event EventHandler<SlideChangedEvent>? SlideChanged;
    event EventHandler? Ended;
    void Start();
}
