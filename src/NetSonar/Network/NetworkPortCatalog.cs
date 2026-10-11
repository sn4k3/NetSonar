using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NetSonar.Avalonia.Models;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Well-known port metadata shared by the scan engines and by the monitoring import.
/// </summary>
public static class NetworkPortCatalog
{
    /// <summary>
    /// The nmap <c>--top-ports 100</c> TCP set, used by the built-in engine so both engines probe the same ports.
    /// </summary>
    public static readonly int[] TopPorts =
    [
        7, 9, 13, 21, 22, 23, 25, 26, 37, 53, 79, 80, 81, 88, 106, 110, 111, 113, 119, 135, 139,
        143, 144, 179, 199, 389, 427, 443, 444, 445, 465, 513, 514, 515, 543, 544, 548, 554, 587,
        631, 646, 873, 990, 993, 995, 1025, 1026, 1027, 1028, 1029, 1110, 1433, 1720, 1723, 1755,
        1900, 2000, 2001, 2049, 2121, 2717, 3000, 3128, 3306, 3389, 3986, 4899, 5000, 5009, 5051,
        5060, 5101, 5190, 5357, 5432, 5631, 5666, 5800, 5900, 6000, 6001, 6646, 7070, 8000, 8008,
        8009, 8080, 8081, 8443, 8888, 9100, 9999, 10000, 32768, 49152, 49153, 49154, 49155, 49156,
        49157,
    ];

    private static readonly FrozenDictionary<int, string> ServiceNames = new Dictionary<int, string>
    {
        [7] = "echo",
        [9] = "discard",
        [13] = "daytime",
        [20] = "ftp-data",
        [21] = "ftp",
        [22] = "ssh",
        [23] = "telnet",
        [25] = "smtp",
        [37] = "time",
        [53] = "domain",
        [67] = "dhcps",
        [68] = "dhcpc",
        [69] = "tftp",
        [79] = "finger",
        [80] = "http",
        [81] = "hosts2-ns",
        [88] = "kerberos-sec",
        [110] = "pop3",
        [111] = "rpcbind",
        [113] = "ident",
        [119] = "nntp",
        [123] = "ntp",
        [135] = "msrpc",
        [137] = "netbios-ns",
        [138] = "netbios-dgm",
        [139] = "netbios-ssn",
        [143] = "imap",
        [161] = "snmp",
        [162] = "snmptrap",
        [179] = "bgp",
        [389] = "ldap",
        [427] = "svrloc",
        [443] = "https",
        [445] = "microsoft-ds",
        [465] = "smtps",
        [500] = "isakmp",
        [514] = "syslog",
        [515] = "printer",
        [548] = "afp",
        [554] = "rtsp",
        [587] = "submission",
        [631] = "ipp",
        [636] = "ldaps",
        [873] = "rsync",
        [990] = "ftps",
        [993] = "imaps",
        [995] = "pop3s",
        [1080] = "socks",
        [1194] = "openvpn",
        [1433] = "ms-sql-s",
        [1521] = "oracle",
        [1701] = "l2tp",
        [1720] = "h323q931",
        [1723] = "pptp",
        [1883] = "mqtt",
        [1900] = "upnp",
        [2049] = "nfs",
        [2082] = "cpanel",
        [2121] = "ccproxy-ftp",
        [2181] = "zookeeper",
        [2375] = "docker",
        [2376] = "docker-s",
        [3000] = "ppp",
        [3128] = "squid-http",
        [3260] = "iscsi",
        [3306] = "mysql",
        [3389] = "ms-wbt-server",
        [3478] = "stun",
        [3689] = "daap",
        [4444] = "krb524",
        [5000] = "upnp",
        [5060] = "sip",
        [5061] = "sip-tls",
        [5222] = "xmpp-client",
        [5353] = "mdns",
        [5432] = "postgresql",
        [5555] = "freeciv",
        [5601] = "kibana",
        [5672] = "amqp",
        [5900] = "vnc",
        [6000] = "x11",
        [6379] = "redis",
        [6667] = "irc",
        [7070] = "realserver",
        [8000] = "http-alt",
        [8008] = "http",
        [8009] = "ajp13",
        [8080] = "http-proxy",
        [8081] = "blackice-icecap",
        [8086] = "influxdb",
        [8096] = "jellyfin",
        [8123] = "home-assistant",
        [8443] = "https-alt",
        [8883] = "secure-mqtt",
        [8888] = "sun-answerbook",
        [9000] = "cslistener",
        [9090] = "zeus-admin",
        [9100] = "jetdirect",
        [9200] = "elasticsearch",
        [9999] = "abyss",
        [10000] = "snet-sensor-mgmt",
        [11211] = "memcached",
        [27017] = "mongodb",
        [32400] = "plex",
        [51820] = "wireguard",
    }.ToFrozenDictionary();

