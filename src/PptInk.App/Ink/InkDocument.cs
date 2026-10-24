using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PptInk.Core;

namespace PptInk.Ink;

/// <summary>
/// 单页墨迹（双级存储）：
///  - 操作级：Strokes 矢量列表 + HistoryTree 指令历史（撤销/重做/回放）；
///  - 像素级：合成位图（像素级精细擦除 + 快速合成 + 导出）。
/// 位图懒创建：不书写的页不占 4K 位图内存（一张 3840×2160 BGRA ≈ 33MB）。
/// </summary>
public sealed class InkPage
{
    public List<StrokeData> Strokes { get; } = new();
    public HistoryTree History { get; }
    public WriteableBitmap? Bitmap { get; private set; }
    public bool VisualDirty { get; private set; } = true;

    public int WidthPx { get; }
    public int HeightPx { get; }

    public InkPage(int widthPx, int heightPx)
    {
        WidthPx = widthPx;
        HeightPx = heightPx;
        History = new HistoryTree(this);
    }

    public void MarkVisualDirty() => VisualDirty = true;

    /// <summary>确保像素层就绪（首次书写/像素擦除时才真正分配）。</summary>
    public WriteableBitmap EnsureBitmap()
    {
        Bitmap ??= new WriteableBitmap(WidthPx, HeightPx, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        return Bitmap;
    }

    public unsafe void ClearBitmap()
    {
        if (Bitmap is null) return;
        Bitmap.Lock();
        try { MemSet((byte*)Bitmap.BackBuffer, 0, (nuint)((long)Bitmap.BackBufferStride * Bitmap.PixelHeight)); }
        finally { Bitmap.Unlock(); }
        VisualDirty = true;
    }

    /// <summary>原生内存填充（此前"Native memset"伪代码的正确实现）。</summary>
    private static unsafe void MemSet(byte* dst, byte value, nuint count) => new Span<byte>(dst, (int)count).Fill(value);

    public byte[] CopyRegionBitmap(Rect region)
    {
        var bmp = EnsureBitmap();
        if (!TryClipRegion(bmp, region, out var clipped)) return Array.Empty<byte>();
        int rowBytes = checked(clipped.Width * 4);
        var buf = new byte[checked(rowBytes * clipped.Height)];
        bmp.Lock();
        try
        {
            IntPtr src = bmp.BackBuffer + (nint)((long)clipped.Y * bmp.BackBufferStride + clipped.X * 4L);
            unsafe
            {
                fixed (byte* dst = buf)
                    for (int row = 0; row < clipped.Height; row++)
                        Buffer.MemoryCopy((byte*)src + (long)row * bmp.BackBufferStride,
                            dst + (long)row * rowBytes, rowBytes, rowBytes);
            }
            return buf;
        }
        finally { bmp.Unlock(); }
    }

    public void EraseRegionBitmap(Rect region)
    {
        var bmp = EnsureBitmap();
        if (!TryClipRegion(bmp, region, out var clipped)) return;
        bmp.Lock();
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)bmp.BackBuffer;
                for (int row = 0; row < clipped.Height; row++)
                    MemSet(basePtr + (long)(clipped.Y + row) * bmp.BackBufferStride + clipped.X * 4L,
                        0, (nuint)(clipped.Width * 4));
            }
            bmp.AddDirtyRect(clipped);
        }
        finally { bmp.Unlock(); }
        VisualDirty = true;
    }

    public void RestoreRegionBitmap(Rect region, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bmp = EnsureBitmap();
        if (!TryClipRegion(bmp, region, out var clipped)) return;
        int rowBytes = checked(clipped.Width * 4);
        int expectedBytes = checked(rowBytes * clipped.Height);
        // Never read beyond a corrupt/truncated undo snapshot; leaving the current pixels intact
        // is safer than corrupting the bitmap back buffer.
        if (snapshot.Length != expectedBytes) return;
        bmp.Lock();
        try
        {
            unsafe
            {
                fixed (byte* src = snapshot)
                {
                    byte* dstBase = (byte*)bmp.BackBuffer + (long)clipped.Y * bmp.BackBufferStride + clipped.X * 4L;
                    for (int row = 0; row < clipped.Height; row++)
                        Buffer.MemoryCopy(src + (long)row * rowBytes,
                            dstBase + (long)row * bmp.BackBufferStride, rowBytes, rowBytes);
                }
            }
            bmp.AddDirtyRect(clipped);
        }
        finally { bmp.Unlock(); }
        VisualDirty = true;
    }

    /// <summary>Clip arbitrary/edge-crossing rectangles to a valid non-empty bitmap region.</summary>
    private static bool TryClipRegion(WriteableBitmap bmp, Rect region, out Int32Rect clipped)
    {
        clipped = Int32Rect.Empty;
        if (region.IsEmpty || !double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.Width <= 0 || region.Height <= 0)
            return false;

        var left = Math.Max(0, Math.Floor(region.Left));
        var top = Math.Max(0, Math.Floor(region.Top));
        var right = Math.Min(bmp.PixelWidth, Math.Ceiling(region.Right));
        var bottom = Math.Min(bmp.PixelHeight, Math.Ceiling(region.Bottom));
        if (right <= left || bottom <= top) return false;
        clipped = new Int32Rect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        return clipped.Width > 0 && clipped.Height > 0;
    }

    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("msvcrt.dll", EntryPoint = "memset")]
        public static extern IntPtr memset(IntPtr dest, int value, nuint length);

        [System.Runtime.InteropServices.DllImport("msvcrt.dll", EntryPoint = "memset")]
        public static extern unsafe byte* memset(byte* dest, int value, nuint length);
    }
}

