using Microsoft.Win32;

namespace PptInk.Core;

/// <summary>
/// 注册为当前用户自启项（HKCU\...\Run），静默启动不打扰桌面。
/// 不写 HKLM：学校机器普通教师无管理员权限，且 HKCU 与用户会话一致，避免 COM 权限不一致。
/// </summary>
public sealed class AutoStartRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PptInk";

    public void EnsureRegistered(bool silent)
    {
        if (!AppConfig.Current.AutoStart) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var want = $"\"{exe}\" --minimized";
            if ((string?)key.GetValue(ValueName) != want)
                key.SetValue(ValueName, want);
        }
        catch { /* 注册表被组策略锁：忽略，仅本次会话 */ }
    }

    public void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>清理旧版本留下的自启项：Run 键里名字像 PptInk/融绘、但值不是当前 exe 的
    /// 一律删除，返回删除数量。老师机器上常残留旧安装目录的自启项 —— 不清理会和新版
    /// 同时拉起，旧版先抢同名单实例，表现为「新版打不开 / 打开还是旧界面」。</summary>
    public int PruneLegacyEntries()
    {
        int removed = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return 0;
            string? self = Environment.ProcessPath;
            // 取不到当前路径时绝不删除任何项：Contains(null/空) 会命中全部，
            // 把用户唯一的自启项也清掉。
            if (string.IsNullOrEmpty(self)) return 0;
            foreach (var name in key.GetValueNames())
            {
                if (!LooksLikePptInk(name)) continue;
                var value = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                // 值已指向当前 exe：保留（同名时 EnsureRegistered 也不会改它）
                if (!string.IsNullOrEmpty(self) && value.Contains(self, StringComparison.OrdinalIgnoreCase)) continue;
                try { key.DeleteValue(name, throwOnMissingValue: false); removed++; }
                catch { }
            }
        }
        catch { /* 注册表被组策略锁：忽略 */ }
        return removed;
    }

    private static bool LooksLikePptInk(string name) =>
        name.Contains("PptInk", StringComparison.OrdinalIgnoreCase)
        || name.Contains("融绘", StringComparison.Ordinal)
        || name.Contains("RongHui", StringComparison.OrdinalIgnoreCase);
}
