using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Templates;

namespace PptInk.PluginStudio.Views;

public partial class WizardWindow : Window
{
    private int _step;   // 0=类型 1=模板 2=基本信息

    /// <summary>完成后生成的工程（取消则为 null）。</summary>
    public ProjectModel? Result { get; private set; }

    private sealed record TemplateChoice(string Display, string? Id);

    public WizardWindow()
    {
        InitializeComponent();
        UpdateStepVisuals();
    }

    private PluginKind SelectedKind =>
        RadioDraw.IsChecked == true ? PluginKind.Draw :
        RadioFree.IsChecked == true ? PluginKind.Free : PluginKind.Panel;

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0) { DialogResult = false; return; }
        _step--;
        UpdateStepVisuals();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 2) { _step++; UpdateStepVisuals(); return; }

        var kind = SelectedKind;
        var id = string.IsNullOrWhiteSpace(IdBox.Text) ? "com.example.myplugin" : IdBox.Text.Trim();
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "我的插件" : NameBox.Text.Trim();

        ProjectModel project;
        if (TemplateCombo.SelectedItem is TemplateChoice { Id: not null } choice)
            project = TemplateLibrary.Create(choice.Id);
        else
            project = TemplateLibrary.Blank(kind);

        project.Manifest.Id = id;
        project.Manifest.Name = name;
        project.Manifest.Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        project.Manifest.Author = AuthorBox.Text?.Trim() ?? "";
        project.Manifest.Description = DescBox.Text?.Trim() ?? "";

        Result = project;
        DialogResult = true;
    }

    private void UpdateStepVisuals()
    {
        StepType.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepTemplate.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepBasic.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        Highlight(Dot1, Label1, _step >= 0);
        Highlight(Dot2, Label2, _step >= 1);
        Highlight(Dot3, Label3, _step >= 2);

        BackBtn.Content = _step == 0 ? "取消" : "上一步";
        NextBtn.Content = _step == 2 ? "完成并进入工作台" : "下一步";

        if (_step == 1) PopulateTemplates();
    }

    private static void Highlight(Border dot, TextBlock label, bool active)
    {
        dot.Background = active
            ? (System.Windows.Media.Brush)Application.Current.FindResource("Accent")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD2, 0xDC));
        label.Foreground = active
            ? (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary")
            : (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary");
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void PopulateTemplates()
    {
        var kind = SelectedKind;
        var items = new List<TemplateChoice> { new("从空白开始（按类型生成骨架）", null) };
        foreach (var t in TemplateLibrary.All.Where(t => t.Kind == kind))
            items.Add(new TemplateChoice($"{t.Name} — {t.Description}", t.Id));
        TemplateCombo.ItemsSource = items;
        TemplateCombo.SelectedIndex = items.Count > 1 ? 1 : 0;
        TemplateCombo.SelectionChanged -= OnTemplateChanged;
        TemplateCombo.SelectionChanged += OnTemplateChanged;
        OnTemplateChanged(null, null);
    }

    private void OnTemplateChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (TemplateCombo.SelectedItem is TemplateChoice { Id: null })
            TemplateDesc.Text = "生成一个最小可运行骨架，稍后在工作台补齐细节。";
        else if (TemplateCombo.SelectedItem is TemplateChoice c)
            TemplateDesc.Text = c.Display;
    }
}