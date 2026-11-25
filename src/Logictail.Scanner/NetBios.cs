using System.Text;

namespace Logictail.Scanner;

public sealed record NetBiosInfo(string? ComputerName, string? Workgroup, string? UserName, string? Mac);

/// <summary>
/// NetBIOS node status (NBSTAT) query over UDP 137, as in RFC 1002 section 4.2.18.
/// Building and parsing are separate from the socket so both can be tested.
/// </summary>
public static class NetBios
{
    public const int Port = 137;

    /// <summary>Node status request for the wildcard name "*".</summary>
    public static byte[] BuildQuery(ushort transactionId)
    {
        var packet = new List<byte>
        {
            (byte)(transactionId >> 8), (byte)transactionId,
            0x00, 0x00, // flags: query
            0x00, 0x01, // questions
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // answers, authority, additional
            0x20,       // encoded name length (32)
        };
        // "*" followed by 15 NUL bytes, each byte split into two nibbles + 'A'.
        var name = new byte[16];
        name[0] = (byte)'*';
        foreach (var b in name)
        {
            packet.Add((byte)('A' + (b >> 4)));
            packet.Add((byte)('A' + (b & 0x0F)));
        }
        packet.Add(0x00);                     // end of name
        packet.AddRange([0x00, 0x21]);        // type NBSTAT
        packet.AddRange([0x00, 0x01]);        // class IN
        return [.. packet];
    }

    /// <summary>Parses a node status response. Returns null for anything malformed; never throws.</summary>
    public static NetBiosInfo? ParseResponse(ReadOnlySpan<byte> data, ushort expectedId)
    {
        // header(12) + name(34) + type(2) + class(2) + ttl(4) + rdlength(2) + count(1)
        const int namesOffset = 12 + 34 + 2 + 2 + 4 + 2 + 1;
        if (data.Length < namesOffset) return null;
        if ((data[0] << 8 | data[1]) != expectedId) return null;
        if ((data[2] & 0x80) == 0) return null; // not a response
        if (data[12 + 34] != 0x00 || data[12 + 34 + 1] != 0x21) return null; // not NBSTAT

        int count = data[namesOffset - 1];
        if (data.Length < namesOffset + count * 18) return null;

        string? computer = null, workgroup = null, user = null;
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(namesOffset + i * 18, 18);
            var name = Encoding.ASCII.GetString(entry[..15]).TrimEnd(' ', '\0');
            var suffix = entry[15];
            var isGroup = (entry[16] & 0x80) != 0;
            if (name.Length == 0 || name.Any(char.IsControl)) continue;

            if (suffix == 0x00 && !isGroup) computer ??= name;
            else if (suffix == 0x00 && isGroup) workgroup ??= name;
            else if (suffix == 0x03 && !isGroup && !string.Equals(name, computer, StringComparison.OrdinalIgnoreCase)) user ??= name;
        }

        string? mac = null;
        var macOffset = namesOffset + count * 18;
        if (data.Length >= macOffset + 6)
        {
            var bytes = data.Slice(macOffset, 6);
            if (bytes.IndexOfAnyExcept((byte)0) >= 0) mac = MacAddress.Format(bytes);
        }
        return new NetBiosInfo(computer, workgroup, user, mac);
    }
}

public static class MacAddress
{
    public static string Format(ReadOnlySpan<byte> bytes) => string.Join("-", bytes.ToArray().Select(b => b.ToString("X2")));

    /// <summary>First three bytes as six hex digits, e.g. "001A2B", or null.</summary>
    public static string? Prefix(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length >= 6 ? hex[..6] : null;
    }
}
