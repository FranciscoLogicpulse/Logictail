using Logictail.Core.Tail;

namespace Logictail.Tests;

public sealed class LogSearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logictail-search-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LogFile Log(params string[] lines)
    {
        var path = Path.Combine(_dir, "s.log");
        File.WriteAllLines(path, lines);
        var log = new LogFile(path);
        log.Refresh();
        return log;
    }

    [Fact]
    public void Finds_next_and_previous_ignoring_case_and_wraps()
    {
        var log = Log("a", "ERROR one", "b", "error two", "c");
        Assert.Equal(1, LogSearch.Find(log, "error", 0, forward: true));
        Assert.Equal(3, LogSearch.Find(log, "error", 1, forward: true));
        Assert.Equal(1, LogSearch.Find(log, "error", 3, forward: true));   // wraps past the end
        Assert.Equal(1, LogSearch.Find(log, "error", 3, forward: false));
        Assert.Equal(3, LogSearch.Find(log, "error", 1, forward: false));  // wraps past the start
        Assert.Equal(1, LogSearch.Find(log, "one", 1, forward: true));     // the start line is checked last
        Assert.Null(LogSearch.Find(log, "missing", 0, forward: true));
        Assert.Null(LogSearch.Find(log, "", 0, forward: true));
    }

    [Fact]
    public void Searches_across_chunks()
    {
        var lines = Enumerable.Range(0, 10_000).Select(i => $"line {i}").ToArray();
        lines[9_000] = "needle";
        lines[17] = "NEEDLE";
        var log = Log(lines);
        Assert.Equal(9_000, LogSearch.Find(log, "needle", 100, forward: true));
        Assert.Equal(17, LogSearch.Find(log, "needle", 9_000, forward: true));
        Assert.Equal(9_000, LogSearch.Find(log, "needle", 17, forward: false));
        Assert.Equal(17, LogSearch.Find(log, "needle", 8_999, forward: false));
    }
}
