using System.Net;
using System.Net.Sockets;

namespace Logitail.Scanner;

public enum ScanSpeed
{
    /// <summary>Short timeouts; hosts that don't answer ping or ARP are reported dead without port checks.</summary>
    Fast,
    /// <summary>Longer timeouts; also tries service ports on hosts that don't answer ping (finds firewalled machines).</summary>
    Accurate,
}

public sealed record ScanOptions
{
    public ScanSpeed Speed { get; init; } = ScanSpeed.Fast;
    public int Threads { get; init; } = 64;
    public bool CheckServices { get; init; } = true;
    /// <summary>
    /// List shared folders on hosts with SMB open. Off by default: Windows signs in to each such device
    /// with the current user's credentials to list them.
    /// </summary>
    public bool ListShares { get; init; }

    public TimeSpan PingTimeout => Speed == ScanSpeed.Fast ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromMilliseconds(1500);
    public TimeSpan PortTimeout => Speed == ScanSpeed.Fast ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(1500);
    public TimeSpan LookupTimeout => TimeSpan.FromMilliseconds(1500);
}

public sealed record ScanProgress(int Done, int Total, int Alive, int Dead, int Unknown)
{
    public double Percent => Total == 0 ? 100 : Done * 100.0 / Total;
}

/// <summary>Lets the UI pause a running scan; workers wait before starting each new address.</summary>
public sealed class PauseGate
{
    private volatile TaskCompletionSource _resumed = CompletedSource();

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _resumed.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct) => _resumed.Task.WaitAsync(ct);

    private static TaskCompletionSource CompletedSource()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}

/// <summary>Scans addresses in parallel and reports each host as soon as it is done.</summary>
public sealed class ScanEngine(IProbes probes, OuiDatabase vendors)
{
    /// <summary>
    /// Resolves host names, then scans every address. <paramref name="onResult"/> and <paramref name="onProgress"/>
    /// are called from worker threads. Cancelling stops new work; hosts already finished stay reported.
    /// </summary>
    public async Task ScanAsync(
        IReadOnlyList<ScanTarget> targets,
        ScanOptions options,
        Action<HostResult> onResult,
        Action<ScanProgress>? onProgress,
        PauseGate? pause,
        CancellationToken ct)
    {
        var addresses = IpRangeParser.Addresses(targets).ToList();
        var known = addresses.Select(IpRangeParser.ToUInt).ToHashSet();
        foreach (var host in targets.OfType<ScanTarget.HostName>())
        {
            foreach (var ip in await ResolveAsync(host.Name, ct))
                if (known.Add(IpRangeParser.ToUInt(ip))) addresses.Add(ip);
        }

        int done = 0, alive = 0, dead = 0, unknown = 0;
        onProgress?.Invoke(new ScanProgress(0, addresses.Count, 0, 0, 0));

        await Parallel.ForEachAsync(addresses,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads), CancellationToken = ct },
            async (ip, token) =>
            {
                if (pause is not null) await pause.WaitAsync(token);
                var result = await ScanHostAsync(ip, options, token);
                onResult(result);
                _ = result.Status switch
                {
                    HostStatus.Alive => Interlocked.Increment(ref alive),
                    HostStatus.Dead => Interlocked.Increment(ref dead),
                    _ => Interlocked.Increment(ref unknown),
                };
                var d = Interlocked.Increment(ref done);
                onProgress?.Invoke(new ScanProgress(d, addresses.Count, Volatile.Read(ref alive), Volatile.Read(ref dead), Volatile.Read(ref unknown)));
            });
    }

    /// <summary>Scans one address. A probe that fails unexpectedly makes the host Unknown instead of ending the scan.</summary>
    public async Task<HostResult> ScanHostAsync(IPAddress ip, ScanOptions options, CancellationToken ct)
    {
        try
        {
            return await ScanHostCoreAsync(ip, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HostResult { Address = ip, Status = HostStatus.Unknown, Error = ex.Message };
        }
    }

    private async Task<HostResult> ScanHostCoreAsync(IPAddress ip, ScanOptions options, CancellationToken ct)
    {
        bool pinged;
        string? arpMac;
        try
        {
            var ping = probes.PingAsync(ip, options.PingTimeout, ct);
            var arp = probes.ArpAsync(ip, ct);
            await Task.WhenAll(ping, arp);
            pinged = ping.Result;
            arpMac = arp.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The probe itself failed (e.g. no permission, adapter down): we can't say dead or alive.
            return new HostResult { Address = ip, Status = HostStatus.Unknown, Error = ex.Message };
        }

        var answered = pinged || arpMac is not null;
        var services = new List<FoundService>();
        if (options.CheckServices && (answered || options.Speed == ScanSpeed.Accurate))
            services = await ServicesAsync(ip, options, ct);

        if (!answered && services.Count == 0)
            return new HostResult { Address = ip, Status = HostStatus.Dead };

        var dns = probes.ReverseDnsAsync(ip, options.LookupTimeout, ct);
        var netbios = probes.NetBiosAsync(ip, options.LookupTimeout, ct);
        var shares = options.ListShares && services.Any(s => s.Kind == ServiceKind.Smb)
            ? probes.SharesAsync(ip, options.LookupTimeout * 2, ct)
            : Task.FromResult<IReadOnlyList<SharedResource>>([]);
        await Task.WhenAll(dns, netbios, shares);
        var nb = netbios.Result;
        var mac = arpMac ?? nb?.Mac;

        var host = new HostResult
        {
            Address = ip,
            Status = HostStatus.Alive,
            Name = ShortName(dns.Result) ?? nb?.ComputerName,
            NetBiosName = nb?.ComputerName,
            Workgroup = nb?.Workgroup,
            User = nb?.UserName,
            Mac = mac,
            Manufacturer = vendors.Lookup(mac),
            Services = services,
            Shares = shares.Result,
        };
        return host with { DeviceType = DeviceTypes.Guess(host) };
    }

    private async Task<List<FoundService>> ServicesAsync(IPAddress ip, ScanOptions options, CancellationToken ct)
    {
        var checks = Services.All.Select(async kind =>
        {
            if (!await probes.TcpOpenAsync(ip, Services.Port(kind), options.PortTimeout, ct)) return null;
            var detail = await probes.ServiceDetailAsync(ip, kind, options.LookupTimeout, ct);
            return new FoundService(kind, detail);
        });
        return (await Task.WhenAll(checks)).OfType<FoundService>().ToList();
    }

    private static string? ShortName(string? fqdn) => string.IsNullOrWhiteSpace(fqdn) ? null : fqdn;

    private static async Task<IPAddress[]> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, ct);
            return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
