namespace Logitail.Scanner;

public enum PortProtocol { Tcp, Udp }

/// <summary>Ports to scan, sorted and without duplicates.</summary>
public sealed record PortList(IReadOnlyList<int> Tcp, IReadOnlyList<int> Udp)
{
    public static PortList Empty { get; } = new([], []);
    public bool IsEmpty => Tcp.Count == 0 && Udp.Count == 0;
    public int Count => Tcp.Count + Udp.Count;

    /// <summary>Logitail's own list of commonly used TCP ports.</summary>
    public const string Common =
        "21-23, 25, 53, 80, 81, 88, 110, 111, 135, 139, 143, 389, 443, 445, 465, 514, 587, 631, 636, 993, 995, 1080, 1433, " +
        "1521, 1723, 1883, 2049, 2375, 3000, 3128, 3306, 3389, 5000, 5060, 5432, 5672, 5900, 5985, 6379, 8000, 8008, 8080, " +
        "8081, 8443, 8888, 9000, 9090, 9100, 9200, 27017";
    public const string WellKnown = "1-1023";
    public const string AllTcp = "1-65535";
}

public sealed record PortParseResult(PortList Ports, IReadOnlyList<string> Errors);

/// <summary>
/// Parses port lists like <c>80, 443, 8000-8100, UDP: 53, 161</c>. A <c>UDP:</c> or <c>TCP:</c> prefix switches the
/// protocol for the items that follow; ports start as TCP.
/// </summary>
public static class PortListParser
{
    public static PortParseResult Parse(string? text)
    {
        var tcp = new SortedSet<int>();
        var udp = new SortedSet<int>();
        var errors = new List<string>();
        var current = tcp;

        foreach (var raw in (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            // A protocol prefix may stand alone ("UDP:") or lead an item ("UDP: 53").
            var colon = item.IndexOf(':');
            if (colon >= 0)
            {
                var proto = item[..colon].Trim().ToUpperInvariant();
                if (proto is "U" or "UD" or "UDP") current = udp;
                else if (proto is "T" or "TC" or "TCP") current = tcp;
                else { errors.Add($"\"{item}\": use TCP: or UDP: before ports"); continue; }
                item = item[(colon + 1)..].Trim();
                if (item.Length == 0) continue;
            }

            var dash = item.IndexOf('-');
            if (dash > 0 && int.TryParse(item[..dash].Trim(), out var from) && int.TryParse(item[(dash + 1)..].Trim(), out var to))
            {
                if (from < 1 || to > 65535 || from > to) { errors.Add($"\"{item}\" is not a valid port range (1-65535)"); continue; }
                for (var p = from; p <= to; p++) current.Add(p);
            }
            else if (int.TryParse(item, out var port) && port is >= 1 and <= 65535)
            {
                current.Add(port);
            }
            else
            {
                errors.Add($"\"{item}\" is not a port or range (example: 80, 443, 8000-8100, UDP: 53)");
            }
        }
        return new PortParseResult(new PortList([.. tcp], [.. udp]), errors);
    }
}
