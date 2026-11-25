namespace Logictail.Scanner;

/// <summary>
/// MAC prefix to manufacturer lookup from the IEEE registry CSV
/// (columns: Registry, Assignment, Organization Name, Organization Address).
/// </summary>
public sealed class OuiDatabase
{
    private readonly Dictionary<string, string> _vendors;

    private OuiDatabase(Dictionary<string, string> vendors) => _vendors = vendors;

    public static OuiDatabase Empty { get; } = new([]);

    public int Count => _vendors.Count;

    /// <summary>Loads the CSV; returns <see cref="Empty"/> when the file is missing or unreadable.</summary>
    public static OuiDatabase Load(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return Parse(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public static OuiDatabase Parse(TextReader reader)
    {
        var vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            var fields = SplitCsv(line);
            if (fields.Count < 3 || fields[0] != "MA-L" || fields[1].Length != 6) continue;
            vendors.TryAdd(fields[1], fields[2].Trim());
        }
        return new OuiDatabase(vendors);
    }

    public string? Lookup(string? mac) =>
        MacAddress.Prefix(mac) is { } prefix && _vendors.TryGetValue(prefix, out var vendor) ? vendor : null;

    /// <summary>Minimal CSV split: commas, double-quoted fields, doubled quotes inside quotes.</summary>
    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
