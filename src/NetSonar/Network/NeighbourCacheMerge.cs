using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Completes a scan result with the operating system neighbour (ARP) cache.
/// </summary>
/// <remarks>
/// An unprivileged nmap cannot ARP-ping, so it falls back to TCP connect pings and reports a host as down
/// whenever those probes miss; the host set then changes between two scans of the same network. The neighbour
/// cache holds the devices the machine actually exchanged packets with, which both stabilizes the host list
/// and supplies the MAC address an unprivileged scan never sees.
/// The cache trails reality, so a device that just left the network can survive here until its entry expires.
/// </remarks>
public static class NeighbourCacheMerge
{
    /// <summary>
    /// Adds the neighbour cache entries that belong to the scanned target and fills in missing MAC addresses.
    /// </summary>
    /// <param name="hosts">The hosts the engine reported.</param>
    /// <param name="target">The scanned target list, used to reject cache entries from other networks.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>The merged hosts, ordered by address.</returns>
    public static async Task<IReadOnlyList<NetworkScannerHost>> ApplyAsync(
        IReadOnlyList<NetworkScannerHost> hosts,
        string target,
        NetworkScanOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var targets = ParseTargets(target);
        if (targets.Count == 0) return hosts;

        var neighbours = await ArpTable.GetAsync(cancellationToken);
        if (neighbours.Count == 0) return hosts;

        var merged = new List<NetworkScannerHost>(hosts.Count);
        var known = new HashSet<string>(hosts.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var host in hosts)
        {
            known.Add(host.Address);

            var macAddress = neighbours.GetValueOrDefault(host.Address, string.Empty);
            merged.Add(
                macAddress.Length == 0 || host.MacAddress.Length > 0
                    ? host
                    : host.WithNeighbour(macAddress, MacVendorLookup.Resolve(macAddress))
            );
        }

        var added = new List<KeyValuePair<string, string>>();
        foreach (var neighbour in neighbours)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (known.Contains(neighbour.Key) || !IsScanHost(targets, neighbour.Key))
                continue;

            added.Add(neighbour);
        }

        var hostnames = options.ResolveDns && added.Count > 0
            ? await NetworkHostResolver.ResolveHostNamesAsync(
                added.Select(neighbour => neighbour.Key).ToArray(),
                cancellationToken: cancellationToken
            )
            : null;

        for (var index = 0; index < added.Count; index++)
        {
            merged.Add(
                new NetworkScannerHost
                {
                    Address = added[index].Key,
                    Hostname = hostnames?[index] ?? string.Empty,
                    MacAddress = added[index].Value,
                    Vendor = MacVendorLookup.Resolve(added[index].Value),
                    State = "up",
                    LastSeen = DateTime.Now,
                }
            );
        }

        return merged.OrderBy(host => host.GetSortKey()).ToArray();
    }

    private static List<NetworkTarget> ParseTargets(string target)
    {
        var parsed = new List<NetworkTarget>();
        foreach (var part in NetworkTargetList.Split(target))
        {
            if (NetworkTarget.TryParse(part, out var networkTarget))
                parsed.Add(networkTarget);
        }

        return parsed;
    }

    /// <summary>
    /// Returns whether a cache address is a host of any target, rather than the network or broadcast address of
    /// the target that contains it.
    /// </summary>
    private static bool IsScanHost(IReadOnlyList<NetworkTarget> targets, string address)
    {
        return targets.Any(networkTarget =>
            networkTarget.Contains(address) && !networkTarget.IsNetworkOrBroadcast(address)
        );
    }
}
