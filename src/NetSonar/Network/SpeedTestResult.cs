using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace NetSonar.Avalonia.Network;

public record SpeedTestBaseResponse
{
    public const int PingRoundingPrecision = 1;
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";


    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    [JsonPropertyName("timestamp_local")]
    public DateTime TimestampLocal => Timestamp.ToLocalTime();

    [JsonPropertyName("error")]

    public string? Error { get; init; }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public record SpeedTestServers : SpeedTestBaseResponse
{
    [JsonPropertyName("servers")]
    public SpeedTestResultServer[] Servers { get; init; } = [];
}


public record SpeedTestResult : SpeedTestBaseResponse
{
    [JsonPropertyName("ping")]
    public SpeedTestResultPing Ping { get; init; } = new();

    [JsonPropertyName("download")]
    public SpeedTestResultStream Download { get; init; } = new();

    [JsonPropertyName("upload")]
    public SpeedTestResultStream Upload { get; init; } = new();

    [JsonPropertyName("packetLoss")]
    public float PacketLoss { get; init; }

    [JsonPropertyName("isp")]
    public string ISP { get; init; } = string.Empty;

    [JsonPropertyName("interface")]
    public SpeedTestResultInterface Interface { get; init; } = new();

    [JsonPropertyName("server")]
    public SpeedTestResultServer Server { get; init; } = new();

    [JsonPropertyName("result")]
    public SpeedTestResultResult Result { get; init; } = new();

    [JsonPropertyName("progress")]
    public float Progress => MathF.Round(Math.Clamp((Ping.Progress + Download.Progress + Upload.Progress) / 3, 0f, 1f), 2, MidpointRounding.AwayFromZero);
}

public record SpeedTestResultPing
{
    [JsonPropertyName("jitter")]
    public float Jitter { get; init; }

    [JsonPropertyName("latency")]
    public float Latency { get; init; }

    [JsonPropertyName("low")]
    public float Low { get; init; }

    [JsonPropertyName("high")]
    public float High { get; init; }

    [JsonPropertyName("progress")]
    public float Progress { get; init; }
}

public record SpeedTestResultStream
{

    /// <summary>
    /// Bandwidth in bytes per second.
    /// </summary>
    [JsonPropertyName("bandwidth")]
    public long Bandwidth { get; init; }

    /// <summary>
    /// Gets the bandwidth in bytes per second.
    /// </summary>
    [JsonIgnore]
    public long BandwidthBps => Bandwidth;

    /// <summary>
    /// Bandwidth in Megabits per second
    /// </summary>
    [JsonIgnore]
    public double BandwidthMbps => Math.Round(Bandwidth / 125_000d, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Bandwidth in Megabytes per second
    /// </summary>
    [JsonIgnore]
    public double BandwidthMBps => Math.Round(Bandwidth / 1_000_000d, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Total bytes transferred
    /// </summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>
    /// Elapsed time in milliseconds
    /// </summary>

    [JsonPropertyName("elapsed")]
    public int Elapsed { get; init; }

    /// <summary>
    /// Gets the latency details of the speed test result.
    /// </summary>

    [JsonPropertyName("latency")]
    public SpeedTestResultLatency Latency { get; init; } = new();

    [JsonPropertyName("progress")]
    public float Progress { get; init; }
}

public record SpeedTestResultLatency
{
    /// <summary>
    /// InterQuartile Mean latency.<br />
    /// It's a statistical average that excludes the extreme high and low
    /// ping values to better represent the typical latency during the test.<br />
    /// The unit is milliseconds (ms)
    /// </summary>
    [JsonPropertyName("iqm")]
    public float Iqm { get; init; }

    [JsonPropertyName("low")]
    public float Low { get; init; }

    [JsonPropertyName("high")]
    public float High { get; init; }

    [JsonPropertyName("jitter")]
    public float Jitter { get; init; }
}

public record SpeedTestResultInterface
{
    [JsonPropertyName("internalIp")]
    public string InternalIp { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("macAddr")]
    public string MacAddr { get; init; } = string.Empty;

    [JsonPropertyName("isVpn")]
    public bool IsVpn { get; init; }

    [JsonPropertyName("externalIp")]
    public string ExternalIp { get; init; } = string.Empty;
}

public record SpeedTestResultServer
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; init; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; init; } = string.Empty;
}

public record SpeedTestResultResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;


    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;


    [JsonPropertyName("persisted")]
    public bool Persisted { get; init; }
}

