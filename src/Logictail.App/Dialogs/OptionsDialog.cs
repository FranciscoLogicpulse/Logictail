using Logictail.Core.Localization;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Logictail.Core.Settings;

namespace Logictail.App.Dialogs;

/// <summary>Edits a copy of the settings. Returns the edited copy, or null on Cancel.</summary>
public sealed class OptionsDialog : Window
{
    private readonly AppSettings _original;
    private readonly ComboBox _font = new() { MinWidth = 220, IsTextSearchEnabled = true };
    private readonly NumericUpDown _size = new() { Minimum = 6, Maximum = 72, Increment = 1, FormatString = "0", Width = 120 };
    private readonly NumericUpDown _spacing = new() { Minimum = 0, Maximum = 40, Increment = 1, FormatString = "0", Width = 120 };
    private readonly ComboBox _wrap = new() { MinWidth = 160 };
    private readonly ComboBox _interval = new() { MinWidth = 160 };
    private readonly NumericUpDown _tabWidth = new() { Minimum = 1, Maximum = 16, Increment = 1, FormatString = "0", Width = 120 };
    private readonly CheckBox _markers = new() { Content = L._("Show line _markers on wrapped lines") };
    private readonly ComboBox _tabPos = new() { MinWidth = 160, ItemsSource = new[] { "Top", "Bottom", "Left", "Right" }.Select(L._).ToList() };
    private readonly ComboBox _showTabs = new() { MinWidth = 160, ItemsSource = new[] { "Never", "At least two tabs", "At least one tab", "Always" }.Select(L._).ToList() };
    private readonly CheckBox _onTop = new() { Content = L._("_Always on top") };
    private readonly CheckBox _hideMenu = new() { Content = L._("_Hide main menu (Ctrl+M shows it again)") };
    private readonly ComboBox _saveOnExit = new() { MinWidth = 220, ItemsSource = new[] { "Never", "To file in working directory", "To file in application directory", "To my user profile" }.Select(L._).ToList() };
    private readonly CheckBox _savePos = new() { Content = L._("Save window _position") };
    private readonly ComboBox _activate = new() { MinWidth = 220, ItemsSource = new[] { "Never", "On scroll to end" }.Select(L._).ToList() };
    private readonly ComboBox _deactivate = new() { MinWidth = 220, ItemsSource = new[] { "Never", "On scroll or selection", "On selection", "On scroll" }.Select(L._).ToList() };

    private readonly ComboBox _language = new() { MinWidth = 220, ItemsSource = new[] { "Português (Portugal)", "English" } };

    private static readonly int[] WrapValues = [AppSettings.WrapOff, AppSettings.WrapAuto, .. AppSettings.WrapColumns];

