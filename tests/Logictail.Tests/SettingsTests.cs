using Logictail.Core.Highlighting;
using Logictail.Core.Settings;

namespace Logictail.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("logictail-settings-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SettingsStore NewStore()
    {
        foreach (var d in new[] { "work", "app", "user" }) Directory.CreateDirectory(Path.Combine(_root, d));
        return new SettingsStore(Path.Combine(_root, "work"), Path.Combine(_root, "app"), Path.Combine(_root, "user"));
    }

    [Fact]
    public void Round_trips_all_fields()
    {
        var s = new AppSettings
        {
            FontSize = 15, WrapColumn = AppSettings.WrapAuto, TabPosition = TabStripPosition.Left,
            FollowDeactivate = FollowDeactivate.OnSelection, Window = new WindowPlacement(10, 20, 800, 600, WindowStateKind.Maximized),
            HighlightRules = [new HighlightRule { Pattern = "x", IsRegex = true, Bold = true, Background = 0xFF123456 }],
        };
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(15, back.FontSize);
        Assert.Equal(AppSettings.WrapAuto, back.WrapColumn);
        Assert.Equal(TabStripPosition.Left, back.TabPosition);
        Assert.Equal(FollowDeactivate.OnSelection, back.FollowDeactivate);
        Assert.Equal(s.Window, back.Window);
        Assert.Equal(s.HighlightRules, back.HighlightRules);
    }

    [Fact]
    public void Missing_fields_get_defaults_and_bad_values_are_clamped()
    {
        var s = SettingsStore.Deserialize("""{ "FontSize": 500, "TabWidth": 0, "HighlightRules": null }""");
        Assert.Equal(72, s.FontSize);
        Assert.Equal(1, s.TabWidth);
        Assert.Equal(250, s.UpdateIntervalMs);
        Assert.Equal(AppSettings.DefaultRules(), s.HighlightRules);
    }

    [Fact]
    public void Load_prefers_working_directory_then_app_then_user()
    {
        var store = NewStore();
        SettingsStore.SaveFile(new AppSettings { FontSize = 20 }, store.PathFor(SaveLocation.UserProfile)!);
        Assert.Equal(20, store.Load().FontSize);

        SettingsStore.SaveFile(new AppSettings { FontSize = 21 }, store.PathFor(SaveLocation.WorkingDirectory)!);
        var fresh = NewStore();
        Assert.Equal(21, fresh.Load().FontSize);
        Assert.Equal(fresh.PathFor(SaveLocation.WorkingDirectory), fresh.LoadedFrom);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_with_an_error()
    {
        var store = NewStore();
        File.WriteAllText(store.PathFor(SaveLocation.UserProfile)!, "{ not json");
        var s = store.Load();
        Assert.Equal(new AppSettings().FontSize, s.FontSize);
        Assert.NotNull(store.LoadError);
        Assert.Null(store.LoadedFrom);
    }

    [Fact]
    public void Unreadable_file_is_kept_as_broken_instead_of_overwritten()
    {
        var store = NewStore();
        var path = store.PathFor(SaveLocation.UserProfile)!;
        File.WriteAllText(path, "{ \"FontSize\": 14, oops }");
        var s = store.Load();
        Assert.Equal(path, store.Save(s));
        Assert.Equal("{ \"FontSize\": 14, oops }", File.ReadAllText(path + ".broken"));
        Assert.Equal(s.FontSize, SettingsStore.LoadFile(path).FontSize);
    }

    [Fact]
    public void Save_never_writes_nothing()
    {
        var store = NewStore();
        Assert.Null(store.Save(new AppSettings { SaveOnExit = SaveLocation.Never }));
        Assert.Empty(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Old_name_profile_is_copied_once()
    {
        var appData = Path.Combine(_root, "appdata");
        var oldDir = Directory.CreateDirectory(Path.Combine(appData, "Logitail")).FullName;
        File.WriteAllText(Path.Combine(oldDir, "logitail.json"), """{ "FontSize": 17 }""");
        File.WriteAllText(Path.Combine(oldDir, "hosts.json"), "{}");

        Assert.True(SettingsStore.MigrateFromOldName(appData));
        var newDir = Path.Combine(appData, "Logictail");
        Assert.Equal(17, SettingsStore.LoadFile(Path.Combine(newDir, SettingsStore.FileName)).FontSize);
        Assert.True(File.Exists(Path.Combine(newDir, "hosts.json")));
        Assert.True(File.Exists(Path.Combine(oldDir, "logitail.json"))); // old copy left in place
        Assert.False(SettingsStore.MigrateFromOldName(appData));          // only once
    }

    [Fact]
    public void Recent_files_dedupe_and_cap()
    {
        var s = new AppSettings { MaxRecentFiles = 3 };
        foreach (var f in new[] { "a", "b", "c", "A", "d" }) s.AddRecent(f);
        Assert.Equal(["d", "A", "c"], s.RecentFiles);
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Files_and_switches()
    {
        var c = CommandLine.Parse(["a.log", "--tile-window-count", "3", "--tile-window-index=1", "b.log", "--window-state", "Maximized"]);
        Assert.Equal(["a.log", "b.log"], c.Files);
        Assert.Equal(3, c.TileCount);
        Assert.Equal(1, c.TileIndex);
        Assert.Equal(WindowStateKind.Maximized, c.State);
        Assert.Empty(c.Errors);
    }

    [Fact]
    public void Window_position()
    {
        var c = CommandLine.Parse(["--window-position", "10,20,800.5,600"]);
        Assert.Equal((10, 20, 800.5, 600), c.Position);
    }

    [Theory]
    [InlineData("--window-position", "1,2,3")]
    [InlineData("--window-state", "huge")]
    [InlineData("--tile-window-count", "0")]
    [InlineData("--bogus", "x")]
    public void Invalid_values_are_reported(string name, string value)
    {
        var c = CommandLine.Parse([name, value]);
        Assert.NotEmpty(c.Errors);
    }

    [Fact]
    public void Tile_index_needs_a_valid_count()
    {
        Assert.NotEmpty(CommandLine.Parse(["--tile-window-index", "0"]).Errors);
        Assert.NotEmpty(CommandLine.Parse(["--tile-window-count", "2", "--tile-window-index", "2"]).Errors);
    }
}
