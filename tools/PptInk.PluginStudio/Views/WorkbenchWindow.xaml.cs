using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PptInk.PluginStudio.Core;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using PptInk.PluginStudio.Core.Projects;
using PptInk.PluginStudio.Core.Templates;
using PptInk.PluginStudio.Core.Validation;

namespace PptInk.PluginStudio.Views;

public partial class WorkbenchWindow : Window
{
    private readonly ObservableCollection<PanelRow> _rows = new();
    private ProjectModel? _project;

    public WorkbenchWindow()
    {
        InitializeComponent();
        TypeColumn.ItemsSource = new[] { "text", "slider", "button" };
        PanelGrid.ItemsSource = _rows;
        KindCombo.SelectedIndex = 0;
        DockCombo.SelectedIndex = 0;

        LoadProject(TemplateLibrary.Blank(PluginKind.Panel));
        Status("就绪。可用「新建（向导）」开始，或从「示例库」克隆一个示例。");
    }

    // ───────────────────── 顶部动作 ─────────────────────

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new WizardWindow { Owner = this };
        if (wizard.ShowDialog() == true && wizard.Result is { } project)
        {
            LoadProject(project);
            Status($"已新建插件：{project.Manifest.Name}");
        }
    }

    private void Templates_Click(object sender, RoutedEventArgs e) => New_Click(sender, e);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var path = Services.FileDialogs.OpenProject(this);
        if (path is null) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            LoadProject(ProjectFile.Load(dir));
            Status("工程已打开。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开工程失败：" + ex.Message, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dir = Services.FileDialogs.PickFolder(this, "选择工程保存目录（将写入 project.ppstudio 与脚本）");
        if (dir is null) return;
        try
        {
            ProjectFile.Save(BuildProject(), dir);
            Status("工程已保存到：" + dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存工程失败：" + ex.Message, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var path = Services.FileDialogs.ImportZip(this);
        if (path is null) return;
        try
        {
            LoadProject(PackageImporter.Import(path));
            Status("已导入插件包（可反向编辑）。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败：" + ex.Message, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var issues = ValidateNow();
        var errors = issues.Count(i => i.IsError);
        Status(errors == 0
            ? $"校验通过（警告 {issues.Count - errors} 条）。可以打包或安装了。"
            : $"校验发现 {errors} 个错误、{issues.Count - errors} 条警告，请先修正。");
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    private void Pack_Click(object sender, RoutedEventArgs e)
    {
        var errors = ValidateNow().Count(i => i.IsError);
        if (errors > 0)
        {
            MessageBox.Show("存在校验错误，无法打包。请查看「校验结果」标签。", "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var project = BuildProject();
        var suggested = $"{project.Manifest.Id}-{project.Manifest.Version}.zip";
        var path = Services.FileDialogs.SaveZip(this, suggested);
        if (path is null) return;
        try
        {
            PackageBuilder.Build(project, path);
            Status("打包完成：" + path);
            MessageBox.Show("打包成功：\n" + path, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打包失败：" + ex.Message, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var errors = ValidateNow().Count(i => i.IsError);
        if (errors > 0)
        {
            MessageBox.Show("存在校验错误，无法安装。请查看「校验结果」标签。", "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var project = BuildProject();
        var installer = new PackageInstaller();
        if (installer.Install(project, out var error))
        {
            Status("已安装到：" + AppPaths.PluginRoot + "\\" + project.Manifest.Id);
            MessageBox.Show(
                $"安装成功。\n\n目标目录：{AppPaths.PluginRoot}\\{project.Manifest.Id}\n\n" +
                "在 PptInk 设置中心「插件」页启用，或点工具栏「插件」按钮使用。",
                "插件工作室", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("安装失败：" + error, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Readme_Click(object sender, RoutedEventArgs e)
    {
        var md = ReadmeGenerator.Generate(BuildProject().Manifest);
        var path = Services.FileDialogs.SaveText(this, "README.md", "Markdown (*.md)|*.md|文本 (*.txt)|*.txt");
        if (path is null) return;
        try
        {
            System.IO.File.WriteAllText(path, md);
            Status("说明已生成：" + path);
        }
        catch (Exception ex)
        {
            MessageBox.Show("生成说明失败：" + ex.Message, "插件工作室", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Visible;

    private void HelpClose_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Collapsed;

    // ───────────────────── 面板设计 ─────────────────────

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdatePanelTab();
        RefreshEditorCallbacks();
        RefreshPreview();
    }

    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new PanelRow { Type = "text", Text = "新文本", OnEvent = "noop" });
        RefreshEditorCallbacks();
        RefreshPreview();
    }

    private void RemoveElement_Click(object sender, RoutedEventArgs e)
    {
        if (PanelGrid.SelectedItem is PanelRow row) _rows.Remove(row);
        RefreshEditorCallbacks();
        RefreshPreview();
    }

    private void ValidationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ValidationList.SelectedItem is not IssueRow row) return;
        if (row.Field != "lua") return;
        var m = Regex.Match(row.Message, @"第\s*(\d+)\s*行");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var line))
            EditorHost.JumpToLine(line);
    }

    // ───────────────────── 内部逻辑 ─────────────────────

    private PluginKind SelectedKind => KindCombo.SelectedIndex switch
    {
        1 => PluginKind.Draw,
        2 => PluginKind.Free,
        _ => PluginKind.Panel,
    };

    private void LoadProject(ProjectModel project)
    {
        _project = project;
        var m = project.Manifest;

        KindCombo.SelectedIndex = m.Magic is >= 1 and <= 3 ? m.Magic - 1 : 0;
        IdBox.Text = m.Id;
        NameBox.Text = m.Name;
        VersionBox.Text = m.Version;
        AuthorBox.Text = m.Author;
        DescBox.Text = m.Description;
        EntryBox.Text = m.Entry;

        var perms = m.Permissions ?? Array.Empty<string>();
        PermFile.IsChecked = perms.Contains("file", StringComparer.OrdinalIgnoreCase);
        PermClip.IsChecked = perms.Contains("clipboard", StringComparer.OrdinalIgnoreCase);

        DockCombo.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "float", "left", "right", "bottom" }, m.Panel?.Dock ?? "float"));
        WidthBox.Text = (m.Panel?.Width ?? 240).ToString("0.###", CultureInfo.InvariantCulture);
        HeightBox.Text = (m.Panel?.Height ?? 160).ToString("0.###", CultureInfo.InvariantCulture);
        AlphaBox.Text = (m.Panel?.Alpha ?? 0.88).ToString("0.###", CultureInfo.InvariantCulture);
        ShowIslandCheck.IsChecked = m.Panel?.ShowInIsland ?? true;

        _rows.Clear();
        foreach (var el in m.Panel?.Elements ?? Array.Empty<PanelElement>())
            _rows.Add(PanelRow.From(el));

        EditorHost.Text = project.LuaSource;
        ValidationList.ItemsSource = null;

        UpdatePanelTab();
        RefreshEditorCallbacks();
        RefreshPreview();
    }

    private void UpdatePanelTab()
    {
        bool isPanel = SelectedKind == PluginKind.Panel;
        PanelTab.Visibility = isPanel ? Visibility.Visible : Visibility.Collapsed;
        if (!isPanel && LeftTabs.SelectedItem == PanelTab)
            LeftTabs.SelectedIndex = 0;
    }

    private void RefreshEditorCallbacks()
    {
        var names = new List<string>();
        foreach (var r in _rows) if (!string.IsNullOrWhiteSpace(r.OnEvent)) names.Add(r.OnEvent);
        names.AddRange(Core.Lua.LuaApiCatalog.EntryCallbacks(SelectedKind));
        EditorHost.SetCallbackNames(names);
    }

    private void RefreshPreview()
    {
        var panel = SelectedKind == PluginKind.Panel ? BuildPanelDecl() : null;
        PreviewHost.Content = Services.PreviewRenderer.Render(panel);
    }

    private PanelDecl BuildPanelDecl()
    {
        return new PanelDecl
        {
            Dock = (DockCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "float",
            Width = ParseDouble(WidthBox.Text, 240),
            Height = ParseDouble(HeightBox.Text, 160),
            Alpha = ParseDouble(AlphaBox.Text, 0.88),
            ShowInIsland = ShowIslandCheck.IsChecked == true,
            Elements = _rows.Select(r => r.ToElement()).ToArray(),
        };
    }

    private ProjectModel BuildProject() => new()
    {
        Manifest = new PluginManifest
        {
            Magic = (int)SelectedKind,
            Id = IdBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim(),
            Author = AuthorBox.Text.Trim(),
            Description = DescBox.Text.Trim(),
            Entry = string.IsNullOrWhiteSpace(EntryBox.Text) ? "main.lua" : EntryBox.Text.Trim(),
            Permissions = CollectPermissions(),
            Panel = SelectedKind == PluginKind.Panel ? BuildPanelDecl() : null,
        },
        LuaSource = EditorHost.Text,
        ProjectPath = _project?.ProjectPath,
    };

    private string[] CollectPermissions()
    {
        var list = new List<string>();
        if (PermFile.IsChecked == true) list.Add("file");
        if (PermClip.IsChecked == true) list.Add("clipboard");
        return list.ToArray();
    }

    private IReadOnlyList<ValidationIssue> ValidateNow()
    {
        var project = BuildProject();
        var manifest = ManifestValidator.Normalize(CloneForCheck(project.Manifest));
        var issues = new List<ValidationIssue>();
        issues.AddRange(ManifestValidator.Validate(manifest, e =>
            string.Equals(e, (manifest.Entry ?? "").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            project.Resources.Any(r => r.RelativePath == e)));
        issues.AddRange(LuaSyntaxChecker.Check(project.LuaSource));
        issues.AddRange(CrossConsistencyChecker.Check(project));

        ValidationList.ItemsSource = issues.Select(i => new IssueRow
        {
            Level = i.IsError ? "错误" : "警告",
            Field = i.Field,
            Message = i.Message,
        }).ToList();
        return issues;
    }

    // 校验前克隆一份，避免 Normalize 就地改写界面模型
    private static PluginManifest CloneForCheck(PluginManifest m) => PluginManifestJson.Deserialize(PluginManifestJson.Serialize(m))!;

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    private void Status(string text) => StatusText.Text = text;
}

/// <summary>面板元素的可编辑行（与 DataGrid 绑定）。</summary>
public sealed class PanelRow : INotifyPropertyChanged
{
    private string _type = "text";
    private string? _id;
    private string? _text;
    private string? _min;
    private string? _max;
    private string? _value;
    private string _onEvent = "noop";

    public string Type { get => _type; set { _type = value; OnChanged(); } }
    public string? Id { get => _id; set { _id = value; OnChanged(); } }
    public string? Text { get => _text; set { _text = value; OnChanged(); } }
    public string? Min { get => _min; set { _min = value; OnChanged(); } }
    public string? Max { get => _max; set { _max = value; OnChanged(); } }
    public string? Value { get => _value; set { _value = value; OnChanged(); } }
    public string OnEvent { get => _onEvent; set { _onEvent = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public PanelElement ToElement() => new()
    {
        Type = string.IsNullOrWhiteSpace(Type) ? "text" : Type.Trim(),
        Id = string.IsNullOrWhiteSpace(Id) ? null : Id.Trim(),
        Text = Text,
        Min = Parse(Min),
        Max = Parse(Max),
        Value = Parse(Value),
        OnEvent = string.IsNullOrWhiteSpace(OnEvent) ? "noop" : OnEvent.Trim(),
    };

    public static PanelRow From(PanelElement e) => new()
    {
        Type = e.Type,
        Id = e.Id,
        Text = e.Text,
        Min = e.Min?.ToString("0.###", CultureInfo.InvariantCulture),
        Max = e.Max?.ToString("0.###", CultureInfo.InvariantCulture),
        Value = e.Value?.ToString("0.###", CultureInfo.InvariantCulture),
        OnEvent = e.OnEvent,
    };

    private static double? Parse(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}

/// <summary>校验结果的显示行。</summary>
public sealed class IssueRow
{
    public string Level { get; init; } = "";
    public string Field { get; init; } = "";
    public string Message { get; init; } = "";
}