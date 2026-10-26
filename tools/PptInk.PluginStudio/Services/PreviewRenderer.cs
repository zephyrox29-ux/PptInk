using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Services;

/// <summary>按 manifest.panel 渲染简化预览（近似主程序观感，不追求像素级一致）。</summary>
internal static class PreviewRenderer
{
    public static UIElement Render(PanelDecl? panel)
    {
        var outer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF1, 0xF4)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            SnapsToDevicePixels = true,
        };

        if (panel is null)
        {
            outer.Child = new TextBlock
            {
                Text = "该类型无面板预览",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return outer;
        }

        double alpha = Math.Clamp(panel.Alpha, 0.3, 1.0);
        var body = new Border
        {
            Width = Math.Clamp(panel.Width, 160, 600),
            Height = Math.Clamp(panel.Height, 100, 500),
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0x25, 0x2A, 0x33)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
        };

        var stack = new StackPanel();
        foreach (var el in panel.Elements ?? Array.Empty<PanelElement>())
        {
            switch ((el?.Type ?? "text").ToLowerInvariant())
            {
                case "button":
                    stack.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 4, 0, 4),
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xDE)),
                        Child = new TextBlock
                        {
                            Text = el!.Text ?? "按钮",
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(6),
                        },
                    });
                    break;

                case "slider":
                    double min = el!.Min ?? 0, max = el.Max ?? 100;
                    double val = el.Value ?? min;
                    double frac = max > min ? Math.Clamp((val - min) / (max - min), 0, 1) : 0;
                    var track = new Border { Width = 220, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x51, 0x5B)) };
                    var fill = new Border { Width = 220 * frac, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xDE)) };
                    var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                    grid.Children.Add(track);
                    grid.Children.Add(fill);
                    stack.Children.Add(grid);
                    break;

                default:
                    stack.Children.Add(new TextBlock
                    {
                        Text = el?.Text ?? "文本",
                        Foreground = Brushes.White,
                        FontSize = 14,
                        Margin = new Thickness(0, 4, 0, 4),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    break;
            }
        }

        body.Child = stack;

        var caption = new TextBlock
        {
            Text = $"Dock={panel.Dock} · {Math.Clamp(panel.Width, 160, 600):0}×{Math.Clamp(panel.Height, 100, 500):0} · α={alpha:0.00}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        wrap.Children.Add(body);
        wrap.Children.Add(caption);
        outer.Child = wrap;
        return outer;
    }
}