using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DotNext.Buffers;
using Microsoft.VisualBasic.FileIO;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Models;

namespace NetSonar.Avalonia.Network;

public partial class PingableService : BasePingableCollectionObject<PingableServiceReply>
{
    #region Constants

    public const double MinPingEverySeconds = 0.50;
    public const double MaxPingEverySeconds = int.MaxValue;
    public const double DefaultPingEverySeconds = 5.0;

    public const double MinTimeoutSeconds = 0.1;
    public const double MaxTimeoutSeconds = ushort.MaxValue;
    public const double DefaultTimeoutSeconds = 5.0;

    public const byte DefaultTtl = 128;

    public const int MinBufferSize = 0;
    public const int MaxBufferSize = 65500;
    public const int DefaultBufferSize = 32;

    private const int WindowsIcmpTimeoutResolution = 500;

    private const int NtpPacketLength = 48;
    private const long NtpEpochOffsetSeconds = 2_208_988_800;

    private const int DnsHeaderLength = 12;
    private const int DnsQuestionLength = 17;
    private const int DnsPacketLength = DnsHeaderLength + DnsQuestionLength;

    private const int MaxSmtpResponseLength = 8192;

    private const int MqttConnectPacketLength = 35;
    private const int MqttConnAckPacketLength = 4;

    private const int MaxSshIdentificationLength = 8192;

    private const int StunPacketLength = 20;
    private const uint StunMagicCookie = 0x2112_A442;

    private const int MaxSipResponseLength = 8192;

    private const int MaxImapResponseLength = 8192;

    #endregion

    #region Members

    private byte[]? _sendBuffer;
    private EndPoint? _socketEndPoint;
    private static readonly byte[] ImapCapabilityRequest = "A001 CAPABILITY\r\n"u8.ToArray();
    private static readonly byte[] SshIdentification = "SSH-2.0-NetSonar\r\n"u8.ToArray();

    private sealed record SipProbeRequest(byte[] Payload, string Branch, string CallId);

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
    public bool CanUseBufferSize =>
        ProtocolType is ServiceProtocolType.ICMP or ServiceProtocolType.TCP or ServiceProtocolType.UDP;

    [JsonIgnore]
    public bool CanUseTtl => ProtocolType is ServiceProtocolType.ICMP
        or ServiceProtocolType.TCP
        or ServiceProtocolType.UDP
        or ServiceProtocolType.TLS
        or ServiceProtocolType.DNS
        or ServiceProtocolType.NTP
        or ServiceProtocolType.SSH
        or ServiceProtocolType.SMTP
        or ServiceProtocolType.IMAP
        or ServiceProtocolType.MQTT
        or ServiceProtocolType.STUN
        or ServiceProtocolType.SIP;

    [JsonIgnore] public bool CanUseDontFragment => ProtocolType is ServiceProtocolType.ICMP;

    /// <summary>
    /// Gets if the default ping options are used.
    /// </summary>
    [JsonIgnore]
    public bool UseDefaultPingOptions => Ttl is 0 or DefaultTtl && !DontFragment;

    /// <summary>
    /// Gets the ping options.
    /// </summary>
    [JsonIgnore]
    public PingOptions PingOptions
    {
        get
        {
            var options = field;
            if (options is null || options.Ttl != Ttl || options.DontFragment != DontFragment)
            {
                options = new PingOptions(Ttl, DontFragment);
                field = options;
            }

            return options;
        }
    }

    #endregion

    #region Constructor

    [SetsRequiredMembers]
    public PingableService(ServiceProtocolType protocolType, string ipAddressOrUrl) : base(protocolType, ipAddressOrUrl)
    {
        BufferSize = GetProtocolBufferSize(ProtocolType, GetInitialBufferSize(ProtocolType));
    }

    [SetsRequiredMembers]
    [JsonConstructor]
    public PingableService(ServiceProtocolType protocolType, string ipAddressOrUrl, string description = "",
        string group = "") : base(protocolType, ipAddressOrUrl, description, group)
    {
        // Persisted services overwrite this when their saved BufferSize is deserialized after construction.
        BufferSize = GetProtocolBufferSize(ProtocolType, GetInitialBufferSize(ProtocolType));
    }

