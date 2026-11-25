using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Logitail.App.Controls;
using Logitail.Core.Highlighting;
using Logitail.Core.Tail;

namespace Logitail.App;

/// <summary>One open file: its tab, its view and its scrollbar.</summary>
public sealed class LogTab
{
    // Index at most this much per tick so a multi-GB file shows its first lines right away.
    private const long BytesPerTick = 64L << 20;

    private readonly TextBlock _header = new();
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical };
    private bool _refreshing;
    private bool _syncingScrollBar;

    public LogTab(string path, HighlightRuleSet rules)
    {
        File = new LogFile(path);
        View = new LogView { File = File, Rules = rules };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(View);
        Grid.SetColumn(_scrollBar, 1);
        grid.Children.Add(_scrollBar);

        Tab = new TabItem { Header = _header, Content = grid, Tag = this };
        View.ViewChanged += (_, _) => SyncScrollBar();
        _scrollBar.Scroll += (_, e) =>
        {
            if (!_syncingScrollBar) View.ScrollTo((int)e.NewValue, fromUser: true);
        };
        UpdateHeader();
    }

    public LogFile File { get; }
    public LogView View { get; }
    public TabItem Tab { get; }
    public FileStatus Status { get; private set; } = FileStatus.Unknown;

    /// <summary>Raised on the UI thread after a refresh changed something.</summary>
    public event EventHandler? Updated;

    /// <summary>Refreshes on a worker thread; skips if the previous refresh is still running.</summary>
    public async Task RefreshAsync(bool isSelected)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var result = await Task.Run(() => File.Refresh(BytesPerTick));
            Status = result switch
            {
                RefreshResult.Error => FileStatus.Error,
                RefreshResult.NoChange when Status != FileStatus.Changed => FileStatus.Unchanged,
                RefreshResult.Appended or RefreshResult.Reset when !isSelected => FileStatus.Changed,
                RefreshResult.Appended or RefreshResult.Reset => FileStatus.Unchanged,
                _ => Status,
            };
            if (result is RefreshResult.Appended or RefreshResult.Reset) View.OnFileChanged(result == RefreshResult.Reset);
            UpdateHeader();
            Updated?.Invoke(this, EventArgs.Empty);
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

    private void SyncScrollBar()
    {
        _syncingScrollBar = true;
        _scrollBar.Maximum = View.MaxTopLine;
        _scrollBar.ViewportSize = View.VisibleLines;
        _scrollBar.SmallChange = 1;
        _scrollBar.LargeChange = View.VisibleLines;
        _scrollBar.Value = View.TopLine;
        _syncingScrollBar = false;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
