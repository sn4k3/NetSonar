using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Resolves the host name of a scanned address.
/// </summary>
public static class NetworkHostResolver
{
    private const int MaxConcurrentLookups = 32;

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Looks up the host name of an address.
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The host name, or an empty string when the address has none.</returns>
    /// <remarks>
    /// A reverse lookup of an unknown address can hang until the resolver gives up, so it is bounded by its own
    /// timeout on top of <paramref name="cancellationToken"/>.
    /// </remarks>
    public static async Task<string> ResolveHostNameAsync(
        string address,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LookupTimeout);

            var entry = await Dns.GetHostEntryAsync(address, timeout.Token);
            return string.Equals(entry.HostName, address, StringComparison.Ordinal)
                ? string.Empty
                : entry.HostName;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return string.Empty;
        }
        catch (SocketException)
        {
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Looks up the host names of many addresses concurrently.
    /// </summary>
    /// <param name="addresses">The addresses to resolve.</param>
    /// <param name="onResolved">
    /// Called after each lookup with the number of lookups finished so far and the total. It runs on pool threads,
    /// possibly concurrently.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The host names, in the order of <paramref name="addresses"/>; an empty string for an unnamed address.</returns>
    /// <remarks>
    /// A lookup of an address without a PTR record waits for the full timeout, so resolving one address at a time
    /// makes a network with many such hosts take minutes.
    /// </remarks>
    public static async Task<string[]> ResolveHostNamesAsync(
        IReadOnlyList<string> addresses,
        Action<int, int>? onResolved = null,
        CancellationToken cancellationToken = default
    )
    {
        var names = new string[addresses.Count];
        var completed = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, addresses.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentLookups,
            },
            async (index, token) =>
            {
                names[index] = await ResolveHostNameAsync(addresses[index], token);
                onResolved?.Invoke(Interlocked.Increment(ref completed), names.Length);
            }
        );

        return names;
    }
}
