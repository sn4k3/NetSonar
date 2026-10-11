using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;

namespace NetSonar.Tests;

[TestClass]
public sealed class NetworkScannerEngineTests
{
    [TestMethod]
    public void TryExpandTarget_ExpandsACidrWithoutNetworkAndBroadcast()
    {
        var expanded = BuiltinNetworkScanner.TryExpandTarget(
            "192.168.4.0/29",
            4096,
            out var addresses,
            out var required
        );

        Assert.IsTrue(expanded);
        Assert.AreEqual(6, required);
        Assert.HasCount(6, addresses);
        Assert.AreEqual("192.168.4.1", addresses[0].ToString());
        Assert.AreEqual("192.168.4.6", addresses[^1].ToString());
    }

    [TestMethod]
    public void TryExpandTarget_RefusesATargetAboveTheLimit()
    {
        var expanded = BuiltinNetworkScanner.TryExpandTarget(
            "10.0.0.0/8",
            4096,
            out var addresses,
            out var required
        );

        Assert.IsFalse(expanded);
        Assert.IsEmpty(addresses);
        Assert.IsGreaterThan(4096, required);
    }

    [TestMethod]
    public async Task DiscoverAsync_RefusesATargetListWhoseTotalIsAboveTheLimit()
    {
        // The check runs before any probe, so the tiny limit keeps this test off the network.
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuiltinNetworkScanner.DiscoverAsync(
                "192.168.1.0/24, 192.168.2.0/24, 192.168.5.0/24",
                new NetworkScanOptions { MaxHosts = 600 }));

