using System.Net;
using Logictail.Scanner;

namespace Logictail.Tests;

public sealed class HostStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logictail-hosts-").FullName;
    private string PathTo => Path.Combine(_dir, "hosts.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HostResult Host(string ip, string? mac = null) =>
        new() { Address = IPAddress.Parse(ip), Status = HostStatus.Alive, Mac = mac, Name = "dns-name" };

    [Fact]
    public void Notes_survive_reload_and_follow_the_mac_across_addresses()
    {
        var store = HostStore.Load(PathTo);
        store.Set(Host("10.0.0.5", "00-11-22-33-44-55"), new HostNote { Name = "Office printer", Comment = "2nd floor", Favorite = true });

        var reloaded = HostStore.Load(PathTo);
        var moved = reloaded.Apply(Host("10.0.0.77", "00-11-22-33-44-55"), OuiDatabase.Empty); // DHCP gave it a new address
        Assert.Equal("Office printer", moved.Name);
        Assert.Equal("2nd floor", moved.Comment);
        Assert.True(moved.IsFavorite);
        Assert.Equal(["10.0.0.77"], HostStore.Load(PathTo).FavoriteAddresses());
    }

    [Fact]
    public void Hosts_without_mac_are_keyed_by_ip_and_user_mac_is_used()
    {
        var store = HostStore.InMemory();
        store.Set(Host("192.168.5.9"), new HostNote { Mac = "AA-BB-CC-DD-EE-FF" });
        var applied = store.Apply(Host("192.168.5.9"), OuiDatabase.Empty);
        Assert.Equal("AA-BB-CC-DD-EE-FF", applied.Mac);
        Assert.Equal("dns-name", applied.Name); // no user name: keep the scanned one
    }

    [Fact]
    public void Switched_off_favorite_keeps_its_notes_and_mac()
    {
        var store = HostStore.Load(PathTo);
        store.Set(Host("10.0.0.5", "00-11-22-33-44-55"), new HostNote { Name = "Desk PC", Favorite = true });

        var off = new HostResult { Address = IPAddress.Parse("10.0.0.5"), Status = HostStatus.Dead }; // no MAC when off
        var applied = HostStore.Load(PathTo).Apply(off, OuiDatabase.Empty);
        Assert.True(applied.IsFavorite);
        Assert.Equal("Desk PC", applied.Name);
        Assert.Equal("00-11-22-33-44-55", applied.Mac); // so Wake-on-LAN still works
    }

    [Fact]
    public void Typed_mac_then_favorite_keeps_one_note()
    {
        var store = HostStore.InMemory();
        var raw = Host("192.168.9.9"); // off-subnet: scan has no MAC
        store.Set(raw, new HostNote { Mac = "AA-BB-CC-00-11-22" });
        store.Set(raw, store.Get(raw)! with { Favorite = true });
        var applied = store.Apply(raw, OuiDatabase.Empty);
        Assert.Single(store.All);
        Assert.True(applied.IsFavorite);
        Assert.Equal("AA-BB-CC-00-11-22", applied.Mac);
    }

    [Fact]
    public void Empty_notes_are_removed_and_clear_deletes_everything()
    {
        var store = HostStore.Load(PathTo);
        var host = Host("10.0.0.1", "00-00-00-00-00-01");
        store.Set(host, new HostNote { Comment = "x" });
        store.Set(host, new HostNote());
        Assert.Empty(store.All);

        store.Set(host, new HostNote { Comment = "y" });
        store.Clear();
        Assert.Empty(HostStore.Load(PathTo).All);
    }

    [Fact]
    public void Corrupt_file_is_kept_aside()
    {
        File.WriteAllText(PathTo, "{ broken");
        Assert.Empty(HostStore.Load(PathTo).All);
        Assert.True(File.Exists(PathTo + ".broken"));
    }
}

public sealed class ExportTests
{
    private static readonly HostResult Evil = new()
    {
        Address = IPAddress.Parse("10.0.0.9"),
        Status = HostStatus.Alive,
        Name = "<script>alert(1)</script>",
        Comment = "=HYPERLINK(\"http://x\")",
        Services = [new FoundService(ServiceKind.Http, "Login, \"admin\"")],
    };

