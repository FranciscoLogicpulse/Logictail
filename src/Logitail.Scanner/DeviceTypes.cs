namespace Logitail.Scanner;

public enum DeviceType { Unknown, Router, AccessPoint, Printer, Nas, Computer, Server, Phone, MediaDevice, GameConsole, Camera, VirtualMachine }

/// <summary>
/// Best guess at what a device is, from its manufacturer and open services.
/// A guess, not a fact: shown so the list is easier to scan, never used for decisions.
/// </summary>
public static class DeviceTypes
{
    private static readonly (string Vendor, DeviceType Type)[] VendorHints =
    [
        ("Synology", DeviceType.Nas), ("QNAP", DeviceType.Nas), ("Western Digital", DeviceType.Nas), ("Buffalo", DeviceType.Nas),
        ("Hewlett Packard", DeviceType.Printer), ("HP Inc", DeviceType.Printer), ("Brother", DeviceType.Printer), ("Seiko Epson", DeviceType.Printer),
        ("Canon", DeviceType.Printer), ("Xerox", DeviceType.Printer), ("Lexmark", DeviceType.Printer), ("Kyocera", DeviceType.Printer), ("Ricoh", DeviceType.Printer),
        ("Ubiquiti", DeviceType.AccessPoint), ("Ruckus", DeviceType.AccessPoint), ("Aruba", DeviceType.AccessPoint),
        ("TP-LINK", DeviceType.Router), ("NETGEAR", DeviceType.Router), ("MikroTik", DeviceType.Router), ("Routerboard", DeviceType.Router),
        ("ASUSTek", DeviceType.Router), ("Cisco", DeviceType.Router), ("Juniper", DeviceType.Router), ("Fortinet", DeviceType.Router),
        ("Huawei", DeviceType.Router), ("ZTE", DeviceType.Router), ("Technicolor", DeviceType.Router), ("Sagemcom", DeviceType.Router), ("AVM", DeviceType.Router),
        ("Sonos", DeviceType.MediaDevice), ("Roku", DeviceType.MediaDevice), ("Google", DeviceType.MediaDevice), ("Amazon", DeviceType.MediaDevice),
        ("Nintendo", DeviceType.GameConsole), ("Sony Interactive", DeviceType.GameConsole),
        ("Hikvision", DeviceType.Camera), ("Dahua", DeviceType.Camera), ("Axis Communications", DeviceType.Camera), ("Reolink", DeviceType.Camera),
        ("VMware", DeviceType.VirtualMachine), ("Xensource", DeviceType.VirtualMachine), ("QEMU", DeviceType.VirtualMachine),
        ("Apple", DeviceType.Phone), ("Samsung", DeviceType.Phone), ("Xiaomi", DeviceType.Phone), ("OnePlus", DeviceType.Phone),
    ];

    public static DeviceType Guess(HostResult host)
    {
        if (host.Status != HostStatus.Alive) return DeviceType.Unknown;
        var has = host.Services.Select(s => s.Kind).ToHashSet();
        var vendor = host.Manufacturer ?? "";

        // Services are stronger evidence than the vendor.
        // Only printers share printers and nothing else; a PC or NAS sharing a printer also shares folders.
        if (host.Shares.Count > 0 && host.Shares.All(s => s.Kind == ShareKind.Printer) && !has.Contains(ServiceKind.Rdp)) return DeviceType.Printer;
        if (has.Contains(ServiceKind.Rdp)) return host.Name?.Contains("srv", StringComparison.OrdinalIgnoreCase) == true ? DeviceType.Server : DeviceType.Computer;

        foreach (var (hint, type) in VendorHints)
        {
            if (!vendor.Contains(hint, StringComparison.OrdinalIgnoreCase)) continue;
            // Phone and printer vendors also make computers: a phone or printer doesn't share files or offer SSH.
            if (type is DeviceType.Phone or DeviceType.Printer && (has.Contains(ServiceKind.Smb) || has.Contains(ServiceKind.Ssh))) return DeviceType.Computer;
            return type;
        }
        if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && host.Mac?.StartsWith("00-15-5D", StringComparison.OrdinalIgnoreCase) == true)
            return DeviceType.VirtualMachine; // Hyper-V
        if (has.Contains(ServiceKind.Smb) || host.NetBiosName is not null) return DeviceType.Computer;
        if (has.Contains(ServiceKind.Ssh)) return DeviceType.Server;
        return DeviceType.Unknown;
    }

    public static string Label(DeviceType type) => type switch
    {
        DeviceType.AccessPoint => "Access point",
        DeviceType.Nas => "NAS",
        DeviceType.MediaDevice => "Media device",
        DeviceType.GameConsole => "Game console",
        DeviceType.VirtualMachine => "Virtual machine",
        DeviceType.Unknown => "",
        _ => type.ToString(),
    };
}
