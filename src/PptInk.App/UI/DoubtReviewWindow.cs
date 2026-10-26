using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PptInk.Ink;
using PptInk.UI.Theme;

namespace PptInk.UI;

/// <summary>
/// 下课后疑惑笔迹询问窗（v1.0.0Beta）。
/// 把识别时置信度落在疑惑区间的笔迹列出来，让老师确认「这样规整对不对」。
/// 否决的形状会被记下，今后不再自动规整（自优化）。仅在下课（放映结束、界面已隐藏）后出现，不打扰课堂。
/// </summary>
internal sealed class DoubtReviewWindow : Window
{
    /// <summary>用户点了「不对」的形状集合（调用方据此自优化）。</summary>
    public HashSet<string> RejectedShapes { get; } = new(StringComparer.Ordinal);

    public DoubtReviewWindow(List<(int Slide, StrokeData Stroke, string Shape, double Confidence)> items)
    {
        Title = "下课后的小检查";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.ContentBackground;
        SnapsToDevicePixels = true;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(Txt("下课后的小检查", 19, "TextPrimaryBrush", FontWeights.SemiBold));
        root.Children.Add(Txt("刚才这几笔我识别得把握不大，想跟你确认一下 —— 点「不对」的形状，以后就不再自动规整了。",
            12, "TextSecondaryBrush", FontWeights.Normal, new Thickness(0, 6, 0, 14)));

        var list = new StackPanel();
        foreach (var (slide, stroke, shapeName, conf) in items)
            list.Children.Add(BuildItem(slide, stroke, shapeName, conf));

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 460,
        };
        root.Children.Add(scroll);

        var done = new Button
        {
            Content = "完成",
            Height = 48, MinWidth = 108, Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["AccentBrush"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        ToolbarFactory.PressWire(done, Close);
        root.Children.Add(done);

        Content = root;
    }

    private UIElement BuildItem(int slide, StrokeData stroke, string shapeName, double conf)
    {
        var thumb = BuildThumbnail(stroke);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        label.Children.Add(Txt($"第 {slide} 页 · 识别为「{ShapeName(shapeName)}」", 13, "TextPrimaryBrush", FontWeights.Medium));
        label.Children.Add(Txt($"把握 {conf:P0}", 11, "TextSecondaryBrush", FontWeights.Normal, new Thickness(0, 2, 0, 0)));

        var yes = SmallBtn("对");
        var no = SmallBtn("不对");
        var verdict = Txt("", 11.5, "TextSecondaryBrush", FontWeights.Normal);
        var wasRejected = false;

        no.Click += (_, _) =>
        {
            if (wasRejected) return;
            wasRejected = true;
            RejectedShapes.Add(shapeName);
            verdict.Text = "已记住：以后不再自动规整";
            yes.IsEnabled = false;
            no.IsEnabled = false;
        };
        yes.Click += (_, _) => { verdict.Text = "好的"; yes.IsEnabled = false; no.IsEnabled = false; };

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(yes);
        right.Children.Add(new Rectangle { Width = 6 });
        right.Children.Add(no);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(thumb);
        left.Children.Add(label);
        left.Children.Add(verdict);
        row.Children.Add(left);

        var border = new Border
        {
            Child = row,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 6),
        };
        border.SetResourceReference(Border.BackgroundProperty, "ItemHoverBrush");
        return border;
    }

    /// <summary>笔迹缩略图：把点序列缩放进 64×44 的方框，用细线画出。</summary>
    private static UIElement BuildThumbnail(StrokeData stroke)
    {
        const double w = 68, h = 46;
        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        var box = stroke.Bounds;
        double bw = Math.Max(1, box.Width), bh = Math.Max(1, box.Height);
        double scale = Math.Min((w - 8) / bw, (h - 8) / bh);
        var geo = new PathGeometry();
        if (stroke.Points.Count >= 2)
        {
            var fig = new PathFigure();
            var p0 = stroke.Points[0];
            fig.StartPoint = new Point((p0.X - box.X) * scale + 4, (p0.Y - box.Y) * scale + 4);
            var seg = new PolyLineSegment();
            for (int i = 1; i < stroke.Points.Count; i++)
            {
                var p = stroke.Points[i];
                seg.Points.Add(new Point((p.X - box.X) * scale + 4, (p.Y - box.Y) * scale + 4));
            }
            fig.Segments.Add(seg);
            geo.Figures.Add(fig);
        }
        var path = new Path
        {
            Data = geo,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
        canvas.Children.Add(path);
        return canvas;
    }

    private static Button SmallBtn(string text)
    {
        var b = new Button
        {
            Content = text, Height = 44, MinWidth = 60, Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12.5, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
            BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(Control.BackgroundProperty, "ItemHoverBrush");
        b.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        b.SetResourceReference(Control.BorderBrushProperty, "PanelBorderBrush");
        return b;
    }

    private static TextBlock Txt(string t, double size, string brushKey, FontWeight weight, Thickness? margin = null)
    {
        var tb = new TextBlock { Text = t, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        if (margin is { } m) tb.Margin = m;
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    private static string ShapeName(string s) => s switch
    {
        "line" => "直线", "dashed" => "虚线", "dotted" => "点线", "arrow" => "箭头",
        "circle" => "圆", "ellipse" => "椭圆", "triangle" => "三角形",
        "quad" => "四边形", "polygon" => "多边形", "check" => "对勾", "cross" => "叉",
        "wave" => "波浪线", "heart" => "心形", "star" => "星形", "cloud" => "云朵",
        _ => s,
    };
}