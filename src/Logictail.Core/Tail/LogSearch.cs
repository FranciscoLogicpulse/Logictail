namespace Logictail.Core.Tail;

/// <summary>Finds the next line containing some text (ignoring case), wrapping around the file.</summary>
public static class LogSearch
{
    private const int Chunk = 4096;

    /// <summary>
    /// First line after <paramref name="from"/> (or before it, going back) that contains <paramref name="text"/>,
    /// wrapping past the end/start; <paramref name="from"/> itself is checked last. Null when no line matches.
    /// </summary>
    public static int? Find(LogFile file, string text, int from, bool forward, CancellationToken cancel = default)
    {
        var count = file.LineCount;
        if (count == 0 || string.IsNullOrEmpty(text)) return null;
        from = Math.Clamp(from, 0, count - 1);
        // Visit count lines in order: from+1, from+2, ... wrapping, ending at from (or the reverse).
        for (var done = 0; done < count;)
        {
            cancel.ThrowIfCancellationRequested();
            var step = Math.Min(Chunk, count - done);
            // Lines from+1+done .. from+done+step (forward) or from-1-done .. from-done-step (back), modulo count.
            var start = forward ? Mod(from + 1 + done, count) : Mod(from - done - step, count);
            var block = ReadWrapped(file, start, step, count);
            for (var i = 0; i < step; i++)
            {
                var k = forward ? i : step - 1 - i;
                if (block[k].Contains(text, StringComparison.OrdinalIgnoreCase)) return Mod(start + k, count);
            }
            done += step;
        }
        return null;
    }

    private static List<string> ReadWrapped(LogFile file, int start, int length, int count)
    {
        var lines = new List<string>(file.ReadLines(start, Math.Min(length, count - start)));
        if (lines.Count < length) lines.AddRange(file.ReadLines(0, length - lines.Count));
        // A file that shrank mid-search reads short; pad so indexes stay valid.
        while (lines.Count < length) lines.Add("");
        return lines;
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;
}
