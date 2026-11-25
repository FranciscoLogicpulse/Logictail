using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Logictail.Core.Highlighting;

namespace Logictail.App.Dialogs;

/// <summary>Edits the ordered highlight rule list. Returns the new list, or null on Cancel.</summary>
public sealed class HighlightingDialog : Window
{
    private readonly ObservableCollection<HighlightRule> _rules;
    private readonly ListBox _list = new() { MinWidth = 260, MaxHeight = 360 }; // scrolls instead of growing off-screen
    private readonly TextBox _pattern = new() { PlaceholderText = "Text to match" };
    private readonly CheckBox _ignoreCase = new() { Content = "_Ignore case" };
    private readonly CheckBox _invert = new() { Content = "In_vert match (style lines that don't match)" };
    private readonly CheckBox _regex = new() { Content = "_Regular expression" };
    private readonly CheckBox _bold = new() { Content = "B_old" };
    private readonly CheckBox _italic = new() { Content = "I_talic" };
    private readonly ColorChoice _fg = new();
    private readonly ColorChoice _bg = new();
    private readonly TextBlock _regexError = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _delete = new() { Content = "D_elete" };
    private readonly Button _up = new() { Content = "Move _Up" };
    private readonly Button _down = new() { Content = "Move _Down" };
    private readonly StackPanel _editor;
    private bool _loading;

    public HighlightingDialog(IEnumerable<HighlightRule> rules)
    {
        Title = "Highlighting";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _rules = new ObservableCollection<HighlightRule>(rules);

        _list.ItemsSource = _rules;
        // The list can briefly hand the template a null item while a rule is being replaced.
        _list.ItemTemplate = new FuncDataTemplate<HighlightRule?>((rule, _) => rule is null ? new Border() : new Border
        {
            Background = new SolidColorBrush(Color.FromUInt32(rule.Background)),
            Padding = new Thickness(6, 3),
            Child = new TextBlock
            {
                Text = (rule.Invert ? "NOT " : "") + (rule.IsRegex ? $"/{rule.Pattern}/" : rule.Pattern),
                Foreground = new SolidColorBrush(Color.FromUInt32(rule.Foreground)),
                FontWeight = rule.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = rule.Italic ? FontStyle.Italic : FontStyle.Normal,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            },
        });
        _list.SelectionChanged += (_, _) => LoadSelected();

        var add = new Button { Content = "_Add" };
        add.Click += (_, _) =>
        {
            _rules.Add(new HighlightRule { Pattern = "new rule" });
            _list.SelectedIndex = _rules.Count - 1;
            _pattern.Focus();
            _pattern.SelectAll();
        };
        _delete.Click += (_, _) =>
        {
            var i = _list.SelectedIndex;
            if (i < 0) return;
            _rules.RemoveAt(i);
            _list.SelectedIndex = Math.Min(i, _rules.Count - 1);
        };
        _up.Click += (_, _) => Move(-1);
        _down.Click += (_, _) => Move(+1);

        foreach (var box in new[] { _ignoreCase, _invert, _regex, _bold, _italic })
            box.IsCheckedChanged += (_, _) => SaveSelected();
        _pattern.TextChanged += (_, _) => SaveSelected();
        _fg.ValueChanged += (_, _) => SaveSelected();
        _bg.ValueChanged += (_, _) => SaveSelected();

        var ruleButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, _delete, _up, _down } };
        var left = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(ruleButtons, Dock.Bottom);
        left.Children.Add(ruleButtons);
        ruleButtons.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_list);

        _editor = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = "String:" },
                _pattern,
                _regexError,
                _ignoreCase, _invert, _regex, _bold, _italic,
                new TextBlock { Text = "Foreground color:", Margin = new Thickness(0, 6, 0, 0) },
                _fg,
                new TextBlock { Text = "Background color:" },
                _bg,
                new TextBlock
                {
                    Text = "The first rule in the list that matches a line sets its colors.",
                    Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Close(_rules.ToList());
        cancel.Click += (_, _) => Close(null);
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel },
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(left);
        Grid.SetColumn(_editor, 1);
        body.Children.Add(_editor);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        _list.SelectedIndex = _rules.Count > 0 ? 0 : -1;
        LoadSelected();
    }

    private HighlightRule? Selected => _list.SelectedIndex >= 0 ? _rules[_list.SelectedIndex] : null;

    private void LoadSelected()
    {
        var rule = Selected;
        _editor.IsEnabled = _delete.IsEnabled = rule is not null;
        _up.IsEnabled = _list.SelectedIndex > 0;
        _down.IsEnabled = _list.SelectedIndex >= 0 && _list.SelectedIndex < _rules.Count - 1;
        if (rule is null) return;

        _loading = true;
        _pattern.Text = rule.Pattern;
        _ignoreCase.IsChecked = rule.IgnoreCase;
        _invert.IsChecked = rule.Invert;
        _regex.IsChecked = rule.IsRegex;
        _bold.IsChecked = rule.Bold;
        _italic.IsChecked = rule.Italic;
        _fg.Value = rule.Foreground;
        _bg.Value = rule.Background;
        _loading = false;
        ShowRegexError(rule);
    }

    private void SaveSelected()
    {
        if (_loading || Selected is null) return;
        var index = _list.SelectedIndex;
        var rule = new HighlightRule
        {
            Pattern = _pattern.Text ?? "",
            IgnoreCase = _ignoreCase.IsChecked == true,
            Invert = _invert.IsChecked == true,
            IsRegex = _regex.IsChecked == true,
            Bold = _bold.IsChecked == true,
            Italic = _italic.IsChecked == true,
            Foreground = _fg.Value,
            Background = _bg.Value,
        };
        _loading = true;
        _rules[index] = rule;           // refreshes the list preview
        _list.SelectedIndex = index;
        _loading = false;
        ShowRegexError(rule);
    }

    private void ShowRegexError(HighlightRule rule)
    {
        _regexError.Text = "";
        _regexError.IsVisible = false;
        if (!rule.IsRegex) return;
        try { _ = new System.Text.RegularExpressions.Regex(rule.Pattern); }
        catch (ArgumentException ex) { _regexError.Text = "Invalid regular expression: " + ex.Message; _regexError.IsVisible = true; }
    }

    private void Move(int delta)
    {
        var i = _list.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        _rules.Move(i, j);
        _list.SelectedIndex = j;
    }
}
