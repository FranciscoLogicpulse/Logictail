using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Logitail.Scanner;

namespace Logitail.App.Scanner;

/// <summary>The Network tab: range input, scan controls, filters and the results table.</summary>
public sealed class ScannerView : UserControl
{
    private enum SortColumn { Status, Name, Address, Manufacturer, Mac, Services }

    // Status, Name, IP, Manufacturer, MAC, Services
    private static readonly string ColumnWidths = "90,220,120,230,150,*";
    private static readonly IBrush AliveBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly IBrush DeadBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x6C, 0x00));

    private readonly IProbes _probes;
    private readonly Task<OuiDatabase> _vendors;
    private readonly TextBox _range = new() { PlaceholderText = "Example: 192.168.0.1-100, 192.168.0.200", MinWidth = 320 };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "Fast", "Accurate (slower)" }, SelectedIndex = 0, Width = 160 };
    private readonly Button _scanButton = new() { Content = "Scan", MinWidth = 90 }; // not IsDefault: Enter in the search box must not start a scan
    private readonly Button _pauseButton = new() { Content = "Pause", MinWidth = 90, IsEnabled = false };
    private readonly CheckBox _showAlive = new() { Content = "Show _alive", IsChecked = true };
    private readonly CheckBox _showDead = new() { Content = "Show _dead", IsChecked = false };
    private readonly CheckBox _showUnknown = new() { Content = "Show _unknown", IsChecked = true };
    private readonly TextBox _search = new() { PlaceholderText = "Search results", Width = 240 };
    private readonly ListBox _list = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 220, Height = 6 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new()
    {
        Text = "No results yet. Enter a range and press Scan.", Foreground = Brushes.Gray,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
    };

    private readonly List<HostResult> _results = [];
    private readonly ObservableCollection<HostResult> _visible = [];
    private readonly ConcurrentQueue<HostResult> _incoming = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ScanProgress? _lastProgress;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;
    private SortColumn _sort = SortColumn.Address;
    private bool _descending;

    public ScannerView() : this(new SystemProbes(), Path.Combine(AppContext.BaseDirectory, "data", "oui.csv")) { }

    public ScannerView(IProbes probes, string vendorFile)
    {
        _probes = probes;
        _vendors = Task.Run(() => OuiDatabase.Load(vendorFile));
        _range.Text = LocalNetwork.Subnets().FirstOrDefault()?.ToRangeText() ?? "";

        var mySubnet = new Button { Content = "My subnet" };
        ToolTip.SetTip(mySubnet, "Fill in this PC's own subnet");
        mySubnet.Click += (_, _) => _range.Text = LocalNetwork.Subnets().FirstOrDefault()?.ToRangeText() ?? _range.Text;
        var fromFile = new Button { Content = "From file..." };
        ToolTip.SetTip(fromFile, "Load addresses from a text file: one IP, range or host name per line");
        fromFile.Click += async (_, _) => await LoadTargetsFileAsync();
        _scanButton.Click += async (_, _) => await ToggleScanAsync();
        _pauseButton.Click += (_, _) => TogglePause();

        foreach (var box in new[] { _showAlive, _showDead, _showUnknown }) box.IsCheckedChanged += (_, _) => Rebuild();
        _search.TextChanged += (_, _) => Rebuild();
        _flush.Tick += (_, _) => Flush();

        _list.ItemsSource = _visible;
        _list.ItemTemplate = new FuncDataTemplate<HostResult?>((host, _) => host is null ? new Control() : Row(host));
        _list.SelectionMode = SelectionMode.Multiple;

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8),
            Children = { new TextBlock { Text = "Range:", VerticalAlignment = VerticalAlignment.Center }, _range, mySubnet, fromFile, _speed, _scanButton, _pauseButton },
        };
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(8, 0, 8, 6),
            Children = { _showAlive, _showDead, _showUnknown, _search },
        };
        var statusBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(8, 4),
            Children = { _progress, _status },
        };

        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(Header());
        var body = new Panel { Children = { _list, _empty } };
        Grid.SetRow(body, 1);
        table.Children.Add(body);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(filters);
        var statusBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar };
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);
        root.Children.Add(table);
        Content = root;
        UpdateStatus();
    }

    /// <summary>All results of the current scan, unfiltered.</summary>
    public IReadOnlyList<HostResult> Results => _results;

    /// <summary>Results as shown (filtered and sorted).</summary>
    public IReadOnlyList<HostResult> VisibleResults => _visible;

    public bool IsScanning => _cts is not null;

    public string RangeText { get => _range.Text ?? ""; set => _range.Text = value; }

    // --- scanning ---

    public async Task ToggleScanAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }

        var parsed = IpRangeParser.Parse(_range.Text ?? "");
        if (parsed.Errors.Count > 0 || parsed.Targets.Count == 0)
        {
            ShowError(parsed.Errors.Count > 0 ? string.Join("; ", parsed.Errors) : "Enter an IP address, range or host name to scan.");
            return;
        }

        _results.Clear();
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

        var options = new ScanOptions { Speed = _speed.SelectedIndex == 1 ? ScanSpeed.Accurate : ScanSpeed.Fast };
        var stopped = false;
        string? failure = null;
        try
        {
            var engine = new ScanEngine(_probes, await _vendors);
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
            UpdateStatus(stopped ? "Stopped" : "Finished");
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
            _range.Text = string.Join(", ", text.Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0));
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
            _results.Add(host);
            if (Matches(host)) { InsertSorted(host); added = true; }
        }
        if (added || _cts is not null) UpdateStatus();
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var host in Sorted(_results.Where(Matches))) _visible.Add(host);
        UpdateStatus();
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
        var q = _search.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return new[] { h.Name, h.Address.ToString(), h.Manufacturer, h.Mac, h.Workgroup, ServicesText(h) }
            .Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void InsertSorted(HostResult host)
    {
        var comparer = Comparer<HostResult>.Create(Compare);
        var i = 0;
        while (i < _visible.Count && comparer.Compare(_visible[i], host) <= 0) i++;
        _visible.Insert(i, host);
    }

    private IEnumerable<HostResult> Sorted(IEnumerable<HostResult> hosts) => hosts.Order(Comparer<HostResult>.Create(Compare));

    private int Compare(HostResult a, HostResult b)
    {
        var c = _sort switch
        {
            SortColumn.Status => a.Status.CompareTo(b.Status),
            SortColumn.Name => string.Compare(a.Name ?? "￿", b.Name ?? "￿", StringComparison.OrdinalIgnoreCase),
            SortColumn.Manufacturer => string.Compare(a.Manufacturer ?? "￿", b.Manufacturer ?? "￿", StringComparison.OrdinalIgnoreCase),
            SortColumn.Mac => string.Compare(a.Mac ?? "￿", b.Mac ?? "￿", StringComparison.OrdinalIgnoreCase),
            SortColumn.Services => b.Services.Count.CompareTo(a.Services.Count),
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

    // --- layout ---

    private Grid Header()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths), Margin = new Thickness(12, 0, 8, 0) };
        (string Text, SortColumn Column)[] columns =
        [
            ("Status", SortColumn.Status), ("Name", SortColumn.Name), ("IP", SortColumn.Address),
            ("Manufacturer", SortColumn.Manufacturer), ("MAC address", SortColumn.Mac), ("Services", SortColumn.Services),
        ];
        for (var i = 0; i < columns.Length; i++)
        {
            var (text, column) = columns[i];
            var button = new Button
            {
                Content = text, Background = Brushes.Transparent, Padding = new Thickness(0, 4),
                FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => SortBy(column);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }
        return grid;
    }

    private static Grid Row(HostResult h)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths) };
        var (label, brush) = h.Status switch
        {
            HostStatus.Alive => ("● Alive", AliveBrush),
            HostStatus.Dead => ("○ Dead", DeadBrush),
            _ => ("? Unknown", UnknownBrush),
        };
        string?[] cells = [label, h.Name ?? h.NetBiosName, h.Address.ToString(), h.Manufacturer, h.Mac, ServicesText(h)];
        for (var i = 0; i < cells.Length; i++)
        {
            var block = new TextBlock { Text = cells[i] ?? "", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            if (i == 0) block.Foreground = brush;
            if (h.Status == HostStatus.Dead && i > 0) block.Foreground = DeadBrush;
            Grid.SetColumn(block, i);
            grid.Children.Add(block);
        }
        var tip = string.Join("\n", new[]
        {
            h.Name, h.NetBiosName is { } nb && nb != h.Name ? $"NetBIOS: {nb}" : null,
            h.Workgroup is { } wg ? $"Workgroup: {wg}" : null,
            h.User is { } u ? $"User: {u}" : null,
            h.Error is { } err ? $"Probe failed: {err}" : null,
        }.Concat(h.Services.Select(s => $"{Services.Label(s.Kind)}: {s.Detail ?? "open"}")).Where(s => !string.IsNullOrEmpty(s)));
        if (tip.Length > 0) ToolTip.SetTip(grid, tip);
        return grid;
    }

    private static string ServicesText(HostResult h) => string.Join(", ", h.Services.Select(s => Services.Label(s.Kind)));

    private void UpdateStatus(string? state = null)
    {
        _empty.IsVisible = _visible.Count == 0;
        if (_results.Count > 0 && _visible.Count == 0) _empty.Text = "No items match your filters.";
        else _empty.Text = "No results yet. Enter a range and press Scan.";

        var p = _lastProgress;
        _progress.Value = p?.Percent ?? 0;
        _status.Foreground = Brushes.Black;
        state ??= _cts is null ? "Ready" : _gate?.IsPaused == true ? "Paused" : "Scanning...";
        _status.Text = p is null
            ? state
            : $"{state}  {p.Percent:0}%  |  {p.Alive} alive, {p.Dead} dead, {p.Unknown} unknown  |  {p.Done:N0} of {p.Total:N0} addresses  |  showing {_visible.Count:N0}";
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        _status.Foreground = Brushes.Firebrick;
    }
}
