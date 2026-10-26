using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Templates;

namespace PptInk.PluginStudio.Views;

/// <summary>示例库：直接克隆一个可运行模板，不经过“先填 Id/名称”的新建向导。</summary>
internal sealed class TemplatePickerWindow : Window
{
    public ProjectModel? Result { get; private set; }

    public TemplatePickerWindow()
    {
        Title = "示例库";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = "选一个示例，直接进入工作台",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "示例会创建为新的未保存工程，可在工作台继续修改。",
            FontSize = 11.5, Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray,
        });

        var list = new StackPanel();
        foreach (var template in TemplateLibrary.All)
        {
            var pick = new Button
            {
                Content = $"{template.Name} — {template.Description}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 52, Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 0, 14, 0),
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
            };
            var captured = template;
            pick.Click += (_, _) =>
            {
                Result = TemplateLibrary.Create(captured.Id);
                DialogResult = true;
            };
            list.Children.Add(pick);
        }
        root.Children.Add(list);
        Content = root;
    }
}