using System.Net;
using System.Net.Sockets;

namespace Logictail.Scanner;

/// <summary>One parsed scan target: an IPv4 range, or a host name to resolve.</summary>
public abstract record ScanTarget
{
    public sealed record Range(uint First, uint Last) : ScanTarget
    {
        public long Count => (long)Last - First + 1;
    }

    public sealed record HostName(string Name) : ScanTarget;
}

public sealed record ParseResult(IReadOnlyList<ScanTarget> Targets, IReadOnlyList<string> Errors)
{
    public long AddressCount => Targets.Sum(t => t is ScanTarget.Range r ? r.Count : 1);
}

/// <summary>
/// Parses scan input such as <c>192.168.0.1-100, 192.168.0.200</c>, <c>10.0.0.1-10.0.1.20</c>,
/// <c>10.0.0.0/24</c> or host names. Items are separated by commas, semicolons, spaces or new lines.
/// </summary>
public static class IpRangeParser
{
    /// <summary>Safety cap so a typo like 10.0.0.0/8 doesn't start a 16-million-address scan.</summary>
    public const long MaxAddresses = 65_536;

    public static ParseResult Parse(string text)
    {
        var targets = new List<ScanTarget>();
        var errors = new List<string>();
        var lines = text.Split('\n');
        for (var lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            foreach (var raw in lines[lineNo].Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var item = raw.Trim();
                if (item.StartsWith('#')) break; // comment in a targets file
                if (TryParseItem(item, out var target, out var error)) targets.Add(target);
                else errors.Add(lines.Length > 1 ? $"Line {lineNo + 1}: {error}" : error);
            }
        }

        var total = targets.Sum(t => t is ScanTarget.Range r ? r.Count : 1);
        if (total > MaxAddresses)
            errors.Add($"{total:N0} addresses is more than the {MaxAddresses:N0} allowed in one scan");
        return new ParseResult(targets, errors);
    }

    /// <summary>All addresses in the targets, in order, without duplicates. Host names are not resolved here.</summary>
    public static IEnumerable<IPAddress> Addresses(IEnumerable<ScanTarget> targets)
    {
        var seen = new HashSet<uint>();
        foreach (var t in targets)
        {
            if (t is not ScanTarget.Range r) continue;
            for (var v = (long)r.First; v <= r.Last; v++)
                if (seen.Add((uint)v)) yield return ToAddress((uint)v);
        }
    }

    public static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static bool TryParseItem(string item, out ScanTarget target, out string error)
    {
        target = null!;
        error = "";

        // CIDR: 10.0.0.0/24
        var slash = item.IndexOf('/');
        if (slash > 0)
        {
            if (TryParseV4(item[..slash], out var baseIp) && int.TryParse(item[(slash + 1)..], out var bits) && bits is >= 0 and <= 32)
            {
                var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                var first = baseIp & mask;
                var last = first | ~mask;
                // Skip network and broadcast addresses for normal subnets.
                if (bits <= 30) { first++; last--; }
                target = new ScanTarget.Range(first, last);
                return true;
            }
            error = $"\"{item}\" is not a valid subnet (example: 192.168.0.0/24)";
            return false;
        }

        // Range: a.b.c.d-e or a.b.c.d-a.b.c.e
        var dash = item.IndexOf('-');
        if (dash > 0 && TryParseV4(item[..dash], out var start))
        {
            var rest = item[(dash + 1)..];
            uint end;
            if (byte.TryParse(rest, out var lastOctet)) end = (start & 0xFFFFFF00) | lastOctet;
            else if (!TryParseV4(rest, out end)) { error = $"\"{item}\" is not a valid range (example: 192.168.0.1-100)"; return false; }
            if (end < start) { error = $"\"{item}\" ends before it starts"; return false; }
            target = new ScanTarget.Range(start, end);
            return true;
        }

        if (TryParseV4(item, out var single))
        {
            target = new ScanTarget.Range(single, single);
            return true;
        }

        if (IsValidHostName(item))
        {
            target = new ScanTarget.HostName(item);
            return true;
        }

        error = $"\"{item}\" is not an IP address, range or host name";
        return false;
    }

    private static bool TryParseV4(string text, out uint value)
    {
        value = 0;
        // IPAddress.TryParse accepts "1" or "1.2" shorthands; require four parts.
        if (text.Count(c => c == '.') != 3) return false;
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        value = ToUInt(ip);
        return true;
    }

    private static bool IsValidHostName(string name) =>
        name.Length is > 0 and <= 253
        && Uri.CheckHostName(name) == UriHostNameType.Dns
        && name.Any(char.IsLetter);
}
