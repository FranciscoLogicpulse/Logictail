using Logitail.Core.Tail;

namespace Logitail.Tests;

public sealed class TextLayoutTests
{
    [Theory]
    [InlineData("a\tb", 4, "a   b")]
    [InlineData("abcd\te", 4, "abcd    e")]
    [InlineData("\t\tx", 2, "    x")]
    [InlineData("none", 4, "none")]
    public void Expands_tabs_to_tab_stops(string input, int width, string expected) =>
        Assert.Equal(expected, TextLayout.ExpandTabs(input, width));

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(30, 10, 3)]
    [InlineData(500, 0, 1)] // wrap off
    public void Row_count(int length, int columns, int rows) => Assert.Equal(rows, TextLayout.RowCount(length, columns));

    [Fact]
    public void Wraps_into_rows() => Assert.Equal(["abc", "def", "g"], TextLayout.Wrap("abcdefg", 3));

    [Fact]
    public void Tail_top_line_accounts_for_wrapped_rows()
    {
        // 10 lines; last three are 1, 3 and 2 rows at 10 columns. A 5-row view fits the last two lines.
        string[] last = ["x", new string('y', 25), new string('z', 15)];
        Assert.Equal(8, TextLayout.TailTopLine(10, last, 10, 5));
        Assert.Equal(7, TextLayout.TailTopLine(10, last, 10, 6));
    }

    [Fact]
    public void Tail_top_line_shows_a_line_taller_than_the_view()
    {
        string[] last = ["a", new string('b', 100)];
        Assert.Equal(1, TextLayout.TailTopLine(2, last, 10, 3));
    }

    [Fact]
    public void Tail_top_line_without_wrap_is_count_minus_rows()
    {
        var last = Enumerable.Repeat("line", 20).ToArray();
        Assert.Equal(80, TextLayout.TailTopLine(100, last, 0, 20));
        Assert.Equal(0, TextLayout.TailTopLine(5, last[..5], 0, 20));
    }
}
