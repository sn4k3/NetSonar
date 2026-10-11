using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Cysharp.Diagnostics;
using NetSonar.Avalonia.Extensions;
using StageKit;
using StageKit.Primitives.System;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Drives the external nmap executable and converts its XML output into scan results.
/// </summary>
public static partial class NmapScannerService
{
    /// <summary>
    /// The number of hosts passed to a single nmap invocation, so a large discovery does not build an
    /// oversized command line or lose every result when one invocation fails.
    /// </summary>
    public const int HostsPerInvocation = 64;

    /// <summary>
    /// The shortest network prefix accepted as a scan target. Anything wider expands to millions of addresses.
    /// </summary>
    public const int MinimumTargetPrefixLength = 16;

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex(
        """<taskprogress\s+task="(?<task>[^"]*)"[^>]*?percent="(?<percent>[^"]*)"(?:[^>]*?remaining="(?<remaining>[^"]*)")?(?:[^>]*?etc="(?<etc>[^"]*)")?""",
        RegexOptions.ExplicitCapture
    )]
    private static partial Regex TaskProgressRegex { get; }

    /// <summary>
    /// Locates the nmap executable.
    /// </summary>
    /// <param name="executablePath">Receives the resolved path, or an empty string.</param>
    /// <returns><see langword="true"/> when nmap was found.</returns>
    public static bool TryFindExecutable(out string executablePath)
    {
        var executableName = HostSystem.NormalizeExecutableExtension("nmap");
        if (HostSystem.TryFindExecutable(executableName, out var foundPath) && foundPath.Length > 0)
        {
            executablePath = foundPath;
            return true;
        }

        executablePath = GetWellKnownExecutablePaths().FirstOrDefault(File.Exists) ?? string.Empty;
        return executablePath.Length > 0;
    }

    /// <summary>
    /// Returns the IPv4 networks of the active real interfaces, as CIDR scan targets.
    /// </summary>
    /// <returns>The distinct, ordered target list.</returns>
    /// <remarks>
    /// Networks wider than <see cref="MinimumTargetPrefixLength"/> are skipped: a misreported prefix would
    /// otherwise turn one click into an internet-wide scan.
    /// </remarks>
    public static IReadOnlyList<string> GetLocalTargets()
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!NetworkInterfaceBridge.IsRealActiveInterface(networkInterface))
                    continue;

                foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (
                        address.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address.Address)
                        || NetworkInterfaceBridge.IsLinkLocal(address.Address)
                    )
                        continue;

                    var prefixLength = TryGetPrefixLength(address);
                    if (prefixLength is < MinimumTargetPrefixLength or > 32)
                        continue;

                    targets.Add(GetNetworkCidr(address.Address, prefixLength));
                }
            }
            catch (Exception e)
                when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                // A disappearing adapter, or one whose prefix the platform does not report,
                // cannot contribute a scan target.
            }
        }

        return targets.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Returns the CIDR network an address belongs to.
    /// </summary>
    /// <param name="address">An IPv4 address inside the network.</param>
    /// <param name="prefixLength">The network prefix length, from 0 to 32.</param>
    /// <returns>The network in CIDR form.</returns>
    public static string GetNetworkCidr(IPAddress address, int prefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses have a CIDR target.", nameof(address));
        }

        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < 32; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return $"{new IPAddress(bytes)}/{prefixLength}";
    }

    /// <summary>
    /// Validates a user supplied scan target list.
    /// </summary>
    /// <param name="target">The target list, as typed in the target box.</param>
    /// <returns><see langword="true"/> when the list holds at least one target and every target is valid.</returns>
    public static bool IsValidTarget(string? target)
    {
        var parts = NetworkTargetList.Split(target);
        return parts.Count > 0 && parts.All(IsValidSingleTarget);
    }

    /// <summary>
    /// Validates one target expression.
    /// </summary>
    /// <param name="target">The target expression.</param>
    /// <returns><see langword="true"/> when the target is a supported address, network, range, or host name.</returns>
    public static bool IsValidSingleTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;
        var value = target.Trim();
        if (value.StartsWith('-') || value.Any(char.IsWhiteSpace))
            return false;

        if (IPAddress.TryParse(value, out _))
            return true;
        if (TryParseCidr(value))
            return true;

        return Uri.CheckHostName(value)
                is UriHostNameType.Dns
                    or UriHostNameType.IPv4
                    or UriHostNameType.IPv6
            || IsIpv4OctetRange(value);
    }

    /// <summary>
    /// Runs host discovery against a target.
    /// </summary>
    /// <param name="executablePath">The nmap executable.</param>
    /// <param name="target">The target expression.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="progress">Receives scan progress, when the engine reports it.</param>
    /// <param name="cancellationToken">Cancels the scan and kills the process.</param>
    /// <returns>The hosts nmap reported as up.</returns>
    /// <exception cref="InvalidOperationException">The target expands to more addresses than the host limit.</exception>
    public static async Task<NetworkScanResult> DiscoverAsync(
        string executablePath,
        string target,
        NetworkScanOptions? options = null,
        IProgress<NetworkScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        options ??= new NetworkScanOptions();
        EnsureWithinHostLimit(target, options.MaxHosts);

        var arguments = new List<string> { "-sn" };
        arguments.AddRange(BuildCommonArguments(options));
        arguments.AddRange(NetworkTargetList.Split(target));

        var xml = await RunAsync(executablePath, arguments, options, progress, cancellationToken);
        var hosts = ParseXml(xml);

        // An elevated nmap ARP-pings the local network itself and already reports MAC addresses, so the
        // neighbour cache would only add entries that are on their way out.
        if (!options.Elevated)
        {
            hosts = await NeighbourCacheMerge.ApplyAsync(hosts, target, options, cancellationToken);
        }

        return new NetworkScanResult(hosts, NetworkScanEngine.Nmap, xml);
    }

    /// <summary>
    /// Rejects a target list that expands to more addresses than the host limit, like the built-in engine does.
    /// </summary>
    /// <param name="target">The target list.</param>
    /// <param name="maxHosts">The maximum number of addresses the targets may expand to, in total.</param>
    /// <exception cref="InvalidOperationException">The targets are above the limit.</exception>
    /// <remarks>
    /// Only targets <see cref="NetworkTarget"/> can range are counted, and their counts are summed; a host name or
    /// an nmap octet list is left to nmap.
    /// </remarks>
    public static void EnsureWithinHostLimit(string target, int maxHosts)
    {
        var requiredHosts = 0L;
        foreach (var part in NetworkTargetList.Split(target))
        {
            if (NetworkTarget.TryParse(part, out var networkTarget))
                requiredHosts += networkTarget.UsableHostCount;
        }

        if (requiredHosts > maxHosts)
        {
            throw new InvalidOperationException(NetworkTarget.DescribeLimit(requiredHosts, maxHosts));
        }
    }

    /// <summary>
    /// Runs a port scan against known hosts.
    /// </summary>
    /// <param name="executablePath">The nmap executable.</param>
    /// <param name="hosts">The addresses to scan.</param>
    /// <param name="options">The port scan options.</param>
    /// <param name="progress">Receives scan progress, when the engine reports it.</param>
    /// <param name="cancellationToken">Cancels the scan and kills the process.</param>
    /// <returns>The scanned hosts with their open ports.</returns>
    public static async Task<NetworkScanResult> ScanPortsAsync(
        string executablePath,
        IEnumerable<string> hosts,
        NetworkPortScanOptions? options = null,
        IProgress<NetworkScanProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        options ??= new NetworkPortScanOptions();

        var targetHosts = hosts.Distinct(StringComparer.Ordinal).ToArray();
        if (targetHosts.Length == 0)
            return NetworkScanResult.Empty(NetworkScanEngine.Nmap);

        var scanned = new List<NetworkScannerHost>(targetHosts.Length);
        var rawOutput = new StringBuilder();
        var batches = (int)Math.Ceiling(targetHosts.Length / (double)HostsPerInvocation);

        for (var batch = 0; batch < batches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchHosts = targetHosts
                .Skip(batch * HostsPerInvocation)
                .Take(HostsPerInvocation)
                .ToArray();

            var arguments = BuildPortScanArguments(options);
            arguments.AddRange(batchHosts);

            var batchProgress = CreateBatchProgress(progress, batch, batches);
            var xml = await RunAsync(
                executablePath,
                arguments,
                options,
                batchProgress,
                cancellationToken
            );

            scanned.AddRange(ParseXml(xml));
            if (rawOutput.Length > 0)
                rawOutput.AppendLine();
            rawOutput.Append(xml);
        }

        return new NetworkScanResult(scanned, NetworkScanEngine.Nmap, rawOutput.ToString());
    }

    /// <summary>
    /// Parses an nmap XML report.
    /// </summary>
    /// <param name="xml">The report. A report truncated by a cancelled scan is repaired when possible.</param>
    /// <returns>The hosts reported as up. Empty when the report cannot be parsed.</returns>
    public static IReadOnlyList<NetworkScannerHost> ParseXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];

        var document = TryParseDocument(xml);
        return document
                ?.Root?.Elements("host")
                .Where(host =>
                    string.Equals(
                        (string?)host.Element("status")?.Attribute("state"),
                        "up",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(ParseHost)
                .Where(host => host is not null)
                .Cast<NetworkScannerHost>()
                .ToArray()
            ?? [];
    }

    private static XDocument? TryParseDocument(string xml)
    {
        try
        {
            return XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            // A killed nmap leaves the report without its closing tag; salvage the hosts already written.
            var lastHostEnd = xml.LastIndexOf("</host>", StringComparison.Ordinal);
            if (lastHostEnd < 0)
                return null;

            try
            {
                return XDocument.Parse(
                    string.Concat(xml.AsSpan(0, lastHostEnd + 7), "</nmaprun>"),
                    LoadOptions.None
                );
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    private static NetworkScannerHost? ParseHost(XElement element)
    {
        var addresses = element.Elements("address").ToArray();
        var ipv4 = addresses.FirstOrDefault(address =>
            (string?)address.Attribute("addrtype") == "ipv4"
        );
        var ipv6 = addresses.FirstOrDefault(address =>
            (string?)address.Attribute("addrtype") == "ipv6"
        );
        var ipAddress = (string?)(ipv4 ?? ipv6)?.Attribute("addr");
        if (string.IsNullOrWhiteSpace(ipAddress))
            return null;

        var mac = addresses.FirstOrDefault(address =>
            (string?)address.Attribute("addrtype") == "mac"
        );
        var macAddress = ArpTable.NormalizeMacAddress(
            (string?)mac?.Attribute("addr") ?? string.Empty
        );
        var vendor = (string?)mac?.Attribute("vendor") ?? string.Empty;
        if (vendor.Length == 0 && macAddress.Length > 0)
        {
            vendor = MacVendorLookup.Resolve(macAddress);
        }

        var portsElement = element.Element("ports");
        var ports =
            portsElement
                ?.Elements("port")
                .Where(port =>
                    string.Equals(
                        (string?)port.Element("state")?.Attribute("state"),
                        "open",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(ParsePort)
                .Where(port => port.Number > 0)
                .OrderBy(port => port.Number)
                .ToArray()
            ?? [];

        return new NetworkScannerHost
        {
            Address = ipAddress,
            Hostname =
                (string?)element.Element("hostnames")?.Element("hostname")?.Attribute("name")
                ?? string.Empty,
            MacAddress = macAddress,
            Vendor = vendor,
            State = (string?)element.Element("status")?.Attribute("state") ?? string.Empty,
            OperatingSystem =
                (string?)
                    element.Element("os")?.Elements("osmatch").FirstOrDefault()?.Attribute("name")
                ?? string.Empty,
            LastSeen = DateTime.Now,
            IsPortScanned = portsElement is not null,
            Ports = ports,
        };
    }

    private static NetworkScannerPort ParsePort(XElement port)
    {
        var service = port.Element("service");
        return new NetworkScannerPort
        {
            Number = int.TryParse((string?)port.Attribute("portid"), out var number) ? number : 0,
            Protocol = (string?)port.Attribute("protocol") ?? string.Empty,
            State = (string?)port.Element("state")?.Attribute("state") ?? string.Empty,
            Service = ParseServiceName(service),
            Product = (string?)service?.Attribute("product") ?? string.Empty,
            Version = (string?)service?.Attribute("version") ?? string.Empty,
            ExtraInfo = (string?)service?.Attribute("extrainfo") ?? string.Empty,
        };
    }

    /// <summary>
    /// Returns the service name, prefixed with <c>ssl/</c> when nmap found it behind TLS, as nmap prints it.
    /// </summary>
    private static string ParseServiceName(XElement? service)
    {
        var name = (string?)service?.Attribute("name") ?? string.Empty;
        return name.Length > 0
            && string.Equals((string?)service?.Attribute("tunnel"), "ssl", StringComparison.OrdinalIgnoreCase)
            ? $"ssl/{name}"
            : name;
    }

    private static List<string> BuildPortScanArguments(NetworkPortScanOptions options)
    {
        // The hosts are already known to be up: without -Pn nmap repeats host discovery, which an
        // unprivileged scan often fails, and then reports no ports at all.
        var arguments = new List<string> { "-Pn", options.Elevated ? "-sS" : "-sT" };

        if (options.ServiceDetection)
            arguments.Add("-sV");
        if (options.Elevated && options.UdpScan)
            arguments.Add("-sU");
        if (options.Elevated && options.OsDetection)
            arguments.Add("-O");

        arguments.Add("--open");
        arguments.AddRange(BuildPortArguments(options));
        arguments.AddRange(BuildCommonArguments(options));
        return arguments;
    }

    private static IEnumerable<string> BuildPortArguments(NetworkPortScanOptions options)
    {
        switch (options.Range)
        {
            case NetworkPortScanRange.Top1000:
                return ["--top-ports", "1000"];
            case NetworkPortScanRange.Full:
                return ["-p-"];
            case NetworkPortScanRange.Custom
                when NetworkPortCatalog.IsValidPortSpec(options.CustomPorts):
                return ["-p", options.CustomPorts.Trim()];
            default:
                return ["--top-ports", "100"];
        }
    }

    private static List<string> BuildCommonArguments(NetworkScanOptions options)
    {
        var arguments = new List<string>();
        if (!options.ResolveDns)
            arguments.Add("-n");

        arguments.Add(
            string.Create(
                CultureInfo.InvariantCulture,
                $"-T{Math.Clamp(options.TimingTemplate, 0, 5)}"
            )
        );

        if (options.HostTimeoutSeconds > 0)
        {
            arguments.Add("--host-timeout");
            arguments.Add(
                string.Create(CultureInfo.InvariantCulture, $"{options.HostTimeoutSeconds}s")
            );
        }

        arguments.Add("--stats-every");
        arguments.Add("2s");
        return arguments;
    }

    private static IProgress<NetworkScanProgress>? CreateBatchProgress(
        IProgress<NetworkScanProgress>? progress,
        int batch,
        int batches
    )
    {
        if (progress is null || batches <= 1)
            return progress;

        return new BatchProgress(progress, batch, batches);
    }

    /// <summary>
    /// Scales the progress of one batch into the progress of the whole scan, without hopping threads.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> would capture the current synchronization context and post every value back to
    /// it; the outer progress already marshals to the UI thread.
    /// </remarks>
    private sealed class BatchProgress(
        IProgress<NetworkScanProgress> inner,
        int batch,
        int batches
    ) : IProgress<NetworkScanProgress>
    {
        public void Report(NetworkScanProgress value)
        {
            inner.Report(value with { Percent = (batch * 100d + value.Percent) / batches });
        }
    }

    /// <summary>
    /// Runs nmap while streaming its XML report, so scan progress can be reported while it works.
    /// </summary>
    /// <remarks>
    /// The process is launched through ProcessX so an elevated run keeps its output: the platform elevation
    /// helpers relay the child pipes back to this process. On macOS the helper is <c>osascript</c>, which only
    /// returns the output once the command completes, so an elevated macOS scan reports no intermediate progress.
    /// </remarks>
    private static async Task<string> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        NetworkScanOptions options,
        IProgress<NetworkScanProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var startInfo = ProcessXExtensions.CreateStartInfo(
            executablePath,
            [.. arguments, "-oX", "-"],
            options.Elevated,
            options.ElevationPrompt
        );

        var (process, standardOutput, standardError) = ProcessX.GetDualAsyncEnumerable(startInfo);

        await using var killRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited between the check and the kill.
            }
        });

        var errorLines = new List<string>();
        var errorTask = Task.Run(
            async () =>
            {
                await foreach (var line in standardError)
                {
                    errorLines.Add(line);
                }
            },
            CancellationToken.None
        );

        var output = new StringBuilder(64 * 1024);

        try
        {
            await foreach (var line in standardOutput)
            {
                output.AppendLine(line);
                ReportProgress(progress, line);
            }
        }
        catch (ProcessErrorException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForErrorOutput(errorTask);
            EnsureSucceeded(
                exception.ExitCode,
                string.Join(
                    Environment.NewLine,
                    exception.ErrorOutput.Length > 0 ? exception.ErrorOutput : errorLines
                )
            );
        }
        finally
        {
            await WaitForErrorOutput(errorTask);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToString();
    }

    private static async Task WaitForErrorOutput(Task errorTask)
    {
        try
        {
            await errorTask;
        }
        catch (ProcessErrorException)
        {
            // The exit code is reported by the standard output enumeration.
        }
    }

    /// <summary>
    /// Parses one <c>taskprogress</c> element of the streamed report.
    /// </summary>
    /// <param name="line">A line of the nmap XML report.</param>
    /// <param name="progress">Receives the parsed progress.</param>
    /// <returns><see langword="true"/> when the line carried progress.</returns>
    public static bool TryParseProgress(string line, out NetworkScanProgress progress)
    {
        progress = default;
        if (!line.Contains("<taskprogress", StringComparison.Ordinal))
            return false;

        var match = TaskProgressRegex.Match(line);
        if (!match.Success)
            return false;

        if (
            !double.TryParse(
                match.Groups["percent"].ValueSpan,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent
            )
        )
            return false;

        progress = new NetworkScanProgress(
            match.Groups["task"].Value,
            percent,
            ParseRemaining(match)
        );
        return true;
    }

    private static void ReportProgress(IProgress<NetworkScanProgress>? progress, string line)
    {
        if (progress is null)
            return;

        if (TryParseProgress(line, out var parsed))
            progress.Report(parsed);
    }

    /// <summary>
    /// Returns the time nmap expects the running task to still need.
    /// </summary>
    /// <param name="match">The parsed <c>taskprogress</c> element.</param>
    /// <returns>The remaining time, or <see langword="null"/> when nmap did not estimate one.</returns>
    /// <remarks>
    /// <c>remaining</c> is a number of seconds, while <c>etc</c> is the estimated completion time as a Unix
    /// timestamp; reporting the raw <c>etc</c> shows a ten digit number instead of a duration.
    /// </remarks>
    private static TimeSpan? ParseRemaining(Match match)
    {
        if (
            match.Groups["remaining"].Success
            && double.TryParse(
                match.Groups["remaining"].ValueSpan,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var remaining
            )
        )
        {
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }

        if (
            match.Groups["etc"].Success
            && long.TryParse(
                match.Groups["etc"].ValueSpan,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var completionEpoch
            )
        )
        {
            var completion = DateTimeOffset.FromUnixTimeSeconds(completionEpoch);
            var left = completion - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        return null;
    }

    private static void EnsureSucceeded(int exitCode, string standardError)
    {
        if (exitCode == 0)
            return;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(standardError)
                ? $"nmap exited with code {exitCode}."
                : standardError.Trim()
        );
    }

    private static int TryGetPrefixLength(UnicastIPAddressInformation address)
    {
        try
        {
            return address.PrefixLength;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
    }

    private static IEnumerable<string> GetWellKnownExecutablePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Nmap",
                "nmap.exe"
            );
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Nmap",
                "nmap.exe"
            );

            var programFiles64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrWhiteSpace(programFiles64))
            {
                yield return Path.Combine(programFiles64, "Nmap", "nmap.exe");
            }

            yield break;
        }

        yield return "/usr/bin/nmap";
        yield return "/usr/local/bin/nmap";
        yield return "/opt/homebrew/bin/nmap";
        yield return "/opt/local/bin/nmap";
    }

    private static bool TryParseCidr(string value)
    {
        var separator = value.LastIndexOf('/');
        if (
            separator <= 0
            || !int.TryParse(value[(separator + 1)..], out var prefix)
            || !IPAddress.TryParse(value[..separator], out var address)
        )
            return false;

        return prefix >= 0
            && (address.AddressFamily == AddressFamily.InterNetwork ? prefix <= 32 : prefix <= 128);
    }

    private static bool IsIpv4OctetRange(string value)
    {
        var octets = value.Split('.');
        return octets.Length == 4
            && octets.All(octet => octet.Split(',').All(IsIpv4OctetRangePart));
    }

    private static bool IsIpv4OctetRangePart(string value)
    {
        var range = value.Split('-');
        if (range.Length is < 1 or > 2)
            return false;
        if (range.Length == 1)
            return byte.TryParse(value, out _);
        return range.All(part => part.Length == 0 || byte.TryParse(part, out _));
    }

    /// <summary>
    /// Gets the version of nmap if available.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query. The query also gives up after five seconds.</param>
    /// <returns>The version string reported by nmap, or <see langword="null"/> if not found or execution failed.</returns>
    public static async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!TryFindExecutable(out var executablePath))
        {
            return null;
        }

        try
        {
            // A broken install can hang on --version, so the query is bounded on top of the caller's token.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(VersionTimeout);

            return await ProcessX
                .StartAsync(executablePath, arguments: "--version")
                .FirstOrDefaultAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            UnhandledExceptions.HandleSafeException(e, nameof(NmapScannerService));
            return null;
        }
    }
}
