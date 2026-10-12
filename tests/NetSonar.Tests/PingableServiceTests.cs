using System;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia;
using NetSonar.Avalonia.Converters;
using NetSonar.Avalonia.Network;

namespace NetSonar.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PingableServiceTests
{
    [TestMethod]
    public void ParseFromString_LocalizedAndInvariantSettings_UsesExpectedValues()
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-PT");

            var localized = PingableService.ParseFromString("localhost | 0,5 | 0,75");
            var invariant = PingableService.ParseFromString("localhost | 0.75 | 1.25");

            Assert.AreEqual(0.5, localized.PingEverySeconds);
            Assert.AreEqual(0.75, localized.TimeoutSeconds);
            Assert.AreEqual(0.75, invariant.PingEverySeconds);
            Assert.AreEqual(1.25, invariant.TimeoutSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static PingableServiceReply CreateReply(bool succeeded, DateTime sentDateTime, double time = 5)
    {
        return new PingableServiceReply(
            succeeded,
            succeeded ? IPStatus.Success : IPStatus.TimedOut,
            new IPEndPoint(IPAddress.Loopback, 0),
            sentDateTime,
            succeeded ? time : double.PositiveInfinity);
    }

    /// <summary>
    /// Builds a service whose replies are stored newest first, like <c>Pings.Insert(0, reply)</c> does at runtime.
    /// </summary>
    private static PingableService CreateServiceWithReplies(DateTime oldest, params bool[] oldestToNewestSucceeded)
    {
        var service = new PingableService(ServiceProtocolType.ICMP, "127.0.0.1");
        for (var i = 0; i < oldestToNewestSucceeded.Length; i++)
        {
            service.Pings.Insert(0, CreateReply(oldestToNewestSucceeded[i], oldest.AddSeconds(i)));
        }

        return service;
    }

    [TestMethod]
    public void RebuildStatistic_NewestFirstReplies_ComputesStreaksFromNewestReply()
    {
        var oldest = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local);
        // Oldest to newest: fail, success, success, fail, fail, fail.
        var service = CreateServiceWithReplies(oldest, false, true, true, false, false, false);

        service.RebuildStatistic();

        Assert.AreEqual(0u, service.ConsecutiveSucceedCount);
        Assert.AreEqual(3u, service.ConsecutiveFailedCount);
        Assert.AreEqual(2u, service.MaxConsecutiveSucceedCount);
        Assert.AreEqual(3u, service.MaxConsecutiveFailedCount);
        Assert.AreEqual(oldest.AddSeconds(2), service.LastSucceedDateTime);
        Assert.AreEqual(oldest.AddSeconds(5), service.LastFailedDateTime);
        Assert.AreEqual(oldest.AddSeconds(5), service.LastExecutedDateTime);
        Assert.AreSame(service[0], service.LastPing);
    }

    [TestMethod]
    public void RebuildStatistic_NewestFirstReplies_NewestSuccessStreakIsCurrent()
    {
        var oldest = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local);
        // Oldest to newest: success, fail, fail, success, success.
        var service = CreateServiceWithReplies(oldest, true, false, false, true, true);

        service.RebuildStatistic();

        Assert.AreEqual(2u, service.ConsecutiveSucceedCount);
        Assert.AreEqual(0u, service.ConsecutiveFailedCount);
        Assert.AreEqual(oldest.AddSeconds(4), service.LastSucceedDateTime);
        Assert.AreEqual(oldest.AddSeconds(2), service.LastFailedDateTime);
    }

    [TestMethod]
    public void StatusChanged_ComparesLastReplyWithThePreviousOne()
    {
        var oldest = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local);

        // Oldest to newest: fail, fail, success, success. The last two replies agree, so nothing changed.
        var unchanged = CreateServiceWithReplies(oldest, false, false, true, true);
        unchanged.RebuildStatistic();
        Assert.IsFalse(unchanged.StatusChanged);

        // Oldest to newest: success, success, success, fail. The newest reply differs from the previous one.
        var changed = CreateServiceWithReplies(oldest, true, true, true, false);
        changed.RebuildStatistic();
        Assert.IsTrue(changed.StatusChanged);
    }

    [TestMethod]
    public async Task PingAsync_WithoutUiThread_CompletesAndReleasesTheService()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var service = new PingableService(ServiceProtocolType.TCP, $"127.0.0.1:{port}");

        Assert.IsTrue(service.CanTimerExecute);

        var reply = await service.PingAsync();

        Assert.IsTrue(reply.IsSucceeded);
        // The service just ran, so the timer must wait for its interval and must not see it as busy forever.
        Assert.IsFalse(service.CanTimerExecute);
        service.PingEverySeconds = PingableService.MinPingEverySeconds;
        await Task.Delay(TimeSpan.FromSeconds(PingableService.MinPingEverySeconds + 0.2));
        Assert.IsTrue(service.CanTimerExecute);
    }

    [TestMethod]
    public void BufferSize_NewTcpService_SendsNoPayloadByDefault()
    {
        var tcp = new PingableService(ServiceProtocolType.TCP, "127.0.0.1:80");
        var udp = new PingableService(ServiceProtocolType.UDP, "127.0.0.1:80");

        Assert.AreEqual(0, tcp.BufferSize);
        Assert.AreEqual(PingableService.DefaultBufferSize, udp.BufferSize);
    }

    [TestMethod]
    public void BufferSize_PersistedTcpService_KeepsSavedBufferSize()
    {
        var service = new PingableService(ServiceProtocolType.TCP, "127.0.0.1:80") { BufferSize = 64 };

        // Mirrors App.JsonSerializerOptions without touching App: its static initializer builds the dialog, toast and
        // update managers, which depend on the entry assembly and fail under some test runners.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            IgnoreReadOnlyFields = true,
            Converters =
            {
                new IPAddressJsonConverter(),
                new IPEndPointJsonConverter(),
                new JsonStringEnumConverter(),
            },
        };

        var json = JsonSerializer.Serialize(service, options);
        var restored = JsonSerializer.Deserialize<PingableService>(json, options);

        Assert.IsNotNull(restored);
        Assert.AreEqual(64, restored.BufferSize);
    }
}
