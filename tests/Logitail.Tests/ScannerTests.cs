using System.Collections.Concurrent;
using System.Net;
using Logitail.Scanner;

namespace Logitail.Tests;

public sealed class IpRangeParserTests
{
    private static long Count(string text) => IpRangeParser.Parse(text).AddressCount;

    [Theory]
    [InlineData("192.168.0.1", 1)]
    [InlineData("192.168.0.1-100", 100)]
    [InlineData("192.168.0.1-192.168.1.1", 257)]
    [InlineData("10.0.0.0/24", 254)]
    [InlineData("10.0.0.5/32", 1)]
    [InlineData("10.0.0.4/31", 2)]
    [InlineData("192.168.0.1-100, 192.168.0.200", 101)]
    [InlineData("10.0.0.1 10.0.0.2;10.0.0.3\n10.0.0.4", 4)]
    public void Counts_addresses(string text, long expected)
    {
        var result = IpRangeParser.Parse(text);
        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.AddressCount);
    }

    [Theory]
    [InlineData("192.168.0.300")]
    [InlineData("192.168.0.50-10")]
    [InlineData("10.0.0.0/33")]
    [InlineData("1.2.3")]
    [InlineData("not a host!")]
    public void Reports_bad_items(string text) => Assert.NotEmpty(IpRangeParser.Parse(text).Errors);

    [Fact]
    public void Errors_name_the_line_in_multi_line_input()
    {
        var result = IpRangeParser.Parse("10.0.0.1\n# comment\n10.0.0.999");
        Assert.Equal(["Line 3: \"10.0.0.999\" is not an IP address, range or host name"], result.Errors);
    }

    [Fact]
    public void Host_names_are_targets()
    {
        var result = IpRangeParser.Parse("printer.local, 10.0.0.1");
        Assert.Empty(result.Errors);
        Assert.Contains(result.Targets, t => t is ScanTarget.HostName { Name: "printer.local" });
    }

    [Fact]
    public void Huge_ranges_are_refused()
    {
        Assert.NotEmpty(IpRangeParser.Parse("10.0.0.0/8").Errors);
        Assert.Empty(IpRangeParser.Parse("10.0.0.0/16").Errors);
    }

    [Fact]
    public void Addresses_are_in_order_without_duplicates()
    {
        var addresses = IpRangeParser.Addresses(IpRangeParser.Parse("10.0.0.3, 10.0.0.1-3").Targets).Select(a => a.ToString());
        Assert.Equal(["10.0.0.3", "10.0.0.1", "10.0.0.2"], addresses);
    }

    [Theory]
    [InlineData("192.168.1.37", 24, "192.168.1.1-254")]
    [InlineData("10.1.2.3", 16, "10.1.2.1-254")]        // big subnets narrowed to the /24
    [InlineData("10.1.2.3", 28, "10.1.2.1-10.1.2.14")]
    public void Local_subnet_range_text(string ip, int prefix, string expected) =>
        Assert.Equal(expected, new LocalSubnet("eth", IPAddress.Parse(ip), prefix).ToRangeText());
}

public sealed class NetBiosTests
{
    [Fact]
    public void Query_has_the_rfc1002_layout()
    {
        var q = NetBios.BuildQuery(0x1234);
        Assert.Equal(50, q.Length);
        Assert.Equal([0x12, 0x34], q[..2]);
        Assert.Equal(0x20, q[12]);
        Assert.Equal((byte)'C', q[13]); // '*' = 0x2A -> 'A'+2, 'A'+10
        Assert.Equal((byte)'K', q[14]);
        Assert.Equal([0x00, 0x21, 0x00, 0x01], q[^4..]);
    }

