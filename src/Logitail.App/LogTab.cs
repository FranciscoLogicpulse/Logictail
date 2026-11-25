using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Logitail.App.Controls;
using Logitail.Core.Highlighting;
using Logitail.Core.Settings;
using Logitail.Core.Tail;

namespace Logitail.App;

/// <summary>One open file: its tab, its view and its scrollbars.</summary>
public sealed class LogTab
{
    // Index at most this much per step so a multi-GB file shows its first lines right away;
    // steps repeat back to back until the file is fully indexed, independent of the update interval.
    private readonly long _bytesPerStep;

    private readonly TextBlock _header = new();
    private readonly ScrollBar _vScroll = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hScroll = new() { Orientation = Orientation.Horizontal };
    private bool _refreshing;
    private bool _syncing;

    public LogTab(string path, HighlightRuleSet rules, AppSettings settings, long bytesPerStep = 64L << 20)
    {
        _bytesPerStep = bytesPerStep;
        File = new LogFile(path);
        View = new LogView { File = File, Rules = rules };
        View.ApplySettings(settings);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        grid.Children.Add(View);
        Grid.SetColumn(_vScroll, 1);
        grid.Children.Add(_vScroll);
        Grid.SetRow(_hScroll, 1);
        grid.Children.Add(_hScroll);

        Tab = new TabItem { Header = _header, Content = grid, Tag = this };
        View.ViewChanged += (_, _) => SyncScrollBars();
        _vScroll.Scroll += (_, e) => { if (!_syncing) View.ScrollTo((int)e.NewValue, fromUser: true); };
        _hScroll.Scroll += (_, e) => { if (!_syncing) View.ScrollHorizontally(e.NewValue); };
        UpdateHeader();
    }

    public LogFile File { get; }
    public LogView View { get; }
    public TabItem Tab { get; }
    public FileStatus Status { get; private set; } = FileStatus.Unknown;

    /// <summary>Set when the tab is closed so a running index loop stops.</summary>
    public bool IsClosed { get; set; }

    /// <summary>Raised on the UI thread after a refresh changed something.</summary>
    public event EventHandler? Updated;

    public void ApplySettings(AppSettings settings, HighlightRuleSet rules)
    {
        View.Rules = rules;
        View.ApplySettings(settings);
    }

    /// <summary>Refreshes on a worker thread; skips if the previous refresh is still running.</summary>
    public async Task RefreshAsync(bool isSelected)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            RefreshResult result;
            do
            {
                result = await Task.Run(() => File.Refresh(_bytesPerStep));
                Status = result switch
                {
                    RefreshResult.Error => FileStatus.Error,
                    RefreshResult.NoChange when Status is FileStatus.Unknown or FileStatus.Error => FileStatus.Unchanged,
                    RefreshResult.Appended or RefreshResult.Reset when !isSelected => FileStatus.Changed,
                    RefreshResult.Appended or RefreshResult.Reset => FileStatus.Unchanged,
                    _ => Status,
                };
                if (result is RefreshResult.Appended or RefreshResult.Reset) View.OnFileChanged(result == RefreshResult.Reset);
                UpdateHeader();
                Updated?.Invoke(this, EventArgs.Empty); // lets the UI show progress between steps
            }
            while (result is RefreshResult.Appended or RefreshResult.Reset && !File.IsFullyIndexed && !IsClosed);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void MarkSeen()
    {
        if (Status == FileStatus.Changed) Status = FileStatus.Unchanged;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        _header.Text = Status == FileStatus.Error ? $"{File.Name} (error)" : $"{File.Name} ({FormatSize(File.Length)})";
        switch (Status)
        {
            case FileStatus.Changed: _header.Foreground = Brushes.DarkOrange; break;
            case FileStatus.Error: _header.Foreground = Brushes.Red; break;
            default: _header.ClearValue(TextBlock.ForegroundProperty); break; // keep the theme's color
        }
        _header.FontWeight = Status == FileStatus.Changed ? FontWeight.Bold : FontWeight.Normal;
        ToolTip.SetTip(Tab, File.Error is { } err ? $"{File.Path}\n{err}" : File.Path);
    }

    private void SyncScrollBars()
    {
        _syncing = true;
        _vScroll.Maximum = View.MaxTopLine;
        _vScroll.ViewportSize = View.VisibleRows;
        _vScroll.SmallChange = 1;
        _vScroll.LargeChange = View.VisibleRows;
        _vScroll.Value = View.TopLine;

        var hMax = Math.Max(0, View.ContentWidth - View.ViewportWidth);
        _hScroll.IsVisible = !View.IsWrapping && hMax > 0;
        _hScroll.Maximum = hMax;
        _hScroll.ViewportSize = View.ViewportWidth;
        _hScroll.SmallChange = 40;
        _hScroll.LargeChange = View.ViewportWidth;
        _hScroll.Value = View.HorizontalOffset;
        _syncing = false;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