        Assert.AreEqual(
            "The target expands to 762 addresses, above the 600 limit.",
            exception.Message);
    }

    [TestMethod]
    public void TryExpandTarget_ExpandsALastOctetRange()
    {
        var expanded = BuiltinNetworkScanner.TryExpandTarget(
            "192.168.1.10-12",
            4096,
            out var addresses,
            out _
        );

        Assert.IsTrue(expanded);
        Assert.AreSequenceEqual(
            new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12" },
            addresses.Select(address => address.ToString()).ToArray()
        );
    }

    [TestMethod]
    public void TryExpandTarget_ExpandsASingleAddress()
    {
        var expanded = BuiltinNetworkScanner.TryExpandTarget("192.168.1.5", 10, out var addresses, out _);

        Assert.IsTrue(expanded);
        Assert.HasCount(1, addresses);
    }

    [TestMethod]
    [DataRow("22,80,443", new[] { 22, 80, 443 })]
    [DataRow("80-82", new[] { 80, 81, 82 })]
    [DataRow("82-80", new[] { 80, 81, 82 })]
    [DataRow("80,80,80", new[] { 80 })]
    public void ParsePortSpec_ParsesListsAndRanges(string value, int[] expected)
    {
        Assert.AreSequenceEqual(expected, NetworkPortCatalog.ParsePortSpec(value));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("http")]
    [DataRow("0")]
    [DataRow("70000")]
    public void ParsePortSpec_RejectsInvalidSpecifications(string value)
    {
        Assert.IsEmpty(NetworkPortCatalog.ParsePortSpec(value));
        Assert.IsFalse(NetworkPortCatalog.IsValidPortSpec(value));
    }

    [TestMethod]
    [DataRow("22")]
    [DataRow("22,80,443")]
    [DataRow("8000-8100")]
    [DataRow("-100")]
    [DataRow("8000-")]
    [DataRow("-")]
    [DataRow(" 22 , 80 ")]
    [DataRow("22,,80")]
    public void IsValidPortSpec_AcceptsPortsAndRanges(string value)
    {
        Assert.IsTrue(NetworkPortCatalog.IsValidPortSpec(value), value);
    }

    [TestMethod]
    [DataRow("abc,80")]
    [DataRow("80,abc")]
    [DataRow("80,70000")]
    [DataRow("80,0")]
    [DataRow("1-2-3")]
    [DataRow("80-abc")]
    [DataRow(",")]
    [DataRow("   ")]
    [DataRow(null)]
    public void IsValidPortSpec_RejectsASpecificationWithAnyInvalidPart(string? value)
    {
        Assert.IsFalse(NetworkPortCatalog.IsValidPortSpec(value));
    }

    [TestMethod]
    public void ParsePortSpec_ExpandsOpenRanges()
    {
        Assert.AreSequenceEqual(new[] { 1, 2, 3 }, NetworkPortCatalog.ParsePortSpec("-3"));
        Assert.HasCount(65535, NetworkPortCatalog.ParsePortSpec("-"));
        Assert.AreSequenceEqual(new[] { 65534, 65535 }, NetworkPortCatalog.ParsePortSpec("65534-"));
    }

    [TestMethod]
    public async Task ScanPortsAsync_ThrowsWhenTheCustomPortSpecificationIsEmpty()
    {
        var options = new NetworkPortScanOptions
        {
            Range = NetworkPortScanRange.Custom,
            CustomPorts = "abc",
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuiltinNetworkScanner.ScanPortsAsync(["192.168.1.10"], options));

        // Nothing to scan is not an error, whatever the specification says.
        var result = await BuiltinNetworkScanner.ScanPortsAsync([], options);
        Assert.IsEmpty(result.Hosts);
    }

    [TestMethod]
    [DataRow(443, "https://192.168.1.10")]
    [DataRow(80, "http://192.168.1.10")]
    [DataRow(8443, "https://192.168.1.10:8443")]
    [DataRow(8080, "http://192.168.1.10:8080")]
    public void TryGetWebUrl_BuildsTheBrowserUrlOfAWebPort(int port, string expected)
    {
        Assert.IsTrue(NetworkPortCatalog.TryGetWebUrl("192.168.1.10", port, out var url));
        Assert.AreEqual(expected, url);
    }

    [TestMethod]
    public void TryGetWebUrl_BracketsIpv6AndRejectsOtherPorts()
    {
        Assert.IsTrue(NetworkPortCatalog.TryGetWebUrl("fe80::1", 8080, out var url));
        Assert.AreEqual("http://[fe80::1]:8080", url);

        Assert.IsFalse(NetworkPortCatalog.TryGetWebUrl("192.168.1.10", 22, out var ssh));
        Assert.AreEqual(string.Empty, ssh);
    }

    [TestMethod]
    public void GetPorts_UsesTheTopPortsPresetByDefault()
    {
        var ports = NetworkPortCatalog.GetPorts(new NetworkPortScanOptions());

        Assert.AreSequenceEqual(NetworkPortCatalog.TopPorts, ports);
        Assert.HasCount(100, ports);
    }

    [TestMethod]
    public void CreatePingService_MapsWellKnownPortsToTheMatchingProbe()
    {
        var host = new NetworkScannerHost { Address = "192.168.1.10", Hostname = "nas.local" };

        var ssh = NetworkPortCatalog.CreatePingService(host, Port(22), "Scanner");
        var https = NetworkPortCatalog.CreatePingService(host, Port(443), "Scanner");
        var http = NetworkPortCatalog.CreatePingService(host, Port(8080), "Scanner");
        var other = NetworkPortCatalog.CreatePingService(host, Port(9999), "Scanner");
        var icmp = NetworkPortCatalog.CreatePingService(host, null, "Scanner");

        Assert.AreEqual(ServiceProtocolType.SSH, ssh.ProtocolType);
        Assert.AreEqual("192.168.1.10:22", ssh.IpAddressOrUrl);
        Assert.AreEqual(ServiceProtocolType.HTTP, https.ProtocolType);
        Assert.AreEqual("https://192.168.1.10", https.IpAddressOrUrl);
        Assert.AreEqual("http://192.168.1.10:8080", http.IpAddressOrUrl);
        Assert.AreEqual(ServiceProtocolType.TCP, other.ProtocolType);
        Assert.AreEqual("192.168.1.10:9999", other.IpAddressOrUrl);
        Assert.AreEqual(ServiceProtocolType.ICMP, icmp.ProtocolType);
        Assert.AreEqual("192.168.1.10", icmp.IpAddressOrUrl);
        Assert.AreEqual("Scanner", icmp.Group);

        static NetworkScannerPort Port(int number) =>
            new()
            {
                Number = number,
                Protocol = "tcp",
                State = "open",
            };
    }

    [TestMethod]
    [DataRow("tcp", 2222, "ssh", ServiceProtocolType.SSH, "192.168.1.10:2222")]
    [DataRow("tcp", 5001, "http", ServiceProtocolType.HTTP, "http://192.168.1.10:5001")]
    [DataRow("tcp", 5001, "ssl/http", ServiceProtocolType.HTTP, "https://192.168.1.10:5001")]
    [DataRow("tcp", 7443, "ssl/unknown", ServiceProtocolType.TLS, "192.168.1.10:7443")]
    [DataRow("tcp", 2525, "smtp", ServiceProtocolType.SMTP, "192.168.1.10:2525")]
    [DataRow("tcp", 12345, "unknown", ServiceProtocolType.TCP, "192.168.1.10:12345")]
    [DataRow("udp", 53, "", ServiceProtocolType.DNS, "192.168.1.10:53")]
    [DataRow("udp", 123, "ntp", ServiceProtocolType.NTP, "192.168.1.10:123")]
    [DataRow("udp", 5353, "domain", ServiceProtocolType.DNS, "192.168.1.10:5353")]
    [DataRow("udp", 161, "snmp", ServiceProtocolType.UDP, "192.168.1.10:161")]
    public void CreatePingService_FallsBackToTheServiceNameThenTheTransport(
        string protocol,
        int number,
        string service,
        ServiceProtocolType expectedType,
        string expectedAddress
    )
    {
        var host = new NetworkScannerHost { Address = "192.168.1.10" };
        var port = new NetworkScannerPort
        {
            Number = number,
            Protocol = protocol,
            State = "open",
            Service = service,
        };

        var created = NetworkPortCatalog.CreatePingService(host, port, "Scanner");

        Assert.AreEqual(expectedType, created.ProtocolType);
        Assert.AreEqual(expectedAddress, created.IpAddressOrUrl);
    }

    [TestMethod]
    public void ArpTable_ParsesEveryPlatformDump()
    {
        const string windows = """
                               Interface: 192.168.1.10 --- 0x5
                                 Internet Address      Physical Address      Type
                                 192.168.1.1           aa-bb-cc-dd-ee-ff     dynamic
                                 192.168.1.255         ff-ff-ff-ff-ff-ff     static
                               """;
        const string macOs = "? (192.168.1.2) at 0:1e:c9:aa:bb:cc on en0 ifscope [ethernet]";
        const string linux =
            "192.168.1.3     0x1         0x2         10:22:33:44:55:66     *        eth0\n"
            + "192.168.1.4     0x1         0x0         00:00:00:00:00:00     *        eth0";

        var windowsEntries = ArpTable.Parse(windows);
        var macEntries = ArpTable.Parse(macOs);
        var linuxEntries = ArpTable.Parse(linux);

        Assert.AreEqual("AA:BB:CC:DD:EE:FF", windowsEntries["192.168.1.1"]);
        Assert.AreEqual("00:1E:C9:AA:BB:CC", macEntries["192.168.1.2"]);
        Assert.AreEqual("10:22:33:44:55:66", linuxEntries["192.168.1.3"]);
        // An incomplete entry carries the all-zero address and is not a host.
        Assert.IsFalse(linuxEntries.ContainsKey("192.168.1.4"));
    }

    [TestMethod]
    public void ArpTable_SkipsBroadcastAndMulticastEntries()
    {
        const string windows = """
                               Interface: 192.168.1.10 --- 0x5
                                 Internet Address      Physical Address      Type
                                 192.168.1.1           aa-bb-cc-dd-ee-ff     dynamic
                                 192.168.1.255         ff-ff-ff-ff-ff-ff     static
                                 224.0.0.22            01-00-5e-00-00-16     static
                                 239.255.255.250       01-00-5e-7f-ff-fa     static
                                 255.255.255.255       ff-ff-ff-ff-ff-ff     static
                               """;
        // Multicast and broadcast addresses never belong to a host, whichever side of the entry gives it away.
        const string oddEntries = """
                                  192.168.1.20  01-00-5e-00-00-16  static
                                  230.1.1.1     aa-bb-cc-dd-ee-ff  static
                                  """;

        var entries = ArpTable.Parse(windows);

        Assert.HasCount(1, entries);
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", entries["192.168.1.1"]);
        Assert.IsEmpty(ArpTable.Parse(oddEntries));
    }

    [TestMethod]
    public void NetworkTarget_RecognizesTheNetworkAndBroadcastAddressOfACidr()
    {
        Assert.IsTrue(NetworkTarget.TryParse("192.168.1.0/24", out var cidr));
        Assert.IsTrue(cidr.IsNetworkOrBroadcast("192.168.1.0"));
        Assert.IsTrue(cidr.IsNetworkOrBroadcast("192.168.1.255"));
        Assert.IsFalse(cidr.IsNetworkOrBroadcast("192.168.1.1"));
        Assert.IsFalse(cidr.IsNetworkOrBroadcast("fe80::1"));

        // A /31 or /32 has no network and broadcast address, and a range or single address never does.
        Assert.IsTrue(NetworkTarget.TryParse("192.168.1.0/31", out var pair));
        Assert.IsFalse(pair.IsNetworkOrBroadcast("192.168.1.0"));
        Assert.IsTrue(NetworkTarget.TryParse("192.168.1.0-255", out var range));
        Assert.IsFalse(range.IsNetworkOrBroadcast("192.168.1.255"));
        Assert.IsTrue(NetworkTarget.TryParse("192.168.1.5", out var single));
        Assert.IsFalse(single.IsNetworkOrBroadcast("192.168.1.5"));
    }

    [TestMethod]
    [DataRow("192.168.1.0/24", 256, 254)]
    [DataRow("192.168.1.0/30", 4, 2)]
    [DataRow("192.168.1.0/31", 2, 2)]
    [DataRow("192.168.1.7/32", 1, 1)]
    [DataRow("192.168.1.10-20", 11, 11)]
    [DataRow("0.0.0.0/0", 4_294_967_296, 4_294_967_294)]
    public void NetworkTarget_CountsItsAddresses(string target, long addressCount, long usableCount)
    {
        Assert.IsTrue(NetworkTarget.TryParse(target, out var parsed), target);
        Assert.AreEqual(addressCount, parsed.AddressCount);
        Assert.AreEqual(usableCount, parsed.UsableHostCount);
    }

    [TestMethod]
    [DataRow("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF")]
    [DataRow("AA-BB-CC-DD-EE-FF", "AA:BB:CC:DD:EE:FF")]
    [DataRow("0:1:2:3:4:5", "00:01:02:03:04:05")]
    [DataRow("00:00:00:00:00:00", "")]
    [DataRow("aa:bb:cc:dd:ee", "")]
    [DataRow("zz:bb:cc:dd:ee:ff", "")]
    public void NormalizeMacAddress_NormalizesOrRejects(string value, string expected)
    {
        Assert.AreEqual(expected, ArpTable.NormalizeMacAddress(value));
    }

    [TestMethod]
    [DataRow("192.168.1.0/24", "192.168.1.77", true)]
    [DataRow("192.168.1.0/24", "192.168.2.77", false)]
    [DataRow("192.168.1.0/24", "fe80::1", false)]
    [DataRow("10.0.0.0/8", "10.255.255.254", true)]
    [DataRow("192.168.1.5", "192.168.1.5", true)]
    [DataRow("192.168.1.5", "192.168.1.6", false)]
    [DataRow("192.168.1.10-20", "192.168.1.15", true)]
    [DataRow("192.168.1.10-20", "192.168.1.21", false)]
    public void NetworkTarget_TestsMembershipWithoutExpanding(string target, string address, bool expected)
    {
        Assert.IsTrue(NetworkTarget.TryParse(target, out var parsed), target);
        Assert.AreEqual(expected, parsed.Contains(address));
    }

    [TestMethod]
    public void NetworkTarget_RejectsWhatItCannotRange()
    {
        Assert.IsFalse(NetworkTarget.TryParse("router.local", out _));
        Assert.IsFalse(NetworkTarget.TryParse("192.168.1.0/40", out _));
        Assert.IsFalse(NetworkTarget.TryParse("", out _));
    }

    [TestMethod]
    public void KeepRecentlySeen_KeepsAHostThatOneScanMissed()
    {
        var previous = new[] { Host("192.168.1.10"), Host("192.168.1.11") };
        var current = new[] { Host("192.168.1.10") };

        var kept = NetworkScanRetention.KeepRecentlySeen(previous, current);

        Assert.HasCount(2, kept);
        var missing = kept.Single(host => host.Address == "192.168.1.11");
        Assert.AreEqual(1, missing.MissedScans);
        Assert.IsTrue(missing.IsMissing);
        Assert.AreEqual("down", missing.State);
        Assert.AreEqual(NetworkScannerChange.Gone, missing.Change);
        Assert.AreEqual(0, kept.Single(host => host.Address == "192.168.1.10").MissedScans);
    }

    [TestMethod]
    public void KeepRecentlySeen_DropsAHostAfterTheGraceScans()
    {
        var host = Host("192.168.1.11");
        var current = new[] { Host("192.168.1.10") };

        for (var missed = 1; missed <= NetworkScanRetention.DefaultGraceScans; missed++)
        {
            var kept = NetworkScanRetention.KeepRecentlySeen([host, current[0]], current);
            host = kept.Single(value => value.Address == "192.168.1.11");
            Assert.AreEqual(missed, host.MissedScans);
        }

        var dropped = NetworkScanRetention.KeepRecentlySeen([host, current[0]], current);

        Assert.HasCount(1, dropped);
        Assert.AreEqual("192.168.1.10", dropped[0].Address);
    }

    [TestMethod]
    public void KeepRecentlySeen_ResetsTheCounterWhenTheHostAnswersAgain()
    {
        var missing = Host("192.168.1.11").WithMissedScan();

        var kept = NetworkScanRetention.KeepRecentlySeen([missing], [Host("192.168.1.11")]);

        Assert.HasCount(1, kept);
        Assert.AreEqual(0, kept[0].MissedScans);
        Assert.IsFalse(kept[0].IsMissing);
    }

    [TestMethod]
    public void KeepRecentlySeen_OrdersHostsNumerically()
    {
        var kept = NetworkScanRetention.KeepRecentlySeen(
            [Host("192.168.1.9")],
            [Host("192.168.1.10"), Host("192.168.1.2")]);

        Assert.AreSequenceEqual(
            new[] { "192.168.1.2", "192.168.1.9", "192.168.1.10" },
            kept.Select(host => host.Address).ToArray());
    }

    [TestMethod]
    public void NetworkScansFile_StoreReplacesTheScanOfTheSameTarget()
    {
        var file = CreateScansFile();
        file.Store(Snapshot("192.168.1.0/24", "192.168.1.10"));
        file.Store(Snapshot("10.0.0.0/24", "10.0.0.5"));
        file.Store(Snapshot("192.168.1.0/24", "192.168.1.11"));

        Assert.HasCount(2, file);
        var latest = file.FindLatest("192.168.1.0/24");
        Assert.IsNotNull(latest);
        Assert.HasCount(1, latest.Hosts);
        Assert.AreEqual("192.168.1.11", latest.Hosts[0].Address);
    }

    [TestMethod]
    public void NetworkScansFile_RemoveTargetForgetsTheStoredScan()
    {
        var file = CreateScansFile();
        file.Store(Snapshot("192.168.1.0/24", "192.168.1.10"));
        file.Store(Snapshot("10.0.0.0/24", "10.0.0.5"));

        Assert.IsTrue(file.RemoveTarget(" 192.168.1.0/24 "));
        Assert.IsNull(file.FindLatest("192.168.1.0/24"));
        Assert.IsNotNull(file.FindLatest("10.0.0.0/24"));

        Assert.IsFalse(file.RemoveTarget("192.168.1.0/24"));
        Assert.IsFalse(file.RemoveTarget(null));
        Assert.IsFalse(file.RemoveTarget("   "));
    }

    // The instance must never touch the real settings directory of the developer running the tests.
    private static NetworkScansFile CreateScansFile() =>
        new() { AutoSave = false, DirectoryPath = Path.Combine(Path.GetTempPath(), $"netsonar-tests-{Guid.NewGuid():N}") };

    private static NetworkScanSnapshot Snapshot(string target, params string[] addresses) =>
        new()
        {
            Target = target,
            Hosts = addresses.Select(Host).ToArray(),
        };

    private static NetworkScannerHost Host(string address) =>
        new() { Address = address, State = "up" };
}
