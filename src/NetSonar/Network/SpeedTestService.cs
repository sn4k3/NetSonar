using Cysharp.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetSonar.Avalonia.SystemOS;

namespace NetSonar.Avalonia.Network;

public class SpeedTestService
{
    private static string SpeedTestPath { get; } = SystemAware.NormalizeExecutableExtension(Path.Combine(AppContext.BaseDirectory, "binaries", "speedtest", "speedtest"));

    private const string SpeedTestDefaultArgs = "--accept-license --accept-gdpr";

    public static async Task<SpeedTestResultServer[]> GetServerList(CancellationToken token = default)
    {
        try
        {
            var serverListJson = await ProcessX.StartAsync($"{SpeedTestPath} {SpeedTestDefaultArgs} --format json --servers")
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
            App.ShowExceptionToast(e, "Unable to get server list");
        }

        return [];
    }

    public static async IAsyncEnumerable<SpeedTestResult> StartSpeedTest(SpeedTestResultServer? server, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var command = $"{SpeedTestPath} {SpeedTestDefaultArgs} --format jsonl";
        if (server is not null)
        {
            command += $" --server-id {server.Id}";
        }
        await foreach (var line in ProcessX.StartAsync(command).WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var result = JsonSerializer.Deserialize<SpeedTestResult>(line);
            if (result is null) continue;
            yield return result;
        }
    }

    public static async IAsyncEnumerable<SpeedTestResult> StartSpeedTest([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var command = $"{SpeedTestPath} {SpeedTestDefaultArgs} --format jsonl";
        await foreach (var line in ProcessX.StartAsync(command).WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var result = JsonSerializer.Deserialize<SpeedTestResult>(line);
            if (result is null) continue;
            yield return result;
        }
    }



    public static bool IsSpeedTestAvailable()
    {
        // try
        // {
        //     await ProcessX.StartAsync("speedtest.exe --version").FirstAsync();
        //     return true;
        // }
        // catch (Exception e)
        // {
        //     return false;
        // }

        return true;

        /*
        if (SystemAware.TryFindEnvFile(SystemAware.NormalizeExecutableExtension("speedtest"), out var path))
        {
            SpeedTestPath = path;
            return true;
        }



        return false;*/
    }

    public static Task<string?> GetSpeedTestVersion()
    {
        try
        {
            return ProcessX.StartAsync($"{SpeedTestPath} {SpeedTestDefaultArgs} --version").FirstOrDefaultAsync();
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "SpeedTestService");
        }

        return Task.FromResult<string?>(null);
    }
}