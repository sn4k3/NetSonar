using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Diagnostics;
using NetSonar.Avalonia.Extensions;
using StageKit;
using StageKit.Primitives.System;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Reads the operating system neighbour (ARP) cache, which reveals hosts that answer at link level while
/// dropping ICMP.
/// </summary>
public static partial class ArpTable
{
    [GeneratedRegex(
        @"(?<ip>\d{1,3}(?:\.\d{1,3}){3}).{0,40}?(?<mac>(?:[0-9a-fA-F]{1,2}[:-]){5}[0-9a-fA-F]{1,2})",
        RegexOptions.ExplicitCapture
    )]
    private static partial Regex ArpEntryRegex { get; }

    /// <summary>
    /// Reads the current neighbour cache.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A map of IPv4 address to normalized MAC address. Empty when the cache cannot be read.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var text = OperatingSystem.IsLinux()
                ? await ReadLinuxTableAsync(cancellationToken)
                : await ReadArpCommandAsync(cancellationToken);

            return Parse(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            UnhandledExceptions.HandleSafeException(e, nameof(ArpTable));
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Parses the textual output of a neighbour cache dump.
    /// </summary>
    /// <param name="text">The <c>arp -a</c>, <c>ip neigh</c>, or <c>/proc/net/arp</c> output.</param>
    /// <returns>A map of IPv4 address to normalized MAC address.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return entries;

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsWhiteSpace()) continue;

            var match = ArpEntryRegex.Match(line.ToString());
            if (!match.Success) continue;

            var mac = NormalizeMacAddress(match.Groups["mac"].ValueSpan);
            if (mac.Length == 0) continue;
            if (!IPAddress.TryParse(match.Groups["ip"].ValueSpan, out var address)) continue;

            // The cache also lists the broadcast and multicast groups the machine joined; none of them is a host.
            if (IsGroupMacAddress(mac) || IsMulticastOrBroadcast(address)) continue;

            entries[address.ToString()] = mac;
        }

        return entries;
    }

    private static bool IsGroupMacAddress(string normalizedMac)
    {
        // The least-significant bit of the first octet marks a group address; broadcast (FF:...) has it set.
        return (HexDigit(normalizedMac[1]) & 1) != 0;
    }

    private static bool IsMulticastOrBroadcast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        Span<byte> octets = stackalloc byte[4];
        return address.TryWriteBytes(octets, out _)
            && (octets[0] is >= 224 and <= 239 || octets is [255, 255, 255, 255]);
    }

    /// <summary>
    /// Normalizes a MAC address to upper-case colon-separated form, rejecting all-zero addresses.
    /// </summary>
    /// <param name="value">The raw MAC address.</param>
    /// <returns>The normalized address, or an empty string when it is not usable.</returns>
    public static string NormalizeMacAddress(ReadOnlySpan<char> value)
    {
        Span<byte> octets = stackalloc byte[6];
        var count = 0;
        var current = 0;
        var digits = 0;

        foreach (var character in value)
        {
            if (character is ':' or '-' or '.')
            {
                if (digits == 0) return string.Empty;
                if (count == octets.Length) return string.Empty;
                octets[count++] = (byte)current;
                current = 0;
                digits = 0;
                continue;
            }

            var digit = HexDigit(character);
            if (digit < 0 || ++digits > 2) return string.Empty;
            current = (current << 4) | digit;
        }

        if (digits == 0 || count != octets.Length - 1) return string.Empty;
        octets[count] = (byte)current;

        var isEmpty = true;
        foreach (var octet in octets)
        {
            if (octet == 0) continue;
            isEmpty = false;
            break;
        }

        if (isEmpty) return string.Empty;

        Span<char> buffer = stackalloc char[17];
        for (var index = 0; index < octets.Length; index++)
        {
            var offset = index * 3;
            if (index > 0) buffer[offset - 1] = ':';
            octets[index].TryFormat(buffer[offset..(offset + 2)], out _, "X2");
        }

        return new string(buffer);
    }

    private static int HexDigit(char character)
    {
        return character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };
    }

    private static async Task<string> ReadLinuxTableAsync(CancellationToken cancellationToken)
    {
        const string procPath = "/proc/net/arp";
        if (File.Exists(procPath))
        {
            return await File.ReadAllTextAsync(procPath, cancellationToken);
        }

        if (HostSystem.TryFindExecutable("ip", out var ipPath))
        {
            var neighbours = await ReadCommandAsync(ipPath, ["neigh", "show"], cancellationToken);
            if (neighbours.Length > 0) return neighbours;
        }

        return await ReadArpCommandAsync(cancellationToken);
    }

    private static Task<string> ReadArpCommandAsync(CancellationToken cancellationToken)
    {
        var executable = HostSystem.NormalizeExecutableExtension("arp");
        if (!HostSystem.TryFindExecutable(executable, out var path)) path = executable;

        return ReadCommandAsync(path, ["-a"], cancellationToken);
    }

    private static async Task<string> ReadCommandAsync(
        string executable,
        string[] arguments,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var lines = await ProcessX
                .StartAsync(ProcessXExtensions.CreateStartInfo(executable, arguments))
                .ToTask(cancellationToken);
            return string.Join(Environment.NewLine, lines);
        }
        catch (ProcessErrorException)
        {
            return string.Empty;
        }
    }
}
