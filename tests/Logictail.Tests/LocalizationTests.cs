using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Logictail.Core.Localization;
using Logictail.Core.Settings;
using Xunit;

namespace Logictail.Tests;

internal static class EnglishForTests
{
    // Assertions in this project use the English text; Portuguese is checked explicitly below.
    [ModuleInitializer]
    internal static void Init() => L.Default = Language.English;
}

public class LocalizationTests
{
    /// <summary>Every English literal passed to L._, L.F or {l:Tr} in src.</summary>
    public static TheoryData<string> UsedTexts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Logictail.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".axaml")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"L\.(?:_|F)\(\s*""((?:[^""\\]|\\.)*)"""))
                texts.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(text, @"L\._\([^;""]*?\?\s*""((?:[^""\\]|\\.)*)""\s*:\s*""((?:[^""\\]|\\.)*)"""))
            {
                texts.Add(Regex.Unescape(m.Groups[1].Value));
                texts.Add(Regex.Unescape(m.Groups[2].Value));
            }
            foreach (Match m in Regex.Matches(text, @"\{l:Tr '([^']*)'\}")) texts.Add(m.Groups[1].Value);
        }
        var data = new TheoryData<string>();
        foreach (var t in texts) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(UsedTexts))]
    public void Every_used_text_has_Portuguese(string english)
    {
        Assert.True(L.HasPortuguese(english), $"No Portuguese for: {english}");
        // Portuguese format strings take the same arguments as the English ones.
        using (L.Use(Language.Portuguese))
            _ = L.F(english, 1, 2, 3, 4);
    }

    [Fact]
    public void Portuguese_is_the_default_language()
    {
        Assert.Equal(Language.Portuguese, new AppSettings().Language);
        Assert.Equal(Language.Portuguese, SettingsStore.Deserialize("{}").Language);
    }

    [Fact]
    public void Language_switches_text_and_number_format()
    {
        using (L.Use(Language.Portuguese))
        {
            Assert.Equal("_Ficheiro", L._("_File"));
            Assert.Equal("1 234 linhas", L.F("{0:N0} lines", 1234).Replace(' ', ' ').Replace(' ', ' '));
        }
        using (L.Use(Language.English))
        {
            Assert.Equal("_File", L._("_File"));
            Assert.Equal("1,234 lines", L.F("{0:N0} lines", 1234));
        }
        Assert.Equal("not translated", L._("not translated"));
    }

    [Fact]
    public void Language_round_trips_through_settings()
    {
        var s = new AppSettings { Language = Language.English };
        Assert.Equal(Language.English, SettingsStore.Deserialize(SettingsStore.Serialize(s)).Language);
    }
}
