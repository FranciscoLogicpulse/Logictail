using System.Text.Json;
using System.Text.Json.Serialization;

namespace Logitail.Scanner;

/// <summary>What the user told us about a device: kept across scans.</summary>
public sealed record HostNote
{
    public string? Name { get; init; }
    public string? Comment { get; init; }
    /// <summary>MAC typed by the user, for devices the scan can't get one for (e.g. off-subnet, for Wake-on-LAN).</summary>
    public string? Mac { get; init; }
    public bool Favorite { get; init; }
    /// <summary>Last address seen, so favorites can be scanned again and found while switched off.</summary>
    public string? LastAddress { get; init; }
    /// <summary>MAC the last scan found, kept so a switched-off device still has it (e.g. for Wake-on-LAN).</summary>
    public string? ScannedMac { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Name is null && Comment is null && Mac is null && !Favorite;
}

/// <summary>
/// Notes per device, saved as JSON. Devices are keyed by the MAC the scan found (survives DHCP address changes),
/// otherwise by IP address. A device that is off (no MAC this scan) is found by its last address.
/// </summary>
public sealed class HostStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<string, HostNote> _notes;
    private readonly string? _path;

    private HostStore(string? path, Dictionary<string, HostNote> notes)
    {
        _path = path;
        _notes = notes;
    }

    public static HostStore InMemory() => new(null, new(StringComparer.OrdinalIgnoreCase));

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Logitail", "hosts.json");

    /// <summary>Loads the file; a missing or unreadable file starts empty (an unreadable one is kept as .broken).</summary>
    public static HostStore Load(string path)
    {
        var notes = new Dictionary<string, HostNote>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var (key, note) in JsonSerializer.Deserialize<Dictionary<string, HostNote>>(File.ReadAllText(path), Json) ?? [])
                    if (!note.IsEmpty) notes[key] = note;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Move(path, path + ".broken", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new HostStore(path, notes);
    }

    public IReadOnlyCollection<KeyValuePair<string, HostNote>> All => _notes;

    /// <summary>Scanned MAC when there is one, else the IP. A MAC the user typed is data, not a key: the next scan can't see it.</summary>
    public static string KeyFor(HostResult host) =>
        MacAddress.Prefix(host.Mac) is not null ? Normalize(host.Mac!) : host.Address.ToString();

    /// <summary>Note for a raw scan result (before notes were applied).</summary>
    public HostNote? Get(HostResult host) => Find(host)?.Note;

    private (string Key, HostNote Note)? Find(HostResult host)
    {
        if (_notes.TryGetValue(KeyFor(host), out var note)) return (KeyFor(host), note);
        var ip = host.Address.ToString();
        if (_notes.TryGetValue(ip, out note)) return (ip, note);
        // No MAC this time (device off, or off-subnet): match the note last seen at this address.
        if (host.Mac is null)
            foreach (var (key, n) in _notes)
                if (n.LastAddress == ip) return (key, n);
        return null;
    }

    /// <summary>Saves a note for a raw scan result.</summary>
    public void Set(HostResult host, HostNote note)
    {
        var existing = Find(host);
        if (existing is { } e) _notes.Remove(e.Key);
        var scannedMac = host.Mac is not null ? Normalize(host.Mac) : existing?.Note.ScannedMac;
        // Keep the MAC key once we've seen one, even if the device is off now.
        var key = scannedMac is not null ? scannedMac : host.Address.ToString();
        note = note with { LastAddress = host.Address.ToString(), ScannedMac = scannedMac };
        if (!note.IsEmpty) _notes[key] = note;
        Save();
    }

    /// <summary>Applies the user's name, comment, MAC and favorite flag to a raw scan result.</summary>
    public HostResult Apply(HostResult host, OuiDatabase vendors)
    {
        if (Get(host) is not { } note) return host;
        if (note.LastAddress != host.Address.ToString() && host.Mac is not null) Set(host, note); // the device moved
        var mac = host.Mac ?? note.Mac ?? note.ScannedMac;
        return host with
        {
            Name = note.Name ?? host.Name,
            Comment = note.Comment,
            Mac = mac,
            Manufacturer = host.Manufacturer ?? vendors.Lookup(mac),
            IsFavorite = note.Favorite,
        };
    }

    /// <summary>Addresses of all favorites, for "scan favorites".</summary>
    public IReadOnlyList<string> FavoriteAddresses() =>
        _notes.Values.Where(n => n.Favorite && n.LastAddress is not null).Select(n => n.LastAddress!).Distinct().ToList();

    public void Clear()
    {
        _notes.Clear();
        Save();
    }

    private static string Normalize(string mac) => mac.ToUpperInvariant().Replace(':', '-');

    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_notes, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