    public OptionsDialog(AppSettings settings, string? loadedFrom)
    {
        _original = settings;
        Title = L._("Options");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _font.ItemsSource = MonospaceFonts(settings.FontFamily);
        _wrap.ItemsSource = WrapValues.Select(v => v switch
        {
            AppSettings.WrapOff => L._("Off (scroll sideways)"),
            AppSettings.WrapAuto => L._("Window width"),
            _ => L.F("{0} columns", v),
        }).ToList();
        _interval.ItemsSource = AppSettings.UpdateIntervals.Select(FormatInterval).ToList();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
        var row = 0;
        void Section(string title)
        {
            var label = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 0) };
            Grid.SetColumnSpan(label, 2);
            Add(label, null);
        }
        void Add(Control first, Control? second)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(first, row);
            grid.Children.Add(first);
            if (second is not null)
            {
                Grid.SetRow(second, row);
                Grid.SetColumn(second, 1);
                second.HorizontalAlignment = HorizontalAlignment.Left;
                grid.Children.Add(second);
            }
            row++;
        }
        TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center };

        Section(L._("Language"));
        Add(Label(L._("Language:")), _language);
        Add(new Control(), new TextBlock { Text = L._("The new language applies after Logictail restarts."), Foreground = Brushes.Gray, FontSize = 12 });
        Section(L._("Font"));
        Add(Label(L._("Name:")), _font);
        Add(Label(L._("Size:")), _size);
        Add(Label(L._("Line spacing (pixels):")), _spacing);
        Section(L._("File viewing"));
        Add(Label(L._("Wrap lines at:")), _wrap);
        Add(Label(L._("Update interval:")), _interval);
        Add(Label(L._("Tab width (characters):")), _tabWidth);
        Add(new Control(), _markers);
        Section(L._("Follow tail"));
        Add(Label(L._("Turn on:")), _activate);
        Add(Label(L._("Turn off:")), _deactivate);
        Section(L._("File tabs"));
        Add(Label(L._("Position:")), _tabPos);
        Add(Label(L._("Show tabs:")), _showTabs);
        Section(L._("Main window"));
        Add(new Control(), _onTop);
        Add(new Control(), _hideMenu);
        Section(L._("Saving settings"));
        Add(Label(L._("Save on exit:")), _saveOnExit);
        Add(new Control(), _savePos);
        Add(Label(L._("Loaded from:")), new SelectableTextBlock { Text = loadedFrom ?? L._("(defaults)"), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });

        var defaults = new Button { Content = L._("_Restore Defaults") };
        defaults.Click += (_, _) => Load(new AppSettings());
        var ok = new Button { Content = L._("OK"), IsDefault = true, MinWidth = 80, Classes = { "primary" } };
        var cancel = new Button { Content = L._("Cancel"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Close(Build());
        cancel.Click += (_, _) => Close(null);
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(defaults, Dock.Left);
        buttons.Children.Add(defaults);
        buttons.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel },
        });

        // The body scrolls so the dialog fits small or scaled screens; buttons stay visible.
        MaxHeight = 760;
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = grid, Padding = new Thickness(0, 0, 14, 0) });
        Content = root;
        Load(settings);
    }

    private void Load(AppSettings s)
    {
        _font.SelectedItem = (_font.ItemsSource as IList<string>)!.FirstOrDefault(f => f == s.FontFamily) ?? s.FontFamily;
        _size.Value = (decimal)s.FontSize;
        _spacing.Value = (decimal)s.LineSpacing;
        _wrap.SelectedIndex = Math.Max(0, Array.IndexOf(WrapValues, s.WrapColumn));
        _interval.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.UpdateIntervals, s.UpdateIntervalMs));
        _tabWidth.Value = s.TabWidth;
        _markers.IsChecked = s.ShowLineMarkers;
        _activate.SelectedIndex = (int)s.FollowActivate;
        _deactivate.SelectedIndex = (int)s.FollowDeactivate;
        _tabPos.SelectedIndex = (int)s.TabPosition;
        _showTabs.SelectedIndex = (int)s.ShowTabs;
        _onTop.IsChecked = s.AlwaysOnTop;
        _hideMenu.IsChecked = s.HideMainMenu;
        _saveOnExit.SelectedIndex = (int)s.SaveOnExit;
        _savePos.IsChecked = s.SaveWindowPosition;
        _language.SelectedIndex = (int)s.Language;
    }

    private AppSettings Build()
    {
        var s = _original.Clone(); // keeps rules, recent files and window placement
        s.FontFamily = _font.SelectedItem as string ?? s.FontFamily;
        s.FontSize = (double)(_size.Value ?? 13);
        s.LineSpacing = (double)(_spacing.Value ?? 0);
        s.WrapColumn = WrapValues[Math.Max(0, _wrap.SelectedIndex)];
        s.UpdateIntervalMs = AppSettings.UpdateIntervals[Math.Max(0, _interval.SelectedIndex)];
        s.TabWidth = (int)(_tabWidth.Value ?? 4);
        s.ShowLineMarkers = _markers.IsChecked == true;
        s.FollowActivate = (FollowActivate)_activate.SelectedIndex;
        s.FollowDeactivate = (FollowDeactivate)_deactivate.SelectedIndex;
        s.TabPosition = (TabStripPosition)_tabPos.SelectedIndex;
        s.ShowTabs = (TabShowMode)_showTabs.SelectedIndex;
        s.AlwaysOnTop = _onTop.IsChecked == true;
        s.HideMainMenu = _hideMenu.IsChecked == true;
        s.SaveOnExit = (SaveLocation)_saveOnExit.SelectedIndex;
        s.SaveWindowPosition = _savePos.IsChecked == true;
        s.Language = (Language)Math.Max(0, _language.SelectedIndex);
        return s.Normalize();
    }

    private static string FormatInterval(int ms) => ms switch
    {
        >= 60_000 => $"{ms / 60_000} min",
        >= 1_000 => $"{ms / 1_000} s",
        _ => $"{ms} ms",
    };

    /// <summary>Installed fixed-width fonts (same advance for 'i' and 'W'), plus the current choice.</summary>
    private static List<string> MonospaceFonts(string current)
    {
        var fonts = new List<string> { current };
        foreach (var family in FontManager.Current.SystemFonts)
        {
            var typeface = new Typeface(family);
            var narrow = new FormattedText("iiiii", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, null).Width;
            var wide = new FormattedText("WWWWW", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, null).Width;
            if (Math.Abs(narrow - wide) < 0.5 && !fonts.Contains(family.Name)) fonts.Add(family.Name);
        }
        return fonts;
    }
}
