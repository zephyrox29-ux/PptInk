namespace PptInk.Bridge;

/// <summary>AI 自检环节（UI 按这 5 行展示状态）。</summary>
public enum AiStage
{
    Script,
    Python,
    Numpy,
    Recognizer,
    /// <summary>桥握手 + 合成识别（最后一步，合并成一行）。</summary>
    Handshake,
}

/// <summary>环节状态。</summary>
public enum AiStepState
{
    Pending,
    Running,
    Passed,
    /// <summary>该环节曾经失败，已被自动修复并复检通过。</summary>
    Repaired,
    Failed,
}

/// <summary>结构化进度：让设置页能显示「每一项在做什么、结果如何」，
/// 而不是一串滚动的纯文本。</summary>
/// <param name="Stage">环节。</param>
/// <param name="State">状态。</param>
/// <param name="Message">一句话说明（面向用户，不包含堆栈/技术错误）。</param>
/// <param name="Percent">总进度 0-100（粗略估算，仅用于进度条）。</param>
public sealed record AiProgress(AiStage Stage, AiStepState State, string Message, int Percent);