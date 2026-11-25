using Logictail.Core.Localization;
using System.Net;
using System.Net.Sockets;

namespace Logictail.Scanner;

/// <summary>Wake-on-LAN "magic packet": 6 x 0xFF then the MAC 16 times, sent as a UDP broadcast.</summary>
public static class WakeOnLan
{
    public static byte[] MagicPacket(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new ArgumentException(L.F("\"{0}\" is not a MAC address", mac), nameof(mac));
        var bytes = Convert.FromHexString(hex);
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>Directed broadcast address of a subnet, e.g. 192.168.1.255 for 192.168.1.20/24.</summary>
    public static IPAddress Broadcast(IPAddress address, int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return IpRangeParser.ToAddress(IpRangeParser.ToUInt(address) | ~mask);
    }

    /// <summary>
    /// Sends the packet out of every local IPv4 adapter (a PC often has several, e.g. Ethernet plus a VM switch),
    /// to both the subnet's broadcast address and 255.255.255.255, on UDP ports 9 and 7.
    /// Returns how many adapters it was sent from.
    /// </summary>
    public static async Task<int> SendAsync(string mac)
    {
        var packet = MagicPacket(mac);
        var sent = 0;
        foreach (var subnet in LocalNetwork.Subnets())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(subnet.Address, 0)) { EnableBroadcast = true };
                foreach (var target in new[] { Broadcast(subnet.Address, subnet.PrefixLength), IPAddress.Broadcast })
                    foreach (var port in new[] { 9, 7 })
                        await udp.SendAsync(packet, new IPEndPoint(target, port));
                sent++;
            }
            catch (SocketException)
            {
                // One adapter failing (e.g. just went down) shouldn't stop the others.
            }
        }
        return sent;
    }
}
