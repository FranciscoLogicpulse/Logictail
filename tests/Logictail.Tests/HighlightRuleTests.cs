using Logictail.Core.Highlighting;

namespace Logictail.Tests;

public sealed class HighlightRuleTests
{
    [Fact]
    public void First_matching_rule_wins()
    {
        var error = new HighlightRule { Pattern = "error" };
        var any = new HighlightRule { Pattern = "e" };
        var set = new HighlightRuleSet([error, any]);
        Assert.Same(error, set.Match("An ERROR happened"));
        Assert.Same(any, set.Match("line"));
        Assert.Null(set.Match("xyz"));
    }

    [Fact]
    public void Case_sensitive_and_inverted_rules()
    {
        var set = new HighlightRuleSet([new HighlightRule { Pattern = "WARN", IgnoreCase = false }]);
        Assert.Null(set.Match("warn"));

        var inverted = new HighlightRuleSet([new HighlightRule { Pattern = "INFO", Invert = true }]);
        Assert.Null(inverted.Match("info: ok"));
        Assert.NotNull(inverted.Match("debug: x"));
    }

    [Fact]
    public void Regex_rules_and_invalid_patterns()
    {
        var set = new HighlightRuleSet([new HighlightRule { Pattern = @"\b5\d\d\b", IsRegex = true }]);
        Assert.NotNull(set.Match("GET / 503"));
        Assert.Null(set.Match("GET / 200"));

        var bad = new HighlightRuleSet([new HighlightRule { Pattern = "([", IsRegex = true }]);
        Assert.Null(bad.Match("(["));
    }

    [Fact]
    public void Empty_patterns_are_ignored()
    {
        var set = new HighlightRuleSet([new HighlightRule { Pattern = "" }]);
        Assert.Null(set.Match("anything"));
    }
}
