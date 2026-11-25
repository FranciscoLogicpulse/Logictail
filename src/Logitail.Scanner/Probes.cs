using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Logitail.Scanner;

/// <summary>Network probes the scan engine uses. Swappable so the engine can be tested without a network.</summary>
public interface IProbes
{
    Task<bool> PingAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct);
    /// <summary>MAC from an ARP request; only answers for hosts on a local subnet.</summary>
    Task<string?> ArpAsync(IPAddress ip, CancellationToken ct);
    Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct);
    Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct);
    Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct);
    /// <summary>Page title or first banner line for an open service, or null.</summary>
    Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind service, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Real probes using the OS network stack.</summary>
public sealed partial class SystemProbes : IProbes
{
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<bool> PingAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, timeout, cancellationToken: ct);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct)
    {
        // ARP only reaches the local subnet; for anything else SendARP would just block and fail.
        if (!OperatingSystem.IsWindows() || !LocalNetwork.IsLocal(ip)) return Task.FromResult<string?>(null);
        // SendARP blocks for up to a few seconds on no answer. A dedicated thread per call keeps those waits
        // out of the shared thread pool, which log tailing and the UI also use.
        return Task.Factory.StartNew(() =>
        {
            var mac = new byte[6];
            var length = (uint)mac.Length;
            var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            return SendARP(dest, 0, mac, ref length) == 0 && length == 6 ? MacAddress.Format(mac) : null;
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip.ToString(), ct);
            var entry = await lookup.WaitAsync(timeout, ct);
            // Windows echoes the IP back when there is no PTR record.
            return entry.HostName == ip.ToString() ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        try
        {
            await udp.SendAsync(NetBios.BuildQuery(id), new IPEndPoint(ip, NetBios.Port), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                if (!result.RemoteEndPoint.Address.Equals(ip)) continue; // ignore stray packets
                return NetBios.ParseResponse(result.Buffer, id);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out
        }
        catch (SocketException)
        {
            return null; // e.g. ICMP port unreachable
        }
    }

    public async Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind service, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return service switch
            {
                ServiceKind.Http => await HttpTitleAsync($"http://{ip}/", timeout, ct),
                ServiceKind.Http8080 => await HttpTitleAsync($"http://{ip}:8080/", timeout, ct),
                ServiceKind.Https => await HttpTitleAsync($"https://{ip}/", timeout, ct),
                ServiceKind.Ftp or ServiceKind.Ssh => await BannerAsync(ip, Services.Port(service), timeout, ct),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure (timeout, refused, TLS, garbage reply) just means "no detail"; only the user's Stop propagates.
            return null;
        }
    }

    /// <summary>
    /// Page title of one URL. Redirects are not followed (they could point outside the scanned range);
    /// a redirect is reported as its target instead.
    /// </summary>
    public static async Task<string?> HttpTitleAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            return Shorten($"Redirects to {location}");
        var buffer = new byte[64 * 1024];
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cts.Token);
        var html = Encoding.UTF8.GetString(buffer, 0, read);
        var title = TitleRegex().Match(html);
        var text = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : null;
        text = string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)response.StatusCode}" : WhitespaceRegex().Replace(text, " ");
        return Shorten(text);
    }

    private static string Shorten(string text) => text.Length > 120 ? text[..120] + "…" : text;

    /// <summary>Reads the first line a service sends on connect (FTP and SSH greet first).</summary>
    public static async Task<string?> BannerAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(ip, port, cts.Token);
        var buffer = new byte[512];
        var read = await client.GetStream().ReadAsync(buffer, cts.Token);
        var line = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim();
        line = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return line.Length == 0 ? null : line.Length > 120 ? line[..120] + "…" : line;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a redirect could lead outside the scanned range
            UseProxy = false,          // talk to the scanned device directly, never through a corporate proxy
            ConnectTimeout = TimeSpan.FromSeconds(3),
            SslOptions =
            {
                // Devices on a LAN (routers, printers) nearly always use self-signed certificates.
                // We only read the page title and send nothing, so accepting any certificate is acceptable here.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Logitail-Scanner/1.0");
        return client;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
