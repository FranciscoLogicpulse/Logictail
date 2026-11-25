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
        public Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<UdpResult> UdpProbeAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(UdpResult.Closed);
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
        public Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<UdpResult> UdpProbeAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(UdpResult.Closed);
    }

    [AvaloniaFact]
    public async Task Port_scan_from_the_ports_field_and_right_click()
    {
        var view = new ScannerView(new PortProbes(), VendorFile, HostStore.InMemory()) { RangeText = "10.0.0.1", PortsText = "20-25, UDP: 53" };
        var window = new Window { Width = 1100, Height = 420, Content = view };
        window.Show();
        await view.ToggleScanAsync();
        Pump();
        var host = view.VisibleResults.Single();
        Assert.Equal(["22/tcp", "53/udp"], host.OpenPorts.Select(p => p.Label));

        // Bad port list: no scan, clear error.
        view.PortsText = "22, 99999";
        await view.ToggleScanAsync();
        Pump();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Ports:") == true);

        // Right-click scan on one device with an empty field uses the common ports.
        view.PortsText = "";
        await view.ScanPortsAsync(view.VisibleResults.Single());
        Pump();
        Assert.Contains(view.VisibleResults.Single().OpenPorts, p => p.Port == 3389);
        window.Close();
    }

    /// <summary>Ping-alive host with TCP 22 and 3389 open and UDP 53 answering.</summary>
    private sealed class PortProbes : IProbes
    {
        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(true);
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>("server");
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(port is 22 or 3389);
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
        public Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(port == 22 ? "SSH-2.0-OpenSSH_9.6" : null);
        public Task<UdpResult> UdpProbeAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) => Task.FromResult(port == 53 ? UdpResult.Open : UdpResult.Closed);
    }

    [AvaloniaFact]
    public void From_and_to_with_prefix_prediction()
    {
        var view = new ScannerView(new FakeProbes(), VendorFile, HostStore.InMemory()) { RangeText = "" };
        var window = new Window { Content = view };
        window.Show();
        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
        var from = boxes.Single(b => b.PlaceholderText == "192.168.1.1");
        var to = boxes.Single(b => b.PlaceholderText?.EndsWith("254") == true && b != from);

        from.Text = "10.20.30.5";
        Pump();
        Assert.Equal("10.20.30.254", to.PlaceholderText);
        Assert.Equal("10.20.30.5", view.RangeText);          // To untouched: single address
        to.Focus();
        Pump();
        Assert.Equal("10.20.30.", to.Text);                   // predicted prefix on focus
        Assert.Equal("10.20.30.5", view.RangeText);          // still just the prefix: not a range yet
        to.Text = "10.20.30.40";
        Assert.Equal("10.20.30.5-10.20.30.40", view.RangeText);

        view.RangeText = "192.168.1.1-192.168.1.254";
        Assert.Equal(("192.168.1.1", "192.168.1.254"), (from.Text, to.Text));
        window.Close();
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
        var view0 = new ScannerView(new SystemProbes(), VendorFile, HostStore.InMemory()) { RangeText = string.Join(", ", own), PortsText = PortList.Common + ", UDP: 53, 123, 137" };
        var window = new Window { Width = 1100, Height = 420, Content = view0 };
        window.Show();
        await view0.ToggleScanAsync();
        Pump();
        var view = view0;
        Assert.Equal(own.Count, view.Results.Count);
        Assert.All(view.Results, h => Assert.Equal(HostStatus.Alive, h.Status));
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "logitail-scanner-live.png"));
        File.WriteAllLines(Path.Combine(ShotDir, "logitail-scanner-live.txt"), view.Results.Select(h =>
            $"{h.Address} | {h.Status} | name={h.Name} | type={h.DeviceType} | mac={h.Mac} | vendor={h.Manufacturer} | shares={string.Join("+", h.Shares.Select(s => s.Name))} | " +
            string.Join(", ", h.Services.Select(s => $"{s.Kind}:{s.Detail}")) + " | ports: " + string.Join(", ", h.OpenPorts.Select(p => $"{p.Label}={p.State}:{p.Detail}"))));
        window.Close();
    }
}
