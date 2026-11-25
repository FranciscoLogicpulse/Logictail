using System.Net;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Logitail.App.Scanner;
using Logitail.Scanner;

namespace Logitail.App.Tests;

public sealed class ScannerUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("LOGITAIL_SHOTS") ?? Path.GetTempPath();
    private static readonly string VendorFile = Path.Combine(AppContext.BaseDirectory, "data", "oui.csv");

    /// <summary>.1 router with a web page, .2 NAS found by ARP only, everything else silent.</summary>
    private sealed class FakeProbes : IProbes
    {
        private static int Last(IPAddress ip) => ip.GetAddressBytes()[3];
        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(Last(ip) == 1);
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) =>
            Task.FromResult(Last(ip) switch { 1 => "FC-FB-FB-00-00-01", 2 => "00-11-32-AA-BB-CC", _ => (string?)null });
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(Last(ip) == 1 ? "router.home" : null);
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(Last(ip) == 2 ? new NetBiosInfo("NAS", "WORKGROUP", null, null) : null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) =>
            Task.FromResult((Last(ip), port) is (1, 80) or (1, 443) or (2, 445));
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(s == ServiceKind.Http ? "Router admin" : null);
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SharedResource>>(Last(ip) == 2 ? [new SharedResource("Media", ShareKind.Folder, "Movies"), new SharedResource("Laser", ShareKind.Printer, null)] : []);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
    }

    private static async Task<(Window, ScannerView)> Open(IProbes probes, string range, bool listShares = false)
    {
        var view = new ScannerView(probes, VendorFile, HostStore.InMemory()) { RangeText = range, ListShares = listShares };
        var window = new Window { Width = 1100, Height = 420, Content = view };
        window.Show();
        await view.ToggleScanAsync();
        Pump();
        return (window, view);
    }

    [AvaloniaFact]
    public async Task Scan_shows_alive_hosts_with_vendor_and_services()
    {
        var (window, view) = await Open(new FakeProbes(), "10.0.0.1-20");
        Assert.Equal(20, view.Results.Count);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], view.VisibleResults.Select(h => h.Address.ToString()));
        var router = view.VisibleResults[0];
        Assert.Equal("router.home", router.Name);
        Assert.NotNull(router.Manufacturer); // FC-FB-FB is in the shipped IEEE registry
        Assert.Equal([ServiceKind.Http, ServiceKind.Https], router.Services.Select(s => s.Kind));
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "logitail-scanner.png"));

        // Show dead too.
        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Show _dead").IsChecked = true;
        Pump();
        Assert.Equal(20, view.VisibleResults.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Details_favorites_notes_and_actions()
    {
        var (window, view) = await Open(new FakeProbes(), "10.0.0.1-5", listShares: true);
        var launched = new List<System.Diagnostics.ProcessStartInfo>();
        view.Launch = psi => { launched.Add(psi); return Task.CompletedTask; };

        var nas = view.VisibleResults.Single(h => h.Name == "NAS");
        Assert.Equal(DeviceType.Nas, nas.DeviceType);                    // Synology vendor
        Assert.Equal(["Media", "Laser"], nas.Shares.Select(s => s.Name));
        view.Select(nas);
        Pump();
        var detailText = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Synology", detailText);
        Assert.Contains("Media - Movies", detailText);

        // Favorite it, then show favorites only.
        view.ToggleFavorite(nas);
        Pump();
        Assert.True(view.VisibleResults.Single(h => h.Name == "NAS").IsFavorite);
        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "_Favorites only").IsChecked = true;
        Pump();
        Assert.Equal(["NAS"], view.VisibleResults.Select(h => h.Name));
        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "_Favorites only").IsChecked = false;
        Pump();

        // Actions build commands from the IP only.
        view.RunAction(HostAction.Explore, view.VisibleResults.Single(h => h.Name == "NAS"), "Media");
        view.RunAction(HostAction.OpenHttp, view.VisibleResults.Single(h => h.Name == "router.home"));
        Pump();
        Assert.Equal([@"\\10.0.0.2\Media"], launched[0].ArgumentList);
        Assert.Equal("http://10.0.0.1/", launched[1].FileName);

        view.Select(view.VisibleResults.Single(h => h.Name == "NAS"));
        Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "logitail-scanner-details.png"));

        // Right-click menu for the NAS.
        var menu = new ContextMenu();
        view.FillContextMenu(menu, view.VisibleResults.Single(h => h.Name == "NAS"));
        var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Contains("Explore shares", headers);
        Assert.Contains("Open share", headers);
        Assert.Contains("Wake-on-LAN", headers);
        Assert.Contains("Remove from favorites", headers);
        Assert.DoesNotContain("Connect with Remote Desktop", headers); // NAS has no RDP
        window.Close();
    }

    [AvaloniaFact]
    public async Task Shares_are_not_listed_unless_asked()
    {
        var (window, view) = await Open(new FakeProbes(), "10.0.0.2");
        var nas = view.VisibleResults.Single();
        Assert.Empty(nas.Shares);
        var menu = new ContextMenu();
        view.FillContextMenu(menu, nas);
        Assert.Contains("List shared folders (signs in as you)", menu.Items.OfType<MenuItem>().Select(m => m.Header as string));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Favorite_survives_a_rescan_while_switched_off()
    {
        var store = HostStore.InMemory();
        var view = new ScannerView(new FakeProbes(), VendorFile, store) { RangeText = "10.0.0.2" };
        var window = new Window { Content = view };
        window.Show();
        await view.ToggleScanAsync();
        Pump();
        view.ToggleFavorite(view.VisibleResults.Single());
        Pump();

        // Same address, now silent: rescan with a probe set where nothing answers.
        var off = new ScannerView(new SilentProbes(), VendorFile, store) { RangeText = "10.0.0.2" };
        window.Content = off;
        await off.ToggleScanAsync();
        Pump();
        var dead = off.Results.Single();
        Assert.Equal(HostStatus.Dead, dead.Status);
        Assert.True(dead.IsFavorite);
        Assert.Equal("00-11-32-AA-BB-CC", dead.Mac); // still there for Wake-on-LAN
        window.Close();
    }

    private sealed class SilentProbes : IProbes
    {
        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
    }

    [AvaloniaFact]
    public async Task Bad_range_shows_an_error_and_does_not_scan()
    {
        var (window, view) = await Open(new FakeProbes(), "10.0.0.999");
        Assert.Empty(view.Results);
        Assert.False(view.IsScanning);
        var status = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.Contains("10.0.0.999") == true);
        Assert.Contains("not an IP address", status.Text);
        window.Close();
    }

    /// <summary>
    /// Real network probes against this PC only (127.0.0.1 and its own IPv4 addresses).
    /// Runs only with LOGITAIL_LIVE=1 so normal test runs never touch the network.
    /// </summary>
    [AvaloniaFact]
    public async Task Live_scan_of_this_pc_only()
    {
        if (Environment.GetEnvironmentVariable("LOGITAIL_LIVE") != "1") return;
        var own = LocalNetwork.Subnets().Select(s => s.Address.ToString()).Prepend("127.0.0.1").ToList();
        var (window, view) = await Open(new SystemProbes(), string.Join(", ", own));
        Assert.Equal(own.Count, view.Results.Count);
        Assert.All(view.Results, h => Assert.Equal(HostStatus.Alive, h.Status));
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "logitail-scanner-live.png"));
        File.WriteAllLines(Path.Combine(ShotDir, "logitail-scanner-live.txt"), view.Results.Select(h =>
            $"{h.Address} | {h.Status} | name={h.Name} | type={h.DeviceType} | mac={h.Mac} | vendor={h.Manufacturer} | shares={string.Join("+", h.Shares.Select(s => s.Name))} | " +
            string.Join(", ", h.Services.Select(s => $"{s.Kind}:{s.Detail}"))));
        window.Close();
    }
}
