namespace PptInk.PluginStudio.Core;

/// <summary>包内相对路径安全判定（与主程序 InstallFromZip 的越界检查口径一致）。</summary>
public static class PathSafety
{
    /// <summary>是否是不安全的相对路径：空、以分隔符开头、绝对路径，或含 <c>..</c> 段。</summary>
    public static bool IsUnsafeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(relativePath)) return true;
        return normalized.Split('/').Any(segment => segment == "..");
    }
}