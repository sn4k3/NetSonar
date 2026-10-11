using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetSonar.Avalonia.Network;

namespace NetSonar.Tests;

[TestClass]
public sealed class ThrottledProgressTests
{
    [TestMethod]
    public void Report_ForwardsSteadyReportsFasterThanTheInterval()
    {
        var sink = new Sink();
        var progress = new ThrottledProgress(sink, TimeSpan.FromMilliseconds(20));

        // Every report arrives well inside the interval of the previous one, like the built-in engine's
        // one report per probe. The window must still open now and then.
        var clock = Stopwatch.StartNew();
        var percent = 1d;
        while (clock.ElapsedMilliseconds < 400)
        {
            progress.Report(new NetworkScanProgress("ICMP sweep", percent));
            percent = Math.Min(percent + 0.01, 99);
            Thread.SpinWait(2_000);
        }

        progress.Report(new NetworkScanProgress("ICMP sweep", 100));

        // 400 ms at a 20 ms interval allows about 20 values. Before the fix only the first and the final value got
        // through, so a loose bound still proves it without depending on timer precision.
        Assert.IsGreaterThan(3, sink.Values.Count);
        Assert.AreEqual(100, sink.Values.ToArray()[^1].Percent);
    }

    [TestMethod]
    public void Report_DropsReportsInsideTheIntervalButAlwaysForwardsCompletion()
    {
        var sink = new Sink();
        var progress = new ThrottledProgress(sink, TimeSpan.FromHours(1));

        for (var step = 1; step <= 50; step++)
            progress.Report(new NetworkScanProgress("Scan", step));

        Assert.HasCount(1, sink.Values);
        Assert.AreEqual(1, sink.Values.ToArray()[0].Percent);

        progress.Report(new NetworkScanProgress("Scan", 100));

        Assert.HasCount(2, sink.Values);
        Assert.AreEqual(100, sink.Values.ToArray()[1].Percent);
    }

    private sealed class Sink : IProgress<NetworkScanProgress>
    {
        public ConcurrentQueue<NetworkScanProgress> Values { get; } = new();

        public void Report(NetworkScanProgress value) => Values.Enqueue(value);
    }
}
