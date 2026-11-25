using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Logitail.Scanner;

namespace Logitail.Tests;

/// <summary>Real probes, but only against listeners on 127.0.0.1 inside the test.</summary>
public sealed class ProbeTests
{
    private static async Task<(int Port, Task Server)> Serve(string reply, bool readRequestFirst)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            if (readRequestFirst)
            {
                var buffer = new byte[4096];
                var request = "";
                while (!request.Contains("\r\n\r\n")) request += Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply));
            await stream.FlushAsync();
            await Task.Delay(100);
            listener.Stop();
        });
        await Task.Yield();
        return (port, server);
    }

    private static string Http(string status, string body, string extraHeaders = "") =>
        $"HTTP/1.1 {status}\r\nContent-Type: text/html\r\n{extraHeaders}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    [Fact]
    public async Task Reads_and_cleans_the_page_title()
    {
        var (port, server) = await Serve(Http("200 OK", "<html><head><TITLE>\n  My &amp; Router\n </TITLE></head></html>"), true);
        var title = await SystemProbes.HttpTitleAsync($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("My & Router", title);
        await server;
    }

    [Fact]
    public async Task Reports_redirects_instead_of_following_them()
    {
        var (port, server) = await Serve(Http("302 Found", "", "Location: http://elsewhere.example/login\r\n"), true);
        var title = await SystemProbes.HttpTitleAsync($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Redirects to http://elsewhere.example/login", title);
        await server;
    }

    [Fact]
    public async Task Page_without_title_shows_the_status()
    {
        var (port, server) = await Serve(Http("404 Not Found", "nope"), true);
        Assert.Equal("HTTP 404", await SystemProbes.HttpTitleAsync($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Reads_a_service_banner()
    {
        var (port, server) = await Serve("SSH-2.0-OpenSSH_9.6\r\nignored second line\r\n", false);
        Assert.Equal("SSH-2.0-OpenSSH_9.6", await SystemProbes.BannerAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Tcp_check_sees_open_and_closed_ports()
    {
        var (port, server) = await Serve("x", false);
        var probes = new SystemProbes();
        Assert.True(await probes.TcpOpenAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(2), CancellationToken.None));
        await server;
        Assert.False(await probes.TcpOpenAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task Arp_is_skipped_for_addresses_off_the_local_subnet()
    {
        // 203.0.113.0/24 is reserved for documentation, so it is never a local subnet; no packet is sent.
        var started = DateTime.UtcNow;
        Assert.Null(await new SystemProbes().ArpAsync(IPAddress.Parse("203.0.113.7"), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(500));
    }
}

public sealed class ScanEngineFailureTests
{
    /// <summary>Everything answers; <see cref="Throw"/> decides which calls blow up.</summary>
    private sealed class ThrowingProbes(Func<IPAddress, string, CancellationToken, bool> @throw) : IProbes
    {
        private void Maybe(IPAddress ip, string probe, CancellationToken ct)
        {
            if (@throw(ip, probe, ct)) throw new InvalidOperationException($"{probe} broke");
        }

        public async Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct) { await Task.Delay(2, ct); Maybe(ip, "ping", ct); return true; }
        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) { Maybe(ip, "dns", ct); return Task.FromResult<string?>(null); }
        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<NetBiosInfo?>(null);
        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) { Maybe(ip, "tcp", ct); return Task.FromResult(port == 80); }
        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) { Maybe(ip, "detail", ct); return Task.FromResult<string?>("ok"); }
        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);
    }

    private static int Last(IPAddress ip) => ip.GetAddressBytes()[3];

    [Theory]
    [InlineData("ping")]
    [InlineData("tcp")]
    [InlineData("dns")]
    [InlineData("detail")]
    public async Task A_failing_probe_makes_that_host_unknown_and_the_scan_finishes(string broken)
    {
        var results = new ConcurrentBag<HostResult>();
        var probes = new ThrowingProbes((ip, probe, _) => Last(ip) == 3 && probe == broken);
        await new ScanEngine(probes, OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.0.0.1-5").Targets, new ScanOptions(), results.Add, null, null, CancellationToken.None);

        Assert.Equal(5, results.Count);
        var host3 = results.Single(r => Last(r.Address) == 3);
        Assert.Equal(HostStatus.Unknown, host3.Status);
        Assert.Contains("broke", host3.Error);
        Assert.All(results.Where(r => Last(r.Address) != 3), r => Assert.Equal(HostStatus.Alive, r.Status));
    }

    [Fact]
    public async Task Errors_after_stop_end_as_a_clean_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var probes = new ThrowingProbes((_, _, ct) => ct.IsCancellationRequested);
        var scan = new ScanEngine(probes, OuiDatabase.Empty).ScanAsync(
            IpRangeParser.Parse("10.0.3.1-200").Targets, new ScanOptions { Threads = 4 }, _ => { }, null, null, cts.Token);
        await Task.Delay(40);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }
}
