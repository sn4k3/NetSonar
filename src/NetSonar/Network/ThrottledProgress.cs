using System;
using System.Diagnostics;
using System.Threading;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// Forwards scan progress at most once per interval, so a fast engine cannot flood the UI thread.
/// </summary>
/// <param name="inner">The progress the throttled values are forwarded to.</param>
/// <param name="interval">The shortest delay between two forwarded values.</param>
/// <remarks>
/// Safe to report from many threads. A value that completes the scan (100%) is always forwarded.
/// </remarks>
public sealed class ThrottledProgress(IProgress<NetworkScanProgress> inner, TimeSpan interval)
    : IProgress<NetworkScanProgress>
{
    private long _lastTimestamp;

    /// <inheritdoc />
    public void Report(NetworkScanProgress value)
    {
        if (value.Percent < 100)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastTimestamp);

            // Only a forwarded value moves the window; otherwise a steady stream of reports faster than the
            // interval would keep resetting it and nothing would ever get through.
            if (
                previous != 0
                && Stopwatch.GetElapsedTime(previous, timestamp) < interval
            )
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastTimestamp, timestamp, previous) != previous)
            {
                return;
            }
        }

        inner.Report(value);
    }
}
