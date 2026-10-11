using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using NetSonar.Avalonia.Network;

namespace NetSonar.Avalonia.Converters;

/// <summary>
/// Builds the localized multi-line cell text of the speed test results grid from one part of a
/// <see cref="SpeedTestResult"/>.
/// </summary>
/// <remarks>
/// The first value is the part (<see cref="SpeedTestResultPing"/>, <see cref="SpeedTestResultStream"/>,
/// <see cref="SpeedTestResultInterface"/> or <see cref="SpeedTestResultServer"/>). The second value is the active
/// culture and only exists so the text is rebuilt when the language changes. Units stay unlocalized.
/// </remarks>
public sealed class SpeedTestGridConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || ReferenceEquals(values[0], AvaloniaProperty.UnsetValue)) return null;

        return values[0] switch
        {
            SpeedTestResultPing ping => FormatPing(ping),
            SpeedTestResultStream stream => FormatStream(stream),
            SpeedTestResultInterface networkInterface => FormatInterface(networkInterface),
            SpeedTestResultServer server => FormatServer(server),
            _ => null
        };
    }

    private static string FormatPing(SpeedTestResultPing ping)
    {
        return $"{App.Localization["Ui.Low"]} {Round(ping.Low)}ms\n"
               + $"{App.Localization["SpeedTest.Grid.Average"]} {Round(ping.Latency)}ms\n"
               + $"{App.Localization["Ui.High"]} {Round(ping.High)}ms\n"
               + $"{App.Localization["Ui.Jitter"]} {Round(ping.Jitter)}ms";
    }

    private static string FormatStream(SpeedTestResultStream stream)
    {
        var latency = stream.Latency;
        return $"{stream.BandwidthMbps} Mbps  ({stream.BandwidthMBps} MB)\n"
               + $"{App.Localization["SpeedTest.Grid.LatencyLow"]} {Round(latency.Low)}ms  {App.Localization["SpeedTest.Grid.LatencyHigh"]} {Round(latency.High)}ms\n"
               + $"{App.Localization["Ui.IQM"]} {Round(latency.Iqm)}ms\n"
               + $"{App.Localization["Ui.Jitter"]} {Round(latency.Jitter)}ms";
    }

    private static string FormatInterface(SpeedTestResultInterface networkInterface)
    {
        var name = string.IsNullOrWhiteSpace(networkInterface.Name) ? string.Empty : $"{networkInterface.Name}  ";
        var vpn = App.Localization[networkInterface.IsVpn ? "Common.Yes" : "Common.No"];
        return $"{name}({App.Localization["SpeedTest.Grid.Vpn"]} {vpn})\n"
               + $"{App.Localization["SpeedTest.Grid.Mac"]} {networkInterface.MacAddr}\n"
               + $"{App.Localization["SpeedTest.Grid.InternalIp"]} {networkInterface.InternalIp}\n"
               + $"{App.Localization["SpeedTest.Grid.ExternalIp"]} {networkInterface.ExternalIp}";
    }

    private static string FormatServer(SpeedTestResultServer server)
    {
        return $"{server.Name}\n{server.Host}\n{server.Ip}:{server.Port}\n{server.Location}, {server.Country}";
    }

    private static float Round(float value)
    {
        return MathF.Round(value, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero);
    }
}
