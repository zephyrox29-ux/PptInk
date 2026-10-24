using System.IO;

namespace PptInk.Core;

/// <summary>轻量日志：单文件追加，S0 阶段零开销（内存环形缓冲 + 定时落盘）。</summary>
public sealed class AppLog
{
    public static AppLog Instance { get; } = new();

    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Queue<string> _buffer = new();
    private Timer? _flushTimer;
    private readonly string _logDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptInk", "logs");

    private AppLog() { }

    public void Start()
    {
        lock (_gate)
        {
            if (_flushTimer is not null) return;
            _flushTimer = new Timer(_ => Flush(), null, 10_000, 10_000); // 10s 批量落盘，避免常驻写盘
        }
    }

    /// <summary>应用正常退出时停止定时器并同步刷盘，避免最后几秒日志丢失。</summary>
    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _flushTimer;
            _flushTimer = null;
        }
        timer?.Dispose();
        Flush();
    }

    public void Info(string message)  => Append("INFO ", message);
    public void Warn(string message)  => Append("WARN ", message);
    /// <summary>错误日志（含堆栈）：审查修复——原实现只记 ex.Message，排障时缺关键定位信息。</summary>
    public void Error(Exception ex, string message) =>
        Append("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private void Append(string level, string message)
    {
        lock (_gate)
        {
            _buffer.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
            while (_buffer.Count > 512) _buffer.Dequeue(); // 环形缓冲，防内存膨胀
        }
    }

    private void Flush()
    {
        // Timer callback and Stop() may race. Serialize the complete disk flush, not just the
        // queue snapshot, so two writers cannot lose/reorder the same batch.
        lock (_flushGate)
        {
            List<string> batch;
            lock (_gate)
            {
                if (_buffer.Count == 0) return;
                batch = _buffer.ToList();
                _buffer.Clear();
            }
            try
            {
                Directory.CreateDirectory(_logDir);
                // 按天分文件，保留 7 天
                var file = Path.Combine(_logDir, $"pptink-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllLines(file, batch);
                CleanupOldLogs();
            }
            catch
            {
                // Keep failed records in the bounded in-memory queue for a later retry.
                lock (_gate)
                {
                    var buffered = _buffer.ToArray();
                    _buffer.Clear();
                    foreach (var item in batch.Concat(buffered).TakeLast(512)) _buffer.Enqueue(item);
                }
            }
        }
    }

    /// <summary>清理 7 天前日志（原注释声明保留 7 天但从未实现 → 会无限增长）。</summary>
    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_logDir, "pptink-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}
