using Avalonia.Media;

namespace Logictail.App;

/// <summary>Design tokens for controls built in code. Same values as App.axaml; see docs/DESIGN.md.</summary>
public static class Palette
{
    public static readonly Color TealColor = Color.Parse("#67929E");
    public static readonly IBrush Teal = new SolidColorBrush(TealColor);
    public static readonly IBrush TealLight = new SolidColorBrush(Color.Parse("#9BB5BC"));
    public static readonly IBrush TealMist = new SolidColorBrush(Color.Parse("#E8EEF0"));
    public static readonly IBrush Canvas = new SolidColorBrush(Color.Parse("#F3F5F6"));
    public static readonly IBrush Charcoal = new SolidColorBrush(Color.Parse("#323537"));
    public static readonly IBrush Slate = new SolidColorBrush(Color.Parse("#6C7A7F"));
    public static readonly IBrush Rule = new SolidColorBrush(Color.Parse("#DEE2E6"));

    // LogicERP status colors.
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#93C949"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#EA843E"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E74E34"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A7B1B5"));

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    // Icons drawn for Logictail (24x24 viewbox), not taken from any icon set.
    public const string IconMenu = "M3 6h18v2H3zM3 11h18v2H3zM3 16h18v2H3z";
    public const string IconLogs = "M4 4h16v16H4zM6 6v12h12V6zM8 8h8v1.6H8zM8 11.2h8v1.6H8zM8 14.4h5v1.6H8z";
    public const string IconNetwork = "M10 3h4v4h-4zM3 17h4v4H3zM17 17h4v4h-4zM11.25 7h1.5v4.25H19V17h-1.5v-4.25h-11V17H5v-5.75h6.25z";
    public const string IconOpen = "M11 5h2v6h6v2h-6v6h-2v-6H5v-2h6z";
}
