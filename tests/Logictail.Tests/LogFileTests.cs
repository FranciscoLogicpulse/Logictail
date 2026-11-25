using System.Diagnostics;
using System.Text;
using Logictail.Core.Tail;

namespace Logictail.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logictail-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewPath(string name = "test.log") => Path.Combine(_dir, name);

    private static void Append(string path, string text, Encoding? encoding = null)
    {
        using var s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        s.Write(bytes);
    }

    [Fact]
    public void Empty_file_has_no_lines()
    {
        var path = NewPath();
        File.WriteAllBytes(path, []);
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(0, log.LineCount);
    }

    [Fact]
    public void Indexes_lines_and_strips_crlf()
    {
        var path = NewPath();
        File.WriteAllText(path, "one\r\ntwo\nthree");
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(3, log.LineCount);
        Assert.Equal(["one", "two", "three"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Trailing_newline_does_not_add_an_empty_line()
    {
        var path = NewPath();
        File.WriteAllText(path, "a\nb\n");
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(2, log.LineCount);
    }

    [Fact]
    public void Partial_last_line_completes_when_more_data_arrives()
    {
        var path = NewPath();
        File.WriteAllText(path, "first\nsec");
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(["first", "sec"], log.ReadLines(0, 10));

        Append(path, "ond\nthird\n");
        Assert.Equal(RefreshResult.Appended, log.Refresh());
        Assert.Equal(["first", "second", "third"], log.ReadLines(0, 10));
        Assert.Equal(RefreshResult.NoChange, log.Refresh());
    }

    [Fact]
    public void Truncation_resets_the_index()
    {
        var path = NewPath();
        File.WriteAllText(path, "old line 1\nold line 2\nold line 3\n");
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(3, log.LineCount);

        File.WriteAllText(path, "new\n");
        Assert.Equal(RefreshResult.Reset, log.Refresh());
        Assert.Equal(["new"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Replaced_file_with_different_start_resets_even_if_larger()
    {
        var path = NewPath();
        File.WriteAllText(path, "2026-01-01 a\n");
        var log = new LogFile(path);
        log.Refresh();

        File.WriteAllText(path, "2026-01-02 rotated file\nmore\n");
        Assert.Equal(RefreshResult.Reset, log.Refresh());
        Assert.Equal(["2026-01-02 rotated file", "more"], log.ReadLines(0, 10));
    }

    [Theory]
    [InlineData("utf8bom", TextEncodingKind.Utf8)]
    [InlineData("utf16le", TextEncodingKind.Utf16Le)]
    [InlineData("utf16be", TextEncodingKind.Utf16Be)]
    public void Detects_bom_encodings(string name, TextEncodingKind expected)
    {
        Encoding enc = expected switch
        {
            TextEncodingKind.Utf8 => new UTF8Encoding(true),
            TextEncodingKind.Utf16Le => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true),
        };
        var path = NewPath(name + ".log");
        File.WriteAllText(path, "héllo\r\nwörld ✓\n", enc);

        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(expected, log.Encoding);
        Assert.Equal(["héllo", "wörld ✓"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Bom_is_detected_when_the_file_starts_empty()
    {
        var path = NewPath();
        File.WriteAllBytes(path, []);
        var log = new LogFile(path);
        log.Refresh();

        File.WriteAllText(path, "first\nsecond\n", new UnicodeEncoding(false, true));
        log.Refresh();
        Assert.Equal(TextEncodingKind.Utf16Le, log.Encoding);
        Assert.Equal(["first", "second"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Bom_is_detected_when_the_first_refresh_saw_only_part_of_it()
    {
        var path = NewPath();
        var bytes = new UTF8Encoding(true).GetPreamble().Concat("hi\n"u8.ToArray()).ToArray();
        File.WriteAllBytes(path, bytes[..2]);
        var log = new LogFile(path);
        log.Refresh();

        File.WriteAllBytes(path, bytes);
        log.Refresh();
        Assert.Equal(["hi"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Giant_line_is_cut_instead_of_read_whole()
    {
        var path = NewPath();
        File.WriteAllText(path, "short\n" + new string('x', 5_000_000) + "\nafter\n");
        var log = new LogFile(path);
        log.Refresh();
        var lines = log.ReadLines(0, 10);
        Assert.Equal(3, lines.Count);
        Assert.Equal("short", lines[0]);
        Assert.Equal(LogFile.MaxLineBytes, lines[1].Length);
        Assert.Equal("after", lines[2]);
    }

    [Fact]
    public void Utf16_newline_bytes_inside_other_characters_are_not_line_breaks()
    {
        // U+0A0A has 0x0A bytes but is not a newline.
        var path = NewPath();
        File.WriteAllText(path, "aਊb\nc", new UnicodeEncoding(false, true));
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(["aਊb", "c"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Utf16_odd_trailing_byte_waits_for_its_pair()
    {
        var path = NewPath();
        var bytes = new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("ab\nc")).ToArray();
        File.WriteAllBytes(path, bytes[..^1]); // cut the last 'c' in half
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal(["ab"], log.ReadLines(0, 10));

        using (var s = new FileStream(path, FileMode.Append)) s.WriteByte(bytes[^1]);
        log.Refresh();
        Assert.Equal(["ab", "c"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Ansi_shows_every_byte_and_null_bytes_are_visible()
    {
        var path = NewPath();
        File.WriteAllBytes(path, [(byte)'x', 0, (byte)'y', 0xE9, (byte)'\n']);
        var log = new LogFile(path, TextEncodingKind.Ascii);
        log.Refresh();
        Assert.Equal("x[00]yé", log.ReadLine(0));
    }

    [Fact]
    public void Changing_encoding_reindexes()
    {
        var path = NewPath();
        File.WriteAllText(path, "é\n", new UTF8Encoding(false));
        var log = new LogFile(path);
        log.Refresh();
        Assert.Equal("é", log.ReadLine(0));

        log.SetEncoding(TextEncodingKind.Ascii);
        log.Refresh();
        Assert.Equal("Ã©", log.ReadLine(0));
    }

    [Fact]
    public void Reads_while_another_process_holds_the_file_open_for_writing()
    {
        var path = NewPath();
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        writer.Write("held open\n"u8);
        writer.Flush();

        var log = new LogFile(path);
        Assert.NotEqual(RefreshResult.Error, log.Refresh());
        Assert.Equal(["held open"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Missing_file_reports_error_and_recovers()
    {
        var path = NewPath();
        var log = new LogFile(path);
        Assert.Equal(RefreshResult.Error, log.Refresh());
        Assert.Equal("File not found", log.Error);

        File.WriteAllText(path, "back\n");
        Assert.NotEqual(RefreshResult.Error, log.Refresh());
        Assert.Null(log.Error);
        Assert.Equal(["back"], log.ReadLines(0, 10));
    }

    [Fact]
    public void Budgeted_refresh_indexes_in_steps()
    {
        var path = NewPath();
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {i:D4}\n")));
        var log = new LogFile(path);
        log.Refresh(maxBytes: 100);
        Assert.False(log.IsFullyIndexed);
        while (!log.IsFullyIndexed) log.Refresh(maxBytes: 100);
        Assert.Equal(1000, log.LineCount);
        Assert.Equal("line 0999", log.ReadLine(999));
    }

    [Fact]
    public void Large_file_indexes_quickly_and_reads_from_the_end()
    {
        var path = NewPath();
        const int lines = 2_000_000; // ~70 MB
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
            for (var i = 0; i < lines; i++) w.Write($"2026-11-25 12:00:00.000 INFO line number {i:D8}\n");

        var log = new LogFile(path);
        var sw = Stopwatch.StartNew();
        log.Refresh();
        sw.Stop();

        Assert.Equal(lines, log.LineCount);
        Assert.EndsWith($"{lines - 1:D8}", log.ReadLine(lines - 1));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Indexing took {sw.Elapsed}");
    }
}
