using System.Net;
using System.Net.Sockets;
using System.Text;
using Logictail.Scanner;

namespace Logictail.Tests;

public sealed class PortListParserTests
{
    [Fact]
    public void Mixed_list_with_protocols()
    {
        var r = PortListParser.Parse("80, 443, 8000-8002, UDP: 53, 161, TCP: 22");
        Assert.Empty(r.Errors);
        Assert.Equal([22, 80, 443, 8000, 8001, 8002], r.Ports.Tcp);
        Assert.Equal([53, 161], r.Ports.Udp);
    }

    [Theory]
    [InlineData("1,2,UDP:1-10", 2, 10)]   // the original's console example
    [InlineData("u:53", 0, 1)]
    [InlineData("80,80,79-81", 3, 0)]     // duplicates removed
    [InlineData("1-65535", 65535, 0)]
    public void Counts(string text, int tcp, int udp)
    {
        var r = PortListParser.Parse(text);
        Assert.Empty(r.Errors);
        Assert.Equal(tcp, r.Ports.Tcp.Count);
        Assert.Equal(udp, r.Ports.Udp.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("100-50")]
    [InlineData("http")]
    [InlineData("ICMP: 1")]
    public void Rejects(string text) => Assert.NotEmpty(PortListParser.Parse(text).Errors);

    [Fact]
    public void Presets_parse()
    {
        Assert.Empty(PortListParser.Parse(PortList.Common).Errors);
        Assert.Equal(1023, PortListParser.Parse(PortList.WellKnown).Ports.Tcp.Count);
    }
}

public sealed class PortScannerTests
{
    /// <summary>Host with TCP 22 and 80 open, UDP 53 answering, UDP 161 silent, everything else closed.</summary>
    private sealed class FakeHost : IProbes
    {
        public int InFlight, MaxInFlight;

        public async Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref InFlight);
            int seen;
            while ((seen = MaxInFlight) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            await Task.Delay(1, ct);
            Interlocked.Decrement(ref InFlight);
            return port is 22 or 80;
        }

        public Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) =>
            port == 22 ? Task.FromResult<string?>("SSH-2.0-OpenSSH_9.6") : throw new IOException("reset");

        public Task<UdpResult> UdpProbeAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(port switch { 53 => UdpResult.Open, 161 => UdpResult.OpenOrFiltered, _ => UdpResult.Closed });

