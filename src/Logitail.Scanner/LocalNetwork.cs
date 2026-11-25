using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Logitail.Scanner;

/// <summary>This machine's IPv4 networks, used for the default scan range.</summary>
public sealed record LocalSubnet(string AdapterName, IPAddress Address, int PrefixLength)
{
    /// <summary>The subnet as text, e.g. <c>192.168.1.1-254</c>. Very large subnets are narrowed to the address's /24.</summary>
    public string ToRangeText()
    {
        var bits = Math.Max(PrefixLength, 24);
        var ip = IpRangeParser.ToUInt(Address);
        var mask = uint.MaxValue << (32 - bits);
        var first = (ip & mask) + 1;
        var last = (ip | ~mask) - 1;
        return bits == 24
            ? $"{IpRangeParser.ToAddress(first)}-{last & 0xFF}"
            : $"{IpRangeParser.ToAddress(first)}-{IpRangeParser.ToAddress(last)}";
    }
}

public static class LocalNetwork
{
    /// <summary>Up, non-loopback adapters with an IPv4 address and a gateway first.</summary>
    public static IReadOnlyList<LocalSubnet> Subnets()
    {
        var result = new List<(LocalSubnet Subnet, bool HasGateway)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = u.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // no DHCP lease (APIPA)
                result.Add((new LocalSubnet(nic.Name, u.Address, u.PrefixLength), hasGateway));
            }
        }
        return result.OrderByDescending(r => r.HasGateway).Select(r => r.Subnet).ToList();
    }

    private static (DateTime At, IReadOnlyList<LocalSubnet> Subnets) _cache = (DateTime.MinValue, []);

    /// <summary>True when the address is on one of this machine's subnets. Adapter info is cached for 30 seconds.</summary>
    public static bool IsLocal(IPAddress address)
    {
        var cache = _cache;
        if (DateTime.UtcNow - cache.At > TimeSpan.FromSeconds(30)) _cache = cache = (DateTime.UtcNow, Subnets());
        var ip = IpRangeParser.ToUInt(address);
        return cache.Subnets.Any(s =>
        {
            var mask = s.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - s.PrefixLength);
            return (IpRangeParser.ToUInt(s.Address) & mask) == (ip & mask);
        });
    }
}
