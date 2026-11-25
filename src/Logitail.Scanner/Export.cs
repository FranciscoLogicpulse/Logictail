using System.Net;
using System.Text;

namespace Logitail.Scanner;

/// <summary>Writes scan results as CSV or HTML.</summary>
public static class Export
{
    private static readonly string[] Headers =
        ["Status", "Name", "IP", "Manufacturer", "MAC address", "Type", "NetBIOS name", "Workgroup", "Services", "Open ports", "Shares", "Comments"];

    private static string[] Fields(HostResult h) =>
    [
        h.Status.ToString(), h.Name ?? "", h.Address.ToString(), h.Manufacturer ?? "", h.Mac ?? "",
        DeviceTypes.Label(h.DeviceType), h.NetBiosName ?? "", h.Workgroup ?? "",
        string.Join("; ", h.Services.Select(s => s.Detail is null ? Services.Label(s.Kind) : $"{Services.Label(s.Kind)} ({s.Detail})")),
        // Only ports that answered; silent UDP ports are "maybe" and would list every port behind a firewall.
        string.Join("; ", h.OpenPorts.Where(p => p.Confirmed).Select(p => p.Detail is null ? p.Label : $"{p.Label} ({p.Detail})")),
        string.Join("; ", h.Shares.Select(s => s.Name)),
        h.Comment ?? "",
    ];

    /// <summary>CSV with a header row. Use ';' for Excel in locales where ',' is the decimal separator.</summary>
    public static string ToCsv(IEnumerable<HostResult> hosts, char delimiter = ',')
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, Headers.Select(f => Csv(f, delimiter))));
        foreach (var h in hosts) sb.AppendLine(string.Join(delimiter, Fields(h).Select(f => Csv(f, delimiter))));
        return sb.ToString();
    }

    /// <summary>Self-contained HTML table. Every value is HTML-encoded: names and page titles come from the network.</summary>
    public static string ToHtml(IEnumerable<HostResult> hosts, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>")
          .Append("<style>body{font:14px system-ui,sans-serif;margin:24px}table{border-collapse:collapse}")
          .Append("th,td{border:1px solid #ccc;padding:4px 8px;text-align:left;vertical-align:top}th{background:#f3f3f3}</style></head><body>")
          .Append("<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1><table><tr>");
        foreach (var h in Headers) sb.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
        sb.Append("</tr>");
        foreach (var host in hosts)
        {
            sb.Append("<tr>");
            foreach (var f in Fields(host)) sb.Append("<td>").Append(WebUtility.HtmlEncode(f)).Append("</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</table></body></html>").ToString();
    }

    private static string Csv(string value, char delimiter)
    {
        // Values starting with = + - @ are run as formulas by spreadsheet apps: prefix a quote to keep them text.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