    /// <summary>
    /// Returns the well-known service name for a port, or an empty string when the port is unknown.
    /// </summary>
    /// <param name="port">The port number.</param>
    /// <returns>The service name, or an empty string.</returns>
    public static string GetServiceName(int port)
    {
        return ServiceNames.GetValueOrDefault(port, string.Empty);
    }

    /// <summary>
    /// Builds the port list the built-in engine probes for a given scan request.
    /// </summary>
    /// <param name="options">The port scan options.</param>
    /// <returns>The distinct, ordered port list.</returns>
    /// <remarks>
    /// <see cref="NetworkPortScanRange.Top1000"/> is approximated with ports 1-1024 plus the top-100 set, because the
    /// built-in engine has no copy of the nmap frequency database. The nmap engine passes <c>--top-ports 1000</c>
    /// instead and therefore uses the exact list.
    /// </remarks>
    public static int[] GetPorts(NetworkPortScanOptions options)
    {
        switch (options.Range)
        {
            case NetworkPortScanRange.Top100:
                return TopPorts;
            case NetworkPortScanRange.Top1000:
            {
                var ports = new HashSet<int>(TopPorts);
                for (var port = 1; port <= 1024; port++)
                    ports.Add(port);
                return ports.Order().ToArray();
            }
            case NetworkPortScanRange.Full:
            {
                var ports = new int[65535];
                for (var index = 0; index < ports.Length; index++)
                    ports[index] = index + 1;
                return ports;
            }
            case NetworkPortScanRange.Custom:
                return ParsePortSpec(options.CustomPorts);
            default:
                return TopPorts;
        }
    }

    /// <summary>
    /// Parses an nmap style port specification, such as <c>22,80,443,8000-8100</c>.
    /// </summary>
    /// <param name="value">The specification to parse.</param>
    /// <returns>The distinct, ordered port list. Empty when nothing could be parsed.</returns>
    public static int[] ParsePortSpec(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var ports = new HashSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePortRange(part, out var from, out var to)) continue;

