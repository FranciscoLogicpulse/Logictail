using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Logitail.App.Dialogs;
using Logitail.Core.Highlighting;
using Logitail.Core.Settings;
using Logitail.Core.Tail;

namespace Logitail.App;

public partial class MainWindow : Window
{
    private readonly List<LogTab> _tabs = [];
    private readonly DispatcherTimer _timer = new();
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private HighlightRuleSet _rules;
    private ItemsPresenter? _tabStrip;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    // Needed by the XAML loader / previewer.
    public MainWindow() : this(SettingsStore.ForCurrentUser(), new AppSettings(), CommandLine.Parse([])) { }

    public MainWindow(SettingsStore store, AppSettings settings, CommandLine commandLine)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _rules = new HighlightRuleSet(settings.HighlightRules);

        foreach (var kind in Enum.GetValues<TextEncodingKind>())
        {
            var item = new MenuItem { Header = kind.DisplayName(), ToggleType = MenuItemToggleType.Radio, Tag = kind };
            item.Click += (_, _) =>
            {
                if (Current is { } tab) { tab.File.SetEncoding(kind); _ = tab.RefreshAsync(true); }
            };
            EncodingMenu.Items.Add(item);
        }

        NetworkTab.Content = new Scanner.ScannerView();
        Modes.SelectionChanged += (_, e) =>
        {
            if (e.Source != Modes) return; // ignore the inner file tabs' events bubbling up
            LogStatusBar.IsVisible = Modes.SelectedItem != NetworkTab;
        };

