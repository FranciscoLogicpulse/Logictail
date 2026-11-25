using System.Globalization;

namespace Logictail.Core.Localization;

public enum Language { Portuguese, English }

/// <summary>
/// UI text in Portuguese (Portugal) or English. Source text is English; <see cref="Pt"/> maps it to Portuguese.
/// Text without a Portuguese entry falls back to English (a test guards that every used text has one).
/// </summary>
public static class L
{
    private static readonly AsyncLocal<Language?> Scoped = new();

    /// <summary>App-wide language, set once at startup from the settings.</summary>
    public static Language Default { get; set; } = Language.Portuguese;

    /// <summary>Language for this flow of work: a scoped override (tests) or the app default.</summary>
    public static Language Current => Scoped.Value ?? Default;

    public static CultureInfo Culture => Current == Language.Portuguese ? CultureInfo.GetCultureInfo("pt-PT") : CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Uses <paramref name="language"/> until disposed, for this async flow only. For tests.</summary>
    public static IDisposable Use(Language language)
    {
        var previous = Scoped.Value;
        Scoped.Value = language;
        return new Restore(() => Scoped.Value = previous);
    }

    /// <summary>Translates English source text.</summary>
    public static string _(string english) =>
        Current == Language.Portuguese && Pt.Text.TryGetValue(english, out var pt) ? pt : english;

    /// <summary>Translates an English format string, then fills it with <paramref name="args"/>.</summary>
    public static string F(string englishFormat, params object?[] args) => string.Format(Culture, _(englishFormat), args);

    /// <summary>True when <paramref name="english"/> has a Portuguese entry. For the coverage test.</summary>
    public static bool HasPortuguese(string english) => Pt.Text.ContainsKey(english);

    public static string Code(Language language) => language == Language.Portuguese ? "pt-PT" : "en";

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
