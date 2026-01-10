using System.Diagnostics.CodeAnalysis;
using System;
using System.Net;
using System.Text.Json.Serialization;

namespace NetSonar.Avalonia.Network;

public record BasePingReply
{
    /// <summary>
    /// Returns true if the ping was successful.
    /// </summary>
    public required bool IsSucceeded { get; init; }

    /// <summary>
    /// Returns true if the ping failed.
    /// </summary>
    [JsonIgnore]
    public bool IsFailed => !IsSucceeded;

    /// <summary>
    /// Gets the status of the ping.
    /// </summary>
    public required object Status { get; init; }

    [JsonIgnore]
    public string StatusStr => Status.ToString() ?? string.Empty;

    /// <summary>
    /// Gets the status code of the ping.
    /// </summary>
    public int StatusCode =>
        Status switch
        {
            Enum => (int)Status,
            byte byteStatus => byteStatus,
            sbyte sbyteStatus => sbyteStatus,
            ushort ushortStatus => ushortStatus,
            short shortStatus => shortStatus,
            int intStatus => intStatus,
            _ => -1
        };

    /// <summary>
    /// Gets the IP:Port of the ping.
    /// </summary>
    public required IPEndPoint IpEndPoint { get; init; }

    /// <summary>
    /// Gets the IP address of the ping.
    /// </summary>
    [JsonIgnore]
    public IPAddress IpAddress => IpEndPoint.Address;

    /// <summary>
    /// Gets the IP address string of the ping.
    /// </summary>
    [JsonIgnore]
    public string IpAddressStr => IpEndPoint.Address.ToString();

    /// <summary>
    /// Gets the date and time the ping was sent.
    /// </summary>
    public required DateTime SentDateTime { get; init; }

    /// <summary>
    /// Gets the time it took for the ping to complete.
    /// </summary>
    public required double Time { get; init; }

    /// <summary>
    /// Gets the error message of the ping, if any.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }


    public BasePingReply()
    {
    }

    [SetsRequiredMembers]
    public BasePingReply(bool isSucceeded, object status, IPEndPoint ipEndPoint, DateTime sentDateTime, double time, string? errorMessage = null)
    {
        IsSucceeded = isSucceeded;
        Status = status;
        IpEndPoint = ipEndPoint;
        SentDateTime = sentDateTime;
        Time = time;
        ErrorMessage = errorMessage;
    }
}