/// <summary>
/// 整份放映文档：以幻灯片索引为键缓存每页墨迹，翻页自动恢复该页墨迹。
/// 会话导出：整份放映结束后一键导出（PNG 逐页 + SVG 矢量 + JSON 点序列，供 AI 回放）。
/// </summary>
    public sealed class InkDocument
    {
        private readonly Dictionary<int, InkPage> _pages = new();
        public int ScreenWidthPx { get; }
        public int ScreenHeightPx { get; }

        /// <summary>渲染口径机型档案（EMA/飞点/断触参数）：精修重绘与 PNG 导出必须与活体书写同参数，
        /// 否则抬笔瞬间笔画形状跳变（审查修复 #1）。由组合根与设置中心在引擎档案变化时同步；null 回退默认参数。</summary>
        public Seewo.DeviceProfile? RenderProfile { get; set; }

    public InkDocument(int widthPx = 3840, int heightPx = 2160)
    {
        ScreenWidthPx = widthPx;
        ScreenHeightPx = heightPx;
    }

    /// <summary>取/建指定页墨迹（PPT 切回上一页时笔记恢复到对应页）。</summary>
    public InkPage GetPage(int slideIndex) =>
        _pages.TryGetValue(slideIndex, out var page) ? page : (_pages[slideIndex] = new InkPage(ScreenWidthPx, ScreenHeightPx));

    public IReadOnlyDictionary<int, InkPage> Pages => _pages;

    /// <summary>页面快照（持引擎锁构造，与墨迹线程的加页/抬笔提交互斥）。
    /// 审查修复：ExportSession 原先直接遍历 _pages 与 page.Strokes——导出（UI 线程）与
    /// 书写（墨迹线程 AddStrokeOp.Apply → Strokes.Add / GetPage 新页）并发时，
    /// 字典/列表遍历中修改会抛 InvalidOperationException，导出随机失败。</summary>
    public List<(int Idx, InkPage Page, StrokeData[] Strokes)> SnapshotPages()
    {
        var snap = new List<(int, InkPage, StrokeData[])>();
        foreach (var kv in _pages)
            if (kv.Value.Strokes.Count > 0)
                snap.Add((kv.Key, kv.Value, kv.Value.Strokes.ToArray()));
        return snap;
    }

    /// <summary>整份放映结束：一键导出到 桌面\PptInk导出\时间戳\（渲染与文件 IO 在快照上锁外执行，
    /// 不阻塞墨迹线程；StrokeData 提交后内容不再变更，引用安全）。</summary>
    public string ExportSession(List<(int Idx, InkPage Page, StrokeData[] Strokes)> snapshot)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "PptInk导出", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        foreach (var (idx, page, strokes) in snapshot)
        {
            File.WriteAllText(Path.Combine(dir, $"slide-{idx:D3}.svg"), BuildSvg(page, strokes, RenderProfile));
            File.WriteAllText(Path.Combine(dir, $"slide-{idx:D3}.json"), BuildPointJson(strokes));
            BuildPng(page, strokes, Path.Combine(dir, $"slide-{idx:D3}.png"));
        }
        return dir;
    }

    /// <summary>PNG 逐页渲染：复用平滑几何（BuildGeometry，参数与活体书写同源），DrawingVisual → RenderTargetBitmap → PngBitmapEncoder。</summary>
    private void BuildPng(InkPage page, StrokeData[] strokes, string path)
    {
        var prof = RenderProfile;                                 // 与活体绘制共用同一物理尺寸/平滑口径
        double pxPerMm = prof?.PxPerMm ?? 96.0 / 25.4;
        double minVisiblePx = prof?.MinimumVisibleStrokePx ?? 2.5;
        double alpha = prof?.EmaAlpha ?? 0.45;
        double jump = prof is null ? 480 : prof.MaxJumpPx * prof.CanvasScale;
        double drop = prof?.DropoutMs ?? 100;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var s in strokes)
            {
                if (s.Points.Count == 0) continue;
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Pen.ColorHex))
                {
                    Opacity = s.Pen.Alpha / 255.0,
                };
                brush.Freeze();
                double strokeWidth = Math.Max(minVisiblePx, s.Pen.ThicknessMm * pxPerMm);
                if (IsDotStroke(s))
                {
                    var point = s.Points[^1];
                    double radius = strokeWidth * 0.5;
                    dc.DrawEllipse(brush, null, new Point(point.X, point.Y), radius, radius);
                    continue;
                }

                // Preserve the live renderer's velocity taper in PNG exports. Historically the
                // export path flattened every pen to one width, so saved pages looked unlike the
                // on-screen annotation even though point data was correct.
                if (s.Pen.Tapered)
                {
                    DrawTapered(dc, s, brush, strokeWidth,
                        1.0 / Math.Max(0.01, prof?.CanvasScale ?? 1.0), minVisiblePx);
                    continue;
                }

                var pen = new Pen(brush, strokeWidth)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, s.BuildGeometry(pxPerMm, alpha, jump, drop));
            }
        }
        var rtb = new RenderTargetBitmap(page.WidthPx, page.HeightPx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string BuildSvg(InkPage page, StrokeData[] strokes, Seewo.DeviceProfile? profile)
    {
        static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        static string SafeColor(string color)
        {
            // Only accept a strict CSS hex color in SVG attributes; never let malformed/plugin
            // supplied strings alter the exported XML structure.
            if (color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
                return color;
            // WPF's 9-digit form is #AARRGGBB; CSS 8-digit hex uses #RRGGBBAA. Strip
            // the ARGB prefix here because pen opacity is exported separately.
            if (color.Length == 9 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
                return "#" + color.Substring(3);
            return "#E8442E";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{page.WidthPx}' height='{page.HeightPx}' viewBox='0 0 {page.WidthPx} {page.HeightPx}'>");
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            var w = Math.Max(profile?.MinimumVisibleStrokePx ?? 2.5,
                s.Pen.ThicknessMm * (profile?.PxPerMm ?? 96.0 / 25.4));
            var color = SafeColor(s.Pen.ColorHex);
            var opacity = F(s.Pen.Alpha / 255.0, "0.00");
            if (IsDotStroke(s))
            {
                var p = s.Points[^1];
                sb.Append("<circle cx='").Append(F(p.X, "0.0"))
                  .Append("' cy='").Append(F(p.Y, "0.0"))
                  .Append("' r='").Append(F(w / 2, "0.0"))
                  .Append("' fill='").Append(color).Append("' opacity='").Append(opacity).Append("'/>");
                continue;
            }
            if (s.Pen.Tapered)
            {
                AppendTaperedSvg(sb, s, color, opacity, w,
                    1.0 / Math.Max(0.01, profile?.CanvasScale ?? 1.0), profile?.MinimumVisibleStrokePx ?? 2.5, F);
                continue;
            }
            sb.Append("<path d='M ").Append(F(s.Points[0].X, "0.0")).Append(' ')
              .Append(F(s.Points[0].Y, "0.0"));
            for (int i = 1; i < s.Points.Count; i++)
                sb.Append(" L ").Append(F(s.Points[i].X, "0.0")).Append(' ').Append(F(s.Points[i].Y, "0.0"));
            sb.Append("' stroke='").Append(color)
              .Append("' stroke-width='").Append(F(w, "0.0"))
              .Append("' stroke-linecap='round' stroke-linejoin='round' fill='none' opacity='")
              .Append(opacity).Append("'/>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static void DrawTapered(DrawingContext dc, StrokeData stroke, Brush brush, double baseWidth, double velocityScale, double minimumWidth)
    {
        var widths = stroke.BuildWidths(baseWidth, velocityScale, minimumWidth);
        int i = 1;
        while (i < stroke.Points.Count)
        {
            int start = i - 1;
            double width = widths[i];
            int end = i;
            while (end + 1 < stroke.Points.Count && Math.Abs(widths[end + 1] - width) < width * 0.05)
                end++;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(stroke.Points[start].X, stroke.Points[start].Y), false, false);
                for (int k = start + 1; k <= end; k++)
                    ctx.LineTo(new Point(stroke.Points[k].X, stroke.Points[k].Y), true, false);
            }
            geometry.Freeze();
            var pen = new Pen(brush, Math.Max(0.8, width))
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round, MiterLimit = 1.5,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            i = end + 1;
        }
    }

    private static void AppendTaperedSvg(
        System.Text.StringBuilder sb, StrokeData stroke, string color, string opacity,
        double baseWidth, double velocityScale, double minimumWidth, Func<double, string, string> format)
    {
        var widths = stroke.BuildWidths(baseWidth, velocityScale, minimumWidth);
        int i = 1;
        while (i < stroke.Points.Count)
        {
            int start = i - 1;
            double width = widths[i];
            int end = i;
            while (end + 1 < stroke.Points.Count && Math.Abs(widths[end + 1] - width) < width * 0.05)
                end++;
            sb.Append("<path d='M ").Append(format(stroke.Points[start].X, "0.0")).Append(' ')
              .Append(format(stroke.Points[start].Y, "0.0"));
            for (int k = start + 1; k <= end; k++)
                sb.Append(" L ").Append(format(stroke.Points[k].X, "0.0")).Append(' ')
                  .Append(format(stroke.Points[k].Y, "0.0"));
            sb.Append("' stroke='").Append(color).Append("' stroke-width='").Append(format(width, "0.0"))
              .Append("' stroke-linecap='round' stroke-linejoin='round' fill='none' opacity='")
              .Append(opacity).Append("'/>");
            i = end + 1;
        }
    }

    /// <summary>Same dot contract as the live renderer: taps and near-stationary two-point strokes stay visible.</summary>
    private static bool IsDotStroke(StrokeData stroke)
    {
        if (stroke.Points.Count is < 1 or > 2) return false;
        if (stroke.Points.Count == 1) return true;
        var a = stroke.Points[0];
        var b = stroke.Points[1];
        return Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) < 1.5;
    }

    private static string BuildPointJson(StrokeData[] strokes) =>
        System.Text.Json.JsonSerializer.Serialize(strokes.Select(s => new
        {
            pen = new { s.Pen.ColorHex, s.Pen.ThicknessMm, s.Pen.Highlighter, s.Pen.Tapered, alpha = s.Pen.Alpha },
            pts = s.Points.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Pressure, 2) })
        }));

    /// <summary>程序退出兜底：会话有墨迹时保存到本地（不弹窗，下次可找回）。
    /// 快照由调用方持引擎锁构造（InkEngine.TrySaveSessionSafe），IO 锁外执行。</summary>
    public void TrySaveSession(List<(int Idx, InkPage Page, StrokeData[] Strokes)> snapshot)
    {
        try
        {
            if (snapshot.Count > 0)
            {
                var dir = ExportSession(snapshot);
                AppLog.Instance.Info($"会话墨迹已自动导出：{dir}");
            }
        }
        catch (Exception ex) { AppLog.Instance.Error(ex, "会话导出失败"); }
    }
}
