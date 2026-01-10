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

    [JsonIgnore]
    public string GridRepresentation => $"Low: {MathF.Round(Low, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms\nAvg: {MathF.Round(Latency, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms\nHigh: {MathF.Round(High, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms\nJitter: {MathF.Round(Jitter, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms";
}

public record SpeedTestResultStream
{

    /// <summary>
    /// Bandwidth in bits per second
    /// </summary>
    [JsonPropertyName("bandwidth")]
    public int Bandwidth { get; init; }

    /// <summary>
    /// Gets the bandwidth in bytes per second.
    /// </summary>
    [JsonIgnore]
    public int BandwidthBps => Bandwidth / 8;

    /// <summary>
    /// Bandwidth in Megabits per second
    /// </summary>
    [JsonIgnore]
    public int BandwidthMbps => Bandwidth / 124_000;

    /// <summary>
    /// Bandwidth in Megabytes per second
    /// </summary>
    [JsonIgnore]
    public int BandwidthMBps => BandwidthBps / 124_000;

    /// <summary>
    /// Total bytes transferred
    /// </summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

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

    [JsonIgnore]
    public string GridRepresentation => $"""
                                         {BandwidthMbps} mbps  ({BandwidthMBps} MB)
                                         LL: {MathF.Round(Latency.Low, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms  HL: {MathF.Round(Latency.High, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms
                                         IQM: {MathF.Round(Latency.Iqm, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms
                                         Jitter: {MathF.Round(Latency.Jitter, SpeedTestBaseResponse.PingRoundingPrecision, MidpointRounding.AwayFromZero)}ms
                                         """;
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

    [JsonIgnore]
    public string GridRepresentation => $"{(!string.IsNullOrWhiteSpace(Name) ? $"{Name}  " : string.Empty)}(VPN: {(IsVpn ? "Yes" : "No")})\nMAC: {MacAddr}\nInternal IP: {InternalIp}\nExternal IP: {ExternalIp}";
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

    [JsonIgnore]
    public string GridRepresentation => $"{Name}\n{Host}\n{Ip}:{Port}\n{Location}, {Country}";
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

