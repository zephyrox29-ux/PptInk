using System.Runtime.InteropServices;
using System.Text;
using PptInk.Core;

namespace PptInk.Seewo;

/// <summary>
/// 运行时探测 Windows 触摸数字化仪。仅使用 Win32 正式 API 返回的设备句柄和
/// GetPointerDeviceRects 物理范围；任何 API 不可用/数据异常均安全退回机型档案。
/// </summary>
public sealed class TouchDigitizer
{
    public DigimizerDevice? Primary { get; private set; }
    public bool IsLargeTouchPanel => Primary is { DiagonalInch: > 40 };
    public int DeviceCount { get; private set; }

    public string Summary => Primary is { } p
        ? $"{p.Name} · {p.DiagonalInch:F0}寸 ({p.WidthMm:F0}×{p.HeightMm:F0}mm) · 最大{p.MaxContacts}点" +
          (IsLargeTouchPanel ? " · 大尺寸触摸面板" : "")
        : "未探测到可校准触摸数字化仪（鼠标/远程会话或 API 不可用）";

    /// <summary>
    /// 枚举系统触摸设备。关键 ABI 纪律：POINTER_DEVICE_INFO 的真实字段为
    /// displayOrientation/device/pointerDeviceType/monitor/startingCursorId/
    /// maxActiveContacts/productString[520]；物理矩形并不在此结构内，必须通过
    /// GetPointerDeviceRects(handle) 单独读取。此前的自造结构把矩形当作结构字段且
    /// 尺寸不足，既会读错设备类型，也可能让 Win32 按 1080 字节结构写入 560 字节步长。
    /// </summary>
    public static TouchDigitizer Probe(AppLog log)
    {
        var result = new TouchDigitizer();
        try
        {
            // Fail closed if the managed ABI size ever diverges from the Windows SDK layout.
            // POINTER_DEVICE_PRODUCT_STRING_MAX is 520 UTF-16 code units.
            int expectedSize = IntPtr.Size == 8 ? 1080 : 1064;
            if (Marshal.SizeOf<PointerDeviceInfoRaw>() != expectedSize)
            {
                try { log.Warn($"数字化仪探测跳过：POINTER_DEVICE_INFO ABI 大小不符 ({Marshal.SizeOf<PointerDeviceInfoRaw>()}/{expectedSize})"); } catch { }
                return result;
            }

            uint count = 0;
            if (!GetPointerDevices(ref count, IntPtr.Zero) || count == 0 || count > MaxEnumeratedDevices)
                return result;

            var devices = new PointerDeviceInfoRaw[checked((int)count)];
            uint capacity = count;
            if (!GetPointerDevices(ref capacity, devices)) return result;
            result.DeviceCount = checked((int)Math.Min(capacity, count));

            DigimizerDevice? best = null;
            double bestAreaMm2 = 0;
            int usableCount = Math.Min(result.DeviceCount, devices.Length);
            for (int i = 0; i < usableCount; i++)
            {
                var device = devices[i];
                if (device.PointerDeviceType != PointerDeviceTypeTouch || device.Device == IntPtr.Zero)
                    continue;

                if (!GetPointerDeviceRects(device.Device, out var physicalRect, out var displayRect))
                    continue;

                // rcPhysical is expressed in HIMETRIC (0.01 mm). Do not assume the origin is 0:
                // touch devices mapped to a display can report negative/rotated origins.
                double widthMm = Math.Abs((double)physicalRect.Right - physicalRect.Left) / 100.0;
                double heightMm = Math.Abs((double)physicalRect.Bottom - physicalRect.Top) / 100.0;
                if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) ||
                    widthMm is < 50 or > 10000 || heightMm is < 50 or > 10000)
                    continue;

                double diagonalInch = Math.Sqrt(widthMm * widthMm + heightMm * heightMm) / 25.4;
                if (!double.IsFinite(diagonalInch) || diagonalInch is < 5 or > 300)
                    continue;

                int displayWidthPx = Math.Abs(displayRect.Right - displayRect.Left);
                int displayHeightPx = Math.Abs(displayRect.Bottom - displayRect.Top);
                // An empty or implausible display rectangle cannot be trusted for calibration.
                // Keep enumeration diagnostics, but do not use it to choose a physical profile.
                if (displayWidthPx < 320 || displayHeightPx < 200 || displayWidthPx > 32768 || displayHeightPx > 32768)
                    continue;

                double area = widthMm * heightMm;
                if (area <= bestAreaMm2) continue;

                string name = (device.ProductString ?? string.Empty).TrimEnd('\0').Trim();
                best = new DigimizerDevice(
                    Name: string.IsNullOrWhiteSpace(name) ? "触摸屏" : name,
                    WidthMm: widthMm,
                    HeightMm: heightMm,
                    DiagonalInch: diagonalInch,
                    MaxContacts: device.MaxActiveContacts,
                    DisplayWidthPx: displayWidthPx,
                    DisplayHeightPx: displayHeightPx,
                    Monitor: device.Monitor);
                bestAreaMm2 = area;
            }

