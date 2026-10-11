using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia.Network;

namespace NetSonar.Tests;

[TestClass]
public sealed class NmapScannerServiceTests
{
    [TestMethod]
    [DataRow("192.168.1.0/24", true)]
    [DataRow("router.local", true)]
    [DataRow("192.168.1.1-254", true)]
    [DataRow("--script=vuln", false)]
    [DataRow("192.168..1", false)]
    [DataRow("192.168.1.0/40", false)]
    [DataRow("not@a.target", false)]
    public void IsValidTarget_OnlyAcceptsSupportedTargetExpressions(string target, bool expected)
    {
        Assert.AreEqual(expected, NmapScannerService.IsValidTarget(target));
    }

    [TestMethod]
    [DataRow("192.168.1.0/24, 192.168.2.0/24, 192.168.5.0/24")]
    [DataRow("192.168.1.0/24;192.168.2.0/24")]
    [DataRow("192.168.1,2.1")]
    public void IsValidTarget_AcceptsATargetListWhoseEveryPartIsValid(string target)
    {
        Assert.IsTrue(NmapScannerService.IsValidTarget(target));
    }

    [TestMethod]
    [DataRow("192.168.1.0/24, -oX")]
    [DataRow("192.168.1.0/24, 192.168.1.0/40")]
    [DataRow("")]
    [DataRow(" ; ")]
    public void IsValidTarget_RejectsATargetListWithAnInvalidOrMissingPart(string target)
    {
        Assert.IsFalse(NmapScannerService.IsValidTarget(target));
    }

    [TestMethod]
    [DataRow("192.168.1.0/24, 192.168.2.0/24, 192.168.5.0/24", 600)]
    [DataRow("192.168.1.0/24,192.168.2.0/24,192.168.5.0/24", 761)]
    public void EnsureWithinHostLimit_SumsTheHostsOfEveryTarget(string target, int maxHosts)
    {
        // Three /24 networks hold 762 usable hosts together, though each one alone fits.
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NmapScannerService.EnsureWithinHostLimit(target, maxHosts));