        public Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult(true);
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
    }

    [Fact]
    public async Task Finds_open_ports_with_names_and_versions()
    {
        var ports = PortListParser.Parse("1-100, UDP: 53, 161, 162").Ports;
        var found = await new PortScanner(new FakeHost()).ScanAsync(IPAddress.Loopback, ports, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(["22/tcp", "80/tcp", "53/udp", "161/udp"], found.Select(p => p.Label));
        Assert.Equal(("SSH", "SSH-2.0-OpenSSH_9.6"), (found[0].Service, found[0].Detail));
        Assert.Null(found[1].Detail);                                 // banner failed: still reported open
        Assert.Equal(PortState.Open, found[2].State);
        Assert.Equal(PortState.OpenOrFiltered, found[3].State);
    }

    [Fact]
    public async Task Caps_connections_in_flight()
    {
        var host = new FakeHost();
        await new PortScanner(host, maxInFlight: 16).ScanAsync(IPAddress.Loopback, PortListParser.Parse("1-2000").Ports, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.InRange(host.MaxInFlight, 2, 16);
    }

    [Fact]
    public async Task Silent_udp_ports_are_kept_but_not_exported_as_open()
    {
        var results = new List<HostResult>();
        var options = new ScanOptions { Ports = PortListParser.Parse("UDP: 161-170").Ports }; // 161 silent, rest closed
        await new ScanEngine(new FakeHost(), OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.0.0.1").Targets, options, results.Add, null, null, CancellationToken.None);
        var port = results.Last().OpenPorts.Single();
        Assert.False(port.Confirmed);
        Assert.DoesNotContain("161/udp", Export.ToCsv(results));
    }

    [Fact]
    public async Task All_ports_on_many_hosts_uses_steady_memory_and_stops_fast()
    {
        var all = PortListParser.Parse(PortList.AllTcp).Ports;
        var options = new ScanOptions { Ports = all, CheckServices = false };
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        using var cts = new CancellationTokenSource();
        var scan = new ScanEngine(new FakeHost(), OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.9.0.1-64").Targets, options, _ => { }, null, null, cts.Token);
        await Task.Delay(1500);
        var during = GC.GetTotalMemory(false);
        var stop = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        stop.Stop();
        Assert.True(during - before < 200L << 20, $"memory grew {(during - before) >> 20} MB");
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(3), $"stop took {stop.Elapsed}");
    }

    [Fact]
    public async Task Host_is_reported_before_its_ports_then_again_with_them()
    {
        var reports = new List<HostResult>();
        var progress = new List<ScanProgress>();
        var options = new ScanOptions { Ports = PortListParser.Parse("1-1000").Ports };
        await new ScanEngine(new FakeHost(), OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.0.0.1").Targets, options, r => { lock (reports) reports.Add(r); }, p => { lock (progress) progress.Add(p); }, null, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Empty(reports[0].OpenPorts);
        Assert.Equal(["22/tcp", "80/tcp"], reports[1].OpenPorts.Select(p => p.Label));
        Assert.Equal(1000, progress.Max(p => p.PortsChecked));
    }

    [Fact]
    public void Local_addresses_get_the_short_timeout()
    {
        var options = new ScanOptions { IsLocal = ip => ip.ToString().StartsWith("192.168.1.") };
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.PortTimeoutFor(IPAddress.Parse("192.168.1.20")));
        Assert.Equal(options.PortTimeout, options.PortTimeoutFor(IPAddress.Parse("8.8.8.8")));
        var accurate = options with { Speed = ScanSpeed.Accurate };
        Assert.Equal(TimeSpan.FromMilliseconds(500), accurate.PortTimeoutFor(IPAddress.Parse("192.168.1.20")));
    }

    [Fact]
    public void Timeout_adapts_to_ping_time_within_limits()
    {
        var options = new ScanOptions { IsLocal = _ => true };
        var ip = IPAddress.Parse("192.168.1.20");
        Assert.Equal(ScanOptions.MinPortTimeout, options.PortTimeoutFor(ip, TimeSpan.FromMilliseconds(1)));   // 34 ms -> floor 50
        Assert.Equal(TimeSpan.FromMilliseconds(110), options.PortTimeoutFor(ip, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.PortTimeoutFor(ip, TimeSpan.FromMilliseconds(300))); // capped
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.PortTimeoutFor(ip, null));                           // no ping reply
    }

    [Fact]
    public async Task Engine_port_scans_live_hosts_when_asked()
    {
        var results = new List<HostResult>();
        var options = new ScanOptions { Ports = PortListParser.Parse("20-25").Ports };
        await new ScanEngine(new FakeHost(), OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.0.0.1").Targets, options, results.Add, null, null, CancellationToken.None);
        Assert.Equal(["22/tcp"], results.Last().OpenPorts.Select(p => p.Label));
        Assert.Contains("22/tcp (SSH-2.0-OpenSSH_9.6)", Export.ToCsv(results));
    }
}

/// <summary>Real TCP and UDP checks, only against listeners on 127.0.0.1 inside the test.</summary>
public sealed class PortProbeLoopbackTests
{
    [Fact]
    public async Task Tcp_port_with_banner_is_found()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++) // connect check, then banner read
            {
                using var c = await listener.AcceptTcpClientAsync();
                await c.GetStream().WriteAsync("220 test-ftpd ready\r\n"u8.ToArray());
                await Task.Delay(50);
            }
            listener.Stop();
        });

        var found = await new PortScanner(new SystemProbes()).ScanAsync(
            IPAddress.Loopback, new PortList([port], []), TimeSpan.FromSeconds(2), CancellationToken.None);
        await server;
        Assert.Equal("220 test-ftpd ready", found.Single().Detail);
    }

    [Fact]
    public async Task Udp_open_closed_and_silent()
    {
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var openPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        var server = Task.Run(async () =>
        {
            var r = await echo.ReceiveAsync();
            await echo.SendAsync(Encoding.ASCII.GetBytes("pong"), r.RemoteEndPoint);
        });
        var probes = new SystemProbes();
        Assert.Equal(UdpResult.Open, await probes.UdpProbeAsync(IPAddress.Loopback, openPort, TimeSpan.FromSeconds(2), CancellationToken.None));
        await server;

        // A port nothing listens on: Windows reports it unreachable on loopback.
        int closedPort;
        using (var temp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) closedPort = ((IPEndPoint)temp.Client.LocalEndPoint!).Port;
        Assert.Equal(UdpResult.Closed, await probes.UdpProbeAsync(IPAddress.Loopback, closedPort, TimeSpan.FromSeconds(2), CancellationToken.None));

        // Listening but never answering: open or filtered.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var silentPort = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        Assert.Equal(UdpResult.OpenOrFiltered, await probes.UdpProbeAsync(IPAddress.Loopback, silentPort, TimeSpan.FromMilliseconds(400), CancellationToken.None));
    }

    [Fact]
    public void Dns_and_ntp_payloads_are_well_formed()
    {
        var dns = SystemProbes.UdpPayload(53);
        Assert.Equal(17, dns.Length);
        Assert.Equal(1, dns[5]);                        // one question
        Assert.Equal(48, SystemProbes.UdpPayload(123).Length);
        Assert.Empty(SystemProbes.UdpPayload(9999));
    }
}
