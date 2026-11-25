using System.Text;

namespace Logictail.Core.Tail;

public enum FileStatus { Unknown, Unchanged, Changed, Error }

public enum RefreshResult
{
    /// <summary>Nothing new on disk.</summary>
    NoChange,
    /// <summary>New bytes were indexed; existing lines are unchanged.</summary>
    Appended,
    /// <summary>The file shrank or was replaced; the whole index was rebuilt from the start.</summary>
    Reset,
    /// <summary>The file could not be read; see <see cref="LogFile.Error"/>.</summary>
    Error,
}

/// <summary>
/// A text file that may keep growing. Keeps only an index of line start offsets in memory and
/// reads line text from disk on demand, so files of any size open quickly.
/// Thread-safe: refresh on a worker thread while the UI reads lines. Disk reads happen outside the lock.
/// </summary>
public sealed class LogFile
{
    private const int ChunkSize = 1 << 20;
    private const int HeadSignatureSize = 64;
    private const int MaxBomLength = 3;

    /// <summary>Longest line text returned, in bytes. The rest of a longer line is cut, so one giant line can't stall drawing.</summary>
    public const int MaxLineBytes = 64 * 1024;

    // shortcut: one long per line (8 bytes); 100M lines = 800 MB. Upgrade to a chunked/delta index if that matters.
    private readonly List<long> _lineStarts = [];
    private readonly Lock _lock = new();
    private byte[] _headSignature = [];
    private long _length;      // bytes indexed so far (aligned to the code unit)
    private int _bomLength;
    private bool _bomResolved; // false until the file had enough bytes to tell whether it has a BOM
    private int _generation;   // bumped on every reset so an in-flight scan can't append to a newer index
    private TextEncodingKind _encoding;
    private bool _encodingPinned;

