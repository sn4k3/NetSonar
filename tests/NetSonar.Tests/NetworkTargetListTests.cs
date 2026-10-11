using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia.Network;

namespace NetSonar.Tests;

[TestClass]
public sealed class NetworkTargetListTests
{
    [TestMethod]
    [DataRow("192.168.1.0/24, 192.168.2.0/24;192.168.5.0/24", 3)]
    [DataRow("192.168.1.0/24,192.168.2.0/24", 2)]
    [DataRow("192.168.1,2.1", 1)]
    [DataRow("10.0.0.1-5,9", 1)]
    [DataRow("192.168.1.0/24 192.168.1.0/24 192.168.1.0/24", 1)]
    [DataRow("", 0)]
    [DataRow("   ", 0)]
    public void Split_SeparatesTargetsAndDropsEmptyAndDuplicateEntries(string targets, int expectedCount)
    {
        Assert.HasCount(expectedCount, NetworkTargetList.Split(targets));
    }

    [TestMethod]
    public void Split_KeepsAnOctetListIntact()
    {
        Assert.AreSequenceEqual(new[] { "192.168.1,2.1" }, NetworkTargetList.Split("192.168.1,2.1"));
    }

    [TestMethod]
    public void Split_RemovesDuplicatesCaseInsensitivelyKeepingTheFirstOccurrence()
    {
        Assert.AreSequenceEqual(
            new[] { "Router.Local", "192.168.1.0/24" },
            NetworkTargetList.Split("Router.Local, 192.168.1.0/24; router.local")
        );
    }

    [TestMethod]
    public void Normalize_JoinsTheTargetsWithCommaAndSpace()
    {
        Assert.AreEqual(
            "192.168.1.0/24, 192.168.2.0/24",
            NetworkTargetList.Normalize("192.168.1.0/24;192.168.2.0/24")
        );
    }
}
