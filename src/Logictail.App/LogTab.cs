using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Logictail.App.Controls;
using Logictail.Core.Highlighting;
using Logictail.Core.Settings;
using Logictail.Core.Tail;

namespace Logictail.App;

/// <summary>One open file: its tab, its view and its scrollbars.</summary>
public sealed class LogTab
{
    // Index at most this much per step so a multi-GB file shows its first lines right away;
    // steps repeat back to back until the file is fully indexed, independent of the update interval.
    private readonly long _bytesPerStep;

    private readonly TextBlock _header = new();
    private readonly TextBlock _sideName = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sideBadge = new() { FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Border _sideBar = new() { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 10, 4) };
    private int _seenLines;
    private readonly ScrollBar _vScroll = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hScroll = new() { Orientation = Orientation.Horizontal };
    private bool _refreshing;
    private bool _syncing;

    public LogTab(string path, HighlightRuleSet rules, AppSettings settings, long bytesPerStep = 64L << 20)
    {
        _bytesPerStep = bytesPerStep;
        File = new LogFile(path);
        View = new LogView { File = File, Rules = rules, Margin = new Thickness(0, 6, 0, 0) };
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
        // Sidebar entry: bar | name | new-line count. Indented under "Logs".
        var side = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(27, 0, 4, 0), Tag = this, Height = 26 };
        side.Children.Add(_sideBar);
        Grid.SetColumn(_sideName, 1);
        side.Children.Add(_sideName);
        Grid.SetColumn(_sideBadge, 2);
        side.Children.Add(_sideBadge);
        SidebarItem = side;
        View.ViewChanged += (_, _) => SyncScrollBars();
        _vScroll.Scroll += (_, e) => { if (!_syncing) View.ScrollTo((int)e.NewValue, fromUser: true); };
        _hScroll.Scroll += (_, e) => { if (!_syncing) View.ScrollHorizontally(e.NewValue); };
        UpdateHeader();
    }

    public LogFile File { get; }
    public LogView View { get; }
    public TabItem Tab { get; }
    /// <summary>The file's entry in the sidebar list.</summary>
    public Control SidebarItem { get; }
    public FileStatus Status { get; private set; } = FileStatus.Unknown;

    private bool _isCurrent;

    /// <summary>The file shown in the log view: its sidebar entry is teal and bold.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; UpdateHeader(); }
    }

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
        _seenLines = File.LineCount;
        UpdateHeader();
    }

    /// <summary>Lines added since the user last looked at this file.</summary>
    public int NewLines => Status == FileStatus.Changed ? Math.Max(0, File.LineCount - _seenLines) : 0;

    private void UpdateHeader()
    {
        _header.Text = Status == FileStatus.Error ? $"{File.Name} (error)" : $"{File.Name} ({FormatSize(File.Length)})";
        switch (Status)
        {
            case FileStatus.Changed: _header.Foreground = Brushes.DarkOrange; break;
            case FileStatus.Error: _header.Foreground = Brushes.Red; break;
            default: _header.ClearValue(TextBlock.ForegroundProperty); break; // keep the theme's color
        }
        if (Status == FileStatus.Changed) _header.FontWeight = FontWeight.Bold;
        else _header.ClearValue(TextBlock.FontWeightProperty); // let the selected-tab style make it bold

        if (Status != FileStatus.Changed && Status != FileStatus.Error) _seenLines = File.LineCount;
        _sideName.Text = File.Name;
        // Only set what the state needs; otherwise leave it to the styles (selected file = teal bold).
        _sideName.FontWeight = Status == FileStatus.Changed || IsCurrent ? FontWeight.Bold : FontWeight.Normal;
        _sideName.Foreground = Status == FileStatus.Error ? Palette.Red : IsCurrent ? Palette.Teal : Palette.Charcoal;
        _sideBar.Background = Status switch
        {
            FileStatus.Changed => Palette.Teal,
            FileStatus.Error => Palette.Red,
            _ => Brushes.Transparent,
        };
        _sideBadge.Foreground = Status == FileStatus.Error ? Palette.Red : Palette.Teal;
        _sideBadge.Text = Status switch
        {
            FileStatus.Error => "!",
            FileStatus.Changed when NewLines > 0 => NewLines > 9999 ? "9999+" : $"+{NewLines:N0}",
            _ => "",
        };
        ToolTip.SetTip(SidebarItem, File.Error is { } e ? $"{File.Path}\n{e}" : $"{File.Path}\n{FormatSize(File.Length)}");
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
