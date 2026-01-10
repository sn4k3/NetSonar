using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.VisualBasic.FileIO;
using NetSonar.Avalonia.Models;

namespace NetSonar.Avalonia.Network;

public partial class PingableService : BasePingableCollectionObject<PingableServiceReply>
{
    #region Constants
    public const double MinPingEverySeconds = 0.50;
    public const double MaxPingEverySeconds = int.MaxValue;
    public const double DefaultPingEverySeconds = 5.0;

    public const double MinTimeoutSeconds = 0.1;
    public const double MaxTimeoutSeconds = int.MaxValue;
    public const double DefaultTimeoutSeconds = 5.0;

    public const byte DefaultTtl = 128;

    public const int MinBufferSize = 0;
    public const int MaxBufferSize = 65500;
    public const int DefaultBufferSize = 32;

    #endregion

    #region Members

    private byte[]? _sendBuffer;

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets the buffer size of the ping.
    /// </summary>
    public int BufferSize
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, MinBufferSize, MaxBufferSize));
    } = 32;

    /// <summary>
    /// Gets or sets the maximum time to live (TTL),
    /// the amount of time or "hops" that a packet is set to exist inside
    /// a network before being discarded by a router.
    /// </summary>
    public byte Ttl
    {
        get;
        set => SetProperty(ref field, Math.Max((byte)1, value));
    } = DefaultTtl;


    /// <summary>
    /// Gets or sets if the ping packet can be fragmented.
    /// </summary>
    [ObservableProperty]
    public partial bool DontFragment { get; set; }

    [JsonIgnore]
    public bool CanUseBufferSize => ProtocolType is ServiceProtocolType.ICMP or ServiceProtocolType.TCP or ServiceProtocolType.UDP;

    [JsonIgnore]
    public bool CanUseTtl => ProtocolType is ServiceProtocolType.ICMP or ServiceProtocolType.TCP or ServiceProtocolType.UDP;

    [JsonIgnore]
    public bool CanUseDontFragment => ProtocolType is ServiceProtocolType.ICMP;

    /// <summary>
    /// Gets if the default ping options are used.
    /// </summary>
    [JsonIgnore]
    public bool UseDefaultPingOptions => Ttl is 0 or DefaultTtl && !DontFragment;

    /// <summary>
    /// Gets the ping options.
    /// </summary>
    [JsonIgnore]
    public PingOptions PingOptions => new(Ttl, DontFragment);
    #endregion

    #region Constructor

    [SetsRequiredMembers]
    public PingableService(ServiceProtocolType protocolType, string ipAddressOrUrl) : base(protocolType, ipAddressOrUrl)
    {
    }

    [SetsRequiredMembers]
    [JsonConstructor]
    public PingableService(ServiceProtocolType protocolType, string ipAddressOrUrl, string description = "", string group = "") : base(protocolType, ipAddressOrUrl, description, group)
    {
    }

    [SetsRequiredMembers]
    public PingableService(NewPingService service) : base(service.ProtocolType, service.IpAddressOrUrl, service.Description, service.Group)
    {
        IsEnabled = service.IsEnabled;
        PingEverySeconds = service.PingEverySeconds;
        TimeoutSeconds = service.TimeoutSeconds;
        BufferSize = service.BufferSize;
        Ttl = service.Ttl;
        DontFragment = service.DontFragment;
    }

    #endregion

    #region Methods

    [MemberNotNull(nameof(_sendBuffer))]
    private void EnsureBuffer()
    {
        if (_sendBuffer is null || _sendBuffer.Length != BufferSize)
        {
            _sendBuffer = CreateBuffer(BufferSize);
        }
    }

    /// <inheritdoc />
    protected override PingableServiceReply PingCore(int timeout = 0)
    {

        if (ProtocolType == ServiceProtocolType.ICMP)
        {
            EnsureBuffer();

            using var ping = new Ping();
            var sentDateTime = DateTime.Now;
            try
            {
                PingReply reply;
                if (OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess)
                {
                    reply = ping.Send(IpAddressOrUrl, timeout);
                }
                else
                {
                    reply = ping.Send(IpAddressOrUrl, timeout, _sendBuffer, UseDefaultPingOptions ? null : PingOptions);
                }

                return new PingableServiceReply(reply);
            }
            catch (Exception e)
            {
                return PingableServiceReply.CreateErrorReply(e.InnerException?.Message ?? e.Message, sentDateTime);
            }
        }
        else if (ProtocolType is ServiceProtocolType.TCP or ServiceProtocolType.UDP)
        {
            return PingCoreAsync(timeout).Result;
        }
        else if (ProtocolType == ServiceProtocolType.HTTP)
        {
            return PingCoreAsync(timeout).Result;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(ProtocolType), ProtocolType, null);
        }
    }


    /// <inheritdoc />
    protected override async Task<PingableServiceReply> PingCoreAsync(int timeout = 0, CancellationToken cancellationToken = default)
    {
        if (ProtocolType == ServiceProtocolType.ICMP)
        {
            EnsureBuffer();

            using var ping = new Ping();
            var sentOn = DateTime.Now;
            try
            {
                PingReply reply;
                if (OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess)
                {
                    reply = await ping.SendPingAsync(IpAddressOrUrl, TimeSpan.FromMilliseconds(timeout), cancellationToken:cancellationToken);
                }
                else
                {
                    reply = await ping.SendPingAsync(IpAddressOrUrl, TimeSpan.FromMilliseconds(timeout), _sendBuffer, UseDefaultPingOptions ? null : PingOptions, cancellationToken);
                }

                return new PingableServiceReply(reply);

            }
            catch (OperationCanceledException e)
            {
                return PingableServiceReply.CreateTimeOutReply(e, sentOn);
            }
            catch (Exception e)
            {
                return PingableServiceReply.CreateErrorReply(e, sentOn);
            }
        }
        else if (ProtocolType is ServiceProtocolType.TCP or ServiceProtocolType.UDP)
        {
            EnsureBuffer();
            var watch = new Stopwatch();
            watch.Start();
            var sentDateTime = DateTime.Now;

            try
            {
                using var socket = new Socket(IpEndPoint.AddressFamily,
                    ProtocolType == ServiceProtocolType.TCP ? SocketType.Stream : SocketType.Dgram,
                    ProtocolType == ServiceProtocolType.TCP ? System.Net.Sockets.ProtocolType.Tcp : System.Net.Sockets.ProtocolType.Udp);
                socket.Ttl = Ttl;

                using var timeoutCts = new CancellationTokenSource();
                if (timeout > 0) timeoutCts.CancelAfter(timeout);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                await socket.ConnectAsync(IpEndPoint, combinedCts.Token);
                if (_sendBuffer.Length > 0)
                {
                    await socket.SendAsync(_sendBuffer, combinedCts.Token);
                }

                socket.Close();
                return new PingableServiceReply(true, IPStatus.Success, IpEndPoint, sentDateTime, Math.Round(watch.Elapsed.TotalMilliseconds, 2, MidpointRounding.AwayFromZero), _sendBuffer.Length, Ttl);
            }
            catch (OperationCanceledException e)
            {
                return PingableServiceReply.CreateTimeOutReply(e, sentDateTime);
            }
            catch (SocketException e)
            {
                return PingableServiceReply.CreateErrorReply(e.SocketErrorCode, e, sentDateTime);
            }
            catch (Exception e)
            {
                return PingableServiceReply.CreateErrorReply(e, sentDateTime);
            }
            finally
            {
                watch.Stop();
            }
        }
        else if (ProtocolType == ServiceProtocolType.HTTP)
        {
            var sentDateTime = DateTime.Now;
            var watch = new Stopwatch();

            try
            {
                using var timeoutCts = new CancellationTokenSource();
                if (timeout > 0) timeoutCts.CancelAfter(timeout);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                watch.Start();
                var result = await App.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, IpAddressOrUrl), combinedCts.Token);
                //var isOk = result.IsSuccessStatusCode || ((int)result.StatusCode >= 300 && (int)result.StatusCode < 400);
                await using var stream = await result.Content.ReadAsStreamAsync(combinedCts.Token);
                return new PingableServiceReply(result.IsSuccessStatusCode, result.StatusCode, IpEndPoint, sentDateTime, Math.Round(watch.Elapsed.TotalMilliseconds, 2, MidpointRounding.AwayFromZero), (int)stream.Length);
            }
            catch (OperationCanceledException e)
            {
                return PingableServiceReply.CreateTimeOutReply(e, sentDateTime);
            }
            catch (HttpRequestException e)
            {
                return PingableServiceReply.CreateErrorReply(e.HttpRequestError, e, sentDateTime);
            }
            catch (Exception e)
            {
                return PingableServiceReply.CreateErrorReply(e, sentDateTime);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(ProtocolType), ProtocolType, null);
        }
    }

    #endregion

    #region Static Methods

    /// <summary>
    /// Creates a buffer with the specified size.
    /// </summary>
    /// <param name="size"></param>
    /// <returns></returns>
    public static byte[] CreateBuffer(int size = 32)
    {
        var buffer = new byte[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = (byte)('a' + i % 23);
        }

        return buffer;
    }

    /// <summary>
    /// Tries to parse a string to a <see cref="PingableService"/>.
    /// </summary>
    /// <param name="line"></param>
    /// <param name="service"></param>
    /// <returns></returns>
    public static bool TryParseFromString(string line, [NotNullWhen(true)] out PingableService? service)
    {
        try
        {
            service = ParseFromString(line);
            return true;
        }
        catch(Exception)
        {
            service = null;
            return false;
        }
    }

    public static PingableService ParseFromString(string line)
    {
        line = line.Trim().ToLowerInvariant();
        ServiceProtocolType protocol;
        if (line.Contains('/'))
        {
            if (line.StartsWith("icmp://"))
            {
                line = line.Remove(0, "icmp://".Length);
                protocol = ServiceProtocolType.ICMP;
                if (line.Contains(':')) throw new MalformedLineException($"The {protocol} protocol must not contain a port number.");
                if (line.Contains('/')) throw new MalformedLineException($"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (line.StartsWith("tcp://"))
            {
                line = line.Remove(0, "tcp://".Length);
                protocol = ServiceProtocolType.TCP;
                if (!line.Contains(':')) throw new MalformedLineException($"The {protocol} protocol must contain a port number.");
                if (line.Contains('/')) throw new MalformedLineException($"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (line.StartsWith("udp://"))
            {
                line = line.Remove(0, "udp://".Length);
                protocol = ServiceProtocolType.UDP;
                if (!line.Contains(':')) throw new MalformedLineException($"The {protocol} protocol must contain a port number.");
                if (line.Contains('/')) throw new MalformedLineException($"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (line.StartsWith("http://"))
            {
                line = line.Remove(0, "http://".Length);
                protocol = ServiceProtocolType.HTTP;
            }
            else if (line.StartsWith("https://"))
            {
                line = line.Remove(0, "https://".Length);
                protocol = ServiceProtocolType.HTTP;
            }
            else
            {
                protocol = ServiceProtocolType.HTTP;
            }
        }
        else
        {
            protocol = ServiceProtocolType.ICMP;
            if (line.Contains(':')) throw new MalformedLineException($"The {protocol} protocol must not contain a port number.");
        }

        var clearString = line.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries);
        var ipDescriptionGroup = line.Split(',', StringSplitOptions.TrimEntries);
        var pingEveryTimeoutBuffer = line.Split('|', StringSplitOptions.TrimEntries);

        var ipAddressOrUrl = clearString[0];
        var description = ipDescriptionGroup.Length >= 2 ? ipDescriptionGroup[1] : string.Empty;
        var group = ipDescriptionGroup.Length >= 3 ? ipDescriptionGroup[2] : string.Empty;

        double pingEvery = pingEveryTimeoutBuffer.Length >= 2
            ? double.TryParse(pingEveryTimeoutBuffer[1], CultureInfo.InvariantCulture, out var pingEveryResult) ? pingEveryResult : App.AppSettings.PingServices.DefaultPingEverySeconds
            : App.AppSettings.PingServices.DefaultPingEverySeconds;

        double timeout = pingEveryTimeoutBuffer.Length >= 3
            ? double.TryParse(pingEveryTimeoutBuffer[2], CultureInfo.InvariantCulture,  out var timeoutResult) ? timeoutResult : App.AppSettings.PingServices.DefaultTimeoutSeconds
            : App.AppSettings.PingServices.DefaultTimeoutSeconds;

        int bufferSize = pingEveryTimeoutBuffer.Length >= 4
            ? int.TryParse(pingEveryTimeoutBuffer[3], out var bufferResult) ? bufferResult : App.AppSettings.PingServices.DefaultBufferSize
            : App.AppSettings.PingServices.DefaultBufferSize;

        return new PingableService(protocol, ipAddressOrUrl, description, group)
        {
            Group = group,
            Description = description,
            PingEverySeconds = pingEvery,
            TimeoutSeconds = timeout,
            BufferSize = bufferSize
        };


    }
    public static List<PingableService> ParseFromText(string text)
    {
        var result = new List<PingableService>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryParseFromString(line, out var service)) result.Add(service);
        }
        return result;
    }

    #endregion
}