    [SetsRequiredMembers]
    public PingableService(NewPingService service) : base(service.ProtocolType, service.IpAddressOrUrl,
        service.Description, service.Group)
    {
        IsEnabled = service.IsEnabled;
        PingEverySeconds = service.PingEverySeconds;
        TimeoutSeconds = service.TimeoutSeconds;
        BufferSize = GetProtocolBufferSize(ProtocolType, service.BufferSize);
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

    private static int GetEffectiveIcmpTimeout(int timeout)
    {
        if (!OperatingSystem.IsWindows() || timeout <= 0) return timeout;

        // Windows floors ICMP timeouts to 500 ms; round up so the effective deadline is never shorter.
        var remainder = timeout % WindowsIcmpTimeoutResolution;
        if (remainder == 0) return timeout;

        var adjustment = WindowsIcmpTimeoutResolution - remainder;
        return timeout > int.MaxValue - adjustment ? int.MaxValue : timeout + adjustment;
    }

    internal static int GetDefaultPort(ServiceProtocolType protocolType)
    {
        return protocolType switch
        {
            ServiceProtocolType.TLS => Protocols.DefaultTlsPort,
            ServiceProtocolType.DNS => Protocols.DefaultDnsPort,
            ServiceProtocolType.NTP => Protocols.DefaultNtpPort,
            ServiceProtocolType.SSH => Protocols.DefaultSshPort,
            ServiceProtocolType.SMTP => Protocols.DefaultSmtpPort,
            ServiceProtocolType.IMAP => Protocols.DefaultImapPort,
            ServiceProtocolType.MQTT => Protocols.DefaultMqttPort,
            ServiceProtocolType.STUN => Protocols.DefaultStunPort,
            ServiceProtocolType.SIP => Protocols.DefaultSipPort,
            _ => IPEndPoint.MinPort
        };
    }

    /// <summary>
    /// Gets the buffer size of a service created without an explicit size.
    /// </summary>
    /// <remarks>A TCP probe succeeds on the connect, so it sends no payload unless the user configures one.</remarks>
    internal static int GetInitialBufferSize(ServiceProtocolType protocolType)
    {
        return protocolType == ServiceProtocolType.TCP ? 0 : DefaultBufferSize;
    }

    /// <summary>
    /// Coerces a configured buffer size into the size required by the given protocol.
    /// </summary>
    /// <param name="protocolType">The protocol the buffer is built for.</param>
    /// <param name="configuredSize">The user configured buffer size.</param>
    /// <returns>The buffer size to use, which is fixed for protocols with a well-known packet layout.</returns>
    public static int GetProtocolBufferSize(ServiceProtocolType protocolType, int configuredSize)
    {
        return protocolType switch
        {
            ServiceProtocolType.TLS => 0,
            ServiceProtocolType.DNS => DnsPacketLength,
            ServiceProtocolType.NTP => NtpPacketLength,
            ServiceProtocolType.WebSocket => 0,
            ServiceProtocolType.SSH
                or ServiceProtocolType.SMTP
                or ServiceProtocolType.IMAP => 0,
            ServiceProtocolType.MQTT => MqttConnectPacketLength,
            ServiceProtocolType.STUN => StunPacketLength,
            ServiceProtocolType.SIP => 0,
            _ => configuredSize
        };
    }

    private EndPoint GetSocketEndPoint()
    {
        if (_socketEndPoint is not null) return _socketEndPoint;
        if (IPEndPoint.TryParse(IpAddressOrUrl, out var ipEndPoint))
        {
            if (ipEndPoint.Port <= IPEndPoint.MinPort)
            {
                var defaultPort = GetDefaultPort(ProtocolType);
                if (defaultPort > IPEndPoint.MinPort)
                {
                    ipEndPoint.Port = defaultPort;
                }
            }

            return _socketEndPoint = ipEndPoint;
        }

        var scheme = ProtocolType switch
        {
            ServiceProtocolType.TCP => "tcp",
            ServiceProtocolType.UDP => "udp",
            ServiceProtocolType.TLS => "tcp",
            ServiceProtocolType.DNS => "udp",
            ServiceProtocolType.NTP => "udp",
            ServiceProtocolType.SSH => "tcp",
            ServiceProtocolType.SMTP => "tcp",
            ServiceProtocolType.IMAP => "tcp",
            ServiceProtocolType.MQTT => "tcp",
            ServiceProtocolType.STUN => "udp",
            ServiceProtocolType.SIP => "udp",
            _ => throw new InvalidOperationException(
                $"{nameof(GetSocketEndPoint)} does not support the {ProtocolType} protocol.")
        };
        if (!Uri.TryCreate($"{scheme}://{IpAddressOrUrl}", UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            throw new ArgumentException($"Invalid {ProtocolType} host and port ({IpAddressOrUrl}).",
                nameof(IpAddressOrUrl));
        }

        var port = uri.Port;
        if (port <= IPEndPoint.MinPort)
        {
            port = GetDefaultPort(ProtocolType);
            if (port <= IPEndPoint.MinPort)
            {
                throw new ArgumentException($"Invalid {ProtocolType} host and port ({IpAddressOrUrl}).",
                    nameof(IpAddressOrUrl));
            }
        }

        return _socketEndPoint = new DnsEndPoint(uri.IdnHost, port);
    }

    private static async ValueTask<int> ReceiveUdpResponseAsync(Socket socket, CancellationToken cancellationToken)
    {
        var receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxBufferSize);
        try
        {
            return await socket.ReceiveAsync(
                receiveBuffer.AsMemory(0, MaxBufferSize),
                SocketFlags.None,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }

    private static async ValueTask SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < buffer.Length)
        {
            var count = await socket.SendAsync(buffer[sent..], SocketFlags.None, cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The socket closed before the request was sent.");
            }

            sent += count;
        }
    }

    private static void CreateNtpRequest(Span<byte> packet)
    {
        packet[..NtpPacketLength].Clear();
        packet[0] = 0x23; // LI = 0, version = 4, mode = 3 (client).

        var elapsedTicks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        var seconds = elapsedTicks / TimeSpan.TicksPerSecond + NtpEpochOffsetSeconds;
        var fractionalTicks = elapsedTicks % TimeSpan.TicksPerSecond;
        var fraction = (uint)(fractionalTicks * 0x1_0000_0000L / TimeSpan.TicksPerSecond);

        BinaryPrimitives.WriteUInt32BigEndian(packet[40..44], unchecked((uint)seconds));
        BinaryPrimitives.WriteUInt32BigEndian(packet[44..48], fraction);
    }

    private static ReadOnlySpan<byte> DnsQuestion =>
    [
        7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        3, (byte)'c', (byte)'o', (byte)'m',
        0,
        0, 1, // QTYPE = A
        0, 1 // QCLASS = IN
    ];

    private static void CreateDnsRequest(Span<byte> packet)
    {
        packet[..DnsPacketLength].Clear();

        var transactionId = unchecked((ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        BinaryPrimitives.WriteUInt16BigEndian(packet[..2], transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..4], 0x0100); // Recursion desired.
        BinaryPrimitives.WriteUInt16BigEndian(packet[4..6], 1); // One question.
        DnsQuestion.CopyTo(packet[DnsHeaderLength..DnsPacketLength]);
    }

    private static void CreateMqttConnectRequest(Span<byte> packet)
    {
        packet[..MqttConnectPacketLength].Clear();
        packet[0] = 0x10; // CONNECT packet.
        packet[1] = MqttConnectPacketLength - 2;
        packet[2] = 0;
        packet[3] = 4;
        "MQTT"u8.CopyTo(packet[4..8]);
        packet[8] = 4; // MQTT 3.1.1.
        packet[9] = 0x02; // Clean session.

        const int clientIdLength = 21;
        packet[12] = 0;
        packet[13] = clientIdLength;
        "NetSonar-"u8.CopyTo(packet[14..23]);

        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        const string hexadecimalDigits = "0123456789abcdef";
        for (var i = 0; i < randomBytes.Length; i++)
        {
            packet[23 + i * 2] = (byte)hexadecimalDigits[randomBytes[i] >> 4];
            packet[24 + i * 2] = (byte)hexadecimalDigits[randomBytes[i] & 0x0F];
        }
    }

    private static void CreateStunBindingRequest(Span<byte> packet)
    {
        packet[..StunPacketLength].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(packet[..2], 0x0001); // Binding request.
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..8], StunMagicCookie);
        RandomNumberGenerator.Fill(packet[8..StunPacketLength]);
    }

