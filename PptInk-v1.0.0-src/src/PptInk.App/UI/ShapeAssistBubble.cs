using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PptInk.UI;

/// <summary>
/// 「规整为」确认气泡（AI 停顿自动识别的 bubble 模式）：浮岛语言家族一员——
/// ShowNearAnchor 锚点弹出（PopOpen 吮吸动画）/ AutoDismissIn 礼貌退场 / HideAnimated 对称收回。
/// 三键：确认替换（主按钮）/ 忽略 / 本次放映不再提示。触控目标 ≥38px，永不抢焦点。
/// v0.9.0 单窗口架构：IslandWindow 窗口基类 → StageElement 舞台元素（短命元素，
/// 每次识别 new 一个、Close 卸载并释放静态订阅）。
/// </summary>
public sealed class ShapeAssistBubble : StageElement
{
    public const double BubbleWidth = 524;   // 审查修复：标签+三键在 470 宽下会溢出（中文按钮最宽 ~116px）
    public const double BubbleHeight = 64;

    public ShapeAssistBubble(string shapeName, double confidence, Action onConfirm, Action onSessionOff)
    {
        var root = new Border
        {
            Width = BubbleWidth,
            Height = BubbleHeight,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(16, 0, 10, 0),
        };
        BindTheme(() =>
        {
            if (Application.Current?.Resources["PanelBrush"] is Brush bg) root.Background = bg;
            if (Application.Current?.Resources["PanelBorderBrush"] is Brush bb) root.BorderBrush = bb;
        });

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        // Run（Inline）无 Opacity 属性——层级感用字号/粗细表达（13 常规 / 15 加粗 / 11 弱化）
        label.Inlines.Add(new Run("规整为 "));
        label.Inlines.Add(new Run(shapeName) { FontWeight = FontWeights.Bold, FontSize = 15 });
        label.Inlines.Add(new Run($"  置信度 {confidence:P0}") { FontSize = 11 });
        BindTheme(() =>
        {
            if (Application.Current?.Resources["TextPrimaryBrush"] is Brush tb) label.Foreground = tb;
        });

        var confirm = BuildButton("变标准图形", primary: true, () => { HideAnimated(); onConfirm(); });
        var ignore = BuildButton("忽略", primary: false, HideAnimated);
        var off = BuildButton("本次放映不再提示", primary: false, () => { HideAnimated(); onSessionOff(); });
        off.Opacity = 0.72;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        row.Children.Add(new Border { Width = 12 });
        row.Children.Add(confirm);
        row.Children.Add(ignore);
        row.Children.Add(off);
        root.Child = row;
        Content = root;
    }

    private Button BuildButton(string text, bool primary, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = primary ? 13 : 12,
            FontWeight = primary ? FontWeights.Medium : FontWeights.Normal,
            Height = 38,
            Padding = new Thickness(primary ? 16 : 10, 0, primary ? 16 : 10, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Stylus.SetIsPressAndHoldEnabled(b, false);
        if (primary)
        {
            BindTheme(() =>
            {
                if (Application.Current?.Resources["AccentBrush"] is Brush ab)
                {
                    b.Background = ab;
                    b.Foreground = Brushes.White;
                }
            });
        }
        else
        {
            BindTheme(() =>
            {
                if (Application.Current?.Resources["ItemHoverBrush"] is Brush ib) b.Background = ib;
                if (Application.Current?.Resources["TextPrimaryBrush"] is Brush tb) b.Foreground = tb;
            });
        }
        ToolbarFactory.PressWire(b, () => { AnimationKit.PressPulse(b); onClick(); });
        return b;
    }
}
