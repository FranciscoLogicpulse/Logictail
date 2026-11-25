using System.Net;

namespace Logitail.Scanner;

public enum HostStatus { Alive, Dead, Unknown }

public enum ServiceKind { Http, Https, Http8080, Ftp, Ssh, Rdp, Smb }

public static class Services
{
    public static readonly ServiceKind[] All = Enum.GetValues<ServiceKind>();

    public static int Port(ServiceKind kind) => kind switch
    {
        ServiceKind.Http => 80,
        ServiceKind.Https => 443,
        ServiceKind.Http8080 => 8080,
        ServiceKind.Ftp => 21,
        ServiceKind.Ssh => 22,
        ServiceKind.Rdp => 3389,
        ServiceKind.Smb => 445,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Label(ServiceKind kind) => kind switch
    {
        ServiceKind.Http => "HTTP",
        ServiceKind.Https => "HTTPS",
        ServiceKind.Http8080 => "HTTP 8080",
        ServiceKind.Ftp => "FTP",
        ServiceKind.Ssh => "SSH",
        ServiceKind.Rdp => "RDP",
        ServiceKind.Smb => "SMB",
        _ => kind.ToString(),
    };
}

public sealed record FoundService(ServiceKind Kind, string? Detail);

/// <summary>Everything learned about one address in one scan.</summary>
public sealed record HostResult
{
    public required IPAddress Address { get; init; }
    public HostStatus Status { get; init; }
    public string? Name { get; init; }
    public string? NetBiosName { get; init; }
    public string? Workgroup { get; init; }
    public string? User { get; init; }
    public string? Mac { get; init; }
    public string? Manufacturer { get; init; }
    public IReadOnlyList<FoundService> Services { get; init; } = [];
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Why the status is Unknown, when a probe failed.</summary>
    public string? Error { get; init; }

    public uint SortKey => IpRangeParser.ToUInt(Address);
}
