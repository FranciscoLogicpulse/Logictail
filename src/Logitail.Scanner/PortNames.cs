namespace Logitail.Scanner;

/// <summary>Usual service on well-known ports. Only a hint: any program can listen on any port.</summary>
public static class PortNames
{
    private static readonly Dictionary<int, string> Tcp = new()
    {
        [20] = "FTP data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP",
        [81] = "HTTP", [88] = "Kerberos", [110] = "POP3", [111] = "RPC portmapper", [135] = "Windows RPC",
        [139] = "NetBIOS session", [143] = "IMAP", [389] = "LDAP", [443] = "HTTPS", [445] = "SMB", [465] = "SMTPS",
        [514] = "Remote shell", [587] = "SMTP submission", [631] = "Printing (IPP)", [636] = "LDAPS", [993] = "IMAPS",
        [995] = "POP3S", [1080] = "SOCKS proxy", [1433] = "SQL Server", [1521] = "Oracle", [1723] = "PPTP VPN",
        [1883] = "MQTT", [2049] = "NFS", [2375] = "Docker API", [3000] = "Web app", [3128] = "HTTP proxy",
        [3306] = "MySQL", [3389] = "Remote Desktop", [5000] = "Web app", [5060] = "SIP", [5432] = "PostgreSQL",
        [5672] = "RabbitMQ", [5900] = "VNC", [5985] = "WinRM", [6379] = "Redis", [8000] = "HTTP", [8008] = "HTTP",
        [8080] = "HTTP", [8081] = "HTTP", [8443] = "HTTPS", [8888] = "HTTP", [9000] = "Web app", [9090] = "Web admin",
        [9100] = "Printer (raw)", [9200] = "Elasticsearch", [27017] = "MongoDB",
    };

    private static readonly Dictionary<int, string> Udp = new()
    {
        [53] = "DNS", [67] = "DHCP", [69] = "TFTP", [123] = "NTP", [137] = "NetBIOS name", [138] = "NetBIOS datagram",
        [161] = "SNMP", [500] = "IPsec", [514] = "Syslog", [1900] = "SSDP", [5353] = "mDNS",
    };

    public static string? Name(PortProtocol protocol, int port) =>
        (protocol == PortProtocol.Tcp ? Tcp : Udp).GetValueOrDefault(port);

    /// <summary>Ports where the page title is a better "version" than a banner.</summary>
    public static bool IsHttp(int port) => port is 80 or 81 or 3000 or 5000 or 8000 or 8008 or 8080 or 8081 or 8888 or 9000 or 9090;

    public static bool IsHttps(int port) => port is 443 or 8443;
}
