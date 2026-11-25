using System.Collections.Concurrent;
using System.Net;

namespace Logitail.Scanner;

public enum PortState { Open, OpenOrFiltered }

public enum UdpResult { Open, Closed, OpenOrFiltered }

/// <summary>One port found on a host. <see cref="PortState.OpenOrFiltered"/> is a UDP port that never answered.</summary>
public sealed record OpenPort(PortProtocol Protocol, int Port, PortState State, string? Service, string? Detail)
{
    public string Label => $"{Port}/{(Protocol == PortProtocol.Tcp ? "tcp" : "udp")}";
    /// <summary>True when the port answered; silent UDP ports are only "maybe".</summary>
    public bool Confirmed => State == PortState.Open;
}

/// <summary>
/// Checks a port list on one host. Ports are walked by a fixed number of workers per host (nothing is queued up
/// front), and a limit shared by every host in a scan caps connections in flight, so even "all ports" on a whole
/// subnet uses steady memory and stops promptly.
/// </summary>
// 1024 connections in flight is comfortable for Windows; most of them are waiting on a timeout, not using CPU.
public sealed class PortScanner(IProbes probes, int maxInFlight = 1024, int workersPerHost = 128)
{
    private readonly SemaphoreSlim _inFlight = new(maxInFlight);

    public async Task<IReadOnlyList<OpenPort>> ScanAsync(IPAddress ip, PortList ports, TimeSpan timeout, CancellationToken ct, Action? onPortChecked = null)
    {
        var found = new ConcurrentBag<OpenPort>();
        var work = ports.Tcp.Select(p => (PortProtocol.Tcp, p)).Concat(ports.Udp.Select(p => (PortProtocol.Udp, p)));
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = workersPerHost, CancellationToken = ct }, async (item, token) =>
        {
            await _inFlight.WaitAsync(token);
            try
            {
                var result = item.Item1 == PortProtocol.Tcp
                    ? await TcpAsync(ip, item.p, timeout, token)
                    : await UdpAsync(ip, item.p, timeout, token);
                if (result is not null) found.Add(result);
            }
            finally
            {
                _inFlight.Release();
                onPortChecked?.Invoke();
            }
        });
        return found.OrderBy(p => p.Protocol).ThenBy(p => p.Port).ToList();
    }

    private async Task<OpenPort?> TcpAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (!await probes.TcpOpenAsync(ip, port, timeout, ct)) return null;
        string? detail;
        try { detail = await probes.BannerAsync(ip, port, TimeSpan.FromMilliseconds(Math.Max(timeout.TotalMilliseconds * 2, 800)), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { detail = null; }
        return new OpenPort(PortProtocol.Tcp, port, PortState.Open, PortNames.Name(PortProtocol.Tcp, port), detail);
    }

    private async Task<OpenPort?> UdpAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        UdpResult result;
        try { result = await probes.UdpProbeAsync(ip, port, timeout, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        return result == UdpResult.Closed
            ? null
            : new OpenPort(PortProtocol.Udp, port, result == UdpResult.Open ? PortState.Open : PortState.OpenOrFiltered, PortNames.Name(PortProtocol.Udp, port), null);
    }
}
