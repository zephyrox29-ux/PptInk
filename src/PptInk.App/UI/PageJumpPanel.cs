using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PptInk.Core;

namespace PptInk.UI;

/// <summary>
/// 页码快捷跳页浮层（v1.1.2）：点按翻页岛的页码胶囊弹出，网格点选目标页。
/// 独占弹出槽（ShowNearAnchor 同一套规则，不压工具栏/翻页岛）；无操作 8s 自动退场。
/// 跳转经 PptController.GotoSlide（COM 直跳）；COM 不可用时由编排器 Toast 说明。
/// </summary>
internal sealed class PageJumpPanel : StageElement
{
    private readonly AppServices _svc;
    private readonly WrapPanel _grid = new() { Margin = new Thickness(4) };
    private int _total;

    /// <summary>请求跳转到第 N 页（1 起）。</summary>
    public event Action<int>? PageRequested;

    public PageJumpPanel(AppServices svc)
    {
        _svc = svc;
        var scroller = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        var body = new Border
        {
            Child = scroller,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 10),
        };
        body.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        body.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        body.SetResourceReference(Border.EffectProperty, "ShadowChrome");

        var content = new StackPanel();
        var title = new TextBlock { Text = "跳转到", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        content.Children.Add(title);
        content.Children.Add(body);

        Content = content;
        Focusable = false;
        Stylus.SetIsPressAndHoldEnabled(this, false);
    }

    /// <summary>按总页数重建页码网格并弹出（锚点 = 翻页岛矩形）。</summary>
    public void ShowFor(Rect anchor, int total)
    {
        if (total <= 0)
        {
            Toast.Show("当前没有可用的页码信息（未放映或 COM 不可用）。");
            return;
        }
        if (total == _total && _grid.Children.Count > 0)
        {
            if (ShowNearAnchor(anchor, Width, Math.Min(Height, 340)))
                AutoDismissIn(8);
            return;
        }
        _total = total;
        _grid.Children.Clear();
        int columns = total > 100 ? 10 : 8;
        _grid.MaxWidth = columns * 40 + (columns - 1) * 4;
        for (int n = 1; n <= total; n++)
        {
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = n.ToString(),
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                Width = 40, Height = 40,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            btn.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            btn.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
            ToolbarFactory.PressWire(btn, () =>
            {
                if (btn.Content is TextBlock tb && int.TryParse(tb.Text, out int page))
                    PageRequested?.Invoke(page);
                HideAnimated();
            });
            _grid.Children.Add(btn);
        }

        // 尺寸先量再弹：宽按网格自然宽（不含滚动条），高最多 340（约 7 行）
        Width = double.NaN;
        Height = double.NaN;
        UpdateLayout();
        double w = Math.Min(360, Math.Max(_grid.MaxWidth + 24, DesiredSize.Width));
        double h = Math.Min(340, Math.Max(120, DesiredSize.Height));
        Width = w;
        Height = h;
        if (ShowNearAnchor(anchor, w, h))
            AutoDismissIn(8);
        else
            Toast.Show("没有空位弹出页码面板，先关掉其他浮窗。");
    }
}
