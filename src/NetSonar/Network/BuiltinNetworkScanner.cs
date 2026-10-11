using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Scans a network without nmap, using an ICMP sweep, the neighbour cache, and TCP connect probes.
/// </summary>
/// <remarks>
/// It is deliberately less capable than nmap: no SYN scan, no UDP scan, no OS fingerprinting, and service
/// names come from the well-known port catalogue instead of a version probe. It exists so the module works
/// on a machine without nmap installed.
/// </remarks>
public static class BuiltinNetworkScanner
{
    private const int MaxConcurrentProbes = 256;

    /// <summary>
    /// Expands a target expression into the addresses to probe.
    /// </summary>
    /// <param name="target">An address, CIDR network, last-octet range, or host name.</param>
    /// <param name="maxHosts">The maximum number of addresses the target may expand to.</param>
    /// <param name="addresses">Receives the expanded addresses.</param>
    /// <param name="requiredHosts">Receives the size the target expands to, even when it exceeds the limit.</param>
    /// <returns><see langword="true"/> when the target expanded within the limit.</returns>
    public static bool TryExpandTarget(
        string? target,
        int maxHosts,
        out IReadOnlyList<IPAddress> addresses,
        out long requiredHosts
    )
    {
        addresses = [];
        requiredHosts = 0;
        if (string.IsNullOrWhiteSpace(target)) return false;

        var value = target.Trim();

        var separator = value.LastIndexOf('/');
        if (separator > 0)
        {
            if (
                !int.TryParse(value[(separator + 1)..], out var prefix)
                || !IPAddress.TryParse(value[..separator], out var network)
                || network.AddressFamily != AddressFamily.InterNetwork
                || prefix is < 0 or > 32
            )
                return false;

            requiredHosts = prefix >= 31 ? 1L << (32 - prefix) : (1L << (32 - prefix)) - 2;
            if (requiredHosts > maxHosts) return false;

            addresses = ExpandCidr(network, prefix);
            return addresses.Count > 0;
        }

        if (IPAddress.TryParse(value, out var single))
        {
            requiredHosts = 1;
            addresses = [single];
            return true;
        }

        if (TryExpandLastOctetRange(value, maxHosts, out addresses, out requiredHosts)) return true;

        try
        {
            var resolved = Dns.GetHostAddresses(value);
            if (resolved.Length == 0) return false;
            requiredHosts = resolved.Length;
            addresses = resolved;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Discovers the hosts that answer on a target.
    /// </summary>
    /// <param name="target">The target expression.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="progress">Receives scan progress.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The hosts that answered ICMP or are present in the neighbour cache.</returns>
    /// <exception cref="InvalidOperationException">The target is not supported or is too large.</exception>
    public static async Task<NetworkScanResult> DiscoverAsync(
        string target,
        NetworkScanOptions? options = null,
        IProgress<NetworkScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        options ??= new NetworkScanOptions();

        var addresses = ExpandTargets(target, options.MaxHosts);

        var responded = new ConcurrentDictionary<string, byte>(
            Environment.ProcessorCount,
            addresses.Count,
            StringComparer.Ordinal
        );

        var completed = 0;

        await Parallel.ForEachAsync(
            addresses,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentProbes,
            },
            async (address, token) =>
            {
                try
                {
                    if (await IsHostUpAsync(address, options.ProbeTimeoutMilliseconds, token))
                    {
                        responded.TryAdd(address.ToString(), 0);
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(
                        new NetworkScanProgress(
                            "ICMP sweep",
                            done * 100d / addresses.Count
                        )
                    );
                }
            }
        );

        var arp = await ArpTable.GetAsync(cancellationToken);
        var targeted = addresses.Select(address => address.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in arp)
        {
            // A device that ignores ICMP still answers ARP, so the neighbour cache adds real hosts.
            if (targeted.Contains(entry.Key)) responded.TryAdd(entry.Key, 0);
        }

        var ordered = responded.Keys.OrderBy(GetSortKey).ToArray();
        var hostnames = options.ResolveDns && ordered.Length > 0
            ? await NetworkHostResolver.ResolveHostNamesAsync(
                ordered,
                (done, total) =>
                    progress?.Report(new NetworkScanProgress("Resolving host names", done * 100d / total)),
                cancellationToken
            )
            : null;

        var hosts = new List<NetworkScannerHost>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var address = ordered[index];
            var macAddress = arp.GetValueOrDefault(address, string.Empty);
            hosts.Add(
                new NetworkScannerHost
                {
                    Address = address,
                    Hostname = hostnames?[index] ?? string.Empty,
                    MacAddress = macAddress,
                    Vendor = MacVendorLookup.Resolve(macAddress),
                    State = "up",
                    LastSeen = DateTime.Now,
                }
            );
        }

        return new NetworkScanResult(hosts, NetworkScanEngine.Builtin, string.Empty);
    }

    /// <summary>
    /// Probes the ports of known hosts with TCP connect attempts.
    /// </summary>
    /// <param name="hosts">The addresses to probe.</param>
    /// <param name="options">The port scan options.</param>
    /// <param name="progress">Receives scan progress.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The probed hosts with their open ports.</returns>
    /// <exception cref="InvalidOperationException">The port specification resolves to no port.</exception>
    public static async Task<NetworkScanResult> ScanPortsAsync(
        IEnumerable<string> hosts,
        NetworkPortScanOptions? options = null,
        IProgress<NetworkScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        options ??= new NetworkPortScanOptions();

        var targets = hosts
            .Distinct(StringComparer.Ordinal)
            .Select(host => (Host: host, Address: IPAddress.TryParse(host, out var parsed) ? parsed : null))
            .Where(target => target.Address is not null)
            .ToArray();

        if (targets.Length == 0)
        {
            return NetworkScanResult.Empty(NetworkScanEngine.Builtin);
        }

        // An empty result would make the caller treat every requested host as having no open ports.
        var ports = NetworkPortCatalog.GetPorts(options);
        if (ports.Length == 0)
        {
            throw new InvalidOperationException("The port specification does not contain any valid port.");
        }

        var total = (long)targets.Length * ports.Length;
        var completed = 0L;
        var results = new List<NetworkScannerHost>(targets.Length);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var open = new ConcurrentBag<int>();
            await Parallel.ForEachAsync(
                ports,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxConcurrentProbes,
                },
                async (port, token) =>
                {
                    if (
                        await IsPortOpenAsync(
                            target.Address!,
                            port,
                            options.ProbeTimeoutMilliseconds,
                            token
                        )
                    )
                    {
                        open.Add(port);
                    }

                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(
                        new NetworkScanProgress(
                            $"TCP connect scan {target.Host}",
                            done * 100d / total
                        )
                    );
                }
            );

            results.Add(
                new NetworkScannerHost
                {
                    Address = target.Host,
                    State = "up",
                    LastSeen = DateTime.Now,
                    IsPortScanned = true,
                    Ports = open.Order()
                        .Select(port => new NetworkScannerPort
                        {
                            Number = port,
                            Protocol = "tcp",
                            State = "open",
                            Service = NetworkPortCatalog.GetServiceName(port),
                        })
                        .ToArray(),
                }
            );
        }

