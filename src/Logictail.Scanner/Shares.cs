using System.Net;
using System.Runtime.InteropServices;

namespace Logictail.Scanner;

public enum ShareKind { Folder, Printer }

public sealed record SharedResource(string Name, ShareKind Kind, string? Remark);

/// <summary>Lists SMB shares with NetShareEnum, the same call Explorer uses for \\host.</summary>
public static partial class SmbShares
{
    private const uint StypeDisk = 0, StypePrintq = 1, StypeMask = 0xFF, StypeSpecial = 0x80000000;
    private const int MaxPreferredLength = -1;

    /// <summary>
    /// Shares visible to the current Windows user, without hidden admin shares (C$, IPC$...).
    /// Returns an empty list when the host refuses or doesn't answer. Blocks, so call off the UI thread.
    /// </summary>
    public static IReadOnlyList<SharedResource> List(IPAddress ip, bool includeHidden = false)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var status = NetShareEnum(@"\\" + ip, 1, out var buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
        try
        {
            if (status != 0 || buffer == IntPtr.Zero) return [];
            var size = Marshal.SizeOf<ShareInfo1>();
            var result = new List<SharedResource>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                var kind = Classify(info.Name, info.Type) ?? (includeHidden && info.Name is not null ? ShareKind.Folder : null);
                if (kind is { } k)
                    result.Add(new SharedResource(info.Name!, k, string.IsNullOrWhiteSpace(info.Remark) ? null : info.Remark));
            }
            return result;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    /// <summary>Folder or printer to show, or null for hidden/admin/IPC shares.</summary>
    public static ShareKind? Classify(string? name, uint type)
    {
        if (string.IsNullOrEmpty(name) || name.EndsWith('$') || (type & StypeSpecial) != 0) return null;
        return (type & StypeMask) switch
        {
            StypeDisk => ShareKind.Folder,
            StypePrintq => ShareKind.Printer,
            _ => null,
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
