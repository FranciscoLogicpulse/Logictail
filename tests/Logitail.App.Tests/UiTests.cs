using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Logitail.App.Dialogs;
using Logitail.Core.Settings;

namespace Logitail.App.Tests;

public sealed class UiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logitail-ui-").FullName;

    /// <summary>Screenshots land here so a person can look at them.</summary>
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("LOGITAIL_SHOTS") ?? Path.GetTempPath();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsStore Store() => new(_dir, _dir, _dir);

    private string WriteLog(string text)
    {
        var path = Path.Combine(_dir, $"log{Guid.NewGuid():N}.log");
        File.WriteAllText(path, text);
        return path;
    }

    private static void Pump(int times = 5)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Pump(1);
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static void Save(Window window, string name)
    {
        Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Opens_files_selects_lines_and_copies_them()
    {
        var path = WriteLog("alpha\nbravo ERROR here\ncharlie\tTabbed\ndelta\n");
        var window = new MainWindow(Store(), new AppSettings(), CommandLine.Parse([path]));
        window.Show();
        var view = window.GetVisualDescendants().OfType<Controls.LogView>().Single();
        await WaitFor(() => view.File!.LineCount == 4);
        Pump();

        // Click line 2, shift+click line 3.
        window.MouseDown(new Point(60, view.TranslatePoint(new Point(0, 0), window)!.Value.Y + 25), MouseButton.Left);
        window.MouseUp(new Point(60, view.TranslatePoint(new Point(0, 0), window)!.Value.Y + 25), MouseButton.Left);
        var y3 = view.TranslatePoint(new Point(0, 0), window)!.Value.Y + 42;
        window.MouseDown(new Point(60, y3), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(new Point(60, y3), MouseButton.Left, RawInputModifiers.Shift);
        Pump();
        Assert.Equal((1, 2), view.Selection);
        Assert.False(view.FollowTail); // selection turns follow-tail off by default

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Pump();
        var clip = await window.Clipboard!.TryGetTextAsync();
        Assert.Equal($"bravo ERROR here{Environment.NewLine}charlie\tTabbed", clip);
        Save(window, "logitail-main");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Wrapping_follows_tail_to_the_last_row()
    {
        var lines = string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i} " + new string('x', i % 7 == 0 ? 300 : 20) + "\n"));
        var path = WriteLog(lines);
        var settings = new AppSettings { WrapColumn = AppSettings.WrapAuto };
        var window = new MainWindow(Store(), settings, CommandLine.Parse([path])) { Width = 700, Height = 400 };
        window.Show();
        var view = window.GetVisualDescendants().OfType<Controls.LogView>().Single();
        await WaitFor(() => view.File!.LineCount == 200);
        Pump();
        Assert.True(view.IsWrapping);
        Assert.True(view.FollowTail);
        Assert.True(view.TopLine > 150 && view.TopLine < 200, $"top line {view.TopLine}");
        Save(window, "logitail-wrapped");
        window.Close();
    }

    [AvaloniaFact]
    public void Options_dialog_round_trips_settings()
    {
        var owner = new Window();
        owner.Show();
        var original = new AppSettings { FontSize = 15, WrapColumn = 80, TabPosition = TabStripPosition.Bottom, AlwaysOnTop = true };
        var dialog = new OptionsDialog(original, @"C:\example\logitail.json");
        AppSettings? result = null;
        _ = dialog.ShowDialog<AppSettings?>(owner).ContinueWith(t => result = t.Result);
        Save(dialog, "logitail-options");

        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "OK")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.NotNull(result);
        Assert.Equal(15, result!.FontSize);
        Assert.Equal(80, result.WrapColumn);
        Assert.Equal(TabStripPosition.Bottom, result.TabPosition);
        Assert.True(result.AlwaysOnTop);
    }

    [AvaloniaFact]
    public void Highlighting_dialog_adds_a_rule()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new HighlightingDialog(AppSettings.DefaultRules());
        List<Core.Highlighting.HighlightRule>? result = null;
        _ = dialog.ShowDialog<List<Core.Highlighting.HighlightRule>?>(owner).ContinueWith(t => result = t.Result);
        Pump();

        Button Find(string label) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label);
        Find("_Add").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var pattern = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Text to match");
        pattern.Text = "timeout";
        Pump();
        Save(dialog, "logitail-highlighting");

        Find("OK").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
        Assert.Equal("timeout", result[^1].Pattern);
    }

    [AvaloniaFact]
    public async Task Indexing_finishes_without_waiting_for_the_update_interval()
    {
        var path = WriteLog(string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i:D5}\n")));
        var tab = new LogTab(path, Core.Highlighting.HighlightRuleSet.Empty, new AppSettings(), bytesPerStep: 1024);
        await tab.RefreshAsync(isSelected: true); // one call, ~60 steps of 1 KB
        Assert.True(tab.File.IsFullyIndexed);
        Assert.Equal(5000, tab.File.LineCount);
    }

    [AvaloniaFact]
    public async Task Horizontal_scroll_survives_scrolling_to_short_lines()
    {
        var text = string.Concat(Enumerable.Range(0, 5).Select(_ => new string('w', 400) + "\n"))
                 + string.Concat(Enumerable.Range(0, 300).Select(i => $"short {i}\n"));
        var path = WriteLog(text);
        var window = new MainWindow(Store(), new AppSettings(), CommandLine.Parse([path])) { Width = 800, Height = 400 };
        window.Show();
        var view = window.GetVisualDescendants().OfType<Controls.LogView>().Single();
        await WaitFor(() => view.File!.LineCount == 305);
        view.FollowTail = false;
        view.ScrollTo(0);
        Pump();
        var wide = view.ContentWidth;
        Assert.True(wide > view.ViewportWidth);

        view.ScrollHorizontally(wide);
        view.ScrollTo(view.MaxTopLine);
        Pump();
        Assert.Equal(wide, view.ContentWidth);              // the scroll range didn't collapse
        Assert.True(view.HorizontalOffset <= wide);
        var hBar = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(s => s.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Assert.True(hBar.IsVisible);                        // so the user can scroll back
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_M_toggles_a_hidden_main_menu()
    {
        var window = new MainWindow(Store(), new AppSettings { HideMainMenu = true }, CommandLine.Parse([]));
        window.Show();
        var menu = window.FindControl<Menu>("MainMenu")!;
        Assert.False(menu.IsVisible);
        window.KeyPress(Key.M, RawInputModifiers.Control, PhysicalKey.M, "m");
        Pump();
        Assert.True(menu.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Session_is_saved_on_close_and_restored()
    {
        var a = WriteLog("a\n");
        var b = WriteLog("b\n");
        var store = Store();
        var settings = new AppSettings { SaveOnExit = SaveLocation.WorkingDirectory };
        var window = new MainWindow(store, settings, CommandLine.Parse([a, b]));
        window.Show();
        Pump();
        window.Close();
        Pump();

        var saved = SettingsStore.LoadFile(store.PathFor(SaveLocation.WorkingDirectory)!);
        Assert.Equal([a, b], saved.OpenFiles);
        Assert.Equal(1, saved.CurrentTabIndex);
        Assert.Equal(b, saved.RecentFiles[0]);

        var restored = new MainWindow(store, saved, CommandLine.Parse([]));
        restored.Show();
        var views = restored.GetVisualDescendants().OfType<Controls.LogView>().ToList();
        await WaitFor(() => restored.Title == $"{Path.GetFileName(b)} - Logitail");
        restored.Close();
    }
}
