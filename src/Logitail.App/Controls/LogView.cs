using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Logitail.Core.Highlighting;
using Logitail.Core.Settings;
using Logitail.Core.Tail;

namespace Logitail.App.Controls;

/// <summary>
/// Draws only the visible lines of a <see cref="LogFile"/>, so file size doesn't matter.
/// Scrolls by whole lines; wrapped lines take several rows. Selection is by whole lines.
/// </summary>
public sealed class LogView : Control
{
    private const double LeftPad = 4;
    private const int MaxCopyLines = 200_000;
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC));
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    private AppSettings _settings = new();
    private Typeface _typeface;
    private double _fontSize;
    private double _rowHeight = 17;
    private double _charWidth = 8;
    private int _topLine;
    private bool _followTail = true;
    private double _wheelRemainder;
    private double _hOffset;
    private double _contentWidth;
    private int? _selAnchor;
    private int? _selActive;
    private bool _dragging;
    private readonly List<(int Line, double Top, double Bottom)> _rowMap = [];

    public LogView()
    {
        Focusable = true;
        ClipToBounds = true;
        ApplySettings(_settings);
    }

    public LogFile? File { get; set; }

    public HighlightRuleSet Rules { get; set; } = HighlightRuleSet.Empty;

    /// <summary>Raised when scroll position, follow state or selection changed.</summary>
    public event EventHandler? ViewChanged;

    public int TopLine => _topLine;

    /// <summary>Rows that fit in the view (one row per line when not wrapping).</summary>
    public int VisibleRows => Math.Max(1, (int)(Bounds.Height / _rowHeight));

    public int MaxTopLine { get; private set; }

    public bool IsWrapping => _settings.WrapColumn != AppSettings.WrapOff;

    public double HorizontalOffset => _hOffset;
    public double ContentWidth => _contentWidth;
    public double ViewportWidth => Math.Max(0, Bounds.Width - TextLeft);

    public (int First, int Last)? Selection =>
        _selAnchor is { } a && _selActive is { } b ? (Math.Min(a, b), Math.Max(a, b)) : null;

    public bool FollowTail
    {
        get => _followTail;
        set
        {
            if (_followTail == value) return;
            _followTail = value;
            if (value) { UpdateMaxTop(); _topLine = MaxTopLine; InvalidateVisual(); }
            Changed();
        }
    }

    private double GutterWidth => _settings.ShowLineMarkers && IsWrapping ? 10 : 0;
    private double TextLeft => GutterWidth + LeftPad;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _typeface = new Typeface(new FontFamily(settings.FontFamily));
        _fontSize = settings.FontSize;
        var probe = Format("MMMMMMMMMM", Brushes.Black, FontWeight.Normal, FontStyle.Normal);
        _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        _rowHeight = Math.Ceiling(probe.Height + settings.LineSpacing);
        _contentWidth = 0;
        _hOffset = 0;
        OnFileChanged(reset: false);
    }

    /// <summary>Call after the file was refreshed.</summary>
    public void OnFileChanged(bool reset)
    {
        if (reset) { _topLine = 0; _contentWidth = 0; _hOffset = 0; ClearSelection(); }
        UpdateMaxTop();
        if (_followTail) _topLine = MaxTopLine;
        _topLine = Math.Clamp(_topLine, 0, Math.Max(0, (File?.LineCount ?? 1) - 1));
        InvalidateVisual();
        Changed();
    }

    public void ScrollTo(int line, bool fromUser = false)
    {
        var target = Math.Clamp(line, 0, MaxTopLine);
        if (fromUser)
        {
            var up = target < _topLine;
            if (up && _followTail && _settings.FollowDeactivate is FollowDeactivate.OnScroll or FollowDeactivate.OnScrollOrSelection)
                _followTail = false;
            else if (target >= MaxTopLine && !_followTail && _settings.FollowActivate == FollowActivate.OnScrollToEnd)
                _followTail = true;
        }
        _topLine = target;
        InvalidateVisual();
        Changed();
    }

    public void ScrollHorizontally(double offset)
    {
        if (IsWrapping) return;
        _hOffset = Math.Clamp(offset, 0, Math.Max(0, _contentWidth - ViewportWidth + _charWidth * 4));
        InvalidateVisual();
        Changed();
    }

    public void ClearSelection()
    {
        _selAnchor = _selActive = null;
    }

    /// <summary>Text of the selected lines, or null when nothing is selected.</summary>
    public string? SelectedText()
    {
        if (File is null || Selection is not var (first, last)) return null;
        var count = Math.Min(last - first + 1, MaxCopyLines);
        return string.Join(Environment.NewLine, File.ReadLines(first, count));
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brushes.White, new Rect(size));
        _rowMap.Clear();
        if (File is null) return;

        var columns = Columns();
        var rows = VisibleRows + 1;
        var lines = File.ReadLines(_topLine, rows);
        var sel = Selection;
        var y = 0.0;
        double widest = 0;

        for (var i = 0; i < lines.Count && y < size.Height; i++)
        {
            var lineIndex = _topLine + i;
            var raw = lines[i];
            var text = TextLayout.ExpandTabs(raw, _settings.TabWidth);
            var rule = Rules.Match(raw);
            var selected = sel is var (a, b) && lineIndex >= a && lineIndex <= b;
            var rowCount = TextLayout.RowCount(text.Length, columns);
            var height = rowCount * _rowHeight;

            IBrush fg = Brushes.Black;
            var weight = FontWeight.Normal;
            var style = FontStyle.Normal;
            if (selected)
            {
                context.FillRectangle(SelectionBrush, new Rect(0, y, size.Width, height));
                fg = Brushes.White;
            }
            else if (rule is not null)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromUInt32(rule.Background)), new Rect(0, y, size.Width, height));
                fg = new SolidColorBrush(Color.FromUInt32(rule.Foreground));
            }
            if (rule is not null)
            {
                if (rule.Bold) weight = FontWeight.Bold;
                if (rule.Italic) style = FontStyle.Italic;
            }

            var rowY = y;
            var first = true;
            foreach (var segment in TextLayout.Wrap(text, columns))
            {
                if (rowY >= size.Height) break;
                if (!first && GutterWidth > 0)
                    context.DrawLine(new Pen(MarkerBrush, 2), new Point(4, rowY + 2), new Point(4, rowY + _rowHeight - 2));
                using (context.PushClip(new Rect(TextLeft - LeftPad, rowY, size.Width, _rowHeight)))
                    context.DrawText(Format(segment, fg, weight, style), new Point(TextLeft - _hOffset, rowY + _settings.LineSpacing / 2));
                widest = Math.Max(widest, segment.Length * _charWidth);
                rowY += _rowHeight;
                first = false;
            }

            _rowMap.Add((lineIndex, y, y + height));
            y += height;
        }

        // Widest line seen since open/reset/settings change: shrinking it as short lines scroll into view
        // would strand the horizontal offset and make the scrollbar flicker in and out.
        if (widest > _contentWidth + 0.5 && !IsWrapping)
        {
            _contentWidth = widest;
            Avalonia.Threading.Dispatcher.UIThread.Post(Changed);
        }
    }

    // --- input ---

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var horizontal = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : -e.Delta.X;
        if (horizontal != 0 && !IsWrapping)
        {
            ScrollHorizontally(_hOffset - horizontal * _charWidth * 8);
        }
        else
        {
            // Keep the fraction so slow touchpad scrolling still moves.
            _wheelRemainder += e.Delta.Y * 3;
            var lines = (int)Math.Truncate(_wheelRemainder);
            _wheelRemainder -= lines;
            if (lines != 0) ScrollTo(_topLine - lines, fromUser: true);
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed && LineAt(point.Position.Y) is { } line)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selAnchor is not null) _selActive = line;
            else _selAnchor = _selActive = line;
            _dragging = true;
            e.Pointer.Capture(this);
            OnSelectionChanged();
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging)
        {
            var y = e.GetPosition(this).Y;
            if (y < 0) ScrollTo(_topLine - 1);
            else if (y > Bounds.Height) ScrollTo(_topLine + 1);
            if (LineAt(Math.Clamp(y, 0, Bounds.Height - 1)) is { } line && line != _selActive)
            {
                _selActive = line;
                OnSelectionChanged();
            }
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape && Selection is not null)
        {
            ClearSelection();
            InvalidateVisual();
            Changed();
            e.Handled = true;
            return;
        }
        if (!IsWrapping && e.Key is Key.Left or Key.Right)
        {
            ScrollHorizontally(_hOffset + (e.Key == Key.Left ? -1 : 1) * _charWidth * 8);
            e.Handled = true;
            return;
        }
        int? target = e.Key switch
        {
            Key.Up => _topLine - 1,
            Key.Down => _topLine + 1,
            Key.PageUp => _topLine - VisibleRows,
            Key.PageDown => _topLine + VisibleRows,
            Key.Home when ctrl => 0,
            Key.End when ctrl => MaxTopLine,
            _ => null,
        };
        if (target is { } t)
        {
            ScrollTo(t, fromUser: true);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        OnFileChanged(reset: false);
    }

    // --- helpers ---

    private int? LineAt(double y)
    {
        foreach (var (line, top, bottom) in _rowMap)
            if (y >= top && y < bottom) return line;
        return _rowMap.Count > 0 && y >= _rowMap[^1].Bottom ? _rowMap[^1].Line : null;
    }

    private void OnSelectionChanged()
    {
        if (_followTail && _settings.FollowDeactivate is FollowDeactivate.OnSelection or FollowDeactivate.OnScrollOrSelection)
            _followTail = false;
        InvalidateVisual();
        Changed();
    }

    private int Columns() => _settings.WrapColumn switch
    {
        AppSettings.WrapOff => 0,
        AppSettings.WrapAuto => Math.Max(10, (int)((Bounds.Width - TextLeft - 4) / _charWidth)),
        var n => n,
    };

    private void UpdateMaxTop()
    {
        var count = File?.LineCount ?? 0;
        var rows = VisibleRows;
        if (count == 0) { MaxTopLine = 0; return; }
        if (!IsWrapping) { MaxTopLine = Math.Max(0, count - rows); return; }
        var tail = File!.ReadLines(Math.Max(0, count - rows), rows)
            .Select(l => TextLayout.ExpandTabs(l, _settings.TabWidth)).ToList();
        MaxTopLine = TextLayout.TailTopLine(count, tail, Columns(), rows);
    }

    private void Changed() => ViewChanged?.Invoke(this, EventArgs.Empty);

    private FormattedText Format(string text, IBrush brush, FontWeight weight, FontStyle style) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(_typeface.FontFamily, style, weight), _fontSize, brush);
}
