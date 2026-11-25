using System.Text.RegularExpressions;

namespace Logitail.Core.Highlighting;

/// <summary>Colors as 0xAARRGGBB so the core stays free of UI types.</summary>
public sealed record HighlightRule
{
    public string Pattern { get; init; } = "";
    public bool IgnoreCase { get; init; } = true;
    /// <summary>Style lines that do NOT match.</summary>
    public bool Invert { get; init; }
    /// <summary>Treat <see cref="Pattern"/> as a regular expression instead of plain text.</summary>
    public bool IsRegex { get; init; }
    public uint Foreground { get; init; } = 0xFF000000;
    public uint Background { get; init; } = 0xFFFFFF00;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
}

/// <summary>An ordered rule list. The first matching rule styles the whole line.</summary>
public sealed class HighlightRuleSet
{
    private readonly (HighlightRule Rule, Regex? Regex)[] _compiled;

    public HighlightRuleSet(IEnumerable<HighlightRule> rules)
    {
        Rules = rules.Where(r => r.Pattern.Length > 0).ToArray();
        _compiled = Rules.Select(r => (r, Compile(r))).ToArray();
    }

    public static HighlightRuleSet Empty { get; } = new([]);

    public IReadOnlyList<HighlightRule> Rules { get; }

    public HighlightRule? Match(string line)
    {
        foreach (var (rule, regex) in _compiled)
        {
            var hit = regex is not null
                ? SafeIsMatch(regex, line)
                : line.Contains(rule.Pattern, rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (hit != rule.Invert) return rule;
        }
        return null;
    }

    private static bool SafeIsMatch(Regex regex, string line)
    {
        try { return regex.IsMatch(line); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Regex? Compile(HighlightRule rule)
    {
        if (!rule.IsRegex) return null;
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : 0);
        try
        {
            return new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            // An invalid pattern never matches rather than crashing the viewer.
            return new Regex("(?!)", RegexOptions.None);
        }
    }
}
