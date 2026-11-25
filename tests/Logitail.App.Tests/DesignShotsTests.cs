using System.Net;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Logitail.App.Scanner;
using Logitail.Core.Settings;
using Logitail.Scanner;

namespace Logitail.App.Tests;

/// <summary>Renders the main screens for design review. Also checks the shell's navigation works.</summary>
public sealed class DesignShotsTests : IDisposable
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("LOGITAIL_SHOTS") ?? Path.GetTempPath();
    private readonly string _dir = Directory.CreateTempSubdirectory("logitail-design-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Pump(int times = 6)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(40);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) { Pump(1); await Task.Delay(20); }
        Assert.True(condition());
    }

    private string Log(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static IEnumerable<string> AppLog(int from, int count) => Enumerable.Range(from, count).Select(i =>
    {
        var level = (i % 11) switch { 3 => "WARN ", 7 => "ERROR", _ => "INFO " };
        var msg = (i % 11) switch
        {
            3 => $"Slow response from inventory service: {800 + i % 300} ms (threshold 750 ms)",
            7 => $"Invoice export failed for order {48210 + i}: connection reset by peer",
            _ => $"Request GET /api/orders/{48210 + i} completed 200 in {12 + i % 40} ms",
        };
        return $"2026-11-25 14:{i / 60 % 60:00}:{i % 60:00}.{i * 37 % 1000:000} {level} [worker-{i % 4}] {msg}";
    });

    [AvaloniaFact]
    public async Task Logs_screen()
    {
        var app = Log("orders-api.log", AppLog(0, 400));
        var db = Log("db-sync.log", AppLog(1000, 50));
        var window = new MainWindow(new SettingsStore(_dir, _dir, _dir), new AppSettings(), CommandLine.Parse([db, app]));
        window.Width = 1180;
        window.Height = 720;
        window.Show();
        await WaitFor(() => window.Title?.StartsWith("orders-api.log") == true);
        Pump();

        // The background file grows: its sidebar entry should show the new-line count.
        File.AppendAllLines(db, AppLog(1050, 12));
        await Task.Delay(600);
        Pump(10);

        var files = window.FindControl<ListBox>("FileList")!;
        Assert.Equal(2, files.ItemCount);
        Assert.NotNull(files.SelectedItem); // the open file is highlighted in the sidebar
        var strip = window.FindControl<TabControl>("Tabs")!.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ItemsPresenter>().First();
        Assert.True(strip.IsVisible); // two files: small tabs on top
        var badges = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("+12", badges);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "design-logs.png"));

        // Collapse the sidebar to its rail.
        window.FindControl<Button>("SidebarToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(50, window.FindControl<Border>("Sidebar")!.Width);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "design-collapsed.png"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Empty_and_network_screens()
    {
        var window = new MainWindow(new SettingsStore(_dir, _dir, _dir), new AppSettings(), CommandLine.Parse([]));
        window.Width = 1180;
        window.Height = 720;
        window.Show();
        Pump();
        Assert.True(window.FindControl<StackPanel>("EmptyLogs")!.IsVisible);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "design-empty.png"));

        // Swap in a scanner on a fake network so nothing real is scanned.
        var view = new ScannerView(new DemoNetwork(), Path.Combine(AppContext.BaseDirectory, "data", "oui.csv"), HostStore.InMemory())
        {
            RangeText = "192.168.1.1-40",
        };
        window.FindControl<ContentControl>("NetworkPage")!.Content = view;
        window.FindControl<Button>("NavNetwork")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(window.FindControl<ContentControl>("NetworkPage")!.IsVisible);
        Assert.False(window.FindControl<DockPanel>("LogsPage")!.IsVisible);

        await view.ToggleScanAsync();
        Pump();
        view.ToggleFavorite(view.VisibleResults.Single(h => h.Name == "nas.home"));
        Pump();
        view.Select(view.VisibleResults.Single(h => h.Name == "nas.home"));
        Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "design-network.png"));
        window.Close();
    }

    /// <summary>A small home network: router, NAS, printer, two PCs, a phone, a TV.</summary>
    private sealed class DemoNetwork : IProbes
    {
        private static readonly Dictionary<int, (string Name, string Mac, ServiceKind[] Ports)> Hosts = new()
        {
            [1] = ("router.home", "F0-9F-C2-10-20-30", [ServiceKind.Http, ServiceKind.Https]),
            [5] = ("nas.home", "00-11-32-AA-BB-CC", [ServiceKind.Http, ServiceKind.Smb, ServiceKind.Ssh]),
            [9] = ("printer-2f", "00-1B-A9-12-34-56", [ServiceKind.Http]),
            [14] = ("DESKTOP-ANA", "74-56-3C-E7-C6-37", [ServiceKind.Rdp, ServiceKind.Smb]),
            [15] = ("LAPTOP-RUI", "3C-52-82-01-02-03", [ServiceKind.Smb]),
            [22] = ("", "DC-A6-32-11-22-33", []),
            [31] = ("living-room-tv", "A8-23-FE-44-55-66", [ServiceKind.Http8080]),
        };

        private static int Last(IPAddress ip) => ip.GetAddressBytes()[3];
        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(Hosts.ContainsKey(Last(ip)));
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult(Hosts.TryGetValue(Last(ip), out var h) ? h.Mac : null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(Hosts.TryGetValue(Last(ip), out var h) && h.Name.Length > 0 ? h.Name : null);
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(Hosts.TryGetValue(Last(ip), out var h) && h.Ports.Any(p => Services.Port(p) == port));
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) =>
            Task.FromResult<string?>(s switch { ServiceKind.Http when Last(ip) == 1 => "Router login", ServiceKind.Ssh => "SSH-2.0-OpenSSH_9.6", _ => null });
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
    }
}
