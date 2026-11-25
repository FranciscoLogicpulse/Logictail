using System.Text.Json;
using System.Text.Json.Serialization;

namespace Logictail.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as <c>logictail.json</c>.
/// Lookup order: working directory, app directory, then %APPDATA%\Logictail.
/// </summary>
public sealed class SettingsStore(string workingDirectory, string appDirectory, string userDirectory)
{
    public const string FileName = "logictail.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The app was first released as "Logitail". Copies that version's profile folder (settings and device notes)
    /// to the new one once, if the new one doesn't exist yet. Returns true when something was copied.
    /// </summary>
    public static bool MigrateFromOldName(string appData)
    {
        var oldDir = Path.Combine(appData, "Logitail");
        var newDir = Path.Combine(appData, "Logictail");
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return false;
        try
        {
            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.GetFiles(oldDir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("logitail.json", StringComparison.OrdinalIgnoreCase)) name = FileName;
                File.Copy(file, Path.Combine(newDir, name));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // not worth failing startup over: the user just starts with defaults
        }
    }

    public static SettingsStore ForCurrentUser() => new(
        Environment.CurrentDirectory,
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Logictail"));

    /// <summary>File the settings were loaded from, or null when defaults were used.</summary>
    public string? LoadedFrom { get; private set; }

    /// <summary>Problem reading the settings file, if any. Defaults are used in that case.</summary>
    public string? LoadError { get; private set; }

    public AppSettings Load()
    {
        foreach (var location in new[] { SaveLocation.WorkingDirectory, SaveLocation.AppDirectory, SaveLocation.UserProfile })
        {
            var path = PathFor(location);
            if (path is null || !File.Exists(path)) continue;
            try
            {
                var settings = LoadFile(path);
                LoadedFrom = path;
                return settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoadError = $"{path}: {ex.Message}";
                _unreadable.Add(path);
            }
        }
        return new AppSettings();
    }

    private readonly List<string> _unreadable = [];

    /// <summary>
    /// Saves to the location chosen in the settings. Returns the path written, or null when nothing was written.
    /// A settings file that failed to load is first moved aside to <c>.broken</c> so the user's edits aren't lost;
    /// if it can't be moved, nothing is written.
    /// </summary>
    public string? Save(AppSettings settings)
    {
        if (PathFor(settings.SaveOnExit) is not { } path) return null;
        if (_unreadable.Contains(path, StringComparer.OrdinalIgnoreCase) && File.Exists(path))
        {
            try { File.Move(path, path + ".broken", overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        SaveFile(settings, path);
        return path;
    }

    public string? PathFor(SaveLocation location) => location switch
    {
        SaveLocation.WorkingDirectory => Path.Combine(workingDirectory, FileName),
        SaveLocation.AppDirectory => Path.Combine(appDirectory, FileName),
        SaveLocation.UserProfile => Path.Combine(userDirectory, FileName),
        _ => null,
    };

    public static AppSettings LoadFile(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Writes atomically so a crash mid-save can't leave a half-written file.</summary>
    public static void SaveFile(AppSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(settings));
        File.Move(temp, path, overwrite: true);
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Json);

    public static AppSettings Deserialize(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings()).Normalize();
}
