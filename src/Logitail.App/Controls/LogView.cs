using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Logitail.Core.Highlighting;
using Logitail.Core.Tail;

namespace Logitail.App.Controls;

/// <summary>
/// Draws only the visible lines of a <see cref="LogFile"/>, so file size doesn't matter.
/// Follow-tail turns off when the user scrolls up and back on when they scroll to the end.
/// </summary>
public sealed class LogView : Control
{
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, Courier New");
    private const double FontSize = 13;
    private const double LeftPad = 6;

    private double _lineHeight = 17;
    private int _topLine;
    private bool _followTail = true;
    private double _wheelRemainder;

    public LogView()
    {
        Focusable = true;
        ClipToBounds = true;
        var probe = Format("Xg", Brushes.Black, FontWeight.Normal, FontStyle.Normal);
        _lineHeight = Math.Ceiling(probe.Height);
    }

    public LogFile? File { get; set; }

    public HighlightRuleSet Rules { get; set; } = HighlightRuleSet.Empty;

    public event EventHandler? ViewChanged;

    public int TopLine => _topLine;

    public int VisibleLines => Math.Max(1, (int)(Bounds.Height / _lineHeight));

    public int MaxTopLine => Math.Max(0, (File?.LineCount ?? 0) - VisibleLines);

    public bool FollowTail
    {
        get => _followTail;
        set
        {
            if (_followTail == value) return;
            _followTail = value;
            if (value) ScrollTo(MaxTopLine);
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Call after the file was refreshed.</summary>
    public void OnFileChanged(bool reset)
    {
        if (reset) _topLine = 0;
        if (_followTail) _topLine = MaxTopLine;
        _topLine = Math.Clamp(_topLine, 0, MaxTopLine);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollTo(int line, bool fromUser = false)
    {
        var target = Math.Clamp(line, 0, MaxTopLine);
        if (fromUser)
        {
            if (target < _topLine && _followTail) { _followTail = false; }
            else if (target >= MaxTopLine && !_followTail) { _followTail = true; }
        }
        _topLine = target;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);
        if (File is null) return;

        var lines = File.ReadLines(_topLine, VisibleLines + 1);
        for (var i = 0; i < lines.Count; i++)
        {
            var y = i * _lineHeight;
            var text = lines[i];
            var rule = Rules.Match(text);
            IBrush fg = Brushes.Black;
            var weight = FontWeight.Normal;
            var style = FontStyle.Normal;
            if (rule is not null)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromUInt32(rule.Background)), new Rect(0, y, bounds.Width, _lineHeight));
                fg = new SolidColorBrush(Color.FromUInt32(rule.Foreground));
                if (rule.Bold) weight = FontWeight.Bold;
                if (rule.Italic) style = FontStyle.Italic;
            }
            // shortcut: no horizontal scroll or wrapping yet (milestone 2); long lines are clipped.
            context.DrawText(Format(ExpandTabs(text), fg, weight, style), new Point(LeftPad, y));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Keep the fraction so slow touchpad scrolling still moves.
        _wheelRemainder += e.Delta.Y * 3;
        var lines = (int)Math.Truncate(_wheelRemainder);
        _wheelRemainder -= lines;
        if (lines != 0) ScrollTo(_topLine - lines, fromUser: true);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int? target = e.Key switch
        {
            Key.Up => _topLine - 1,
            Key.Down => _topLine + 1,
            Key.PageUp => _topLine - VisibleLines,
            Key.PageDown => _topLine + VisibleLines,
            Key.Home when e.KeyModifiers.HasFlag(KeyModifiers.Control) => 0,
            Key.End when e.KeyModifiers.HasFlag(KeyModifiers.Control) => MaxTopLine,
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

    private static string ExpandTabs(string text) => text.Contains('\t') ? text.Replace("\t", "    ") : text;

    private static FormattedText Format(string text, IBrush brush, FontWeight weight, FontStyle style) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Mono.FontFamily, style, weight), FontSize, brush);
}
