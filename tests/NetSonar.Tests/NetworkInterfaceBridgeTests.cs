using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia.Network;

namespace NetSonar.Tests;

[TestClass]
public sealed class NetworkInterfaceBridgeTests
{
    private const string NetworkServiceOrder = """
        An asterisk (*) denotes that a network service is disabled.
        (1) Wi-Fi
        (Hardware Port: Wi-Fi, Device: en0)

        (2) USB 10/100/1000 LAN
        (Hardware Port: USB 10/100/1000 LAN, Device: en5)

        (*) Bluetooth PAN
        (Hardware Port: Bluetooth PAN, Device: en6)

        (4) Thunderbolt Bridge
        (Hardware Port: Thunderbolt Bridge, Device: bridge0)

        (5) VPN (Work)
        (Hardware Port: VPN (Work), Device: )

        """;

    [TestMethod]
    [DataRow("en0", "Wi-Fi")]
    [DataRow("en5", "USB 10/100/1000 LAN")]
    [DataRow("bridge0", "Thunderbolt Bridge")]
    public void ParseMacNetworkServiceName_ReturnsTheServiceUsingTheDevice(string device, string expected)
    {
        Assert.AreEqual(expected, NetworkInterfaceBridge.ParseMacNetworkServiceName(NetworkServiceOrder, device));
    }

    [TestMethod]
    public void ParseMacNetworkServiceName_ResolvesDisabledServices()
    {
        Assert.AreEqual("Bluetooth PAN", NetworkInterfaceBridge.ParseMacNetworkServiceName(NetworkServiceOrder, "en6"));
    }

    [TestMethod]
    public void ParseMacNetworkServiceName_HandlesWindowsLineEndings()
    {
        var output = NetworkServiceOrder.ReplaceLineEndings("\r\n");
        Assert.AreEqual("Wi-Fi", NetworkInterfaceBridge.ParseMacNetworkServiceName(output, "en0"));
    }

    [TestMethod]
    [DataRow("en1")]
    [DataRow("en")]
    [DataRow("")]
    public void ParseMacNetworkServiceName_ReturnsNullWhenNoServiceUsesTheDevice(string device)
    {
        Assert.IsNull(NetworkInterfaceBridge.ParseMacNetworkServiceName(NetworkServiceOrder, device));
    }

    [TestMethod]
    public void ParseMacNetworkServiceName_ReturnsNullForEmptyOutput()
    {
        Assert.IsNull(NetworkInterfaceBridge.ParseMacNetworkServiceName(string.Empty, "en0"));
    }

    [TestMethod]
    public void ParseNameServers_KeepsTheConfiguredOrderOfTheRequestedFamily()
    {
        var servers = NetworkInterfaceBridge.ParseNameServers(
            "1.1.1.1,2606:4700:4700::1111 8.8.8.8",
            System.Net.Sockets.AddressFamily.InterNetwork
        );

        Assert.AreSequenceEqual(
            new[] { System.Net.IPAddress.Parse("1.1.1.1"), System.Net.IPAddress.Parse("8.8.8.8") },
            servers
        );
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("not-an-address")]
    public void ParseNameServers_ReturnsNothingWithoutStaticServers(string? value)
    {
        Assert.IsEmpty(
            NetworkInterfaceBridge.ParseNameServers(value, System.Net.Sockets.AddressFamily.InterNetwork)
        );
    }
}
