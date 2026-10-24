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
}
