using System;
using System.Collections.Generic;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Splits the target box into the individual target expressions it holds, such as several CIDR networks.
/// </summary>
/// <remarks>
/// Targets are separated by whitespace, semicolons, or commas. A comma also separates the octets of an nmap target
/// such as <c>192.168.1,2.1</c>, so a comma-separated token is kept whole when it is a valid target on its own.
/// </remarks>
public static class NetworkTargetList
{
    /// <summary>
    /// Splits a target box into its distinct target expressions, in the order they were typed.
    /// </summary>
    /// <param name="targets">The text of the target box.</param>
    /// <returns>The trimmed expressions, without empty entries or case-insensitive duplicates.</returns>
    public static IReadOnlyList<string> Split(string? targets)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(targets))
            return parts;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = targets
            .Replace(';', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (token.Contains(',') && !NmapScannerService.IsValidSingleTarget(token))
            {
                var options = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
                foreach (var part in token.Split(',', options))
                    AddDistinct(parts, seen, part);

                continue;
            }

            AddDistinct(parts, seen, token);
        }

        return parts;
    }

    /// <summary>
    /// Normalizes a target box to the form stored as the scan target and used as the history key.
    /// </summary>
    /// <param name="targets">The text of the target box.</param>
    /// <returns>The targets joined by <c>", "</c>, or an empty string when none were given.</returns>
    public static string Normalize(string? targets)
    {
        return string.Join(", ", Split(targets));
    }

    private static void AddDistinct(List<string> parts, HashSet<string> seen, string part)
    {
        if (seen.Add(part))
            parts.Add(part);
    }
}