    public LogFile(string path, TextEncodingKind? encoding = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (encoding is { } e) { _encoding = e; _encodingPinned = true; }
        else _encoding = TextEncodingKind.Utf8;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    public TextEncodingKind Encoding
    {
        get { lock (_lock) return _encoding; }
    }

    /// <summary>Indexed file size in bytes.</summary>
    public long Length
    {
        get { lock (_lock) return _length; }
    }

    public int LineCount
    {
        get { lock (_lock) return CountLines(); }
    }

    /// <summary>Error from the last refresh, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>True once the whole file on disk has been indexed.</summary>
    public bool IsFullyIndexed { get; private set; }

    /// <summary>Forces an encoding and rebuilds the index on the next refresh.</summary>
    public void SetEncoding(TextEncodingKind encoding)
    {
        lock (_lock)
        {
            _encoding = encoding;
            _encodingPinned = true;
            ResetIndex();
        }
    }

    /// <summary>
    /// Reads new bytes from disk and extends the line index.
    /// <paramref name="maxBytes"/> bounds the work per call so a huge file can be indexed across several calls.
    /// Call from one thread at a time.
    /// </summary>
    public RefreshResult Refresh(long maxBytes = long.MaxValue)
    {
        try
        {
            using var stream = OpenShared(Path);
            var diskLength = stream.Length;
            var head = ReadHead(stream);

            long scanFrom, limit;
            int generation, unit;
            bool bigEndian, reset = false;
            long before;
            lock (_lock)
            {
                var common = Math.Min(head.Length, _headSignature.Length);
                var replaced = !head.AsSpan(0, common).SequenceEqual(_headSignature.AsSpan(0, common));
                var bomNowKnown = !_bomResolved && head.Length >= MaxBomLength;
                if (diskLength < _length || replaced || bomNowKnown)
                {
                    // Nothing was shown yet if the file was empty, so that's not a visible reset.
                    reset = _length > 0;
                    ResetIndex();
                }

                if (_lineStarts.Count == 0) StartIndex(head);
                if (_headSignature.Length < HeadSignatureSize) _headSignature = head;

                before = _length;
                unit = _encoding.UnitSize();
                bigEndian = _encoding == TextEncodingKind.Utf16Be;
                scanFrom = _length;
                limit = AlignDown(maxBytes >= diskLength - scanFrom ? diskLength : scanFrom + maxBytes);
                generation = _generation;
            }

            Scan(stream, scanFrom, limit, unit, bigEndian, generation);

            lock (_lock)
            {
                IsFullyIndexed = _length >= AlignDown(diskLength);
                Error = null;
                if (reset) return RefreshResult.Reset;
                return _length != before ? RefreshResult.Appended : RefreshResult.NoChange;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex is FileNotFoundException or DirectoryNotFoundException ? "File not found" : ex.Message;
            return RefreshResult.Error;
        }
    }

    /// <summary>
    /// Reads lines <paramref name="first"/> to first+count-1 (clamped). Newlines are stripped and each line is
    /// cut at <see cref="MaxLineBytes"/>.
    /// </summary>
    public IReadOnlyList<string> ReadLines(int first, int count)
    {
        Encoding encoding;
        long[] bounds; // bounds[i]..bounds[i+1] is line i
        int unit;
        lock (_lock)
        {
            var lineCount = CountLines();
            if (first < 0) first = 0;
            var last = Math.Min(first + count, lineCount) - 1;
            if (last < first) return [];
            bounds = new long[last - first + 2];
            for (var i = first; i <= last; i++) bounds[i - first] = _lineStarts[i];
            bounds[^1] = LineEnd(last);
            encoding = _encoding.ToEncoding();
            unit = _encoding.UnitSize();
        }

        var lines = new string[bounds.Length - 1];
        try
        {
            using var stream = OpenShared(Path);
            var total = bounds[^1] - bounds[0];
            if (total <= 4L * MaxLineBytes)
            {
                // Common case: one read for all visible lines.
                var buffer = new byte[total];
                stream.Position = bounds[0];
                stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                for (var i = 0; i < lines.Length; i++)
                {
                    var from = (int)(bounds[i] - bounds[0]);
                    var len = (int)Math.Min(bounds[i + 1] - bounds[i], MaxLineBytes);
                    lines[i] = Clean(encoding.GetString(buffer, from, len - len % unit));
                }
            }
            else
            {
                var buffer = new byte[MaxLineBytes];
                for (var i = 0; i < lines.Length; i++)
                {
                    var len = (int)Math.Min(bounds[i + 1] - bounds[i], MaxLineBytes);
                    stream.Position = bounds[i];
                    var read = stream.ReadAtLeast(buffer.AsSpan(0, len), len, throwOnEndOfStream: false);
                    lines[i] = Clean(encoding.GetString(buffer, 0, read - read % unit));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return lines;
    }

    public string ReadLine(int index) => ReadLines(index, 1) is [var line] ? line : string.Empty;

    // --- internals ---

    // Call with _lock held.
    private int CountLines()
    {
        if (_lineStarts.Count == 0) return 0;
        // The last start is a line only if it has bytes after it.
        return _lineStarts[^1] < _length ? _lineStarts.Count : _lineStarts.Count - 1;
    }

    private long LineEnd(int line) => line + 1 < _lineStarts.Count ? _lineStarts[line + 1] : _length;

    private void ResetIndex()
    {
        _lineStarts.Clear();
        _headSignature = [];
        _length = 0;
        _bomLength = 0;
        _bomResolved = false;
        _generation++;
        IsFullyIndexed = false;
    }

    private void StartIndex(byte[] head)
    {
        _bomLength = 0;
        _bomResolved = head.Length >= MaxBomLength;
        if (TextEncodings.DetectBom(head) is var (kind, bomLength))
        {
            _bomLength = bomLength;
            _bomResolved = true;
            if (!_encodingPinned) _encoding = kind;
        }
        _lineStarts.Add(_bomLength);
        _length = _bomLength;
    }

    private long AlignDown(long value)
    {
        var unit = _encoding.UnitSize();
        return value - Math.Max(0, value - _bomLength) % unit;
    }

    /// <summary>Reads chunks without the lock, then appends each chunk's line starts under the lock.</summary>
    private void Scan(FileStream stream, long from, long limit, int unit, bool bigEndian, int generation)
    {
        var buffer = new byte[ChunkSize];
        var found = new List<long>();
        var position = from;

        while (position < limit)
        {
            var toRead = (int)Math.Min(buffer.Length, limit - position);
            stream.Position = position;
            var read = stream.ReadAtLeast(buffer.AsSpan(0, toRead), toRead, throwOnEndOfStream: false);
            read -= read % unit;
            if (read == 0) break;
            var span = buffer.AsSpan(0, read);

            found.Clear();
            if (unit == 1)
            {
                var offset = 0;
                int idx;
                while ((idx = span[offset..].IndexOf((byte)'\n')) >= 0)
                {
                    offset += idx + 1;
                    found.Add(position + offset);
                }
            }
            else
            {
                for (var i = 0; i + 1 < read; i += 2)
                {
                    var isNewline = bigEndian ? span[i] == 0 && span[i + 1] == '\n' : span[i] == '\n' && span[i + 1] == 0;
                    if (isNewline) found.Add(position + i + 2);
                }
            }

            lock (_lock)
            {
                if (_generation != generation) return; // reset meanwhile (e.g. encoding changed)
                _lineStarts.AddRange(found);
                position += read;
                _length = position;
            }
        }
    }

    private static byte[] ReadHead(FileStream stream)
    {
        var head = new byte[(int)Math.Min(HeadSignatureSize, stream.Length)];
        stream.Position = 0;
        stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head;
    }

    /// <summary>Strips the line terminator and makes null bytes visible.</summary>
    private static string Clean(string text)
    {
        text = text.TrimEnd('\n').TrimEnd('\r');
        return text.Contains('\0') ? text.Replace("\0", "[00]") : text;
    }

    /// <summary>Opens for reading while other programs keep writing, renaming or deleting the file.</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
}