    private static SipProbeRequest CreateSipOptionsRequest(string authority)
    {
        var branch = $"z9hG4bK{Guid.NewGuid():N}";
        var callId = $"{Guid.NewGuid():N}@netsonar.invalid";
        var tag = Guid.NewGuid().ToString("N");
        var payload = Encoding.ASCII.GetBytes(
            $"OPTIONS sip:{authority} SIP/2.0\r\n"
            + $"Via: SIP/2.0/UDP netsonar.invalid;branch={branch}\r\n"
            + "Max-Forwards: 0\r\n"
            + $"To: <sip:{authority}>\r\n"
            + $"From: <sip:netsonar@netsonar.invalid>;tag={tag}\r\n"
            + $"Call-ID: {callId}\r\n"
            + "CSeq: 1 OPTIONS\r\n"
            + "Content-Length: 0\r\n\r\n");
        return new SipProbeRequest(payload, branch, callId);
    }

    private string GetSipAuthority()
    {
        var host = GetDnsLookupTarget();
        if (IPAddress.TryParse(host, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = $"[{host}]";
        }

        return IpEndPoint.Port == Protocols.DefaultSipPort ? host : $"{host}:{IpEndPoint.Port}";
    }

    private static async ValueTask<int> ReceiveAndValidateNtpResponseAsync(
        Socket socket,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxBufferSize);
        try
        {
            var length = await socket.ReceiveAsync(
                receiveBuffer.AsMemory(0, MaxBufferSize),
                SocketFlags.None,
                cancellationToken);
            ValidateNtpResponse(receiveBuffer.AsSpan(0, length), request.Span);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }

    private static async ValueTask<int> ReceiveAndValidateDnsResponseAsync(
        Socket socket,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxBufferSize);
        var length = await socket.ReceiveAsync(
            response.Memory,
            SocketFlags.None,
            cancellationToken);
        ValidateDnsResponse(response.Span[..length], request.Span);
        return length;
    }