    private static byte[] Response(ushort id, params (string Name, byte Suffix, bool Group)[] names)
    {
        var p = new List<byte> { (byte)(id >> 8), (byte)id, 0x84, 0x00, 0, 0, 0, 1, 0, 0, 0, 0 };
        p.Add(0x20);
        p.AddRange(Enumerable.Repeat((byte)'A', 32));
        p.Add(0);
        p.AddRange([0x00, 0x21, 0x00, 0x01, 0, 0, 0, 0]);
        var rdata = names.Length * 18 + 1 + 6;
        p.AddRange([(byte)(rdata >> 8), (byte)rdata, (byte)names.Length]);
        foreach (var (name, suffix, group) in names)
        {
            p.AddRange(System.Text.Encoding.ASCII.GetBytes(name.PadRight(15)));
            p.Add(suffix);
            p.AddRange([group ? (byte)0x84 : (byte)0x04, 0x00]);
        }
        p.AddRange([0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E]);
        return [.. p];
    }

    [Fact]
    public void Parses_names_workgroup_user_and_mac()
    {
        var data = Response(7, ("DESKTOP-1", 0x00, false), ("WORKGROUP", 0x00, true), ("DESKTOP-1", 0x20, false), ("ALICE", 0x03, false));
        var info = NetBios.ParseResponse(data, 7);
        Assert.Equal(new NetBiosInfo("DESKTOP-1", "WORKGROUP", "ALICE", "00-1A-2B-3C-4D-5E"), info);
    }

    [Fact]
    public void Rejects_wrong_id_and_truncated_packets()
    {
        var data = Response(7, ("PC", 0x00, false));
        Assert.Null(NetBios.ParseResponse(data, 8));
        for (var len = 0; len < data.Length - 6; len += 5)
            Assert.Null(NetBios.ParseResponse(data.AsSpan(0, len), 7));
    }

    [Fact]
    public void Survives_random_garbage()
    {
        var rng = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(0, 300)];
            rng.NextBytes(junk);
            if (junk.Length > 2) { junk[0] = 0; junk[1] = 7; }
            _ = NetBios.ParseResponse(junk, 7); // must not throw
        }
    }
}

public sealed class OuiDatabaseTests
{
    private const string Csv = """
        Registry,Assignment,Organization Name,Organization Address
        MA-L,001A2B,"Acme, Inc.",Somewhere
        MA-L,FCFBFB,Cisco Systems,San Jose
        MA-M,FCFBFB1,Not MA-L,Ignored
        """;

    [Theory]
    [InlineData("00-1A-2B-11-22-33", "Acme, Inc.")]
    [InlineData("fc:fb:fb:00:00:01", "Cisco Systems")]
    [InlineData("AA-BB-CC-00-00-00", null)]
    [InlineData(null, null)]
    public void Looks_up_vendor_by_prefix(string? mac, string? vendor) =>
        Assert.Equal(vendor, OuiDatabase.Parse(new StringReader(Csv)).Lookup(mac));

    [Fact]
    public void Shipped_registry_loads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "oui.csv");
        var db = OuiDatabase.Load(path);
        Assert.True(db.Count > 30_000, $"only {db.Count} vendors");
    }
}

public sealed class ScanEngineTests
{
    /// <summary>Fake network: .1 answers ping with HTTP, .2 only ARP, .3 blocks ping but has RDP open, the rest are silent.</summary>
    private sealed class FakeProbes : IProbes
    {
        public int Concurrent;
        public int MaxConcurrent;
        public ConcurrentBag<string> Pinged { get; } = [];

        private static int Last(IPAddress ip) => ip.GetAddressBytes()[3];

        public async Task<bool> PingAsync(IPAddress ip, TimeSpan t, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            Pinged.Add(ip.ToString());
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref Concurrent);
            return Last(ip) == 1;
        }

        public Task<string?> ArpAsync(IPAddress ip, CancellationToken ct) =>
            Task.FromResult(Last(ip) is 1 or 2 ? $"00-1A-2B-00-00-0{Last(ip)}" : null);

