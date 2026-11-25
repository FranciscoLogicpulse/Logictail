using Logictail.Core.Localization;
using System.Diagnostics;
using System.Net;

namespace Logictail.Scanner;

public enum HostAction { Explore, OpenHttp, OpenHttps, OpenHttp8080, OpenFtp, Rdp, Ssh, Telnet, Ping, Tracert }

/// <summary>
/// Builds the program launch for each host action. Commands use only the IP address,
/// never names or titles from the network, so a hostile device can't inject a command.
/// </summary>
public static class HostActions
{
    public static string Label(HostAction action) => L._(action switch
    {
        HostAction.Explore => "Explore shares",
        HostAction.OpenHttp => "Open in browser (HTTP)",
        HostAction.OpenHttps => "Open in browser (HTTPS)",
        HostAction.OpenHttp8080 => "Open in browser (port 8080)",
        HostAction.OpenFtp => "Open FTP",
        HostAction.Rdp => "Connect with Remote Desktop",
        HostAction.Ssh => "Connect with SSH",
        HostAction.Telnet => "Connect with Telnet",
        HostAction.Ping => "Ping",
        HostAction.Tracert => "Trace route",
        _ => action.ToString(),
    });

    /// <summary>True when the host showed the service this action needs (ping and trace route always apply).</summary>
    public static bool Applies(HostAction action, HostResult host)
    {
        bool Has(ServiceKind k) => host.Services.Any(s => s.Kind == k);
        return action switch
        {
            HostAction.Explore => Has(ServiceKind.Smb) || host.Shares.Count > 0,
            HostAction.OpenHttp => Has(ServiceKind.Http),
            HostAction.OpenHttps => Has(ServiceKind.Https),
            HostAction.OpenHttp8080 => Has(ServiceKind.Http8080),
            HostAction.OpenFtp => Has(ServiceKind.Ftp),
            HostAction.Rdp => Has(ServiceKind.Rdp),
            HostAction.Ssh => Has(ServiceKind.Ssh),
            HostAction.Telnet => false, // not scanned; offered from the "all actions" menu only
            _ => true,
        };
    }

    public static ProcessStartInfo Command(HostAction action, IPAddress ip, string? share = null)
    {
        var host = ip.ToString(); // dotted decimal only
        return action switch
        {
            HostAction.Explore => Program("explorer.exe", share is null ? $@"\\{host}" : $@"\\{host}\{SafeShare(share)}"),
            HostAction.OpenHttp => Url($"http://{host}/"),
            HostAction.OpenHttps => Url($"https://{host}/"),
            HostAction.OpenHttp8080 => Url($"http://{host}:8080/"),
            HostAction.OpenFtp => Url($"ftp://{host}/"),
            HostAction.Rdp => Program("mstsc.exe", $"/v:{host}"),
            HostAction.Ssh => Console($"ssh {host}"),
            HostAction.Telnet => Console($"telnet {host}"),
            HostAction.Ping => Console($"ping {host}"),
            HostAction.Tracert => Console($"tracert {host}"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    /// <summary>UNC path to show or copy, e.g. <c>\\192.168.1.5\Media</c>.</summary>
    public static string UncPath(IPAddress ip, string? share = null) =>
        share is null ? $@"\\{ip}" : $@"\\{ip}\{SafeShare(share)}";

    private static string SafeShare(string share)
    {
        // Share names come from the remote host: refuse anything that could change the path.
        if (share.Length == 0 || share.IndexOfAny(['\\', '/', '"', ':', '\0']) >= 0 || share.Contains(".."))
            throw new ArgumentException(L.F("Refusing unusual share name \"{0}\"", share), nameof(share));
        return share;
    }

    private static ProcessStartInfo Program(string file, string argument) =>
        new(file) { UseShellExecute = false, ArgumentList = { argument } };

    private static ProcessStartInfo Url(string url) => new(url) { UseShellExecute = true };

    /// <summary>Console window that stays open so the user can read the output.</summary>
    private static ProcessStartInfo Console(string command) =>
        new("cmd.exe") { UseShellExecute = true, Arguments = "/k " + command };
}
