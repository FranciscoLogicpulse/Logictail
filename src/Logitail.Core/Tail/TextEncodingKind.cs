using System.Text;

namespace Logitail.Core.Tail;

/// <summary>Encodings a log file can be read as.</summary>
public enum TextEncodingKind
{
    /// <summary>Single-byte ANSI text, decoded as Latin-1 so every byte stays visible.</summary>
    Ascii,
    Utf8,
    Utf16Le,
    Utf16Be,
}

public static class TextEncodings
{
    public static Encoding ToEncoding(this TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Ascii => Encoding.Latin1,
        TextEncodingKind.Utf8 => new UTF8Encoding(false),
        TextEncodingKind.Utf16Le => new UnicodeEncoding(false, false),
        TextEncodingKind.Utf16Be => new UnicodeEncoding(true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Bytes per code unit: newlines are searched on this alignment.</summary>
    public static int UnitSize(this TextEncodingKind kind) =>
        kind is TextEncodingKind.Utf16Le or TextEncodingKind.Utf16Be ? 2 : 1;

    /// <summary>Detects a byte-order mark. Returns null when there is none.</summary>
    public static (TextEncodingKind Kind, int BomLength)? DetectBom(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (TextEncodingKind.Utf8, 3);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (TextEncodingKind.Utf16Le, 2);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (TextEncodingKind.Utf16Be, 2);
        return null;
    }

    public static string DisplayName(this TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Ascii => "ANSI",
        TextEncodingKind.Utf8 => "UTF-8",
        TextEncodingKind.Utf16Le => "UTF-16 LE",
        TextEncodingKind.Utf16Be => "UTF-16 BE",
        _ => kind.ToString(),
    };
}
