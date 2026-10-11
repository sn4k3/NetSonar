using Cysharp.Diagnostics;
using StageKit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StageKit.Primitives.System;

namespace NetSonar.Avalonia.Network;

public class SpeedTestService
{
    /// <summary>
    /// The Ookla CLI copied next to the application from <c>dependencies/&lt;rid&gt;/speedtest</c>.
    /// </summary>
    private static string BundledPath { get; } = HostSystem.NormalizeExecutableExtension(Path.Combine(AppContext.BaseDirectory, "binaries", "speedtest", "speedtest"));

    // The CLI is non-interactive, so it needs these flags to run at all. The user accepts Ookla's terms in the app
    // (SpeedTestSettings.OoklaTermsAccepted) before any test or server lookup starts.
    private const string SpeedTestDefaultArgs = "--accept-license --accept-gdpr";

    private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the speedtest executable found by the last <see cref="DetectAsync"/>, or <see langword="null"/> when none
    /// was found.
    /// </summary>
    public static string? ExecutablePath { get; private set; }

    /// <summary>
    /// Locates the Ookla speedtest CLI: the bundled copy first, then one installed on the system.
    /// </summary>
    /// <param name="cancellationToken">Cancels the detection.</param>
    /// <returns>The version line reported by the executable, or <see langword="null"/> when no Ookla CLI was found.</returns>
    /// <remarks>
    /// Not every runtime has a bundled copy, so an installed CLI is accepted too. Only the Ookla CLI is: Linux
    /// distributions ship an unrelated Python <c>speedtest-cli</c> that installs a <c>speedtest</c> command with
    /// different arguments and output.
    /// </remarks>
    public static async Task<string?> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var candidate in GetCandidatePaths())
        {
            var version = await TryGetOoklaVersionAsync(candidate, cancellationToken);
            if (version is null) continue;

            ExecutablePath = candidate;
            return version;
        }

        ExecutablePath = null;
        return null;
    }

    public static async Task<SpeedTestResultServer[]> GetServerList(CancellationToken token = default)
    {
        try
        {
            var serverListJson = await ProcessX.StartAsync(GetExecutablePath(), arguments:$"{SpeedTestDefaultArgs} --format json --servers")
                .FirstOrDefaultAsync(token);
            if (string.IsNullOrWhiteSpace(serverListJson)) return [];
            var servers = JsonSerializer.Deserialize<SpeedTestServers>(serverListJson);
            return servers is null ? [] : servers.Servers;
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["SpeedTest.ServerList.Error"]);
        }

        return [];
    }

    public static async IAsyncEnumerable<SpeedTestResult> StartSpeedTest(SpeedTestResultServer? server, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = $"{SpeedTestDefaultArgs} --format jsonl";
        if (server is not null)
        {
            args += $" --server-id {server.Id}";
        }

        await foreach (var line in ProcessX.StartAsync(GetExecutablePath(), arguments:args).WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var result = JsonSerializer.Deserialize<SpeedTestResult>(line);
            if (result is null) continue;
            yield return result;
        }
    }

    private static string GetExecutablePath()
    {
        return ExecutablePath
               ?? throw new FileNotFoundException("The Ookla speedtest CLI was not found.", BundledPath);
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        if (File.Exists(BundledPath)) yield return BundledPath;

        if (HostSystem.TryFindExecutable(HostSystem.NormalizeExecutableExtension("speedtest"), out var installedPath)
            && installedPath.Length > 0
            && !string.Equals(installedPath, BundledPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return installedPath;
        }
    }

    private static async Task<string?> TryGetOoklaVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DetectTimeout);

            var version = await ProcessX.StartAsync(executablePath, arguments: "--version")
                .FirstOrDefaultAsync(timeout.Token);

            // The Ookla CLI answers "Speedtest by Ookla <version>"; anything else is a different tool.
            return version is not null && version.Contains("Ookla", StringComparison.OrdinalIgnoreCase)
                ? version.Trim()
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            UnhandledExceptions.HandleSafeException(e, nameof(SpeedTestService));
            return null;
        }
    }
}