            for (var port = from; port <= to; port++)
                ports.Add(port);
        }

        return ports.Order().ToArray();
    }

    /// <summary>
    /// Validates an nmap style port specification.
    /// </summary>
    /// <param name="value">The specification to validate.</param>
    /// <returns>
    /// <see langword="true"/> when the specification has at least one part and every part is a port or a range.
    /// Open ranges such as <c>-100</c>, <c>8000-</c> and <c>-</c> are accepted.
    /// </returns>
    /// <remarks>
    /// Strict on purpose: nmap rejects the whole <c>-p</c> argument when one part is invalid, so a lenient check
    /// would let a specification through that only one of the two engines can run.
    /// </remarks>
    public static bool IsValidPortSpec(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var hasPart = false;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePortRange(part, out _, out _)) return false;
            hasPart = true;
        }

        return hasPart;
    }

    private static bool TryParsePortRange(string part, out int from, out int to)
    {
        from = 0;
        to = 0;

        var separator = part.IndexOf('-');
        if (separator < 0)
        {
            if (!TryParsePort(part, out from)) return false;
            to = from;
            return true;
        }

        var fromText = part[..separator];
        var toText = part[(separator + 1)..];
        from = 1;
        to = 65535;
        if (fromText.Length > 0 && !TryParsePort(fromText, out from)) return false;
        if (toText.Length > 0 && !TryParsePort(toText, out to)) return false;
        if (from > to) (from, to) = (to, from);

        return true;
    }

    /// <summary>
    /// Builds a monitoring service for a scanned host, using the port to pick the closest protocol probe.
    /// </summary>
    /// <param name="host">The scanned host.</param>
    /// <param name="port">The open port, or <see langword="null"/> to create an ICMP monitor for the host itself.</param>
    /// <param name="group">The group assigned to the created service.</param>
    /// <returns>The monitoring service to import.</returns>
    /// <remarks>
    /// The well-known port decides the probe first, then the service name the scan reported, so a service on a
    /// non-standard port still gets its protocol probe. Anything else falls back to a plain TCP or UDP probe.
    /// </remarks>
    public static NewPingService CreatePingService(
        NetworkScannerHost host,
        NetworkScannerPort? port,
        string group
    )
    {
        var description = string.IsNullOrWhiteSpace(host.Hostname) ? host.Vendor : host.Hostname;
        if (port is null)
        {
            return new NewPingService(
                ServiceProtocolType.ICMP,
                host.Address,
                description,
                group
            );
        }

        var isUdp = string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase);
        var (protocolType, address) = isUdp
            ? MapUdpPort(host.Address, port.Number, port.Service)
            : MapPort(host.Address, port.Number, port.Service);
        var portDescription = string.IsNullOrWhiteSpace(port.Service)
            ? description
            : string.IsNullOrWhiteSpace(description)
                ? port.Service
                : $"{description} ({port.Service})";

        return new NewPingService(protocolType, address, portDescription, group);
    }

    /// <summary>
    /// Builds the browser URL of a web service listening on a port.
    /// </summary>
    /// <param name="address">The host address.</param>
    /// <param name="port">The port number.</param>
    /// <param name="url">Receives the URL, or an empty string when the port is not a web port.</param>
    /// <returns><see langword="true"/> when the port maps to an HTTP probe.</returns>
    public static bool TryGetWebUrl(string address, int port, out string url)
    {
        var (protocolType, mapped) = MapPort(address, port);
        url = protocolType == ServiceProtocolType.HTTP ? mapped : string.Empty;
        return url.Length > 0;
    }

    private static (ServiceProtocolType ProtocolType, string Address) MapPort(
        string address,
        int port,
        string? service = null
    )
    {
        var literal = address.Contains(':') ? $"[{address}]" : address;
        var mapped = port switch
        {
            22 => (ServiceProtocolType.SSH, $"{literal}:{port}"),
            25 or 587 => (ServiceProtocolType.SMTP, $"{literal}:{port}"),
            465 => (ServiceProtocolType.TLS, $"{literal}:{port}"),
            53 => (ServiceProtocolType.DNS, $"{literal}:{port}"),
            123 => (ServiceProtocolType.NTP, $"{literal}:{port}"),
            143 => (ServiceProtocolType.IMAP, $"{literal}:{port}"),
            993 or 995 or 636 or 990 or 5061 or 8883 => (
                ServiceProtocolType.TLS,
                $"{literal}:{port}"
            ),
            80 => (ServiceProtocolType.HTTP, $"http://{literal}"),
            443 => (ServiceProtocolType.HTTP, $"https://{literal}"),
            8443 => (ServiceProtocolType.HTTP, $"https://{literal}:{port}"),
            8000 or 8008 or 8080 or 8081 or 8096 or 8123 or 8888 or 9090 => (
                ServiceProtocolType.HTTP,
                $"http://{literal}:{port}"
            ),
            1883 => (ServiceProtocolType.MQTT, $"{literal}:{port}"),
            3478 => (ServiceProtocolType.STUN, $"{literal}:{port}"),
            5060 => (ServiceProtocolType.SIP, $"{literal}:{port}"),
            _ => ((ServiceProtocolType, string)?)null,
        };

        return mapped ?? MapTcpService(literal, port, service);
    }

    /// <summary>
    /// Maps a TCP port that is not well known by the service name the scan reported for it.
    /// </summary>
    private static (ServiceProtocolType ProtocolType, string Address) MapTcpService(
        string literal,
        int port,
        string? service
    )
    {
        var name = service?.Trim().ToLowerInvariant() ?? string.Empty;

        // nmap reports a TLS-wrapped service as "ssl/<name>".
        var isTls = name.StartsWith("ssl/", StringComparison.Ordinal);
        if (isTls)
            name = name[4..];

        return name switch
        {
            "http" or "http-alt" or "http-proxy" or "https" or "https-alt" => (
                ServiceProtocolType.HTTP,
                isTls || name.StartsWith("https", StringComparison.Ordinal)
                    ? $"https://{literal}:{port}"
                    : $"http://{literal}:{port}"
            ),
            "ssh" => (ServiceProtocolType.SSH, $"{literal}:{port}"),
            "smtp" or "submission" when !isTls => (ServiceProtocolType.SMTP, $"{literal}:{port}"),
            "imap" when !isTls => (ServiceProtocolType.IMAP, $"{literal}:{port}"),
            "domain" => (ServiceProtocolType.DNS, $"{literal}:{port}"),
            "mqtt" when !isTls => (ServiceProtocolType.MQTT, $"{literal}:{port}"),
            "sip" when !isTls => (ServiceProtocolType.SIP, $"{literal}:{port}"),
            "smtps" or "imaps" or "pop3s" or "ldaps" or "ftps" or "sip-tls" or "secure-mqtt" => (
                ServiceProtocolType.TLS,
                $"{literal}:{port}"
            ),
            _ when isTls => (ServiceProtocolType.TLS, $"{literal}:{port}"),
            _ => (ServiceProtocolType.TCP, $"{literal}:{port}"),
        };
    }

    /// <summary>
    /// Maps a UDP port to the closest probe, by well-known port first and reported service name second.
    /// </summary>
    private static (ServiceProtocolType ProtocolType, string Address) MapUdpPort(
        string address,
        int port,
        string? service
    )
    {
        var literal = address.Contains(':') ? $"[{address}]" : address;
        var name = service?.Trim().ToLowerInvariant() ?? string.Empty;

        var protocolType = port switch
        {
            53 => ServiceProtocolType.DNS,
            123 => ServiceProtocolType.NTP,
            3478 => ServiceProtocolType.STUN,
            5060 => ServiceProtocolType.SIP,
            _ => name switch
            {
                "domain" => ServiceProtocolType.DNS,
                "ntp" => ServiceProtocolType.NTP,
                "stun" => ServiceProtocolType.STUN,
                "sip" => ServiceProtocolType.SIP,
                _ => ServiceProtocolType.UDP,
            },
        };

        return (protocolType, $"{literal}:{port}");
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
}
