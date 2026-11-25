using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Logictail.App.Dialogs;
using Logictail.App.Scanner;
using Logictail.Core.Settings;
using Logictail.Scanner;

namespace Logictail.App.Tests;

/// <summary>
/// Renders every screen at several window sizes for review, and fails if any two visible controls in the
/// toolbars overlap or any control sticks out of its window.
/// </summary>
public sealed class UiReviewTests : IDisposable
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("LOGICTAIL_SHOTS") ?? Path.GetTempPath();
    private readonly string _dir = Directory.CreateTempSubdirectory("logictail-review-").FullName;
    public static readonly int[] Widths = [1180, 960, 800, 680];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Pump(int times = 6)
    {
        for (var i = 0; i < times; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(30); }
    }

    /// <summary>Leaf controls a user can see and click, with their bounds in window coordinates.</summary>
    private static List<(Control Control, Rect Bounds)> Leaves(Visual root, Window window) =>
        root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c is TextBox or Button or ComboBox or CheckBox or ToggleButton)
            // Skip the internals of composite controls (scrollbar arrows, number-box spinners, button content).
            .Where(c => c is not RepeatButton && !c.GetVisualAncestors().OfType<Control>().Any(a => a is TextBox or ComboBox or Button or ScrollBar or NumericUpDown))
            .Select(c => (c, new Rect(c.TranslatePoint(new Point(0, 0), window)!.Value, c.Bounds.Size)))
            .ToList();

    private static List<string> Problems(Visual root, Window window)
    {
        var problems = new List<string>();
        var leaves = Leaves(root, window);
        for (var i = 0; i < leaves.Count; i++)
        {
            var (a, ra) = leaves[i];
            if (ra.Right > window.Bounds.Width + 0.5) problems.Add($"{Name(a)} sticks out right ({ra.Right:0} > {window.Bounds.Width:0})");
            for (var j = i + 1; j < leaves.Count; j++)
            {
                var (b, rb) = leaves[j];
                var overlap = ra.Intersect(rb);
                if (overlap.Width > 0.5 && overlap.Height > 0.5) problems.Add($"{Name(a)} overlaps {Name(b)} by {overlap.Width:0}x{overlap.Height:0}");
                // Column headers are meant to sit edge to edge.
                else if (!(a.Classes.Contains("quiet") && b.Classes.Contains("quiet")) && Math.Abs(ra.Top - rb.Top) < ra.Height / 2 && (Math.Abs(ra.Right - rb.Left) < 2 || Math.Abs(rb.Right - ra.Left) < 2))
                    problems.Add($"{Name(a)} touches {Name(b)} (no gap)");
            }
        }
        return problems;
    }

    private static string Name(Control c) => c switch
    {
        TextBox t => $"TextBox[{t.PlaceholderText ?? t.Name}]",
        CheckBox { Content: string s } => $"CheckBox[{s}]",
        Button { Content: string s } => $"Button[{s}]",
        _ => c.GetType().Name + (c.Name is { } n ? $"#{n}" : ""),
    };

    [AvaloniaFact]
    public async Task Network_screen_has_no_overlaps_at_any_width()
    {
        var all = new List<string>();
        foreach (var width in Widths)
        {
            var view = new ScannerView(new Demo(), Path.Combine(AppContext.BaseDirectory, "data", "oui.csv"), HostStore.InMemory())
            {
                RangeText = "192.168.1.1-40", PortsText = "22, 3389",
            };
            var window = new MainWindow(new SettingsStore(_dir, _dir, _dir), new AppSettings(), CommandLine.Parse([])) { Width = width, Height = 640 };
            window.Show();
            window.FindControl<ContentControl>("NetworkPage")!.Content = view;
            window.FindControl<Button>("NavNetwork")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.ToggleScanAsync();
            Pump();
            view.Select(view.VisibleResults[0]);
            Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, $"review-network-{width}.png"));
            all.AddRange(Problems(window, window).Select(p => $"network @{width}: {p}"));
            window.Close();
        }
        Assert.True(all.Count == 0, string.Join("\n", all));
    }

    [AvaloniaFact]
    public void Logs_screen_has_no_overlaps_at_any_width()
    {
        var a = Path.Combine(_dir, "orders-api.log");
        var b = Path.Combine(_dir, "db-sync.log");
        File.WriteAllLines(a, Enumerable.Range(0, 200).Select(i => $"2026-11-25 14:00:{i % 60:00} INFO  request {i} completed" + (i % 9 == 0 ? " ERROR timeout" : "")));
        File.WriteAllLines(b, ["one", "two"]);
        var all = new List<string>();
        foreach (var width in Widths)
        {
            var window = new MainWindow(new SettingsStore(_dir, _dir, _dir), new AppSettings(), CommandLine.Parse([b, a])) { Width = width, Height = 560 };
            window.Show();
            Pump(10);
            window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, $"review-logs-{width}.png"));
            all.AddRange(Problems(window, window).Select(p => $"logs @{width}: {p}"));
            window.Close();
        }
        Assert.True(all.Count == 0, string.Join("\n", all));
    }

    [AvaloniaFact]
    public void Dialogs_have_no_overlaps()
    {
        var owner = new Window();
        owner.Show();
        var all = new List<string>();
        Window[] dialogs =
        [
            new OptionsDialog(new AppSettings(), @"C:\Users\someone\AppData\Roaming\Logictail\logictail.json"),
            new HighlightingDialog(AppSettings.DefaultRules()),
            new EditHostDialog(new HostResult { Address = IPAddress.Parse("192.168.1.5"), Status = HostStatus.Alive, Name = "nas.home" }, null),
        ];
        foreach (var dialog in dialogs)
        {
            _ = dialog.ShowDialog(owner);
            Pump();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, $"review-{dialog.GetType().Name}.png"));
            all.AddRange(Problems(dialog, dialog).Select(p => $"{dialog.GetType().Name}: {p}"));
            dialog.Close();
        }
        Assert.True(all.Count == 0, string.Join("\n", all));
    }

    private sealed class Demo : IProbes
    {
        private static int Last(IPAddress ip) => ip.GetAddressBytes()[3];
        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(Last(ip) is 1 or 5 or 14);
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult<string?>(Last(ip) == 5 ? "00-11-32-AA-BB-CC" : null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>($"host-{Last(ip)}.home");
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(port is 22 or 80);
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
        public Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<UdpResult> UdpProbeAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(UdpResult.Closed);
    }
}