    [Fact]
    public void Csv_quotes_and_neutralises_formulas()
    {
        var csv = Export.ToCsv([Evil]).Split(Environment.NewLine);
        Assert.StartsWith("Status,Name,IP", csv[0]);
        Assert.Contains("\"HTTP (Login, \"\"admin\"\")\"", csv[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv[1]);
    }

    [Fact]
    public void Csv_with_semicolons()
    {
        var csv = Export.ToCsv([Evil], ';').Split(Environment.NewLine);
        Assert.StartsWith("Status;Name;IP", csv[0]);
        Assert.Contains("HTTP (Login, \"\"admin\"\")", csv[1]); // commas don't need quoting now, quotes still do
    }

    [Fact]
    public void Html_encodes_everything()
    {
        var html = Export.ToHtml([Evil], "Scan <1>");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<title>Scan &lt;1&gt;</title>", html);
    }
}

public sealed class WakeOnLanTests
{
    [Fact]
    public void Magic_packet_layout()
    {
        var p = WakeOnLan.MagicPacket("00:11:22:33:44:55");
        Assert.Equal(102, p.Length);
        Assert.All(p[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, p[(6 + i * 6)..(12 + i * 6)]);
    }

    [Fact]
    public void Rejects_bad_mac() => Assert.Throws<ArgumentException>(() => WakeOnLan.MagicPacket("00-11-22"));

    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.255")]
    [InlineData("172.28.80.1", 20, "172.28.95.255")]
    [InlineData("10.0.0.5", 8, "10.255.255.255")]
    public void Subnet_broadcast(string ip, int prefix, string expected) =>
        Assert.Equal(expected, WakeOnLan.Broadcast(IPAddress.Parse(ip), prefix).ToString());

    /// <summary>Opt-in: proves NetShareEnum works and parses real rows, against this PC only.</summary>
    [Fact]
    public void Lists_this_pcs_hidden_shares_when_live()
    {
        if (Environment.GetEnvironmentVariable("LOGICTAIL_LIVE") != "1") return;
        var shares = SmbShares.List(IPAddress.Loopback, includeHidden: true);
        Assert.Contains(shares, s => s.Name == "IPC$");
    }
}

public sealed class DeviceTypeTests
{
    private static HostResult Host(string? vendor, params ServiceKind[] services) => new()
    {
        Address = IPAddress.Parse("10.0.0.1"), Status = HostStatus.Alive, Manufacturer = vendor,
        Services = services.Select(s => new FoundService(s, null)).ToList(),
    };

    [Theory]
    [InlineData("Synology Incorporated", DeviceType.Nas)]
    [InlineData("TP-LINK TECHNOLOGIES CO.,LTD.", DeviceType.Router)]
    [InlineData("Brother Industries, LTD.", DeviceType.Printer)]
    [InlineData("Apple, Inc.", DeviceType.Phone)]
    [InlineData("Nintendo Co.,Ltd", DeviceType.GameConsole)]
    [InlineData(null, DeviceType.Unknown)]
    public void Guesses_from_vendor(string? vendor, DeviceType expected) => Assert.Equal(expected, DeviceTypes.Guess(Host(vendor)));

    [Fact]
    public void Services_beat_the_vendor()
    {
        Assert.Equal(DeviceType.Computer, DeviceTypes.Guess(Host("Apple, Inc.", ServiceKind.Smb)));   // a Mac, not a phone
        Assert.Equal(DeviceType.Computer, DeviceTypes.Guess(Host("Hewlett Packard", ServiceKind.Smb))); // an HP laptop, not a printer
        Assert.Equal(DeviceType.Computer, DeviceTypes.Guess(Host("Synology Incorporated", ServiceKind.Rdp)));
        var printer = Host(null) with { Shares = [new SharedResource("Laser", ShareKind.Printer, null)] };
        Assert.Equal(DeviceType.Printer, DeviceTypes.Guess(printer));
        var nasSharingPrinter = Host("Synology Incorporated", ServiceKind.Smb) with
        {
            Shares = [new SharedResource("Media", ShareKind.Folder, null), new SharedResource("Laser", ShareKind.Printer, null)],
        };
        Assert.Equal(DeviceType.Nas, DeviceTypes.Guess(nasSharingPrinter));
    }

    [Fact]
    public void Dead_hosts_have_no_type() =>
        Assert.Equal(DeviceType.Unknown, DeviceTypes.Guess(Host("Synology") with { Status = HostStatus.Dead }));
}

public sealed class HostActionTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.1.20");

    [Fact]
    public void Commands_use_only_the_ip()
    {
        Assert.Equal("mstsc.exe", HostActions.Command(HostAction.Rdp, Ip).FileName);
        Assert.Equal(["/v:192.168.1.20"], HostActions.Command(HostAction.Rdp, Ip).ArgumentList);
        Assert.Equal("/k ping 192.168.1.20", HostActions.Command(HostAction.Ping, Ip).Arguments);
        Assert.Equal("https://192.168.1.20/", HostActions.Command(HostAction.OpenHttps, Ip).FileName);
        Assert.Equal([@"\\192.168.1.20\Media"], HostActions.Command(HostAction.Explore, Ip, "Media").ArgumentList);
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData("a\" & calc")]
    [InlineData("x/y")]
    public void Hostile_share_names_are_refused(string share) =>
        Assert.Throws<ArgumentException>(() => HostActions.Command(HostAction.Explore, Ip, share));

    [Fact]
    public void Actions_apply_only_to_found_services()
    {
        var host = new HostResult { Address = Ip, Status = HostStatus.Alive, Services = [new FoundService(ServiceKind.Rdp, null)] };
        Assert.True(HostActions.Applies(HostAction.Rdp, host));
        Assert.False(HostActions.Applies(HostAction.OpenHttp, host));
        Assert.True(HostActions.Applies(HostAction.Ping, host));
    }

    [Fact]
    public void Share_classification_hides_admin_shares()
    {
        Assert.Equal(ShareKind.Folder, SmbShares.Classify("Media", 0));
        Assert.Equal(ShareKind.Printer, SmbShares.Classify("Laser", 1));
        Assert.Null(SmbShares.Classify("C$", 0x80000000));
        Assert.Null(SmbShares.Classify("ADMIN$", 0));
        Assert.Null(SmbShares.Classify("IPC$", 3));
    }
}