            result.Primary = best;
            if (best is not null) log.Info($"数字化仪探测：{result.Summary}");
        }
        catch (Exception ex)
        {
            try { log.Warn($"数字化仪探测失败（回退机型档案）：{ex.GetType().Name}: {ex.Message}"); } catch { }
        }
        return result;
    }

    /// <summary>显示器/触摸硬件变更时重新读取 Win32 设备映射（例如先启动笔记本，再接入希沃大屏）。</summary>
    public void Refresh(AppLog log)
    {
        var latest = Probe(log);
        Primary = latest.Primary;
        DeviceCount = latest.DeviceCount;
    }

    /// <summary>
    /// 计算实际显示像素 / 面板毫米。仅在待校准屏幕分辨率与该设备 API 映射的显示器
    /// 分辨率相符时采用实测值，避免双屏（笔记本触摸 + 外接红外大屏）串用尺寸。
    /// </summary>
    public double? ComputePxPerMm(double screenPhysicalWidthPx)
    {
        if (Primary is not { } p || !double.IsFinite(screenPhysicalWidthPx) || screenPhysicalWidthPx <= 100)
            return null;

        // ShowScreen returns the current target in physical pixels. The device rect API returns
        // the resolution for the monitor the device is mapped to. Large mismatch means the
        // digitizer and projection target are probably different displays; fall back safely.
        if (p.DisplayWidthPx <= 0 || Math.Abs(p.DisplayWidthPx - screenPhysicalWidthPx) > Math.Max(160, screenPhysicalWidthPx * 0.20))
            return null;

        double pxPerMm = screenPhysicalWidthPx / p.WidthMm;
        return double.IsFinite(pxPerMm) && pxPerMm is >= 0.5 and <= 20 ? pxPerMm : null;
    }

    /// <summary>
    /// 同步当前显示器坐标口径，并在能确认同屏红外数字化仪时注入实测校准。
    /// 即使当前是笔记本/非红外档案，也必须更新 CanvasScale：输入坐标统一落在
    /// 3840×2160 逻辑画布，而不是混用屏幕 DIP、物理像素和逻辑像素。
    /// </summary>
    public bool ApplyTo(DeviceProfile profile, string source)
    {
        try
        {
            var b = ShowScreen.Bounds;
            double dpiScale = ShowScreen.DpiScale;
            double physicalWidthPx = b.Width * dpiScale;
            profile.ConfigureRuntimeDisplay(physicalWidthPx, dpiScale);

            if (profile is not { IsInfraredFrame: true })
            {
                profile.CalibratedPxPerMm = null;
                profile.CalibrationSource = null;
                return false;
            }

            // Never keep a calibration from a previously attached display if the current mapping
            // is not confirmed; stale calibration is more harmful than the conservative profile.
            profile.CalibratedPxPerMm = null;
            profile.CalibrationSource = null;
            if (ComputePxPerMm(physicalWidthPx) is not { } ppm) return false;

            profile.CalibratedPxPerMm = ppm;
            profile.CalibrationSource = source;
            return true;
        }
        catch { return false; }
    }

    public sealed record DigimizerDevice(
        string Name,
        double WidthMm,
        double HeightMm,
        double DiagonalInch,
        uint MaxContacts,
        int DisplayWidthPx,
        int DisplayHeightPx,
        IntPtr Monitor);

    #region Win32 ABI

    private const uint PointerDeviceTypeTouch = 3;
    private const uint MaxEnumeratedDevices = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PointerDeviceInfoRaw
    {
        public uint DisplayOrientation;
        public IntPtr Device;
        public uint PointerDeviceType;
        public IntPtr Monitor;
        public uint StartingCursorId;
        public ushort MaxActiveContacts;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)]
        public string? ProductString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerDevices(ref uint deviceCount, IntPtr pointerDevices);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerDevices(ref uint deviceCount, [Out] PointerDeviceInfoRaw[] pointerDevices);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerDeviceRects(IntPtr device, out NativeRect pointerDeviceRect, out NativeRect displayRect);

    #endregion
}