    private static async ValueTask<int> ReceiveAndValidateSmtpGreetingAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxSmtpResponseLength);
        var length = 0;
        while (length < response.Length)
        {
            var received = await socket.ReceiveAsync(
                response.Memory[length..],
                SocketFlags.None,
                cancellationToken);
            if (received == 0)
            {
                throw new EndOfStreamException("The SMTP server closed the connection before completing its greeting.");
            }

            length += received;
            if (TryValidateSmtpGreeting(response.Span[..length]))
            {
                return length;
            }
        }

        throw new InvalidDataException(
            $"The SMTP greeting exceeds the {MaxSmtpResponseLength}-byte validation limit.");
    }

    private static async ValueTask<int> ReceiveAndValidateMqttConnAckAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MqttConnAckPacketLength);
        var length = 0;
        while (length < response.Length)
        {
            var received = await socket.ReceiveAsync(
                response.Memory[length..],
                SocketFlags.None,
                cancellationToken);
            if (received == 0)
            {
                throw new EndOfStreamException("The MQTT broker closed the connection before sending CONNACK.");
            }

            length += received;
        }

        ValidateMqttConnAck(response.Span);
        return length;
    }

    private static async ValueTask<SslProtocols> AuthenticateTlsAsync(
        Socket socket,
        string targetHost,
        CancellationToken cancellationToken)
    {
        using var networkStream = new NetworkStream(socket, false);
        using var tlsStream = new SslStream(networkStream, false);
        await tlsStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = targetHost
            },
            cancellationToken);
        return tlsStream.SslProtocol;
    }

    private static async ValueTask<int> ReceiveAndValidateSshIdentificationAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxSshIdentificationLength);
        var length = 0;
        var parsedLength = 0;
        while (length < response.Length)
        {
            var received = await socket.ReceiveAsync(
                response.Memory[length..],
                SocketFlags.None,
                cancellationToken);
            if (received == 0)
            {
                throw new EndOfStreamException(
                    "The SSH server closed the connection before sending its identification.");
            }

            length += received;
            while (parsedLength < length)
            {
                var remaining = response.Span[parsedLength..length];
                var lineEnd = remaining.IndexOf((byte)'\n');
                if (lineEnd < 0) break;

                var line = remaining[..lineEnd];
                if (!line.IsEmpty && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                parsedLength += lineEnd + 1;
                if (!line.StartsWith("SSH-"u8)) continue;
                if (line.Length > 253 || !line.StartsWith("SSH-2.0-"u8) || line.Length <= "SSH-2.0-"u8.Length)
                {
                    throw new InvalidDataException("The SSH server returned an invalid or unsupported identification.");
                }

                return length;
            }
        }

        throw new InvalidDataException(
            $"The SSH identification was not found within {MaxSshIdentificationLength} bytes.");
    }

    private static async ValueTask<int> ReceiveAndValidateStunResponseAsync(
        Socket socket,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxBufferSize);
        var length = await socket.ReceiveAsync(response.Memory, SocketFlags.None, cancellationToken);
        ValidateStunResponse(response.Span[..length], request.Span);
        return length;
    }

    private static async ValueTask<int> ReceiveAndValidateSipResponseAsync(
        Socket socket,
        SipProbeRequest request,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxSipResponseLength);
        while (true)
        {
            var length = await socket.ReceiveAsync(response.Memory, SocketFlags.None, cancellationToken);
            var statusCode = ValidateSipResponse(response.Span[..length], request);
            if (statusCode < 200) continue;
            if (statusCode >= 300)
            {
                throw new InvalidDataException($"The SIP endpoint returned status code {statusCode}.");
            }

            return length;
        }
    }

    private static async ValueTask<int> ReceiveAndValidateImapAsync(
        Socket socket,
        string targetHost,
        bool useImplicitTls,
        CancellationToken cancellationToken)
    {
        using var networkStream = new NetworkStream(socket, false);
        if (!useImplicitTls)
        {
            return await ReceiveAndValidateImapAsync(networkStream, cancellationToken);
        }

        using var tlsStream = new SslStream(networkStream, false);
        await tlsStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = targetHost
            },
            cancellationToken);
        return await ReceiveAndValidateImapAsync(tlsStream, cancellationToken);
    }

    private static async ValueTask<int> ReceiveAndValidateImapAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryOwner<byte>(ArrayPool<byte>.Shared, MaxImapResponseLength);
        var greetingLength = 0;
        var greetingValidated = false;
        while (greetingLength < response.Length)
        {
            var received = await stream.ReadAsync(response.Memory[greetingLength..], cancellationToken);
            if (received == 0)
            {
                throw new EndOfStreamException("The IMAP server closed the connection before its greeting.");
            }

            greetingLength += received;
            var lineEnd = response.Span[..greetingLength].IndexOf("\r\n"u8);
            if (lineEnd < 0) continue;

            var greeting = response.Span[..lineEnd];
            var isOk = StartsWithAsciiIgnoreCase(greeting, "* OK"u8)
                       && (greeting.Length == 4 || greeting[4] == (byte)' ');
            var isPreAuthenticated = StartsWithAsciiIgnoreCase(greeting, "* PREAUTH"u8)
                                     && (greeting.Length == 9 || greeting[9] == (byte)' ');
            if (!isOk && !isPreAuthenticated)
            {
                throw new InvalidDataException("The IMAP server did not return an OK or PREAUTH greeting.");
            }

            greetingValidated = true;
            break;
        }

        if (!greetingValidated)
        {
            throw new InvalidDataException(
                $"The IMAP greeting exceeds the {MaxImapResponseLength}-byte validation limit.");
        }

        await stream.WriteAsync(ImapCapabilityRequest, cancellationToken);

        var responseLength = 0;
        var parsedLength = 0;
        var capabilitySeen = false;
        while (responseLength < response.Length)
        {
            var received = await stream.ReadAsync(response.Memory[responseLength..], cancellationToken);
            if (received == 0)
            {
                throw new EndOfStreamException(
                    "The IMAP server closed the connection before completing CAPABILITY.");
            }

            responseLength += received;
            while (parsedLength < responseLength)
            {
                var remaining = response.Span[parsedLength..responseLength];
                var lineEnd = remaining.IndexOf("\r\n"u8);
                if (lineEnd < 0) break;

                var line = remaining[..lineEnd];
                parsedLength += lineEnd + 2;
                if (StartsWithAsciiIgnoreCase(line, "* CAPABILITY"u8)
                    && (line.Length == 12 || line[12] == (byte)' '))
                {
                    capabilitySeen = true;
                    continue;
                }

                if (StartsWithAsciiIgnoreCase(line, "* BYE"u8))
                {
                    throw new InvalidDataException("The IMAP server rejected the session.");
                }

                if (!StartsWithAsciiIgnoreCase(line, "A001 "u8)) continue;
                var completion = line[5..];
                if (!StartsWithAsciiIgnoreCase(completion, "OK"u8)
                    || (completion.Length > 2 && completion[2] != (byte)' '))
                {
                    throw new InvalidDataException("The IMAP server rejected the CAPABILITY command.");
                }

                if (!capabilitySeen)
                {
                    throw new InvalidDataException("The IMAP server did not return its capabilities.");
                }

                return greetingLength + responseLength;
            }
        }

        throw new InvalidDataException(
            $"The IMAP CAPABILITY response exceeds the {MaxImapResponseLength}-byte validation limit.");
    }

    private static void ValidateNtpResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request)
    {
        if (response.Length < NtpPacketLength)
        {
            throw new InvalidDataException(
                $"The NTP response is {response.Length} bytes; at least {NtpPacketLength} bytes are required.");
        }

        var leapIndicator = response[0] >> 6;
        var version = (response[0] >> 3) & 0x07;
        var mode = response[0] & 0x07;
        var stratum = response[1];

        if (mode != 4)
        {
            throw new InvalidDataException($"The NTP response has mode {mode}; server mode 4 is required.");
        }

        if (version is < 3 or > 4)
        {
            throw new InvalidDataException($"The NTP response has unsupported version {version}.");
        }

        if (leapIndicator == 3)
        {
            throw new InvalidDataException("The NTP server reports that its clock is not synchronized.");
        }

        if (stratum is 0 or > 15)
        {
            throw new InvalidDataException($"The NTP response has invalid or unsynchronized stratum {stratum}.");
        }

        if (!response.Slice(24, 8).SequenceEqual(request.Slice(40, 8)))
        {
            throw new InvalidDataException("The NTP response does not match the request transmit timestamp.");
        }

        if (IsZeroTimestamp(response.Slice(32, 8)) || IsZeroTimestamp(response.Slice(40, 8)))
        {
            throw new InvalidDataException("The NTP response has an empty receive or transmit timestamp.");
        }
    }

    private static void ValidateDnsResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request)
    {
        if (response.Length < DnsPacketLength)
        {
            throw new InvalidDataException(
                $"The DNS response is {response.Length} bytes; at least {DnsPacketLength} bytes are required.");
        }

        if (!response[..2].SequenceEqual(request[..2]))
        {
            throw new InvalidDataException("The DNS response transaction ID does not match the request.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..4]);
        if ((flags & 0x8000) == 0)
        {
            throw new InvalidDataException("The DNS packet is not a response.");
        }

        if ((flags & 0x7800) != 0)
        {
            throw new InvalidDataException("The DNS response has an unsupported operation code.");
        }

        if ((flags & 0x0200) != 0)
        {
            throw new InvalidDataException("The DNS response was truncated.");
        }

        var responseCode = flags & 0x000F;
        if (responseCode != 0)
        {
            throw new InvalidDataException($"The DNS server returned response code {responseCode}.");
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
        if (questionCount != 1)
        {
            throw new InvalidDataException($"The DNS response contains {questionCount} questions; one is required.");
        }

        if (!response.Slice(DnsHeaderLength, DnsQuestionLength)
                .SequenceEqual(request.Slice(DnsHeaderLength, DnsQuestionLength)))
        {
            throw new InvalidDataException("The DNS response question does not match the request.");
        }

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..8]);
        if (answerCount == 0)
        {
            throw new InvalidDataException("The DNS response does not contain an answer.");
        }
    }

    private static bool TryValidateSmtpGreeting(ReadOnlySpan<byte> response)
    {
        var offset = 0;
        while (offset < response.Length)
        {
            var remaining = response[offset..];
            var lineEnd = remaining.IndexOf("\r\n"u8);
            if (lineEnd < 0) return false;

            var line = remaining[..lineEnd];
            if (line.Length < 3
                || line[0] != (byte)'2'
                || line[1] != (byte)'2'
                || line[2] != (byte)'0')
            {
                throw new InvalidDataException("The SMTP server did not return a 220 greeting.");
            }

            if (line.Length == 3) return true;
            if (line[3] == (byte)' ') return true;
            if (line[3] != (byte)'-')
            {
                throw new InvalidDataException("The SMTP greeting has an invalid reply separator.");
            }

            offset += lineEnd + 2;
        }

        return false;
    }

    private static void ValidateMqttConnAck(ReadOnlySpan<byte> response)
    {
        if (response.Length != MqttConnAckPacketLength
            || response[0] != 0x20
            || response[1] != 0x02)
        {
            throw new InvalidDataException("The MQTT broker returned an invalid CONNACK packet.");
        }

        if (response[2] != 0)
        {
            throw new InvalidDataException("The MQTT CONNACK flags are invalid for a clean session.");
        }

        if (response[3] != 0)
        {
            throw new InvalidDataException(
                $"The MQTT broker rejected the connection with return code {response[3]}.");
        }
    }

    private static void ValidateStunResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request)
    {
        if (response.Length < StunPacketLength)
        {
            throw new InvalidDataException(
                $"The STUN response is {response.Length} bytes; at least {StunPacketLength} bytes are required.");
        }

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        if (messageType == 0x0111)
        {
            throw new InvalidDataException("The STUN server returned a Binding error response.");
        }

        if (messageType != 0x0101)
        {
            throw new InvalidDataException($"The STUN response has unexpected message type 0x{messageType:X4}.");
        }

        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(response[2..4]);
        if ((messageLength & 3) != 0 || StunPacketLength + messageLength != response.Length)
        {
            throw new InvalidDataException("The STUN response has an invalid message length.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(response[4..8]) != StunMagicCookie)
        {
            throw new InvalidDataException("The STUN response has an invalid magic cookie.");
        }

        if (!response[8..StunPacketLength].SequenceEqual(request[8..StunPacketLength]))
        {
            throw new InvalidDataException("The STUN response transaction ID does not match the request.");
        }

        var hasMappedAddress = false;
        var offset = StunPacketLength;
        var messageEnd = StunPacketLength + messageLength;
        while (offset < messageEnd)
        {
            if (offset + 4 > messageEnd)
            {
                throw new InvalidDataException("The STUN response contains a truncated attribute header.");
            }

            var attributeType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..(offset + 2)]);
            var attributeLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..(offset + 4)]);
            var valueStart = offset + 4;
            var valueEnd = valueStart + attributeLength;
            if (valueEnd > messageEnd)
            {
                throw new InvalidDataException("The STUN response contains a truncated attribute.");
            }

            if (attributeType is 0x0001 or 0x0020)
            {
                var value = response[valueStart..valueEnd];
                if (value.Length < 4
                    || value[0] != 0
                    || value[1] is not (0x01 or 0x02)
                    || value.Length != (value[1] == 0x01 ? 8 : 20))
                {
                    throw new InvalidDataException("The STUN response contains an invalid mapped address.");
                }

                hasMappedAddress = true;
            }

            offset = valueStart + ((attributeLength + 3) & ~3);
            if (offset > messageEnd)
            {
                throw new InvalidDataException("The STUN response contains invalid attribute padding.");
            }
        }

        if (!hasMappedAddress)
        {
            throw new InvalidDataException("The STUN response does not contain a mapped address.");
        }
    }

    private static int ValidateSipResponse(ReadOnlySpan<byte> response, SipProbeRequest request)
    {
        var message = Encoding.ASCII.GetString(response);
        if (!message.StartsWith("SIP/2.0 ", StringComparison.OrdinalIgnoreCase)
            || message.Length < 12
            || !int.TryParse(message.AsSpan(8, 3), CultureInfo.InvariantCulture, out var statusCode)
            || statusCode is < 100 or > 699
            || !message.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The SIP endpoint returned an invalid response.");
        }

        if (!message.Contains($"branch={request.Branch}", StringComparison.OrdinalIgnoreCase)
            || !ContainsSipHeaderValue(message, "Call-ID", "i", request.CallId)
            || !ContainsSipOptionsCSeq(message))
        {
            throw new InvalidDataException("The SIP response does not match the OPTIONS request.");
        }

        return statusCode;
    }

    private static bool ContainsSipHeaderValue(
        string message,
        string headerName,
        string? compactHeaderName,
        string expectedValue)
    {
        var remaining = message.AsSpan();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOf("\r\n", StringComparison.Ordinal);
            var line = lineEnd >= 0 ? remaining[..lineEnd] : remaining;
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                var name = line[..separator].Trim();
                if (name.Equals(headerName, StringComparison.OrdinalIgnoreCase)
                    || (compactHeaderName is not null
                        && name.Equals(compactHeaderName, StringComparison.OrdinalIgnoreCase)))
                {
                    return line[(separator + 1)..].Trim()
                        .Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (lineEnd < 0) break;
            remaining = remaining[(lineEnd + 2)..];
        }

        return false;
    }

    private static bool ContainsSipOptionsCSeq(string message)
    {
        var remaining = message.AsSpan();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOf("\r\n", StringComparison.Ordinal);
            var line = lineEnd >= 0 ? remaining[..lineEnd] : remaining;
            var separator = line.IndexOf(':');
            if (separator > 0
                && line[..separator].Trim().Equals("CSeq", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(separator + 1)..].Trim();
                var methodStart = value.IndexOfAny((char)' ', (char)'\t');
                if (methodStart <= 0
                    || !int.TryParse(value[..methodStart], CultureInfo.InvariantCulture, out var sequence)
                    || sequence != 1)
                {
                    return false;
                }

                return value[methodStart..].Trim()
                    .Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);
            }

            if (lineEnd < 0) break;
            remaining = remaining[(lineEnd + 2)..];
        }

        return false;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            var left = value[i];
            var right = prefix[i];
            if (left is >= (byte)'A' and <= (byte)'Z') left += (byte)('a' - 'A');
            if (right is >= (byte)'A' and <= (byte)'Z') right += (byte)('a' - 'A');
            if (left != right) return false;
        }

        return true;
    }

    private static bool IsZeroTimestamp(ReadOnlySpan<byte> timestamp)
    {
        foreach (var value in timestamp)
        {
            if (value != 0) return false;
        }

        return true;
    }

    private static CancellationTokenSource CreateTimeoutTokenSource(int timeout, CancellationToken cancellationToken)
    {
        var timeoutCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        if (timeout > 0) timeoutCts.CancelAfter(timeout);
        return timeoutCts;
    }

    private static double GetElapsedMilliseconds(long startingTimestamp)
    {
        return Math.Round(
            Stopwatch.GetElapsedTime(startingTimestamp).TotalMilliseconds,
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    protected override PingableServiceReply PingCore(int timeout = 0)
    {
        if (ProtocolType == ServiceProtocolType.ICMP)
        {
            EnsureBuffer();
            var effectiveTimeout = GetEffectiveIcmpTimeout(timeout);

            using var ping = new Ping();
            var sentDateTime = DateTime.Now;
            try
            {
                PingReply reply;
                if (OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess)
                {
                    reply = ping.Send(IpAddressOrUrl, effectiveTimeout);
                }
                else
                {
                    reply = ping.Send(IpAddressOrUrl, effectiveTimeout, _sendBuffer,
                        UseDefaultPingOptions ? null : PingOptions);
                }

                return new PingableServiceReply(reply);
            }
            catch (Exception e)
            {
                return PingableServiceReply.CreateErrorReply(e.InnerException?.Message ?? e.Message, sentDateTime);
            }
        }
        else if (ProtocolType is ServiceProtocolType.TCP
                 or ServiceProtocolType.UDP
                 or ServiceProtocolType.TLS
                 or ServiceProtocolType.DNS
                 or ServiceProtocolType.NTP
                 or ServiceProtocolType.HTTP
                 or ServiceProtocolType.WebSocket
                 or ServiceProtocolType.SSH
                 or ServiceProtocolType.SMTP
                 or ServiceProtocolType.IMAP
                 or ServiceProtocolType.MQTT
                 or ServiceProtocolType.STUN
                 or ServiceProtocolType.SIP)
        {
            return PingCoreAsync(timeout).GetAwaiter().GetResult();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(ProtocolType), ProtocolType, null);
        }
    }


    /// <inheritdoc />
    protected override async Task<PingableServiceReply> PingCoreAsync(int timeout = 0,
        CancellationToken cancellationToken = default)
    {
        if (ProtocolType == ServiceProtocolType.ICMP)
        {
            EnsureBuffer();
            var effectiveTimeout = GetEffectiveIcmpTimeout(timeout);

            using var ping = new Ping();
            var sentOn = DateTime.Now;
            try
            {
                PingReply reply;
                if (OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess)
                {
                    reply = await ping.SendPingAsync(IpAddressOrUrl, TimeSpan.FromMilliseconds(effectiveTimeout),
                        cancellationToken: cancellationToken);
                }
                else
                {
                    reply = await ping.SendPingAsync(IpAddressOrUrl, TimeSpan.FromMilliseconds(effectiveTimeout),
                        _sendBuffer, UseDefaultPingOptions ? null : PingOptions, cancellationToken);
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
        else if (ProtocolType is ServiceProtocolType.TCP
                 or ServiceProtocolType.UDP
                 or ServiceProtocolType.TLS
                 or ServiceProtocolType.DNS
                 or ServiceProtocolType.NTP
                 or ServiceProtocolType.SSH
                 or ServiceProtocolType.SMTP
                 or ServiceProtocolType.IMAP
                 or ServiceProtocolType.MQTT
                 or ServiceProtocolType.STUN
                 or ServiceProtocolType.SIP)
        {
            using var protocolRequest = ProtocolType switch
            {
                ServiceProtocolType.DNS => new MemoryOwner<byte>(ArrayPool<byte>.Shared, DnsPacketLength),
                ServiceProtocolType.NTP => new MemoryOwner<byte>(ArrayPool<byte>.Shared, NtpPacketLength),
                ServiceProtocolType.MQTT => new MemoryOwner<byte>(ArrayPool<byte>.Shared, MqttConnectPacketLength),
                ServiceProtocolType.STUN => new MemoryOwner<byte>(ArrayPool<byte>.Shared, StunPacketLength),
                _ => default
            };

            SipProbeRequest? sipRequest = null;
            ReadOnlyMemory<byte> sendBuffer;
            if (ProtocolType is ServiceProtocolType.TCP or ServiceProtocolType.UDP)
            {
                EnsureBuffer();
                sendBuffer = _sendBuffer;
            }
            else if (ProtocolType == ServiceProtocolType.TLS)
            {
                sendBuffer = ReadOnlyMemory<byte>.Empty;
            }
            else if (ProtocolType == ServiceProtocolType.DNS)
            {
                CreateDnsRequest(protocolRequest.Span);
                sendBuffer = protocolRequest.Memory;
            }
            else if (ProtocolType == ServiceProtocolType.NTP)
            {
                CreateNtpRequest(protocolRequest.Span);
                sendBuffer = protocolRequest.Memory;
            }
            else if (ProtocolType is ServiceProtocolType.SSH
                     or ServiceProtocolType.SMTP
                     or ServiceProtocolType.IMAP)
            {
                sendBuffer = ReadOnlyMemory<byte>.Empty;
            }
            else if (ProtocolType == ServiceProtocolType.MQTT)
            {
                CreateMqttConnectRequest(protocolRequest.Span);
                sendBuffer = protocolRequest.Memory;
            }
            else if (ProtocolType == ServiceProtocolType.STUN)
            {
                CreateStunBindingRequest(protocolRequest.Span);
                sendBuffer = protocolRequest.Memory;
            }
            else if (ProtocolType == ServiceProtocolType.SIP)
            {
                sipRequest = CreateSipOptionsRequest(GetSipAuthority());
                sendBuffer = sipRequest.Payload;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(ProtocolType), ProtocolType, null);
            }

            var startingTimestamp = Stopwatch.GetTimestamp();
            var sentDateTime = DateTime.Now;

            try
            {
                var remoteEndPoint = GetSocketEndPoint();
                var usesTcp = ProtocolType is ServiceProtocolType.TCP
                    or ServiceProtocolType.TLS
                    or ServiceProtocolType.SSH
                    or ServiceProtocolType.SMTP
                    or ServiceProtocolType.IMAP
                    or ServiceProtocolType.MQTT;
                var socketType = usesTcp ? SocketType.Stream : SocketType.Dgram;
                var socketProtocol = usesTcp
                    ? System.Net.Sockets.ProtocolType.Tcp
                    : System.Net.Sockets.ProtocolType.Udp;
                using var socket = remoteEndPoint is IPEndPoint endpoint
                    ? new Socket(endpoint.AddressFamily, socketType, socketProtocol)
                    : new Socket(socketType, socketProtocol);
                socket.Ttl = Ttl;

                using var timeoutCts = CreateTimeoutTokenSource(timeout, cancellationToken);
                await socket.ConnectAsync(remoteEndPoint, timeoutCts.Token);

                object replyStatus = IPStatus.Success;
                int bufferLength;
                if (ProtocolType == ServiceProtocolType.TCP)
                {
                    if (sendBuffer.Length > 0)
                    {
                        await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    }

                    bufferLength = sendBuffer.Length;
                }
                else if (ProtocolType == ServiceProtocolType.UDP)
                {
                    await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    // UDP has no connection handshake; only a response verifies the remote service.
                    bufferLength = await ReceiveUdpResponseAsync(socket, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.TLS)
                {
                    replyStatus = await AuthenticateTlsAsync(
                        socket,
                        GetDnsLookupTarget(),
                        timeoutCts.Token);
                    bufferLength = 0;
                }
                else if (ProtocolType == ServiceProtocolType.DNS)
                {
                    await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateDnsResponseAsync(socket, sendBuffer, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.NTP)
                {
                    await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateNtpResponseAsync(socket, sendBuffer, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.SSH)
                {
                    await SendAllAsync(socket, SshIdentification, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateSshIdentificationAsync(socket, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.SMTP)
                {
                    bufferLength = await ReceiveAndValidateSmtpGreetingAsync(socket, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.IMAP)
                {
                    var remotePort = (socket.RemoteEndPoint as IPEndPoint)?.Port ?? IpEndPoint.Port;
                    bufferLength = await ReceiveAndValidateImapAsync(
                        socket,
                        GetDnsLookupTarget(),
                        remotePort == Protocols.DefaultImapTlsPort,
                        timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.MQTT)
                {
                    await SendAllAsync(socket, sendBuffer, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateMqttConnAckAsync(socket, timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.STUN)
                {
                    await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateStunResponseAsync(
                        socket,
                        sendBuffer,
                        timeoutCts.Token);
                }
                else if (ProtocolType == ServiceProtocolType.SIP)
                {
                    await socket.SendAsync(sendBuffer, timeoutCts.Token);
                    bufferLength = await ReceiveAndValidateSipResponseAsync(
                        socket,
                        sipRequest!,
                        timeoutCts.Token);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(ProtocolType), ProtocolType, null);
                }

                var connectedEndPoint = socket.RemoteEndPoint as IPEndPoint ?? IpEndPoint;
                return new PingableServiceReply(true, replyStatus, connectedEndPoint, sentDateTime,
                    GetElapsedMilliseconds(startingTimestamp), bufferLength, Ttl);
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
        }
        else if (ProtocolType == ServiceProtocolType.HTTP)
        {
            var sentDateTime = DateTime.Now;

            try
            {
                using var timeoutCts = CreateTimeoutTokenSource(timeout, cancellationToken);

                var startingTimestamp = Stopwatch.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Get, IpAddressOrUrl);
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
                var contentLength = Math.Min(response.Content.Headers.ContentLength.GetValueOrDefault(), int.MaxValue);
                var replyEndPoint = new IPEndPoint(IpEndPoint.Address, IpEndPoint.Port);
                return new PingableServiceReply(response.IsSuccessStatusCode, response.StatusCode, replyEndPoint,
                    sentDateTime, GetElapsedMilliseconds(startingTimestamp), (int)contentLength);
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
        else if (ProtocolType == ServiceProtocolType.WebSocket)
        {
            var sentDateTime = DateTime.Now;

            try
            {
                using var timeoutCts = CreateTimeoutTokenSource(timeout, cancellationToken);
                using var webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;

                var startingTimestamp = Stopwatch.GetTimestamp();
                await webSocket.ConnectAsync(new Uri(IpAddressOrUrl, UriKind.Absolute), timeoutCts.Token);
                if (webSocket.State != WebSocketState.Open)
                {
                    throw new WebSocketException(
                        WebSocketError.InvalidState,
                        $"The WebSocket connection entered the {webSocket.State} state.");
                }

                var replyEndPoint = new IPEndPoint(IpEndPoint.Address, IpEndPoint.Port);
                return new PingableServiceReply(
                    true,
                    WebSocketState.Open,
                    replyEndPoint,
                    sentDateTime,
                    GetElapsedMilliseconds(startingTimestamp));
            }
            catch (OperationCanceledException e)
            {
                return PingableServiceReply.CreateTimeOutReply(e, sentDateTime);
            }
            catch (WebSocketException e)
            {
                return PingableServiceReply.CreateErrorReply(e.WebSocketErrorCode, e, sentDateTime);
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
        catch (Exception)
        {
            service = null;
            return false;
        }
    }

    public static PingableService ParseFromString(string line)
    {
        line = line.Trim();
        var pingSettings = line.Split('|', StringSplitOptions.TrimEntries);
        var serviceFields = pingSettings[0].Split(',', StringSplitOptions.TrimEntries);
        if (serviceFields.Length == 0 || string.IsNullOrWhiteSpace(serviceFields[0]))
        {
            throw new MalformedLineException("The service address must not be empty.");
        }

        var ipAddressOrUrl = serviceFields[0];
        var description = serviceFields.Length >= 2 ? serviceFields[1] : string.Empty;
        var group = serviceFields.Length >= 3 ? serviceFields[2] : string.Empty;

        ServiceProtocolType protocol;
        if (ipAddressOrUrl.Contains('/'))
        {
            if (ipAddressOrUrl.StartsWith("icmp://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "icmp://".Length);
                protocol = ServiceProtocolType.ICMP;
                if (ipAddressOrUrl.Contains(':'))
                    throw new MalformedLineException($"The {protocol} protocol must not contain a port number.");
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("tcp://"))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "tcp://".Length);
                protocol = ServiceProtocolType.TCP;
                if (!ipAddressOrUrl.Contains(':'))
                    throw new MalformedLineException($"The {protocol} protocol must contain a port number.");
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "udp://".Length);
                protocol = ServiceProtocolType.UDP;
                if (!ipAddressOrUrl.Contains(':'))
                    throw new MalformedLineException($"The {protocol} protocol must contain a port number.");
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "tls://".Length);
                protocol = ServiceProtocolType.TLS;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("dns://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "dns://".Length);
                protocol = ServiceProtocolType.DNS;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("ntp://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "ntp://".Length);
                protocol = ServiceProtocolType.NTP;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || ipAddressOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = ServiceProtocolType.HTTP;
            }
            else if (ipAddressOrUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                     || ipAddressOrUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = ServiceProtocolType.WebSocket;
            }
            else if (ipAddressOrUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "ssh://".Length);
                protocol = ServiceProtocolType.SSH;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("smtp://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "smtp://".Length);
                protocol = ServiceProtocolType.SMTP;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("imap://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "imap://".Length);
                protocol = ServiceProtocolType.IMAP;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "mqtt://".Length);
                protocol = ServiceProtocolType.MQTT;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("stun://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "stun://".Length);
                protocol = ServiceProtocolType.STUN;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else if (ipAddressOrUrl.StartsWith("sip://", StringComparison.OrdinalIgnoreCase))
            {
                ipAddressOrUrl = ipAddressOrUrl.Remove(0, "sip://".Length);
                protocol = ServiceProtocolType.SIP;
                if (ipAddressOrUrl.Contains('/'))
                    throw new MalformedLineException(
                        $"The address must not contain path separator '/' for the {protocol} protocol.");
            }
            else
            {
                protocol = ServiceProtocolType.HTTP;
            }
        }
        else
        {
            protocol = ServiceProtocolType.ICMP;
            if (ipAddressOrUrl.Contains(':'))
                throw new MalformedLineException($"The {protocol} protocol must not contain a port number.");
        }

        var pingEvery = pingSettings.Length >= 2
            ? ParseExtensions.ParseLocalizedDoubleOrDefault(pingSettings[1],
                App.AppSettings.PingServices.DefaultPingEverySeconds)
            : App.AppSettings.PingServices.DefaultPingEverySeconds;

        var timeout = pingSettings.Length >= 3
            ? ParseExtensions.ParseLocalizedDoubleOrDefault(pingSettings[2],
                App.AppSettings.PingServices.DefaultTimeoutSeconds)
            : App.AppSettings.PingServices.DefaultTimeoutSeconds;

        var defaultBufferSize = protocol == ServiceProtocolType.TCP
            ? GetInitialBufferSize(protocol)
            : App.AppSettings.PingServices.DefaultBufferSize;
        var bufferSize = pingSettings.Length >= 4
            ? int.TryParse(pingSettings[3], out var bufferResult)
                ? bufferResult
                : defaultBufferSize
            : defaultBufferSize;

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