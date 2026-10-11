using System;
using System.Net;
using System.Net.Sockets;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Tests whether an address belongs to a scan target, without expanding the target into addresses.
/// </summary>
/// <remarks>
/// Expanding is fine for the built-in sweep, which has to probe every address anyway, but a membership test
/// must also work for targets far larger than the host limit.
/// </remarks>
public readonly struct NetworkTarget
{
    private readonly uint _first;
    private readonly uint _last;
    private readonly bool _hasNetworkAndBroadcast;

    private NetworkTarget(uint first, uint last, bool hasNetworkAndBroadcast = false)
    {
        _first = first;
        _last = last;
        _hasNetworkAndBroadcast = hasNetworkAndBroadcast;
    }

    /// <summary>
    /// Gets the number of addresses the target spans, including the network and broadcast addresses of a CIDR.
    /// </summary>
    public long AddressCount => (long)_last - _first + 1;

    /// <summary>
    /// Gets the number of addresses a scan probes: a CIDR of /30 or wider leaves out its network and broadcast
    /// addresses.
    /// </summary>
    public long UsableHostCount => _hasNetworkAndBroadcast ? AddressCount - 2 : AddressCount;

    /// <summary>
    /// Describes a target that expands to more addresses than a scan allows.
    /// </summary>
    /// <param name="requiredHosts">The number of addresses the target expands to.</param>
    /// <param name="maxHosts">The limit the target exceeds.</param>
    /// <returns>The message shared by both scan engines.</returns>
    public static string DescribeLimit(long requiredHosts, int maxHosts)
    {
        return $"The target expands to {requiredHosts} addresses, above the {maxHosts} limit.";
    }

    /// <summary>
    /// Parses a target expression into an address range.
    /// </summary>
    /// <param name="target">An IPv4 address, CIDR network, or last-octet range.</param>
    /// <param name="networkTarget">Receives the parsed range.</param>
    /// <returns><see langword="true"/> when the expression describes an IPv4 range.</returns>
    public static bool TryParse(string? target, out NetworkTarget networkTarget)
    {
        networkTarget = default;
        if (string.IsNullOrWhiteSpace(target)) return false;

        var value = target.Trim();

        var separator = value.LastIndexOf('/');
        if (separator > 0)
        {
            if (
                !int.TryParse(value[(separator + 1)..], out var prefix)
                || prefix is < 0 or > 32
                || !TryParseIpv4(value[..separator], out var network)
            )
                return false;

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            networkTarget = new NetworkTarget(network & mask, (network & mask) | ~mask, prefix <= 30);
            return true;
        }

        if (TryParseIpv4(value, out var single))
        {
            networkTarget = new NetworkTarget(single, single);
            return true;
        }

        return TryParseLastOctetRange(value, out networkTarget);
    }

    /// <summary>
    /// Returns whether an address falls inside the target.
    /// </summary>
    /// <param name="address">The address to test.</param>
    /// <returns><see langword="true"/> when the address belongs to the target.</returns>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var value = ToUInt32(address.GetAddressBytes());
        return value >= _first && value <= _last;
    }

    /// <summary>
    /// Returns whether a textual address falls inside the target.
    /// </summary>
    /// <param name="address">The address to test.</param>
    /// <returns><see langword="true"/> when the address belongs to the target.</returns>
    public bool Contains(string address)
    {
        return IPAddress.TryParse(address, out var parsed) && Contains(parsed);
    }

    /// <summary>
    /// Returns whether an address is the network or broadcast address of a CIDR target, which is not a host.
    /// </summary>
    /// <param name="address">The address to test.</param>
    /// <returns>
    /// <see langword="true"/> when the target is a CIDR of /30 or wider and the address is its first or last address.
    /// </returns>
    public bool IsNetworkOrBroadcast(string address)
    {
        if (
            !_hasNetworkAndBroadcast
            || !IPAddress.TryParse(address, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
        )
            return false;

        var value = ToUInt32(parsed.GetAddressBytes());
        return value == _first || value == _last;
    }

    private static bool TryParseLastOctetRange(string value, out NetworkTarget networkTarget)
    {
        networkTarget = default;

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
        if (fromText.Length == 0) fromText = "0";
        if (toText.Length == 0) toText = "255";
        if (!byte.TryParse(fromText, out var from) || !byte.TryParse(toText, out var to))
            return false;
        if (from > to) (from, to) = (to, from);

        var prefix = ToUInt32([first, second, third, 0]);
        networkTarget = new NetworkTarget(prefix | from, prefix | to);
        return true;
    }

    private static bool TryParseIpv4(string value, out uint address)
    {
        address = 0;
        if (
            !IPAddress.TryParse(value, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
        )
            return false;

        address = ToUInt32(parsed.GetAddressBytes());
        return true;
    }

    private static uint ToUInt32(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