        Tabs.TemplateApplied += (_, e) =>
        {
            _tabStrip = e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter");
            UpdateTabStrip();
        };
        Tabs.SelectionChanged += (_, _) =>
        {
            Current?.MarkSeen();
            UpdateChrome();
            // Focus after layout so keyboard scrolling works without clicking first.
            if (Current is { } tab) Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Loaded);
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddKey(Key.O, KeyModifiers.Control, () => OnOpen(null, null!));
        AddKey(Key.W, KeyModifiers.Control, () => OnClose(null, null!));
        AddKey(Key.T, KeyModifiers.Control, () => OnToggleFollow(null, null!));
        AddKey(Key.C, KeyModifiers.Control, () => OnCopy(null, null!));
        AddKey(Key.M, KeyModifiers.Control, () => { _settings.HideMainMenu = !_settings.HideMainMenu; ApplySettings(); });
        _timer.Tick += (_, _) => RefreshAll();

        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _normalPosition = Position; };
        Resized += (_, _) => { if (WindowState == WindowState.Normal) _normalSize = ClientSize; };
        Closing += (_, _) => SaveOnExit();

        ApplySettings();
        RebuildRecentMenu();
        PlaceWindow(commandLine);

        var files = commandLine.Files.Count > 0 ? commandLine.Files : _settings.OpenFiles;
        OpenFiles(files.Where(f => commandLine.Files.Count > 0 || File.Exists(f)));
        if (commandLine.Files.Count == 0 && _settings.CurrentTabIndex >= 0 && _settings.CurrentTabIndex < _tabs.Count)
            Tabs.SelectedItem = _tabs[_settings.CurrentTabIndex].Tab;

        var problems = commandLine.Errors.ToList();
        if (_store.LoadError is { } loadError) problems.Add("Settings file could not be read, defaults used:\n" + loadError);
        if (problems.Count > 0)
            Opened += async (_, _) => await MessageWindow.Show(this, "Logitail", string.Join("\n", problems) + "\n\n" + CommandLine.Usage);
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

            var tab = new LogTab(full, _rules, _settings);
            tab.Updated += (_, _) => { if (tab == Current) UpdateChrome(); };
            tab.View.ViewChanged += (_, _) => { if (tab == Current) UpdateChrome(); };
            tab.View.ContextMenu = ViewContextMenu(tab);
            tab.Tab.ContextMenu = TabContextMenu(tab);
            _tabs.Add(tab);
            Tabs.Items.Add(tab.Tab);
            Tabs.SelectedItem = tab.Tab;
            _settings.AddRecent(full);
            _ = tab.RefreshAsync(true);
        }
        RebuildRecentMenu();
        UpdateTabStrip();
    }

    // --- settings ---

    private void ApplySettings()
    {
        _rules = new HighlightRuleSet(_settings.HighlightRules);
        foreach (var tab in _tabs) tab.ApplySettings(_settings, _rules);
        _timer.Interval = TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs);
        Topmost = _settings.AlwaysOnTop;
        OnTopItem.IsChecked = _settings.AlwaysOnTop;
        MainMenu.IsVisible = !_settings.HideMainMenu;
        Tabs.TabStripPlacement = _settings.TabPosition switch
        {
            TabStripPosition.Bottom => Dock.Bottom,
            TabStripPosition.Left => Dock.Left,
            TabStripPosition.Right => Dock.Right,
            _ => Dock.Top,
        };
        UpdateTabStrip();
        UpdateChrome();
    }

    private void UpdateTabStrip()
    {
        if (_tabStrip is null) return;
        _tabStrip.IsVisible = _settings.ShowTabs switch
        {
            TabShowMode.Never => false,
            TabShowMode.AtLeastTwo => _tabs.Count >= 2,
            TabShowMode.AtLeastOne => _tabs.Count >= 1,
            _ => true,
        };
    }

    private void SaveOnExit()
    {
        _settings.OpenFiles = _tabs.Select(t => t.File.Path).ToList();
        _settings.CurrentTabIndex = Current is { } c ? _tabs.IndexOf(c) : 0;
        if (_settings.SaveWindowPosition && _normalSize.Width > 0)
        {
            var state = WindowState switch
            {
                WindowState.Maximized or WindowState.FullScreen => WindowStateKind.Maximized,
                _ => WindowStateKind.Normal, // never restore minimized
            };
            _settings.Window = new WindowPlacement(_normalPosition.X, _normalPosition.Y, _normalSize.Width, _normalSize.Height, state);
        }
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing sensible to do while closing */ }
    }

    private void PlaceWindow(CommandLine cmd)
    {
        if (cmd.TileCount is { } count) { Tile(count, cmd.TileIndex ?? 0); return; }
        if (cmd.Position is var (x, y, w, h))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)x, (int)y);
            Width = w;
            Height = h;
        }
        else if (_settings.SaveWindowPosition && _settings.Window is { } p && IsOnScreen(p))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)p.X, (int)p.Y);
            Width = p.Width;
            Height = p.Height;
            if (p.State == WindowStateKind.Maximized) WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (cmd.State is { } state)
            WindowState = state switch
            {
                WindowStateKind.Maximized => WindowState.Maximized,
                WindowStateKind.Minimized => WindowState.Minimized,
                _ => WindowState.Normal,
            };
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
    }

    private bool IsOnScreen(WindowPlacement p) =>
        Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint((int)p.X + 40, (int)p.Y + 10)));

    /// <summary>Places the window in row <paramref name="index"/> of <paramref name="count"/> equal rows of the screen.</summary>
    private void Tile(int count, int index)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var rowHeight = area.Height / count;
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(area.X, area.Y + rowHeight * index);
        Width = area.Width / screen.Scaling;
        Height = rowHeight / screen.Scaling - 32; // leave room for the title bar
    }

    // --- refresh and chrome ---

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
        var selection = tab.View.Selection is var (a, b) ? $"  |  selected {b - a + 1:N0} line(s)" : "";
        StatusText.Text = f.Error is { } err
            ? $"{f.Path}  |  {err}"
            : $"{(tab.View.FollowTail ? "Following" : "Paused")}  |  {f.LineCount:N0} lines  |  {LogTab.FormatSize(f.Length)}  |  " +
              $"{f.Encoding.DisplayName()}{selection}{indexing}  |  {f.Path}";
        FollowTailItem.IsChecked = tab.View.FollowTail;
        foreach (var item in EncodingMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is TextEncodingKind k && k == f.Encoding;
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = path.Replace("_", "__") };
            item.Click += (_, _) => OpenFiles([path]);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = _settings.RecentFiles.Count > 0;
    }

    private ContextMenu ViewContextMenu(LogTab tab)
    {
        var copy = new MenuItem { Header = "_Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => OnCopy(null, null!);
        var follow = new MenuItem { Header = "Follow _Tail", ToggleType = MenuItemToggleType.CheckBox };
        follow.Click += (_, _) => { tab.View.FollowTail = !tab.View.FollowTail; UpdateChrome(); };
        var showMenu = new MenuItem { Header = "Show _Main Menu" };
        showMenu.Click += (_, _) => { _settings.HideMainMenu = false; ApplySettings(); };
        var menu = new ContextMenu { Items = { copy, follow, new Separator(), showMenu } };
        menu.Opening += (_, _) =>
        {
            follow.IsChecked = tab.View.FollowTail;
            copy.IsEnabled = tab.View.Selection is not null;
            showMenu.IsVisible = _settings.HideMainMenu;
        };
        return menu;
    }

    private ContextMenu TabContextMenu(LogTab tab)
    {
        var close = new MenuItem { Header = "_Close" };
        close.Click += (_, _) => CloseTab(tab);
        var others = new MenuItem { Header = "Close O_thers" };
        others.Click += (_, _) => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); };
        var all = new MenuItem { Header = "Close _All" };
        all.Click += (_, _) => OnCloseAll(null, null!);
        return new ContextMenu { Items = { close, others, all } };
    }

    // --- menu handlers ---

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
        tab.IsClosed = true;
        _tabs.Remove(tab);
        Tabs.Items.Remove(tab.Tab);
        UpdateTabStrip();
        UpdateChrome();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Current?.View.SelectedText() is { } text && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void OnToggleFollow(object? sender, RoutedEventArgs e)
    {
        if (Current is { } tab) tab.View.FollowTail = !tab.View.FollowTail;
        UpdateChrome();
    }

    private void OnToggleOnTop(object? sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        ApplySettings();
    }

    private void OnHideMenu(object? sender, RoutedEventArgs e)
    {
        _settings.HideMainMenu = true;
        ApplySettings();
    }

    private void OnTile(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && tag.Split(',') is [var c, var i])
            Tile(int.Parse(c), int.Parse(i));
    }

    private void OnAutoSize(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        Width = 1100;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(area.X + (int)((area.Width - Width * screen.Scaling) / 2),
                                      area.Y + (int)((area.Height - Height * screen.Scaling) / 2));
        }
    }

    private async void OnOptions(object? sender, RoutedEventArgs e)
    {
        var result = await new OptionsDialog(_settings, _store.LoadedFrom).ShowDialog<AppSettings?>(this);
        if (result is null) return;
        _settings = result;
        ApplySettings();
    }

    private async void OnHighlighting(object? sender, RoutedEventArgs e)
    {
        var result = await new HighlightingDialog(_settings.HighlightRules).ShowDialog<List<HighlightRule>?>(this);
        if (result is null) return;
        _settings.HighlightRules = result;
        ApplySettings();
    }

    private async void OnLoadSettings(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a settings file to load",
            FileTypeFilter = [new FilePickerFileType("Logitail settings") { Patterns = ["*.json"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var loaded = SettingsStore.LoadFile(path);
            // Keep this window's session; take everything else from the file.
            loaded.OpenFiles = _settings.OpenFiles;
            loaded.RecentFiles = _settings.RecentFiles;
            loaded.Window = _settings.Window;
            _settings = loaded;
            ApplySettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await MessageWindow.Show(this, "Load settings", $"Could not load {path}:\n{ex.Message}");
        }
    }

    private async void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save settings to file",
            SuggestedFileName = SettingsStore.FileName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Logitail settings") { Patterns = ["*.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { SettingsStore.SaveFile(_settings, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageWindow.Show(this, "Save settings", $"Could not save {path}:\n{ex.Message}");
        }
    }

    private async void OnUsage(object? sender, RoutedEventArgs e) =>
        await MessageWindow.Show(this, "Command line usage", CommandLine.Usage, monospace: true);

    private async void OnAbout(object? sender, RoutedEventArgs e) =>
        await MessageWindow.Show(this, "About Logitail",
            $"Logitail {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nReal-time log viewer for personal use.\n\nSettings: {_store.LoadedFrom ?? _store.PathFor(_settings.SaveOnExit) ?? "not saved"}");

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void AddKey(Key key, KeyModifiers modifiers, Action action) =>
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = new Relay(action) });

    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
