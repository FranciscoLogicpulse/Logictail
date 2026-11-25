using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Logitail.Core.Highlighting;
using Logitail.Core.Tail;

namespace Logitail.App;

public partial class MainWindow : Window
{
    private readonly List<LogTab> _tabs = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // shortcut: built-in rules until the Highlighting dialog and settings file land in milestone 2.
    private readonly HighlightRuleSet _rules = new([
        new HighlightRule { Pattern = "error", Foreground = 0xFFFFFFFF, Background = 0xFFC62828 },
        new HighlightRule { Pattern = "exception", Foreground = 0xFFFFFFFF, Background = 0xFFC62828 },
        new HighlightRule { Pattern = "warn", Foreground = 0xFF000000, Background = 0xFFFFE082 },
    ]);

    public MainWindow()
    {
        InitializeComponent();
        foreach (var kind in Enum.GetValues<TextEncodingKind>())
        {
            var item = new MenuItem { Header = kind.DisplayName(), ToggleType = MenuItemToggleType.Radio, Tag = kind };
            item.Click += (_, _) =>
            {
                if (Current is { } tab) { tab.File.SetEncoding(kind); _ = tab.RefreshAsync(true); }
            };
            EncodingMenu.Items.Add(item);
        }

        Tabs.SelectionChanged += (_, _) =>
        {
            Current?.MarkSeen();
            UpdateChrome();
            // Focus after layout so keyboard scrolling works without clicking first.
            if (Current is { } tab) Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Loaded);
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new Relay(() => OnOpen(null, null!)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Control), Command = new Relay(() => OnClose(null, null!)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.T, KeyModifiers.Control), Command = new Relay(() => OnToggleFollow(null, null!)) });
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
    }

    private LogTab? Current => (Tabs.SelectedItem as TabItem)?.Tag as LogTab;

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var full = Path.GetFullPath(path);
            var existing = _tabs.FirstOrDefault(t => string.Equals(t.File.Path, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { Tabs.SelectedItem = existing.Tab; continue; }

            var tab = new LogTab(full, _rules);
            tab.Updated += (_, _) => { if (tab == Current) UpdateChrome(); };
            tab.View.ViewChanged += (_, _) => { if (tab == Current) UpdateChrome(); };
            _tabs.Add(tab);
            Tabs.Items.Add(tab.Tab);
            Tabs.SelectedItem = tab.Tab;
            _ = tab.RefreshAsync(true);
        }
    }

    private void RefreshAll()
    {
        foreach (var tab in _tabs) _ = tab.RefreshAsync(tab == Current);
    }

    private void UpdateChrome()
    {
        if (Current is not { } tab)
        {
            Title = "Logitail";
            StatusText.Text = "Open a file to view (Ctrl+O or drag and drop)";
            FollowTailItem.IsChecked = false;
            return;
        }
        var f = tab.File;
        Title = $"{f.Name} - Logitail";
        var indexing = f.IsFullyIndexed ? "" : "  |  indexing...";
        StatusText.Text = f.Error is { } err
            ? $"{f.Path}  |  {err}"
            : $"{f.Path}  |  {f.LineCount:N0} lines  |  {LogTab.FormatSize(f.Length)}  |  {f.Encoding.DisplayName()}  |  " +
              $"{(tab.View.FollowTail ? "following tail" : "paused")}{indexing}";
        FollowTailItem.IsChecked = tab.View.FollowTail;
        foreach (var item in EncodingMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is TextEncodingKind k && k == f.Encoding;
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file(s) to view",
            AllowMultiple = true,
        });
        OpenFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } items)
            OpenFiles(items.Select(i => i.TryGetLocalPath()).OfType<string>());
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Current is { } tab) CloseTab(tab);
    }

    private void OnCloseOthers(object? sender, RoutedEventArgs e)
    {
        foreach (var tab in _tabs.Where(t => t != Current).ToList()) CloseTab(tab);
    }

    private void OnCloseAll(object? sender, RoutedEventArgs e)
    {
        foreach (var tab in _tabs.ToList()) CloseTab(tab);
    }

    private void CloseTab(LogTab tab)
    {
        _tabs.Remove(tab);
        Tabs.Items.Remove(tab.Tab);
        UpdateChrome();
    }

    private void OnToggleFollow(object? sender, RoutedEventArgs e)
    {
        if (Current is { } tab) tab.View.FollowTail = !tab.View.FollowTail;
        UpdateChrome();
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
