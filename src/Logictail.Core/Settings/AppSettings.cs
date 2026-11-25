using Logictail.Core.Highlighting;
using Logictail.Core.Localization;

namespace Logictail.Core.Settings;

public enum TabStripPosition { Top, Bottom, Left, Right }

/// <summary>When the file tab strip is shown.</summary>
public enum TabShowMode { Never, AtLeastTwo, AtLeastOne, Always }

public enum FollowActivate { Never, OnScrollToEnd }

public enum FollowDeactivate { Never, OnScrollOrSelection, OnSelection, OnScroll }

/// <summary>Where settings are written on exit.</summary>
public enum SaveLocation { Never, WorkingDirectory, AppDirectory, UserProfile }

public enum WindowStateKind { Normal, Maximized, Minimized }

public sealed record WindowPlacement(double X, double Y, double Width, double Height, WindowStateKind State);

/// <summary>Everything Logictail remembers. Serialized as JSON; unknown or missing fields fall back to defaults.</summary>
public sealed class AppSettings
{
    public const int WrapOff = 0;
    public const int WrapAuto = -1;

    /// <summary>Interface language. Portuguese (Portugal) unless English is chosen in Options.</summary>
    public Language Language { get; set; } = Language.Portuguese;

    // Font and layout
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, Courier New";
    public double FontSize { get; set; } = 13;
    /// <summary>Extra pixels between lines.</summary>
    public double LineSpacing { get; set; } = 2;
    /// <summary>Wrap column: <see cref="WrapOff"/>, <see cref="WrapAuto"/> (window width) or a column count.</summary>
    public int WrapColumn { get; set; } = WrapOff;
    public int TabWidth { get; set; } = 4;
    /// <summary>Marks wrapped continuation rows in the left gutter.</summary>
    public bool ShowLineMarkers { get; set; } = true;

    // Tailing
    public int UpdateIntervalMs { get; set; } = 250;
    public FollowActivate FollowActivate { get; set; } = FollowActivate.OnScrollToEnd;
    public FollowDeactivate FollowDeactivate { get; set; } = FollowDeactivate.OnScrollOrSelection;

    // Tabs
    public TabStripPosition TabPosition { get; set; } = TabStripPosition.Top;
    /// <summary>Small file tabs above the log when more than one file is open (the sidebar lists them too).</summary>
    public TabShowMode ShowTabs { get; set; } = TabShowMode.AtLeastTwo;

    // Window
    public bool AlwaysOnTop { get; set; }
    public bool HideMainMenu { get; set; }
    public bool SaveWindowPosition { get; set; } = true;
    public WindowPlacement? Window { get; set; }

    // Persistence and session
    public SaveLocation SaveOnExit { get; set; } = SaveLocation.UserProfile;
    public int MaxRecentFiles { get; set; } = 10;
    public List<string> RecentFiles { get; set; } = [];
    public List<string> OpenFiles { get; set; } = [];
    public int CurrentTabIndex { get; set; }

    public List<HighlightRule> HighlightRules { get; set; } = DefaultRules();

    public static readonly int[] UpdateIntervals = [600_000, 300_000, 60_000, 30_000, 20_000, 10_000, 5_000, 2_000, 1_000, 500, 250, 100, 50, 10];
    public static readonly int[] WrapColumns = [40, 60, 80, 100, 120, 132];

    public static List<HighlightRule> DefaultRules() =>
    [
        // Tinted rows with dark text: readable for hours, using the status palette.
        new() { Pattern = "error", Foreground = 0xFF8A2A1B, Background = 0xFFFBE1DC },
        new() { Pattern = "exception", Foreground = 0xFF8A2A1B, Background = 0xFFFBE1DC },
        new() { Pattern = "warn", Foreground = 0xFF7A4A12, Background = 0xFFFDEBDD },
    ];

    /// <summary>Adds a file to the top of the recent list.</summary>
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    /// <summary>Clamps values a hand-edited file could break.</summary>
    public AppSettings Normalize()
    {
        FontSize = Math.Clamp(FontSize, 6, 72);
        LineSpacing = Math.Clamp(LineSpacing, 0, 40);
        TabWidth = Math.Clamp(TabWidth, 1, 16);
        if (WrapColumn is not (WrapOff or WrapAuto)) WrapColumn = Math.Clamp(WrapColumn, 10, 10_000);
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 10, 600_000);
        MaxRecentFiles = Math.Clamp(MaxRecentFiles, 0, 50);
        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = new AppSettings().FontFamily;
        RecentFiles ??= [];
        OpenFiles ??= [];
        HighlightRules ??= DefaultRules();
        return this;
    }

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));
}
