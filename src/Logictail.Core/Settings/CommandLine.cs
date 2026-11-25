using Logictail.Core.Localization;
using System.Globalization;

namespace Logictail.Core.Settings;

/// <summary>
/// Parsed command line: files to open plus window layout switches.
/// <code>
/// logictail [files...] [--tile-window-count N --tile-window-index I]
///          [--window-position X,Y,W,H] [--window-state normal|maximized|minimized]
/// </code>
/// </summary>
public sealed record CommandLine(
    IReadOnlyList<string> Files,
    int? TileCount,
    int? TileIndex,
    (double X, double Y, double W, double H)? Position,
    WindowStateKind? State,
    IReadOnlyList<string> Errors)
{
    public static string Usage => L._(UsageEnglish);

    internal const string UsageEnglish =
        "Usage: Logictail [files...] [options]\n\n" +
        "  --tile-window-count N      Split the screen into N equal rows\n" +
        "  --tile-window-index I      Put this window in row I (0-based)\n" +
        "  --window-position X,Y,W,H  Place the window at X,Y with size W x H\n" +
        "  --window-state S           normal, maximized or minimized\n\n" +
        "Example: start two windows, one above the other:\n" +
        "  Logictail app.log --tile-window-count 2 --tile-window-index 0\n" +
        "  Logictail db.log  --tile-window-count 2 --tile-window-index 1";

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        var errors = new List<string>();
        int? count = null, index = null;
        (double, double, double, double)? position = null;
        WindowStateKind? state = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { files.Add(arg); continue; }

            // Accept both "--name value" and "--name=value".
            var eq = arg.IndexOf('=');
            var name = eq > 0 ? arg[..eq] : arg;
            string? value = eq > 0 ? arg[(eq + 1)..] : i + 1 < args.Count ? args[++i] : null;
            if (value is null) { errors.Add(L.F("{0} needs a value", name)); continue; }

            switch (name.ToLowerInvariant())
            {
                case "--tile-window-count" when int.TryParse(value, out var n) && n >= 1:
                    count = n; break;
                case "--tile-window-index" when int.TryParse(value, out var n) && n >= 0:
                    index = n; break;
                case "--window-position" when TryParsePosition(value, out var p):
                    position = p; break;
                case "--window-state" when Enum.TryParse<WindowStateKind>(value, ignoreCase: true, out var s):
                    state = s; break;
                default:
                    errors.Add(L.F("Invalid option: {0}", arg + (eq > 0 ? "" : " " + value))); break;
            }
        }

        if (index is not null && count is null) errors.Add(L._("--tile-window-index needs --tile-window-count"));
        if (index >= count) errors.Add(L._("--tile-window-index must be less than --tile-window-count"));
        return new CommandLine(files, count, index, position, state, errors);
    }

    private static bool TryParsePosition(string value, out (double, double, double, double) position)
    {
        position = default;
        var parts = value.Split(',');
        if (parts.Length != 4) return false;
        var n = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[2] <= 0 || n[3] <= 0) return false;
        position = (n[0], n[1], n[2], n[3]);
        return true;
    }
}
