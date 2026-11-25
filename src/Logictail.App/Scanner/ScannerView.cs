using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Logictail.Scanner;

namespace Logictail.App.Scanner;

/// <summary>The Network tab: range input, scan controls, filters, the results table and a details pane.</summary>
public sealed class ScannerView : UserControl
{
    private enum SortColumn { Favorite, Status, Name, Address, Manufacturer, Mac, Type, Services, Comment }

    // ★, Status, Name, IP, Manufacturer, MAC, Type, Services, Comments
    // ★, Status, Name, IP, Manufacturer, MAC, Type, Services, Comments. Stars share the leftover width.
    private const string WideColumns = "24,28,2*,104,2*,136,100,1.5*,1.5*";
    private const string CompactColumns = "24,28,2*,104,2*,0,100,1.5*,0";
    private static readonly int[] WideOnly = [5, 8];
    private bool _compact;
    private Grid _header = null!;
    private string ColumnWidths => _compact ? CompactColumns : WideColumns;
    private static readonly IBrush AliveBrush = Palette.Green;
    private static readonly IBrush DeadBrush = Palette.Muted;
    private static readonly IBrush UnknownBrush = Palette.Orange;
    private static readonly IBrush StarBrush = Palette.Teal;

    private readonly IProbes _probes;
    private readonly HostStore _store;
    private readonly Task<OuiDatabase> _vendors;
    private readonly TextBox _from = new() { PlaceholderText = "192.168.1.1", MinWidth = 140 };
    private readonly TextBox _to = new() { PlaceholderText = "192.168.1.254", MinWidth = 140 };
    private string? _predicted; // prefix we filled into "To", so we can update it while the user hasn't typed there
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "Fast", "Accurate (slower)" }, SelectedIndex = 0, Width = 140 };
    private readonly Button _scanButton = new() { Content = "Scan", MinWidth = 90, Classes = { "primary" } }; // not IsDefault: Enter in the search box must not start a scan
    private readonly Button _pauseButton = new() { Content = "Pause", MinWidth = 80, IsEnabled = false };
    private readonly CheckBox _showAlive = new() { Content = "Show _alive", IsChecked = true };
    private readonly CheckBox _showDead = new() { Content = "Show _dead", IsChecked = false };
    private readonly CheckBox _showUnknown = new() { Content = "Show _unknown", IsChecked = true };
    private readonly CheckBox _favoritesOnly = new() { Content = "_Favorites only", IsChecked = false };
    private readonly CheckBox _listShares = new() { Content = "Shares", IsChecked = false };
    private readonly TextBox _search = new() { PlaceholderText = "Search results", Width = 220 };
    private readonly TextBox _ports = new() { PlaceholderText = "No port scan (e.g. 22, 80, 443, UDP: 53)", Width = 280 };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly StackPanel _details = new() { Spacing = 4, Margin = new Thickness(12) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 200, Height = 6 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new()
    {
        Foreground = Palette.Slate, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
    };

    private readonly List<HostResult> _results = [];
    private readonly Dictionary<System.Net.IPAddress, HostResult> _raw = []; // scan results before the user's notes
    private readonly ObservableCollection<HostResult> _visible = [];
    private readonly ConcurrentQueue<HostResult> _incoming = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private OuiDatabase _vendorDb = OuiDatabase.Empty;
    private ScanProgress? _lastProgress;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;
    private SortColumn _sort = SortColumn.Address;
    private bool _descending;
    private string _idleState = "Ready"; // shown when not scanning: Ready, Finished or Stopped

    public ScannerView() : this(new SystemProbes(), Path.Combine(AppContext.BaseDirectory, "data", "oui.csv"), HostStore.Load(HostStore.DefaultPath)) { }

    public ScannerView(IProbes probes, string vendorFile, HostStore store)
    {
        _probes = probes;
        _store = store;
        _vendors = Task.Run(() => OuiDatabase.Load(vendorFile));
        RangeText = LocalNetwork.Subnets().FirstOrDefault()?.ToRangeText() ?? "";
        // Predict "To": it almost always shares the first three parts of "From" (192.168.1.x -> 192.168.1.).
        _from.TextChanged += (_, _) =>
        {
            var prefix = Prefix(_from.Text);
            _to.PlaceholderText = prefix is null ? "192.168.1.254" : prefix + "254";
            if (string.IsNullOrEmpty(_to.Text) || _to.Text == _predicted) { _to.Text = null; _predicted = null; }
        };
        _to.GotFocus += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_to.Text) || Prefix(_from.Text) is not { } prefix) return;
            _to.Text = _predicted = prefix;
            _to.CaretIndex = prefix.Length;
        };

        ToolTip.SetTip(_listShares, "Also list shared folders. Windows signs in to each device as you to do this.");
        // One "Fill range" menu instead of three buttons keeps the toolbar on one line.
        var fill = new Button { Content = "Fill range" };
        fill.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("This PC's subnet", () => RangeText = LocalNetwork.Subnets().FirstOrDefault()?.ToRangeText() ?? RangeText),
                MenuItem("My favorites", FillFavorites),
                MenuItem("From a file (one address or range per line)...", async () => await LoadTargetsFileAsync()),
            },
        };
        _scanButton.Click += async (_, _) => await ToggleScanAsync();
        _pauseButton.Click += (_, _) => TogglePause();

        var export = new Button { Content = "Export" };
        export.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("CSV...", async () => await ExportAsync("csv", ',')),
                MenuItem("CSV for Excel (semicolons)...", async () => await ExportAsync("csv", ';')),
                MenuItem("HTML...", async () => await ExportAsync("html", ',')),
                new Separator(),
                MenuItem("Delete saved names, comments and favorites...", async () => await ForgetAllAsync()),
            },
        };

        foreach (var box in new[] { _showAlive, _showDead, _showUnknown, _favoritesOnly }) box.IsCheckedChanged += (_, _) => Rebuild();
        _search.TextChanged += (_, _) => Rebuild();
        _flush.Tick += (_, _) => Flush();

        _list.ItemsSource = _visible;
        // Rows stretch to the list width so star-sized columns line up with the header.
        _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>()) { Setters = { new Avalonia.Styling.Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<HostResult?>((host, _) => host is null ? new Control() : Row(host));
        _list.SelectionChanged += (_, _) => ShowDetails();
        _list.DoubleTapped += (_, _) => { if (Selected is { } h) _ = EditAsync(h); };
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenu.Opening += (_, e) =>
        {
            if (Selected is { } h) FillContextMenu(_list.ContextMenu, h);
            else e.Cancel = true;
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(14, 12, 14, 8) };
        Control[] tools = [new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold }, _from,
            new TextBlock { Text = "To", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold }, _to, fill, _speed, _listShares, _scanButton, _pauseButton, export];
        for (var i = 0; i < tools.Length; i++)
        {
            tools[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            Grid.SetColumn(tools[i], i);
            toolbar.Children.Add(tools[i]);
        }
        // Labels sit close to their own box and apart from the previous one.
        tools[0].Margin = new Thickness(0, 0, 10, 0);
        tools[2].Margin = new Thickness(14, 0, 10, 0);
        tools[1].Margin = tools[3].Margin = new Thickness(0);
        var filters = new WrapPanel { Margin = new Thickness(14, 0, 14, 8) };
        var presets = new Button { Content = "Presets" };
        presets.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("Common ports", () => _ports.Text = PortList.Common),
                MenuItem("Well-known TCP ports (1-1023)", () => _ports.Text = PortList.WellKnown),
                MenuItem("All TCP ports (1-65535, slow)", () => _ports.Text = PortList.AllTcp),
                new Separator(),
                MenuItem("No port scan", () => _ports.Text = null),
            },
        };
        ToolTip.SetTip(_ports, "Ports to check on every live device. Ranges and UDP allowed: 22, 80, 8000-8100, UDP: 53, 161");
        var portsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(14, 0, 14, 8) };
        Control[] portControls = [new TextBlock { Text = "Ports", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, _ports, presets, new Control(), _search];
        presets.Margin = new Thickness(8, 0, 0, 0);
        for (var i = 0; i < portControls.Length; i++) { Grid.SetColumn(portControls[i], i); portsRow.Children.Add(portControls[i]); }
        foreach (var c in new Control[] { _showAlive, _showDead, _showUnknown, _favoritesOnly })
        {
            c.Margin = new Thickness(0, 0, 18, 4);
            filters.Children.Add(c);
        }
        var statusBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(14, 6), Children = { _progress, _status } };

        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), ClipToBounds = true };
        _header = Header();
        table.Children.Add(_header);
        // Narrow tables drop the MAC and Comments columns (both stay in the details pane).
        table.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 900;
            if (compact == _compact) return;
            _compact = compact;
            table.Children.Remove(_header);
            _header = Header();
            table.Children.Insert(0, _header);
            Rebuild();
        };
        var body = new Panel { Children = { _list, _empty } };
        Grid.SetRow(body, 1);
        table.Children.Add(body);

        var detailsPane = new Border
        {
            Width = 280,
            BorderBrush = Palette.Rule,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = _details },
        };
        var main = new DockPanel();
        DockPanel.SetDock(detailsPane, Dock.Right);
        main.Children.Add(detailsPane);
        main.Children.Add(table);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(portsRow, Dock.Top);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(portsRow);
        root.Children.Add(filters);
        var statusBorder = new Border { BorderBrush = Palette.Rule, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar };
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);
        root.Children.Add(main);
        Content = root;
        UpdateStatus();
        ShowDetails();
    }

    /// <summary>All results of the current scan, unfiltered.</summary>
    public IReadOnlyList<HostResult> Results => _results;

    /// <summary>Results as shown (filtered and sorted).</summary>
    public IReadOnlyList<HostResult> VisibleResults => _visible;

    public bool IsScanning => _cts is not null;

    /// <summary>
    /// The scan input. One range "a-b" fills From and To; anything else (a list, host names) goes into From.
    /// A "To" that is just a number is the last part of the address (192.168.1.10 to 50).
    /// </summary>
    public string RangeText
    {
        get
        {
            var from = _from.Text?.Trim() ?? "";
            var to = _to.Text?.Trim() ?? "";
            return to.Length == 0 || to == _predicted ? from : $"{from}-{to}";
        }
        set
        {
            _predicted = null;
            var parsed = IpRangeParser.Parse(value ?? "");
            if (parsed.Errors.Count == 0 && parsed.Targets is [ScanTarget.Range r] && r.First != r.Last)
            {
                _from.Text = IpRangeParser.ToAddress(r.First).ToString();
                _to.Text = IpRangeParser.ToAddress(r.Last).ToString();
            }
            else
            {
                _from.Text = value;
                _to.Text = null;
            }
        }
    }

    /// <summary>"192.168.1." for a complete IPv4 address, else null.</summary>
    private static string? Prefix(string? ip)
    {
        var parts = (ip ?? "").Trim().Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, out _)) ? string.Join('.', parts[..3]) + "." : null;
    }

    public HostResult? Selected => _list.SelectedItem as HostResult;

    /// <summary>Ports to scan on every live device; empty means no port scan.</summary>
    public string PortsText { get => _ports.Text ?? ""; set => _ports.Text = value; }

    /// <summary>Off by default: listing shares makes Windows sign in to each device as the current user.</summary>
    public bool ListShares { get => _listShares.IsChecked == true; set => _listShares.IsChecked = value; }

    /// <summary>The scan result before the user's notes were applied (what the notes store is keyed on).</summary>
    private HostResult Raw(HostResult shown) => _raw.GetValueOrDefault(shown.Address, shown);

    public void Select(HostResult host) => _list.SelectedItem = host;

    /// <summary>Raised to launch a program; tests replace it so nothing really starts.</summary>
    public Func<ProcessStartInfo, Task> Launch { get; set; } = psi => { Process.Start(psi)?.Dispose(); return Task.CompletedTask; };

    // --- scanning ---

    public async Task ToggleScanAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }

        var parsed = IpRangeParser.Parse(RangeText);
        if (parsed.Errors.Count > 0 || parsed.Targets.Count == 0)
        {
            ShowError(parsed.Errors.Count > 0 ? string.Join("; ", parsed.Errors) : "Enter an IP address, range or host name to scan.");
            return;
        }

        var portParse = PortListParser.Parse(PortsText);
        if (portParse.Errors.Count > 0) { ShowError("Ports: " + string.Join("; ", portParse.Errors)); return; }
        var ports = portParse.Ports;

        _results.Clear();
        _raw.Clear();
        _visible.Clear();
        while (_incoming.TryDequeue(out _)) { }
        _lastProgress = null;
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        _scanButton.Content = "Stop";
        _pauseButton.IsEnabled = true;
        _pauseButton.Content = "Pause";
        _flush.Start();
        UpdateStatus();
        ShowDetails();

        var options = new ScanOptions { Speed = _speed.SelectedIndex == 1 ? ScanSpeed.Accurate : ScanSpeed.Fast, ListShares = ListShares, Ports = ports };
        var stopped = false;
        string? failure = null;
        try
        {
            _vendorDb = await _vendors;
            var engine = new ScanEngine(_probes, _vendorDb);
            await Task.Run(() => engine.ScanAsync(parsed.Targets, options, _incoming.Enqueue, p => _lastProgress = p, _gate, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        catch (Exception ex)
        {
            // Never let a scan failure take down the app (this runs from a click handler).
            failure = ex.Message;
        }
        finally
        {
            _flush.Stop();
            Flush();
            _cts.Dispose();
            _cts = null;
            _gate = null;
            _scanButton.Content = "Scan";
            _pauseButton.IsEnabled = false;
            _pauseButton.Content = "Pause";
            _idleState = stopped ? "Stopped" : "Finished";
            UpdateStatus();
            if (failure is not null) ShowError("Scan failed: " + failure);
        }
    }

    private void TogglePause()
    {
        if (_gate is null) return;
        if (_gate.IsPaused) { _gate.Resume(); _pauseButton.Content = "Pause"; }
        else { _gate.Pause(); _pauseButton.Content = "Resume"; }
        UpdateStatus();
    }

    private void FillFavorites()
    {
        var addresses = _store.FavoriteAddresses();
        if (addresses.Count == 0) { ShowError("No favorites yet. Right-click a device and choose Add to favorites."); return; }
        RangeText = string.Join(", ", addresses);
    }

    private async Task LoadTargetsFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load addresses to scan",
            FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt", "*.*"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var parsed = IpRangeParser.Parse(text);
            if (parsed.Errors.Count > 0) { ShowError(string.Join("; ", parsed.Errors.Take(5))); return; }
            // Show the file's targets in the box so the user sees what will be scanned.
            RangeText = string.Join(", ", text.Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not read {path}: {ex.Message}");
        }
    }

    // --- results ---

    private void Flush()
    {
        var added = false;
        while (_incoming.TryDequeue(out var host))
        {
            var isUpdate = _raw.ContainsKey(host.Address); // second report of a host: now with its open ports
            _raw[host.Address] = host;
            host = _store.Apply(host, _vendorDb);
            if (isUpdate)
            {
                var old = _results.FindIndex(r => r.Address.Equals(host.Address));
                if (old >= 0)
                {
                    var shown = _visible.IndexOf(_results[old]);
                    var wasSelected = Selected is { } s && s.Address.Equals(host.Address);
                    _results[old] = host;
                    if (shown >= 0) _visible[shown] = host;
                    if (wasSelected) { _list.SelectedItem = host; ShowDetails(); }
                    continue;
                }
            }
            _results.Add(host);
            if (Matches(host)) { InsertSorted(host); added = true; }
        }
        if (added || _cts is not null) UpdateStatus();
    }

    private void Rebuild()
    {
        var selected = Selected;
        _visible.Clear();
        foreach (var host in Sorted(_results.Where(Matches))) _visible.Add(host);
        if (selected is not null) _list.SelectedItem = _visible.FirstOrDefault(h => h.Address.Equals(selected.Address));
        UpdateStatus();
    }

    /// <summary>Replaces a host after the user edited its notes.</summary>
    private void Replace(HostResult old, HostResult updated)
    {
        var i = _results.IndexOf(old);
        if (i >= 0) _results[i] = updated;
        Rebuild();
        _list.SelectedItem = _visible.FirstOrDefault(h => h.Address.Equals(updated.Address));
        ShowDetails();
    }

    private bool Matches(HostResult h)
    {
        var statusOk = h.Status switch
        {
            HostStatus.Alive => _showAlive.IsChecked == true,
            HostStatus.Dead => _showDead.IsChecked == true,
            _ => _showUnknown.IsChecked == true,
        };
        if (!statusOk) return false;
        if (_favoritesOnly.IsChecked == true && !h.IsFavorite) return false;
        var q = _search.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return new[] { h.Name, h.Address.ToString(), h.Manufacturer, h.Mac, h.Workgroup, h.Comment, DeviceTypes.Label(h.DeviceType), SearchableServices(h) }
            .Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void InsertSorted(HostResult host)
    {
        var i = 0;
        while (i < _visible.Count && Compare(_visible[i], host) <= 0) i++;
        _visible.Insert(i, host);
    }

    private IEnumerable<HostResult> Sorted(IEnumerable<HostResult> hosts) => hosts.Order(Comparer<HostResult>.Create(Compare));

    private int Compare(HostResult a, HostResult b)
    {
        static int Text(string? x, string? y) => string.Compare(x ?? "￿", y ?? "￿", StringComparison.OrdinalIgnoreCase);
        var c = _sort switch
        {
            SortColumn.Favorite => b.IsFavorite.CompareTo(a.IsFavorite),
            SortColumn.Status => a.Status.CompareTo(b.Status),
            SortColumn.Name => Text(a.Name, b.Name),
            SortColumn.Manufacturer => Text(a.Manufacturer, b.Manufacturer),
            SortColumn.Mac => Text(a.Mac, b.Mac),
            SortColumn.Type => Text(DeviceTypes.Label(a.DeviceType).NullIfEmpty(), DeviceTypes.Label(b.DeviceType).NullIfEmpty()),
            SortColumn.Services => b.Services.Count.CompareTo(a.Services.Count),
            SortColumn.Comment => Text(a.Comment, b.Comment),
            _ => 0,
        };
        if (c == 0) c = a.SortKey.CompareTo(b.SortKey); // IP order breaks ties
        return _descending ? -c : c;
    }

    private void SortBy(SortColumn column)
    {
        _descending = _sort == column && !_descending;
        _sort = column;
        Rebuild();
    }

    // --- actions ---

    public void FillContextMenu(ContextMenu menu, HostResult h)
    {
        menu.Items.Clear();
        foreach (var action in Enum.GetValues<HostAction>().Where(a => HostActions.Applies(a, h)))
            menu.Items.Add(MenuItem(HostActions.Label(action), () => RunAction(action, h)));
        if (h.Shares.Count > 0)
        {
            var shares = new MenuItem { Header = "Open share" };
            foreach (var share in h.Shares) shares.Items.Add(MenuItem($"{share.Name}{(share.Kind == ShareKind.Printer ? " (printer)" : "")}", () => RunAction(HostAction.Explore, h, share.Name)));
            menu.Items.Add(shares);
        }
        else if (h.Services.Any(s => s.Kind == ServiceKind.Smb))
        {
            var list = MenuItem("List shared folders (signs in as you)", async () => await ListSharesAsync(h));
            menu.Items.Add(list);
        }
        menu.Items.Add(MenuItem(h.OpenPorts.Count > 0 ? "Scan ports again" : "Scan ports", async () => await ScanPortsAsync(h)));
        var more = new MenuItem { Header = "All tools" };
        foreach (var action in Enum.GetValues<HostAction>()) more.Items.Add(MenuItem(HostActions.Label(action), () => RunAction(action, h)));
        menu.Items.Add(more);
        var wol = MenuItem("Wake-on-LAN", async () => await WakeAsync(h));
        wol.IsEnabled = h.Mac is not null;
        menu.Items.Add(wol);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Edit name and comment...", async () => await EditAsync(h)));
        menu.Items.Add(MenuItem(h.IsFavorite ? "Remove from favorites" : "Add to favorites", () => ToggleFavorite(h)));
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy" };
        copy.Items.Add(MenuItem("IP address", async () => await CopyAsync(h.Address.ToString())));
        if (h.Name is { } n) copy.Items.Add(MenuItem("Name", async () => await CopyAsync(n)));
        if (h.Mac is { } m) copy.Items.Add(MenuItem("MAC address", async () => await CopyAsync(m)));
        copy.Items.Add(MenuItem("Network path", async () => await CopyAsync(HostActions.UncPath(h.Address))));
        copy.Items.Add(MenuItem("Row", async () => await CopyAsync(Export.ToCsv([h], '\t').Split(Environment.NewLine)[1])));
        menu.Items.Add(copy);
        menu.Items.Add(MenuItem("Export selected...", async () => await ExportAsync("csv", ',', selectedOnly: true)));
    }

    public async void RunAction(HostAction action, HostResult host, string? share = null)
    {
        try
        {
            await Launch(HostActions.Command(action, host.Address, share));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            ShowError($"{HostActions.Label(action)} failed: {ex.Message}");
        }
    }

    /// <summary>Port-scans one device with the Ports field, or the common ports when it's empty.</summary>
    public async Task ScanPortsAsync(HostResult h)
    {
        var parse = PortListParser.Parse(string.IsNullOrWhiteSpace(PortsText) ? PortList.Common : PortsText);
        if (parse.Errors.Count > 0) { ShowError("Ports: " + string.Join("; ", parse.Errors)); return; }
        ShowInfo($"Scanning {parse.Ports.Count:N0} port(s) on {h.Address}...");
        var options = new ScanOptions { Speed = _speed.SelectedIndex == 1 ? ScanSpeed.Accurate : ScanSpeed.Fast };
        try
        {
            long checkedPorts = 0;
            var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            ticker.Tick += (_, _) => ShowInfo($"Scanning ports on {h.Address}: {Interlocked.Read(ref checkedPorts):N0} of {parse.Ports.Count:N0} checked...");
            ticker.Start();
            IReadOnlyList<OpenPort> found;
            try
            {
                // Time one ping so silent ports on a fast device don't each wait the full timeout.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan? rtt = await _probes.PingAsync(h.Address, options.PingTimeout, CancellationToken.None) ? clock.Elapsed : null;
                var timeout = options.PortTimeoutFor(h.Address, rtt);
                found = await Task.Run(() => new PortScanner(_probes).ScanAsync(h.Address, parse.Ports, timeout,
                    CancellationToken.None, () => Interlocked.Increment(ref checkedPorts)));
            }
            finally
            {
                ticker.Stop();
            }
            var raw = Raw(h) with { OpenPorts = found };
            _raw[h.Address] = raw;
            Replace(h, _store.Apply(raw, _vendorDb));
            ShowInfo(found.Count == 0 ? $"No open ports found on {h.Address}." : $"{found.Count} open port(s) on {h.Address}.");
        }
        catch (Exception ex)
        {
            ShowError("Port scan failed: " + ex.Message);
        }
    }

    /// <summary>Lists one device's shares on request (signs in to it as the current Windows user).</summary>
    private async Task ListSharesAsync(HostResult h)
    {
        ShowInfo($"Listing shared folders on {h.Address}...");
        var shares = await _probes.SharesAsync(h.Address, TimeSpan.FromSeconds(10), CancellationToken.None);
        var raw = Raw(h) with { Shares = shares };
        raw = raw with { DeviceType = DeviceTypes.Guess(raw) };
        _raw[h.Address] = raw;
        Replace(h, _store.Apply(raw, _vendorDb));
        ShowInfo(shares.Count == 0 ? $"{h.Address} shares nothing you can see (or refused)." : $"{h.Address} shares {shares.Count} item(s).");
    }

    private async Task WakeAsync(HostResult h)
    {
        if (h.Mac is null) return;
        try
        {
            var adapters = await WakeOnLan.SendAsync(h.Mac);
            if (adapters == 0) ShowError("Wake-on-LAN: no network adapter could send the packet.");
            else ShowInfo($"Wake-on-LAN packet for {h.Mac} sent from {adapters} network adapter(s).");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            ShowError("Wake-on-LAN failed: " + ex.Message);
        }
    }

    public async Task EditAsync(HostResult h)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var note = await new EditHostDialog(Raw(h), _store.Get(Raw(h))).ShowDialog<HostNote?>(owner);
        if (note is null) return;
        SaveNote(h, note);
    }

    public void ToggleFavorite(HostResult h)
    {
        var note = _store.Get(Raw(h)) ?? new HostNote();
        SaveNote(h, note with { Favorite = !note.Favorite });
    }

    private void SaveNote(HostResult h, HostNote note)
    {
        try
        {
            _store.Set(Raw(h), note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save: " + ex.Message);
            return;
        }
        // Re-apply notes to the raw scan result so a cleared name falls back to the scanned one.
        Replace(h, _store.Apply(_raw.GetValueOrDefault(h.Address, h), _vendorDb));
    }

    private async Task ForgetAllAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirm = new Window
        {
            Title = "Delete saved data", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = "Delete" };
        var no = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        yes.Click += (_, _) => confirm.Close(true);
        no.Click += (_, _) => confirm.Close(false);
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Delete the names, comments, MAC addresses and favorites you saved for {_store.All.Count} device(s)?\nThis can't be undone.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        if (!await confirm.ShowDialog<bool>(owner)) return;
        _store.Clear();
        for (var i = 0; i < _results.Count; i++) _results[i] = _raw.GetValueOrDefault(_results[i].Address, _results[i]);
        Rebuild();
        ShowInfo("Saved device data deleted.");
    }

    private async Task ExportAsync(string format, char delimiter, bool selectedOnly = false)
    {
        var hosts = selectedOnly ? _list.SelectedItems?.OfType<HostResult>().ToList() ?? [] : _visible.ToList();
        if (hosts.Count == 0) { ShowError("Nothing to export."); return; }
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export scan results",
            SuggestedFileName = $"Scan_results_{DateTime.Now:yyyy-MM-dd_HHmm}.{format}",
            DefaultExtension = format,
            FileTypeChoices = [new FilePickerFileType(format.ToUpperInvariant()) { Patterns = [$"*.{format}"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            var text = format == "html" ? Export.ToHtml(hosts, $"Logictail scan {DateTime.Now:yyyy-MM-dd HH:mm}") : Export.ToCsv(hosts, delimiter);
            // UTF-8 with BOM so Excel shows accented names correctly.
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(format == "csv"));
            ShowInfo($"Exported {hosts.Count} device(s) to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not write {path}: {ex.Message}");
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // --- layout ---

    private Grid Header()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths), Margin = new Thickness(12, 0, 8, 0) };
        (string Text, SortColumn Column)[] columns =
        [
            ("★", SortColumn.Favorite), ("", SortColumn.Status), ("Name", SortColumn.Name), ("IP", SortColumn.Address),
            ("Manufacturer", SortColumn.Manufacturer), ("MAC address", SortColumn.Mac), ("Type", SortColumn.Type),
            ("Services", SortColumn.Services), ("Comments", SortColumn.Comment),
        ];
        for (var i = 0; i < columns.Length; i++)
        {
            if (_compact && WideOnly.Contains(i)) continue;
            var (text, column) = columns[i];
            var button = new Button
            {
                Content = text, Classes = { "quiet" }, Padding = new Thickness(0, 8), FontSize = 12, Foreground = Palette.Slate,
                FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => SortBy(column);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }
        return grid;
    }

    private Grid Row(HostResult h)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths) };
        var (label, brush) = h.Status switch
        {
            HostStatus.Alive => ("Alive", AliveBrush),
            HostStatus.Dead => ("Dead", DeadBrush),
            _ => ("Unknown", UnknownBrush),
        };
        string?[] cells =
        [
            h.IsFavorite ? "★" : "", label, h.Name ?? h.NetBiosName, h.Address.ToString(), h.Manufacturer, h.Mac,
            DeviceTypes.Label(h.DeviceType), ServicesText(h), h.Comment,
        ];
        for (var i = 0; i < cells.Length; i++)
        {
            if (_compact && WideOnly.Contains(i)) continue;
            var block = new TextBlock { Text = cells[i] ?? "", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (i == 0) block.Foreground = StarBrush;
            else if (h.Status == HostStatus.Dead) block.Foreground = DeadBrush;
            if (i == 1)
            {
                block.Text = ""; // the dot says it; the word is in the tooltip
                ToolTip.SetTip(block, label);
                var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = brush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dot, 1);
                grid.Children.Add(dot);
            }
            Grid.SetColumn(block, i);
            grid.Children.Add(block);
        }
        return grid;
    }

    private void ShowDetails()
    {
        _details.Children.Clear();
        if (Selected is not { } h)
        {
            _details.Children.Add(new TextBlock { Text = "Select a device to see its details.\nRight-click for tools; double-click to add a name or comment.", Foreground = Palette.Slate, TextWrapping = TextWrapping.Wrap });
            return;
        }

        _details.Children.Add(new SelectableTextBlock { Text = h.Name ?? h.Address.ToString(), FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        void Field(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _details.Children.Add(new TextBlock { Text = label, Foreground = Palette.Slate, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
            _details.Children.Add(new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
        }
        Field("Status", h.Status + (h.Error is { } err ? $" ({err})" : ""));
        Field("IP address", h.Address.ToString());
        Field("MAC address", h.Mac);
        Field("Manufacturer", h.Manufacturer);
        Field("Type (guess)", DeviceTypes.Label(h.DeviceType));
        Field("NetBIOS name", h.NetBiosName);
        Field("Workgroup", h.Workgroup);
        Field("User", h.User);
        Field("Services", string.Join("\n", h.Services.Select(s => $"{Services.Label(s.Kind)} (port {Services.Port(s.Kind)}){(s.Detail is null ? "" : ": " + s.Detail)}")));
        Field("Open ports", string.Join("\n", h.OpenPorts.Select(p =>
            $"{p.Label}{(p.Service is null ? "" : " " + p.Service)}{(p.State == PortState.OpenOrFiltered ? " (open or filtered)" : "")}{(p.Detail is null ? "" : ": " + p.Detail)}")));
        Field("Shared", string.Join("\n", h.Shares.Select(s => $"{s.Name}{(s.Kind == ShareKind.Printer ? " (printer)" : "")}{(s.Remark is null ? "" : " - " + s.Remark)}")));
        Field("Comments", h.Comment);
        Field("Scanned", h.ScannedAt.ToLocalTime().ToString("g"));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var action in Enum.GetValues<HostAction>().Where(a => HostActions.Applies(a, h) && a is not (HostAction.Ping or HostAction.Tracert)))
            buttons.Children.Add(SmallButton(HostActions.Label(action), () => RunAction(action, h)));
        buttons.Children.Add(SmallButton("Edit...", () => _ = EditAsync(h)));
        buttons.Children.Add(SmallButton(h.IsFavorite ? "★ Favorite" : "☆ Add to favorites", () => ToggleFavorite(h)));
        _details.Children.Add(buttons);
    }

    private static Button SmallButton(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3), FontSize = 12 };
        b.Click += (_, _) => click();
        return b;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Table text: open port numbers when a port scan ran (answered ports only), else the service names.</summary>
    private static string ServicesText(HostResult h) => h.OpenPorts.Any(p => p.Confirmed)
        ? string.Join(", ", h.OpenPorts.Where(p => p.Confirmed).Select(p => p.Protocol == PortProtocol.Tcp ? p.Port.ToString() : p.Label))
        : string.Join(", ", h.Services.Select(s => Services.Label(s.Kind)));

    /// <summary>Everything search should find: service names, port numbers and what runs on them.</summary>
    private static string SearchableServices(HostResult h) =>
        string.Join(" ", h.Services.Select(s => Services.Label(s.Kind))
            .Concat(h.OpenPorts.Select(p => $"{p.Label} {p.Service} {p.Detail}")));

    private void UpdateStatus(string? state = null)
    {
        _empty.IsVisible = _visible.Count == 0;
        _empty.Text = _results.Count > 0 ? "No items match your filters." : "No results yet. Enter a range and press Scan.";

        var p = _lastProgress;
        _progress.Value = p?.Percent ?? 0;
        _status.Foreground = Palette.Slate;
        state ??= _cts is null ? _idleState : _gate?.IsPaused == true ? "Paused" : "Scanning...";
        _status.Text = p is null
            ? state
            : $"{state}  {p.Percent:0}%  |  {p.Alive} alive, {p.Dead} dead, {p.Unknown} unknown  |  {p.Done:N0} of {p.Total:N0} addresses" +
              (p.PortsChecked > 0 ? $"  |  {p.PortsChecked:N0} ports checked" : "") + $"  |  showing {_visible.Count:N0}";
    }

    private void ShowInfo(string message)
    {
        _status.Text = message;
        _status.Foreground = Palette.Slate;
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        _status.Foreground = Palette.Red;
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
