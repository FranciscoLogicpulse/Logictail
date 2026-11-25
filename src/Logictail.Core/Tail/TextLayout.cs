using System.Text;

namespace Logictail.Core.Tail;

/// <summary>Monospace text layout helpers: tab expansion and wrapping by column count.</summary>
public static class TextLayout
{
    /// <summary>Replaces tabs with spaces up to the next tab stop.</summary>
    public static string ExpandTabs(string text, int tabWidth)
    {
        if (!text.Contains('\t')) return text;
        tabWidth = Math.Max(1, tabWidth);
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == '\t') sb.Append(' ', tabWidth - sb.Length % tabWidth);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Rows a line of <paramref name="length"/> characters takes when wrapped at <paramref name="columns"/>.</summary>
    public static int RowCount(int length, int columns) =>
        columns <= 0 || length <= columns ? 1 : (length + columns - 1) / columns;

    /// <summary>Splits text into rows of at most <paramref name="columns"/> characters (hard wrap).</summary>
    // shortcut: assumes every character is one column wide; wide CJK/emoji characters overflow their row.
    public static IEnumerable<string> Wrap(string text, int columns)
    {
        if (columns <= 0 || text.Length <= columns) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += columns)
            yield return text.Substring(i, Math.Min(columns, text.Length - i));
    }

    /// <summary>
    /// First line index such that lines from it to the end fill at most <paramref name="rows"/> rows.
    /// <paramref name="lastLines"/> are the final lines of the file (expanded), oldest first.
    /// </summary>
    public static int TailTopLine(int lineCount, IReadOnlyList<string> lastLines, int columns, int rows)
    {
        var used = 0;
        var top = lineCount;
        for (var i = lastLines.Count - 1; i >= 0; i--)
        {
            var need = RowCount(lastLines[i].Length, columns);
            if (used + need > rows && top < lineCount) break;
            used += need;
            top--;
            if (used >= rows) break;
        }
        return Math.Max(0, top);
    }
}