        StringAssert.Contains(exception.Message, "expands to 762 addresses");
    }

    [TestMethod]
    public void EnsureWithinHostLimit_AcceptsTargetsWhoseTotalFitsTheLimit()
    {
        NmapScannerService.EnsureWithinHostLimit("192.168.1.0/24, 192.168.2.0/24, 192.168.5.0/24", 1000);
    }

    [TestMethod]
    [DataRow("10.0.0.0/8", 10_000)]
    [DataRow("0.0.0.0/0", 10_000)]
    [DataRow("192.168.0.0/16", 4_096)]
    [DataRow("192.168.1.0/24", 100)]
    public void EnsureWithinHostLimit_RejectsATargetAboveTheLimit(string target, int maxHosts)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NmapScannerService.EnsureWithinHostLimit(target, maxHosts));

        StringAssert.Contains(exception.Message, $"above the {maxHosts} limit");
    }

    [TestMethod]
    [DataRow("192.168.1.0/24", 254)]
    [DataRow("192.168.1.0/31", 2)]
    [DataRow("192.168.1.7", 1)]
    [DataRow("192.168.1.10-20", 11)]
    [DataRow("router.local", 1)]
    public void EnsureWithinHostLimit_AcceptsATargetWithinTheLimit(string target, int maxHosts)
    {
        // The usable count leaves out the network and broadcast addresses, exactly like the built-in engine.
        NmapScannerService.EnsureWithinHostLimit(target, maxHosts);
    }

    [TestMethod]
    public async Task DiscoverAsync_RefusesAnOversizedTargetBeforeStartingNmap()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            NmapScannerService.DiscoverAsync(
                "nmap-that-does-not-exist",
                "10.0.0.0/8",
                new NetworkScanOptions { MaxHosts = 4_096 }));

        Assert.AreEqual(
            "The target expands to 16777214 addresses, above the 4096 limit.",
            exception.Message);
    }

    [TestMethod]
    public void GetNetworkCidr_ClearsHostBits()
    {
        var cidr = NmapScannerService.GetNetworkCidr(IPAddress.Parse("192.168.4.99"), 24);

        Assert.AreEqual("192.168.4.0/24", cidr);
    }

    [TestMethod]
    public void GetNetworkCidr_RejectsNonIpv4Addresses()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            NmapScannerService.GetNetworkCidr(IPAddress.Parse("fe80::1"), 24));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            NmapScannerService.GetNetworkCidr(IPAddress.Parse("192.168.1.1"), 33));
    }

    [TestMethod]
    public void GetLocalTargets_NeverReturnsATargetWiderThanTheMinimumPrefix()
    {
        foreach (var target in NmapScannerService.GetLocalTargets())
        {
            var separator = target.LastIndexOf('/');
            Assert.IsGreaterThan(0, separator, target);
            Assert.IsTrue(int.TryParse(target[(separator + 1)..], out var prefix), target);
            Assert.IsGreaterThanOrEqualTo(NmapScannerService.MinimumTargetPrefixLength, prefix, target);
        }
    }

    [TestMethod]
    public void ParseXml_ReturnsUpHostsAndOpenPorts()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <nmaprun>
                             <host>
                               <status state="up" />
                               <address addr="192.168.1.10" addrtype="ipv4" />
                               <address addr="AA:BB:CC:DD:EE:FF" addrtype="mac" vendor="Example Vendor" />
                               <hostnames><hostname name="printer.local" /></hostnames>
                               <ports>
                                 <port protocol="tcp" portid="80"><state state="open" /><service name="http" product="nginx" version="1.26" /></port>
                                 <port protocol="tcp" portid="22"><state state="closed" /></port>
                               </ports>
                             </host>
                             <host><status state="down" /><address addr="192.168.1.11" addrtype="ipv4" /></host>
                           </nmaprun>
                           """;

        var hosts = NmapScannerService.ParseXml(xml);

        Assert.HasCount(1, hosts);
        var host = hosts[0];
        Assert.AreEqual("192.168.1.10", host.Address);
        Assert.AreEqual("printer.local", host.Hostname);
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", host.MacAddress);
        Assert.AreEqual("Example Vendor", host.Vendor);
        Assert.IsTrue(host.IsPortScanned);
        Assert.HasCount(1, host.Ports);
        Assert.AreEqual(80, host.Ports[0].Number);
        Assert.AreEqual("http", host.Ports[0].Service);
        Assert.AreEqual("nginx 1.26", host.Ports[0].ProductVersion);
    }

    [TestMethod]
    public void ParseXml_ReadsTheOsMatchOfAnElevatedScan()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <nmaprun>
                             <host>
                               <status state="up" />
                               <address addr="10.0.0.5" addrtype="ipv4" />
                               <os><osmatch name="Linux 5.X" accuracy="97" /></os>
                             </host>
                           </nmaprun>
                           """;

        var hosts = NmapScannerService.ParseXml(xml);

        Assert.HasCount(1, hosts);
        Assert.AreEqual("Linux 5.X", hosts[0].OperatingSystem);
        Assert.IsFalse(hosts[0].IsPortScanned);
    }

    [TestMethod]
    public void ParseXml_SalvagesAReportTruncatedByACancelledScan()
    {
        // A killed nmap leaves the report without </nmaprun>.
        const string xml = """
                           <?xml version="1.0"?>
                           <nmaprun>
                             <host>
                               <status state="up" />
                               <address addr="192.168.1.10" addrtype="ipv4" />
                             </host>
                             <host>
                               <status state="up" />
                           """;

        var hosts = NmapScannerService.ParseXml(xml);

        Assert.HasCount(1, hosts);
        Assert.AreEqual("192.168.1.10", hosts[0].Address);
    }

    [TestMethod]
    public void ParseXml_ReturnsNothingForGarbage()
    {
        Assert.IsEmpty(NmapScannerService.ParseXml("not xml at all"));
        Assert.IsEmpty(NmapScannerService.ParseXml(string.Empty));
        Assert.IsEmpty(NmapScannerService.ParseXml(null));
    }

    [TestMethod]
    public void WithPortScan_DropsPortsOfAHostThatStoppedAnswering()
    {
        var host = new NetworkScannerHost
        {
            Address = "192.168.1.10",
            Hostname = "nas.local",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            IsPortScanned = true,
            Ports =
            [
                new NetworkScannerPort
                {
                    Number = 445,
                    Protocol = "tcp",
                    State = "open",
                },
            ],
        };

        var merged = host.WithPortScan(null);

        Assert.IsEmpty(merged.Ports);
        Assert.IsTrue(merged.IsPortScanned);
        Assert.AreEqual("nas.local", merged.Hostname);
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", merged.MacAddress);
    }

    [TestMethod]
    public void WithCarriedPorts_KeepsKnownPortsAcrossARediscovery()
    {
        var previous = new NetworkScannerHost
        {
            Address = "192.168.1.10",
            IsPortScanned = true,
            Ports =
            [
                new NetworkScannerPort
                {
                    Number = 22,
                    Protocol = "tcp",
                    State = "open",
                },
            ],
        };
        var discovered = new NetworkScannerHost { Address = "192.168.1.10", State = "up" };

        var merged = discovered.WithCarriedPorts(previous);

        Assert.HasCount(1, merged.Ports);
        Assert.AreEqual(22, merged.Ports[0].Number);
    }

    [TestMethod]
    public void Compare_TagsNewGoneAndChangedHosts()
    {
        NetworkScannerHost Host(string address, params int[] ports) =>
            new()
            {
                Address = address,
                IsPortScanned = true,
                Ports = ports
                    .Select(port => new NetworkScannerPort
                    {
                        Number = port,
                        Protocol = "tcp",
                        State = "open",
                    })
                    .ToArray(),
            };

        var previous = new[] { Host("192.168.1.10", 22), Host("192.168.1.11") };
        var current = new[] { Host("192.168.1.10", 22, 80), Host("192.168.1.12") };

        var diff = NetworkScanDiff.Compare(previous, current);

        Assert.IsTrue(diff.HasChanges);
        CollectionAssert.AreEquivalent(new[] { "192.168.1.12" }, diff.NewHosts.ToArray());
        CollectionAssert.AreEquivalent(new[] { "192.168.1.11" }, diff.GoneHosts.ToArray());
        CollectionAssert.AreEquivalent(new[] { "192.168.1.10" }, diff.ChangedHosts.ToArray());
        Assert.AreEqual(NetworkScannerChange.PortsChanged, current[0].Change);
        Assert.AreEqual(NetworkScannerChange.New, current[1].Change);
    }

    [TestMethod]
    public async Task GetVersionAsync_ReturnsExpectedResultBasedOnExecutableAvailability()
    {
        var version = await NmapScannerService.GetVersionAsync();
        if (NmapScannerService.TryFindExecutable(out _))
        {
            Assert.IsNotNull(version);
            StringAssert.Contains(version, "Nmap");
        }
        else
        {
            Assert.IsNull(version);
        }
    }

    [TestMethod]
    public void TryParseProgress_ReadsTheRemainingSecondsInsteadOfTheCompletionTimestamp()
    {
        const string line =
            """<taskprogress task="SYN Stealth Scan" time="1758553189" percent="42.50" remaining="95" etc="1758553284" />""";

        Assert.IsTrue(NmapScannerService.TryParseProgress(line, out var progress));
        Assert.AreEqual("SYN Stealth Scan", progress.Task);
        Assert.AreEqual(42.5, progress.Percent);
        Assert.AreEqual(TimeSpan.FromSeconds(95), progress.Eta);
    }

    [TestMethod]
    public void TryParseProgress_FallsBackToTheCompletionTimestamp()
    {
        var completion = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
        var line =
            $"""<taskprogress task="Ping Scan" time="1" percent="10.00" etc="{completion}" />""";

        Assert.IsTrue(NmapScannerService.TryParseProgress(line, out var progress));
        Assert.IsNotNull(progress.Eta);
        // The fallback subtracts the current time, so it lands close to the two minutes left.
        Assert.IsGreaterThan(TimeSpan.FromSeconds(90), progress.Eta!.Value);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(120), progress.Eta!.Value);
    }

    [TestMethod]
    public void TryParseProgress_HasNoEtaWhenNmapReportsNone()
    {
        const string line = """<taskprogress task="Ping Scan" time="1" percent="10.00" />""";

        Assert.IsTrue(NmapScannerService.TryParseProgress(line, out var progress));
        Assert.IsNull(progress.Eta);
    }

    [TestMethod]
    public void TryParseProgress_IgnoresOtherLines()
    {
        Assert.IsFalse(NmapScannerService.TryParseProgress("<host><status state=\"up\" /></host>", out _));
    }
}