        return new NetworkScanResult(results, NetworkScanEngine.Builtin, string.Empty);
    }

    private static async Task<bool> IsHostUpAsync(
        IPAddress address,
        int timeoutMilliseconds,
        CancellationToken cancellationToken
    )
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(
                address,
                TimeSpan.FromMilliseconds(Math.Max(100, timeoutMilliseconds)),
                cancellationToken: cancellationToken
            );
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PingException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<bool> IsPortOpenAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken
    )
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(100, timeoutMilliseconds));

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return socket.Connected;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Expands every part of a target list into one list of distinct addresses.
    /// </summary>
    /// <param name="target">The target list.</param>
    /// <param name="maxHosts">The maximum number of addresses the targets may expand to, in total.</param>
    /// <returns>The distinct addresses, in the order their targets were typed.</returns>
    /// <exception cref="InvalidOperationException">A target cannot be expanded, or the targets are too large.</exception>
    private static IReadOnlyList<IPAddress> ExpandTargets(string target, int maxHosts)
    {
        var parts = NetworkTargetList.Split(target);
        if (parts.Count == 0)
            throw new InvalidOperationException($"The built-in scanner cannot expand the target '{target}'.");

        var addresses = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var requiredHosts = 0L;

        foreach (var part in parts)
        {
            var expanded = TryExpandTarget(part, maxHosts, out var partAddresses, out var partRequired);
            requiredHosts += partRequired;
            if (requiredHosts > maxHosts)
                throw new InvalidOperationException(NetworkTarget.DescribeLimit(requiredHosts, maxHosts));

            if (!expanded)
                throw new InvalidOperationException($"The built-in scanner cannot expand the target '{part}'.");

            foreach (var address in partAddresses)
            {
                if (seen.Add(address.ToString()))
                    addresses.Add(address);
            }
        }

        return addresses;
    }

    private static IReadOnlyList<IPAddress> ExpandCidr(IPAddress network, int prefix)
    {
        var bytes = network.GetAddressBytes();
        var start = BinaryPrimitivesToUInt32(bytes);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var first = start & mask;
        var last = first | ~mask;

        // Skip the network and broadcast addresses on networks that have them.
        var from = prefix <= 30 ? first + 1 : first;
        var to = prefix <= 30 ? last - 1 : last;

        var addresses = new List<IPAddress>((int)(to - from + 1));
        for (var value = from; value <= to; value++)
        {
            addresses.Add(new IPAddress(ToAddressBytes(value)));
            if (value == uint.MaxValue) break;
        }

        return addresses;
    }

    private static bool TryExpandLastOctetRange(
        string value,
        int maxHosts,
        out IReadOnlyList<IPAddress> addresses,
        out long requiredHosts
    )
    {
        addresses = [];
        requiredHosts = 0;

        var octets = value.Split('.');
        if (octets.Length != 4) return false;

        var separator = octets[3].IndexOf('-');
        if (separator < 0) return false;

        if (
            !byte.TryParse(octets[0], out var first)
            || !byte.TryParse(octets[1], out var second)
            || !byte.TryParse(octets[2], out var third)
        )
            return false;

        var fromText = octets[3][..separator];
        var toText = octets[3][(separator + 1)..];
        if (!byte.TryParse(fromText, out var from)) return false;
        if (toText.Length == 0) toText = "255";
        if (!byte.TryParse(toText, out var to)) return false;
        if (from > to) (from, to) = (to, from);

        requiredHosts = to - from + 1;
        if (requiredHosts > maxHosts) return false;

        var expanded = new List<IPAddress>((int)requiredHosts);
        for (var last = from; ; last++)
        {
            expanded.Add(new IPAddress([first, second, third, last]));
            if (last == to) break;
        }

        addresses = expanded;
        return true;
    }

    private static uint BinaryPrimitivesToUInt32(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static byte[] ToAddressBytes(uint value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static ulong GetSortKey(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed)) return ulong.MaxValue;
        if (parsed.AddressFamily != AddressFamily.InterNetwork) return ulong.MaxValue - 1;

        return BinaryPrimitivesToUInt32(parsed.GetAddressBytes());
    }
}
