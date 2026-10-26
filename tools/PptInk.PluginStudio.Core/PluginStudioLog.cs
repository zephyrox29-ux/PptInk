namespace PptInk.PluginStudio.Core;

/// <summary>工具日志接口（可注入以隔离测试）。</summary>
public interface IPluginStudioLog
{
    void Info(string message);
    void Warn(string message);
    void Error(Exception ex, string message);
}

/// <summary>默认日志：写 <see cref="AppPaths.LogRoot"/> 下的按天文件；全程 try-catch，绝不外溢到调用方。</summary>
public sealed class FilePluginStudioLog : IPluginStudioLog
{
    public static FilePluginStudioLog Instance { get; } = new();

    private readonly object _gate = new();

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogRoot);
            var file = Path.Combine(AppPaths.LogRoot, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            lock (_gate) File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(Exception ex, string message) => Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");
}