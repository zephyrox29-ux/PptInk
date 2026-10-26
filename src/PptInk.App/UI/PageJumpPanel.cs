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
        _total = total;
        _grid.Children.Clear();
        // 44px 视觉目标 + 4px 间距；列数与面板宽度同步计算，不能只算按钮宽度
        // 而漏掉每个按钮的 margin（原实现 8 列时 352 > 348，WrapPanel 实际只能放 7 列）。
        int columns = total > 100 ? 9 : 7;
        const double cell = 48;
        double panelW = Math.Min(456, columns * cell + 24);
        _grid.MaxWidth = columns * cell;
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
                Width = 44, Height = 44,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            btn.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            btn.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
            ToolbarFactory.PressWire(btn, () =>
            {
                if (btn.Content is TextBlock tb && int.TryParse(tb.Text, out int page))
                    PageRequested?.Invoke(page);
                HideAnimated();
            });
            _grid.Children.Add(btn);
        }

        // SizeToContent 的等价物：先以 Hidden 参与测量（Collapsed 不产生 DesiredSize），
        // 再按实际内容算高；旧实现首次打开高度恒为 120，约两行就截断。
        int rows = (total + columns - 1) / columns;
        double h = Math.Min(340, Math.Max(120, rows * cell + 44));
        Width = panelW;
        Height = h;
        Visibility = Visibility.Hidden;
        Measure(new Size(panelW, double.PositiveInfinity));
        UpdateLayout();
        if (ShowNearAnchor(anchor, panelW, h))
            AutoDismissIn(8);
        else
        {
            Visibility = Visibility.Collapsed;
            Toast.Show("没有空位弹出页码面板，先关掉其他浮窗。");
        }
    }
}
