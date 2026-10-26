using System.Runtime.InteropServices;
using PptInk.Core;

namespace PptInk.Core;

/// <summary>
/// 翻页笔“亮度键”兜底：很多演示器/笔记本的亮度键不是标准虚拟键，而是 HID Consumer
/// Control（Usage Page 0x0C）的 Brightness Increment/Decrement（0x006F/0x0070）。
/// 低级键盘钩子看不到这类设备，因此用 Raw Input 的 RIDEV_INPUTSINK 在后台接收，
/// 仅在舞台激活/放映会话中映射成上一页/下一页。音量键仍由 PresenterForwarder 的
/// 键盘钩子处理，避免同一次按键被两条路径各翻一页。
/// </summary>
internal sealed class BrightnessHotkeyRouter
{
    private const ushort UsagePageConsumer = 0x0C;
    private const ushort UsageConsumerControl = 0x01;
    private const ushort UsageBrightnessIncrement = 0x006F;
    private const ushort UsageBrightnessDecrement = 0x0070;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEHID = 2;
    private const int WM_INPUT = 0x00FF;

    private static IntPtr _hwnd;
    private static Action<bool>? _advance;
    private static Func<bool>? _enabled;
    private static bool _registered;
    private static long _lastEventTick;
    private static bool _loggedFirst;

    public static void Register(IntPtr hwnd, Action<bool> advance, Func<bool> enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        _advance = advance;
        _enabled = enabled;
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = UsagePageConsumer,
            usUsage = UsageConsumerControl,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = hwnd,
        };
        try
        {
            if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                AppLog.Instance.Warn("亮度键 Raw Input 注册失败（设备可能不支持 Consumer Control）");
            else
            {
                _registered = true;
                AppLog.Instance.Info("亮度键 Raw Input 已注册（HID Consumer Control 0x0C/0x01）");
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn($"亮度键 Raw Input 不可用：{ex.GetType().Name}: {ex.Message}"); }
    }

    public static void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero) return;
        try
        {
            var device = new RAWINPUTDEVICE
            {
                usUsagePage = UsagePageConsumer,
                usUsage = UsageConsumerControl,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            };
            RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }
        _registered = false;
        _hwnd = IntPtr.Zero;
        _advance = null;
        _enabled = null;
    }

    /// <summary>舞台 WndProc 的 WM_INPUT 分支调用；返回是否由本路由消费。</summary>
    public static bool HandleRawInput(IntPtr lParam)
    {
        if (!_registered || _advance is null || _enabled?.Invoke() != true) return false;
        uint size = 0;
        try
        {
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
                return false;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
                    return false;
                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.dwType != RIM_TYPEHID) return false;
                IntPtr hidPtr = IntPtr.Add(buffer, (int)headerSize);
                // RAWHID 的 bRawData 是紧跟 dwCount 之后的内联字节数组（不是指针）——
                // 用 IntPtr 字段反序列化会把它当地址去 Marshal.Copy，直接访问违例（不可捕获）。
                uint sizeHid = (uint)Marshal.ReadInt32(hidPtr);
                uint count = (uint)Marshal.ReadInt32(hidPtr, 4);
                if (sizeHid == 0 || count == 0 || sizeHid > 512 || count > 16)
                    return false;
                int bytes = (int)(sizeHid * count);
                if (bytes > (long)size - headerSize - 8) return false;
                var raw = new byte[bytes];
                Marshal.Copy(IntPtr.Add(hidPtr, 8), raw, 0, bytes);
                if (!TryDecodeBrightness(raw, out bool forward)) return false;
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastEventTick) < 80) return true; // 同一次按键去重
                Interlocked.Exchange(ref _lastEventTick, now);
                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    AppLog.Instance.Info($"亮度键翻页：{(forward ? "下一" : "上一")}页（HID Consumer Control）");
                }
                _advance(forward);
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"亮度键解析失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Consumer Control 报告是 16 位 usage 数组（little-endian）。只按 16 位扫描，
    /// 不再做单字节兜底 —— 任意 HID 报告里的 0x6F/0x70 字节会造成随机翻页。</summary>
    private static bool TryDecodeBrightness(byte[] raw, out bool forward)
    {
        forward = false;
        for (int i = 0; i + 1 < raw.Length; i += 2)
        {
            ushort usage = (ushort)(raw[i] | (raw[i + 1] << 8));
            if (usage == UsageBrightnessIncrement) { forward = true; return true; }
            if (usage == UsageBrightnessDecrement) { forward = false; return true; }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data,
        ref uint size, uint sizeHeader);
}