        public Task<string?> ReverseDnsAsync(IPAddress ip, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(Last(ip) == 1 ? "router.lan" : null);

        public Task<NetBiosInfo?> NetBiosAsync(IPAddress ip, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(Last(ip) == 2 ? new NetBiosInfo("NAS", "WORKGROUP", null, null) : null);

        public Task<bool> TcpOpenAsync(IPAddress ip, int port, TimeSpan t, CancellationToken ct) =>
            Task.FromResult((Last(ip), port) is (1, 80) or (3, 3389));

        public Task<string?> ServiceDetailAsync(IPAddress ip, ServiceKind s, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(s == ServiceKind.Http ? "Router login" : null);

        public Task<IReadOnlyList<SharedResource>> SharesAsync(IPAddress ip, TimeSpan t, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedResource>>([]);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private static readonly OuiDatabase Vendors = OuiDatabase.Parse(new StringReader("h\nMA-L,001A2B,Acme,x"));

    private static async Task<List<HostResult>> Scan(string range, ScanOptions options, FakeProbes? probes = null)
    {
        var results = new ConcurrentBag<HostResult>();
        await new ScanEngine(probes ?? new FakeProbes(), Vendors)
            .ScanAsync(IpRangeParser.Parse(range).Targets, options, results.Add, null, null, CancellationToken.None);
        return results.OrderBy(r => r.SortKey).ToList();
    }

    [Fact]
    public async Task Classifies_hosts_and_collects_details()
    {
        var results = await Scan("10.0.0.1-5", new ScanOptions());
        Assert.Equal(5, results.Count);

        var router = results[0];
        Assert.Equal(HostStatus.Alive, router.Status);
        Assert.Equal("router.lan", router.Name);
        Assert.Equal("Acme", router.Manufacturer);
        Assert.Equal([new FoundService(ServiceKind.Http, "Router login")], router.Services);

        var nas = results[1];
        Assert.Equal(HostStatus.Alive, nas.Status);  // ARP only
        Assert.Equal("NAS", nas.Name);
        Assert.Equal("WORKGROUP", nas.Workgroup);

        Assert.Equal(HostStatus.Dead, results[2].Status); // fast mode doesn't port-check silent hosts
        Assert.Equal(HostStatus.Dead, results[4].Status);
    }

    [Fact]
    public async Task Accurate_mode_finds_hosts_that_block_ping()
    {
        var results = await Scan("10.0.0.3", new ScanOptions { Speed = ScanSpeed.Accurate });
        Assert.Equal(HostStatus.Alive, results.Single().Status);
        Assert.Equal(ServiceKind.Rdp, results.Single().Services.Single().Kind);
    }

    [Fact]
    public async Task Respects_the_thread_limit()
    {
        var probes = new FakeProbes();
        await Scan("10.0.1.1-200", new ScanOptions { Threads = 8, CheckServices = false }, probes);
        Assert.Equal(200, probes.Pinged.Count);
        Assert.InRange(probes.MaxConcurrent, 2, 8);
    }

    [Fact]
    public async Task Reports_progress_to_100_percent()
    {
        var progress = new ConcurrentBag<ScanProgress>();
        await new ScanEngine(new FakeProbes(), Vendors).ScanAsync(
            IpRangeParser.Parse("10.0.0.1-10").Targets, new ScanOptions(), _ => { }, progress.Add, null, CancellationToken.None);
        var last = progress.MaxBy(p => p.Done)!;
        Assert.Equal(new ScanProgress(10, 10, 2, 8, 0), last);
    }

    [Fact]
    public async Task Pause_holds_new_work_and_cancel_stops_it()
    {
        var gate = new PauseGate();
        gate.Pause();
        var results = new ConcurrentBag<HostResult>();
        using var cts = new CancellationTokenSource();
        var scan = new ScanEngine(new FakeProbes(), Vendors).ScanAsync(
            IpRangeParser.Parse("10.0.2.1-50").Targets, new ScanOptions { Threads = 2 }, results.Add, null, gate, cts.Token);
        await Task.Delay(150);
        Assert.Empty(results); // nothing runs while paused

        gate.Resume();
        await Task.Delay(30);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.True(results.Count < 50, $"{results.Count} hosts finished after cancel");
    }
}
