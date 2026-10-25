using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using PptInk.PluginStudio.Core;
using PptInk.PluginStudio.Core.Lua;

namespace PptInk.PluginStudio.Services;

/// <summary>
/// AvalonEdit 宿主：Lua 语法高亮、行号、括号匹配、按 API 清单的补全（含面板回调名）。
/// 仅做编辑，不执行脚本（脚本执行交给主程序沙箱）。
/// </summary>
public partial class AvalonLuaEditorHost : UserControl
{
    private readonly List<string> _candidates = new();

    public AvalonLuaEditorHost()
    {
        InitializeComponent();
        LoadLuaHighlighting();
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        RefreshCandidates();
    }

    /// <summary>脚本文本（双向：设置外部内容，读取当前编辑内容）。</summary>
    public string Text
    {
        get => Editor.Text;
        set => Editor.Text = value ?? "";
    }

    /// <summary>追加面板元素回调名（供补全）。</summary>
    public void SetCallbackNames(IEnumerable<string> names)
    {
        _extra.Clear();
        _extra.AddRange(names.Where(n => !string.IsNullOrWhiteSpace(n)));
        RefreshCandidates();
    }

    private readonly List<string> _extra = new();

    private void RefreshCandidates()
    {
        _candidates.Clear();
        _candidates.AddRange(LuaApiCatalog.All.Select(a => a.Name));
        _candidates.AddRange(new[] { "onActivate", "onStart", "onDeactivate", "onTick", "function", "local", "if", "then", "end", "for", "while", "return", "nil", "true", "false" });
        _candidates.AddRange(_extra);
    }

    private void LoadLuaHighlighting()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/Lua.xshd");
            using var stream = Application.GetResourceStream(uri).Stream;
            using var reader = XmlReader.Create(stream);
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            FilePluginStudioLog.Instance.Warn("Lua 语法高亮加载失败（不影响编辑）：" + ex.Message);
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) { HideCompletion(); return; }
        var ch = e.Text[0];
        if (char.IsLetter(ch) || ch == '_') ShowCompletion();
        else HideCompletion();
    }

    private void ShowCompletion()
    {
        var word = CurrentWord();
        var matches = _candidates.Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).Take(50).ToList();
        if (matches.Count == 0) { HideCompletion(); return; }

        CompletionList.ItemsSource = matches;
        CompletionList.SelectedIndex = 0;
        CompletionPopup.IsOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!CompletionPopup.IsOpen) return;
        if (e.Key is Key.Down) { CompletionList.SelectedIndex = Math.Min(CompletionList.SelectedIndex + 1, CompletionList.Items.Count - 1); e.Handled = true; }
        else if (e.Key is Key.Up) { CompletionList.SelectedIndex = Math.Max(CompletionList.SelectedIndex - 1, 0); e.Handled = true; }
        else if (e.Key is Key.Tab || (e.Key is Key.Enter && CompletionList.SelectedItem is not null)) { InsertSelected(); e.Handled = true; }
        else if (e.Key is Key.Escape) { HideCompletion(); e.Handled = true; }
    }

    private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => InsertSelected();

    private void InsertSelected()
    {
        if (CompletionList.SelectedItem is not string api) { HideCompletion(); return; }
        var word = CurrentWord();
        var caret = Editor.CaretOffset;
        var start = caret - word.Length;
        Editor.Document.Replace(start, word.Length, api);
        Editor.CaretOffset = start + api.Length;
        HideCompletion();
        Editor.Focus();
    }

    private void HideCompletion() => CompletionPopup.IsOpen = false;

    /// <summary>把光标定位到指定行（校验错误定位用）。</summary>
    public void JumpToLine(int line)
    {
        if (line < 1) return;
        line = Math.Min(line, Editor.Document.LineCount);
        var docLine = Editor.Document.GetLineByNumber(line);
        Editor.CaretOffset = docLine.Offset;
        Editor.ScrollToLine(line);
        Editor.Focus();
    }

    private string CurrentWord()
    {
        var doc = Editor.Document;
        var caret = Editor.CaretOffset;
        int i = caret;
        while (i > 0)
        {
            var c = doc.GetCharAt(i - 1);
            if (char.IsLetterOrDigit(c) || c == '_') i--; else break;
        }
        return doc.GetText(i, caret - i);
    }
}