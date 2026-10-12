using CommunityToolkit.Mvvm.ComponentModel;
using NetSonar.Avalonia.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Threading;
using System.Net;
using System.Text.Json.Serialization;
using DotNext.Threading;
using ObservableCollections;
using ZLinq;

namespace NetSonar.Avalonia.Network;

public abstract partial class BasePingableCollectionObject<T> : ObservableObject, IEquatable<BasePingableCollectionObject<T>> where T : BasePingReply
{
    private const int MaxDnsResolutionAttempts = 3;

    private int _dnsResolutionAttempts;
    private int _isDnsResolutionInProgress;

    // Thread-safe mirrors read by the timer thread; the observable properties are only assigned on the UI thread.
    private int _isDnsResolvedFlag;
    private int _isBusyFlag;
    private long _lastExecutedTicks;

    #region Events
    public class PingCompletedEventArgs : EventArgs
    {
        public required T Reply { get; init; }

        [SetsRequiredMembers]
        // ReSharper disable once ConvertToPrimaryConstructor
        public PingCompletedEventArgs(T reply)
        {
            Reply = reply;
        }
    }

    public event EventHandler? PingStarted;
    public event EventHandler<PingCompletedEventArgs>? PingCompleted;

    protected virtual void OnPingStarted()
    {
        PingStarted?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void OnPingCompleted(PingCompletedEventArgs e)
    {
        PingCompleted?.Invoke(this, e);
    }
    #endregion

    #region Properties
    /// <summary>
    /// Gets the unique identifier of this service.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Gets the replies list for this service.
    /// </summary>
    [JsonIgnore]
    public ObservableList<T> Pings { get; } = [];

    /// <summary>
    /// Gets the date and time this service was created.
    /// </summary>
    public DateTime SinceDateTime { get; init; }

    /// <summary>
    /// Gets the protocol type of this service.
    /// </summary>
    public ServiceProtocolType ProtocolType { get; init; }

    /// <summary>
    /// Gets or sets if is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the display order.
    /// </summary>
    [ObservableProperty]
    public partial int Order { get; set; }

    /// <summary>
    /// Gets or sets the description of this service.
    /// </summary>
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display group.
    /// </summary>
    [ObservableProperty]
    public partial string Group { get; set; } = string.Empty;

    /// <summary>
    /// Gets the initial ip address or the url where to send the ping.
    /// </summary>
    public required string IpAddressOrUrl { get; init; }

    /// <summary>
    /// Gets the IP address of the host.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IpEndPointStr))]
    [NotifyPropertyChangedFor(nameof(IpEndPointAndSecondaryIpAddressStr))]
    public partial IPEndPoint IpEndPoint { get; protected set; } = new IPEndPoint(IPAddress.Any, 0);

    /// <summary>
    /// Gets the IP address of the host as a string.
    /// </summary>
    [JsonIgnore]
    public string IpEndPointStr => IpEndPoint.Port <= 0 ? IpEndPoint.Address.ToString() : IpEndPoint.ToString();

    /// <summary>
    /// Gets or sets the IP address of the service.
    /// </summary>
    public IPAddress IpAddress
    {
        get => IpEndPoint.Address;
        protected set
        {
            if (Equals(IpEndPoint.Address, value)) return;
            IpEndPoint.Address = value;
            OnPropertyChanged(nameof(IpEndPoint));
            OnPropertyChanged(nameof(IpEndPointStr));
            OnPropertyChanged(nameof(IpEndPointAndSecondaryIpAddressStr));
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string IpEndPointAndSecondaryIpAddressStr
    {
        get
        {
            if (IpAddresses.Length == 0) return IpEndPointStr;
            var other = IpAddresses
                .AsValueEnumerable()
                .OrderBy(address => address.ToString().Length)
                .FirstOrDefault(address => !Equals(address, IpEndPoint.Address));
            if (other is null) return IpEndPointStr;
            return IpEndPoint.Port > 0
                ? $"{IpEndPointStr}\n{other}:{IpEndPoint.Port}"
                : $"{IpEndPointStr}\n{other}";
        }
    }

    /// <summary>
    /// Gets the host name of the host passed by user or parsed by the DNS.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostNameAndQuery))]
    [JsonInclude]
    public partial string HostName { get; protected set; } = string.Empty;

    [JsonIgnore]
    public string HostNameAndQuery
    {
        get
        {
            if (IpAddressOrUrl == HostName) return HostName;
            if (IpAddressOrUrl == IpEndPointStr) return HostName;
            return string.IsNullOrWhiteSpace(HostName) ? IpAddressOrUrl : $"{HostName}\n> {IpAddressOrUrl}";
        }
    }

    /// <summary>
    /// Gets the IP addresses of the host resolved by the DNS.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IpAddressesStr))]
    [NotifyPropertyChangedFor(nameof(IpEndPointAndSecondaryIpAddressStr))]
    public partial IPAddress[] IpAddresses { get; protected set; } = [];

    /// <summary>
    /// Gets the IP addresses of the host resolved by the DNS separated by a line break.
    /// </summary>
    [JsonIgnore]
    public string IpAddressesStr => string.Join<IPAddress>("\n", IpAddresses);

    /// <summary>
    /// Gets the aliases of the host resolved by the DNS.
    /// </summary>
    [ObservableProperty]
    public partial string[] Aliases { get; protected set; } = [];

    /// <summary>
    /// Gets if the dns has been resolved for this host.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDnsResolved { get; private set; }

    /// <summary>
    /// Gets or sets the timeout for the ping method.
    /// </summary>
    public double TimeoutSeconds
    {
        get;
        set => SetProperty(ref field, Math.Round(
            Math.Clamp(value, PingableService.MinTimeoutSeconds, PingableService.MaxTimeoutSeconds),
            2,
            MidpointRounding.AwayFromZero)
        );
    } = PingableService.DefaultTimeoutSeconds;

    /// <summary>
    /// Gets the ping count made to this service.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial uint SentCount { get; private set; }

    /// <summary>
    /// Gets the count of successful pings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SucceedPercentage))]
    [NotifyPropertyChangedFor(nameof(SucceedCountRepresentation))]
    [NotifyPropertyChangedFor(nameof(AverageTime))]
    [JsonIgnore]
    public partial uint SucceedCount { get; private set; }

    /// <summary>
    /// Gets the percentage of the successful pings.
    /// </summary>
    [JsonIgnore]
    public double SucceedPercentage => SentCount > 0 ? Math.Round((double)SucceedCount * 100 / SentCount, 2, MidpointRounding.AwayFromZero) : 0;

    [JsonIgnore]
    public string SucceedCountRepresentation => $"{SucceedCount} ({SucceedPercentage:F2}%)";

    /// <summary>
    /// Gets the count of failed pings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailedPercentage))]
    [NotifyPropertyChangedFor(nameof(FailedCountRepresentation))]
    [JsonIgnore]
    public partial uint FailedCount { get; private set; }

    /// <summary>
    /// Gets the percentage of the failed pings.
    /// </summary>
    [JsonIgnore]
    public double FailedPercentage => SentCount > 0 ? Math.Round((double)FailedCount * 100 / SentCount, 2, MidpointRounding.AwayFromZero) : 0;

    [JsonIgnore]
    public string FailedCountRepresentation => $"{FailedCount} ({FailedPercentage:F2}%)";
    /// <summary>
    /// Gets the count of consecutive successful pings.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial uint ConsecutiveSucceedCount { get; private set; }

    /// <summary>
    /// Gets the count of consecutive failed pings.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial uint ConsecutiveFailedCount { get; private set; }
    /// <summary>
    /// Gets the maximum count of consecutive successful pings.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial uint MaxConsecutiveSucceedCount { get; private set; }

    /// <summary>
    /// Gets the maximum count of consecutive failed pings.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial uint MaxConsecutiveFailedCount { get; private set; }

    /// <summary>
    /// Gets the last ping made to this service.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastStatus))]
    [NotifyPropertyChangedFor(nameof(WasLastPingSucceeded))]
    [NotifyPropertyChangedFor(nameof(WasLastPingFailed))]
    [NotifyPropertyChangedFor(nameof(LastTime))]
    [NotifyPropertyChangedFor(nameof(LastPingBaseReply))]
    [NotifyPropertyChangedFor(nameof(StatusChanged))]
    [JsonIgnore]
    public partial T? LastPing { get; private set; }

    [JsonIgnore]
    private BasePingReply? LastPingBaseReply => LastPing;

    /// <summary>
    /// Gets the last failed ping date time.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial DateTime? LastExecutedDateTime { get; private set; }

    /// <summary>
    /// Gets the last successful ping date time.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial DateTime? LastSucceedDateTime { get; private set; }

    /// <summary>
    /// Gets the last failed ping date time.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial DateTime? LastFailedDateTime { get; private set; }

    /// <summary>
    /// Gets if the <see cref="LastStatus"/> changed in relation to it previous ping.
    /// </summary>
    [JsonIgnore]
    public bool StatusChanged =>
        Count switch
        {
            0 => false,
            1 => !LastStatus.Equals(IPStatus.Unknown),
            _ => !this[1].Status.Equals(LastStatus)
        };

    /// <summary>
    /// Gets the last ping status.
    /// </summary>
    [JsonIgnore]
    public object LastStatus => LastPingBaseReply?.Status ?? IPStatus.Unknown;

    /// <summary>
    /// Gets the last ping status.
    /// </summary>
    [JsonIgnore]
    public string LastStatusStr => LastStatus?.ToString() ?? string.Empty;

    /// <summary>
    /// Gets if the last ping was successful.
    /// </summary>
    [JsonIgnore]
    public bool WasLastPingSucceeded => LastPing?.IsSucceeded ?? false;

    /// <summary>
    /// Gets if the last ping was failed.
    /// </summary>
    [JsonIgnore]
    public bool WasLastPingFailed => LastPing?.IsFailed ?? false;

    /// <summary>
    /// Gets or sets to ping every time in seconds.
    /// </summary>
    public double PingEverySeconds
    {
        get;
        set => SetProperty(ref field, Math.Round(
            Math.Clamp(value, PingableService.MinPingEverySeconds, PingableService.MaxPingEverySeconds),
            2,
            MidpointRounding.AwayFromZero
            )
        );
    } = PingableService.DefaultPingEverySeconds;

    /// <summary>
    /// Gets the last ping time.
    /// </summary>
    [JsonIgnore]
    public double LastTime => LastPing?.Time ?? double.PositiveInfinity;

    /// <summary>
    /// Gets the total time sum of the pings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AverageTime))]
    [JsonIgnore]
    public partial ulong TotalTimeSum { get; private set; }

    /// <summary>
    /// Gets or sets the minimum ping time.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial double MinimumTime { get; private set; } = double.PositiveInfinity;

    /// <summary>
    /// Gets or sets the maximum ping time.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial double MaximumTime { get; private set; } = double.PositiveInfinity;

    /// <summary>
    /// Gets or sets the average ping time.
    /// </summary>
    [JsonIgnore]
    public double AverageTime => SucceedCount > 0
        ? Math.Round(TotalTimeSum / (double)SucceedCount, 2, MidpointRounding.AwayFromZero)
        : double.PositiveInfinity;

    /// <summary>
    /// Checks if the ping can be executed by the timer.
    /// </summary>
    [JsonIgnore]
    public bool CanTimerExecute
    {
        get
        {
            if (!IsEnabled || PingEverySeconds <= 0 || Volatile.Read(ref _isBusyFlag) != 0) return false;
            var lastExecutedTicks = Volatile.Read(ref _lastExecutedTicks);
            if (lastExecutedTicks == 0) return true;
            return TimeSpan.FromTicks(DateTime.Now.Ticks - lastExecutedTicks).TotalSeconds >= PingEverySeconds;
        }
    }

    /// <summary>
    /// Gets if the service is busy doing some work, eg: Pinging.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsBusy { get; private set; }

    #endregion

    #region Constructor

    [SetsRequiredMembers]
    protected BasePingableCollectionObject(ServiceProtocolType protocolType, string ipAddressOrUrl)
    {
        ipAddressOrUrl = ipAddressOrUrl.Trim('/');
        if (protocolType == ServiceProtocolType.HTTP)
        {
            if (!ipAddressOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                ipAddressOrUrl = $"http://{ipAddressOrUrl}";
        }
        else if (protocolType == ServiceProtocolType.WebSocket
                 && !ipAddressOrUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                 && !ipAddressOrUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            ipAddressOrUrl = $"ws://{ipAddressOrUrl}";
        }

        SinceDateTime = DateTime.Now;
        ProtocolType = protocolType;
        IpAddressOrUrl = ipAddressOrUrl;



        if (IPEndPoint.TryParse(ipAddressOrUrl, out var endpoint))
        {
            if (endpoint.Port <= IPEndPoint.MinPort)
            {
                var defaultPort = PingableService.GetDefaultPort(protocolType);
                if (defaultPort > IPEndPoint.MinPort)
                {
                    endpoint.Port = defaultPort;
                }
            }

            IpEndPoint = endpoint;
        }
        else if (protocolType is ServiceProtocolType.TCP
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
            var scheme = protocolType is ServiceProtocolType.TCP
                or ServiceProtocolType.TLS
                or ServiceProtocolType.SSH
                or ServiceProtocolType.SMTP
                or ServiceProtocolType.IMAP
                or ServiceProtocolType.MQTT
                ? "tcp"
                : "udp";
            if (!Uri.TryCreate($"{scheme}://{ipAddressOrUrl}", UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.IdnHost))
            {
                throw new ArgumentException($"Invalid {protocolType} host and port ({ipAddressOrUrl}).", nameof(ipAddressOrUrl));
            }

            var port = uri.Port;
            if (port <= IPEndPoint.MinPort)
            {
                port = PingableService.GetDefaultPort(protocolType);
                if (port <= IPEndPoint.MinPort)
                {
                    throw new ArgumentException($"Invalid {protocolType} host and port ({ipAddressOrUrl}).", nameof(ipAddressOrUrl));
                }
            }

            HostName = uri.IdnHost;
            IpEndPoint.Port = port;
        }
        else if (protocolType == ServiceProtocolType.WebSocket)
        {
            if (!Uri.TryCreate(ipAddressOrUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("ws" or "wss")
                || string.IsNullOrWhiteSpace(uri.IdnHost)
                || uri.Port <= IPEndPoint.MinPort)
            {
                throw new ArgumentException($"Invalid {protocolType} address ({ipAddressOrUrl}).", nameof(ipAddressOrUrl));
            }

            HostName = uri.IdnHost;
            IpEndPoint.Port = uri.Port;
        }
        else
        {

            var hostType = Uri.CheckHostName(ipAddressOrUrl);
            switch (hostType)
            {
                case UriHostNameType.Unknown:
                case UriHostNameType.Basic:
                    if (Uri.IsWellFormedUriString(ipAddressOrUrl, UriKind.RelativeOrAbsolute))
                    {
                        var uriBuilder = new UriBuilder(ipAddressOrUrl);
                        if (uriBuilder.Port == -1 && ushort.TryParse(uriBuilder.Path, out var port))
                        {
                            uriBuilder.Port = port;
                            uriBuilder.Path = string.Empty;
                        }
                        HostName = uriBuilder.Host;
                        IpEndPoint.Port = uriBuilder.Port;
                    }
                    else
                    {
                        throw new ArgumentException($"Invalid host name or address ({ipAddressOrUrl}).", nameof(ipAddressOrUrl));
                    }

                    break;
                case UriHostNameType.Dns:
                    HostName = ipAddressOrUrl;
                    break;
                case UriHostNameType.IPv4:
                case UriHostNameType.IPv6:
                    IpEndPoint.Address = IPAddress.Parse(ipAddressOrUrl);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(ipAddressOrUrl, hostType, null);
            }
        }
    }

    [SetsRequiredMembers]
    [JsonConstructor]
    protected BasePingableCollectionObject(ServiceProtocolType protocolType, string ipAddressOrUrl, string description = "", string group = "") : this(protocolType, ipAddressOrUrl)
    {
        Description = description;
        Group = group;
    }

    #endregion

    #region Methods
    /// <summary>
    /// Pings the service.
    /// </summary>
    /// <param name="timeout">The timeout time in milliseconds.</param>
    /// <returns></returns>
    public T Ping(int timeout = 0)
    {
        BeginPing();
        T result;
        try
        {
            if (timeout <= 0)
            {
                timeout = (int)(TimeoutSeconds * 1000);
            }

            OnPingStarted();
            result = PingCore(timeout);

            if (!IpEndPoint.IsValid())
            {
                RunOnUiThread(() => IpAddress = result.IpAddress);
            }

            ResolveDns();

            Add(result);
        }
        finally
        {
            EndPing();
        }

        OnPingCompleted(new PingCompletedEventArgs(result));
        return result;
    }

    private void BeginPing()
    {
        Volatile.Write(ref _isBusyFlag, 1);
        RunOnUiThread(SyncExecutionState);
    }

    private void EndPing()
    {
        Volatile.Write(ref _lastExecutedTicks, DateTime.Now.Ticks);
        Volatile.Write(ref _isBusyFlag, 0);
        RunOnUiThread(SyncExecutionState);
    }

    /// <summary>
    /// Copies the thread-safe busy and last executed state into the observable properties. Must run on the UI thread.
    /// </summary>
    private void SyncExecutionState()
    {
        var lastExecutedTicks = Volatile.Read(ref _lastExecutedTicks);
        LastExecutedDateTime = lastExecutedTicks == 0 ? null : new DateTime(lastExecutedTicks, DateTimeKind.Local);
        IsBusy = Volatile.Read(ref _isBusyFlag) != 0;
    }

    /// <summary>
    /// Runs the action inline on the UI thread, otherwise posts it to the UI thread.
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Default);
        }
    }

    protected abstract T PingCore(int timeout = 0);

    /// <summary>
    /// Pings the service.
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds for the ping to cancel.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<T> PingAsync(int timeout = 0, CancellationToken cancellationToken = default)
    {
        BeginPing();
        T result;
        try
        {
            if (timeout <= 0)
            {
                timeout = (int)(TimeoutSeconds * 1000);
            }

            OnPingStarted();
            result = await PingCoreAsync(timeout, cancellationToken);
            Add(result);

            if (!IpEndPoint.IsValid())
            {
                RunOnUiThread(() => IpAddress = result.IpAddress);
            }

            await ResolveDnsAsync(cancellationToken: cancellationToken);
        }
        finally
        {
            EndPing();
        }

        OnPingCompleted(new PingCompletedEventArgs(result));
        return result;
    }

    /// <summary>
    /// Pings the service.
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds for the ping to cancel.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected abstract Task<T> PingCoreAsync(int timeout = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the statistic cache of the service from the <see cref="Pings"/> collection.
    /// </summary>
    public void RebuildStatistic()
    {
        uint succeedCount = 0;
        uint failedCount = 0;
        uint consecutiveSucceedCount = 0;
        uint consecutiveFailedCount = 0;
        uint maxConsecutiveSucceedCount = 0;
        uint maxConsecutiveFailedCount = 0;
        DateTime? lastExecutedDateTime = null;
        DateTime? lastSucceedDateTime = null;
        DateTime? lastFailedDateTime = null;
        double minimumTime = double.PositiveInfinity;
        double maximumTime = double.NegativeInfinity;
        ulong totalTimeSum = 0;

        // Pings are stored newest first, so walk from the oldest to the newest reply.
        for (int i = Count - 1; i >= 0; i--)
        {
            BasePingReply item = this[i];
            if (item.IsSucceeded)
            {
                succeedCount++;
                consecutiveSucceedCount++;
                consecutiveFailedCount = 0;
                if (consecutiveSucceedCount > maxConsecutiveSucceedCount)
                {
                    maxConsecutiveSucceedCount = consecutiveSucceedCount;
                }
                lastSucceedDateTime = item.SentDateTime;
                if (double.IsFinite(item.Time) && item.Time < minimumTime)
                {
                    minimumTime = item.Time;
                }
                if (double.IsFinite(item.Time) && item.Time > maximumTime)
                {
                    maximumTime = item.Time;
                }

                unchecked
                {
                    totalTimeSum += Convert.ToUInt64(item.Time);
                }
            }
            else
            {
                failedCount++;
                consecutiveFailedCount++;
                consecutiveSucceedCount = 0;
                if (consecutiveFailedCount > maxConsecutiveFailedCount)
                {
                    maxConsecutiveFailedCount = consecutiveFailedCount;
                }
                lastFailedDateTime = item.SentDateTime;
            }

            lastExecutedDateTime = item.SentDateTime;
        }

        if (double.IsNegativeInfinity(maximumTime)) maximumTime = double.PositiveInfinity;

        SentCount = (uint)Count;
        SucceedCount = succeedCount;
        FailedCount = failedCount;
        ConsecutiveSucceedCount = consecutiveSucceedCount;
        ConsecutiveFailedCount = consecutiveFailedCount;
        MaxConsecutiveSucceedCount = maxConsecutiveSucceedCount;
        MaxConsecutiveFailedCount = maxConsecutiveFailedCount;
        TotalTimeSum = totalTimeSum;
        MaximumTime = maximumTime;
        MinimumTime = minimumTime;
        Volatile.Write(ref _lastExecutedTicks, lastExecutedDateTime?.Ticks ?? 0);
        LastExecutedDateTime = lastExecutedDateTime;
        LastSucceedDateTime = lastSucceedDateTime;
        LastFailedDateTime = lastFailedDateTime;

        RebuildStatisticCore();

        LastPing = Count > 0 ? this[0] : null;
    }

    /// <summary>
    /// Rebuilds the statistic cache of the service from the <see cref="Pings"/> collection.
    /// </summary>
    protected virtual void RebuildStatisticCore() { }

    protected string GetDnsLookupTarget()
    {
        if (ProtocolType is ServiceProtocolType.TCP
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
            var scheme = ProtocolType is ServiceProtocolType.TCP
                or ServiceProtocolType.TLS
                or ServiceProtocolType.SSH
                or ServiceProtocolType.SMTP
                or ServiceProtocolType.IMAP
                or ServiceProtocolType.MQTT
                ? "tcp"
                : "udp";
            if (Uri.TryCreate($"{scheme}://{IpAddressOrUrl}", UriKind.Absolute, out var uri))
            {
                return uri.IdnHost;
            }
        }
        else if (ProtocolType is (ServiceProtocolType.HTTP or ServiceProtocolType.WebSocket)
                 && Uri.TryCreate(IpAddressOrUrl, UriKind.Absolute, out var uri))
        {
            return uri.IdnHost;
        }

        return IpAddressOrUrl;
    }

    protected void ResolveDns(bool updateEndpointAddress = false)
    {
        if (!TryBeginDnsResolution()) return;

        try
        {
            ApplyDnsResult(Dns.GetHostEntry(GetDnsLookupTarget()), updateEndpointAddress);
        }
        catch (Exception e)
        {
            App.WriteLine(e);
        }
        finally
        {
            Volatile.Write(ref _isDnsResolutionInProgress, 0);
        }
    }

    protected async ValueTask ResolveDnsAsync(
        bool updateEndpointAddress = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryBeginDnsResolution()) return;

        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(GetDnsLookupTarget(), cancellationToken);
            ApplyDnsResult(hostEntry, updateEndpointAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            App.WriteLine(e);
        }
        finally
        {
            Volatile.Write(ref _isDnsResolutionInProgress, 0);
        }
    }

    private bool TryBeginDnsResolution()
    {
        if (Volatile.Read(ref _isDnsResolvedFlag) != 0
            || Volatile.Read(ref _dnsResolutionAttempts) >= MaxDnsResolutionAttempts)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _isDnsResolutionInProgress, 1, 0) != 0)
        {
            return false;
        }

        if (Volatile.Read(ref _isDnsResolvedFlag) != 0
            || Volatile.Read(ref _dnsResolutionAttempts) >= MaxDnsResolutionAttempts)
        {
            Volatile.Write(ref _isDnsResolutionInProgress, 0);
            return false;
        }

        Interlocked.Increment(ref _dnsResolutionAttempts);
        return true;
    }

    private void ApplyDnsResult(IPHostEntry hostEntry, bool updateEndpointAddress)
    {
        if (hostEntry.AddressList.Length > 0)
        {
            Volatile.Write(ref _isDnsResolvedFlag, 1);
        }

        RunOnUiThread(() =>
        {
            HostName = hostEntry.HostName;
            IpAddresses = hostEntry.AddressList;
            Aliases = hostEntry.Aliases;

            if (IpAddresses.Length == 0) return;

            if (updateEndpointAddress || !IpEndPoint.IsValid())
            {
                IpAddress = IpAddresses[0];
            }

            IsDnsResolved = true;
        });
    }
    #endregion

    #region Commands
    public void Pause()
    {
        IsEnabled = false;
    }

    public void Resume()
    {
        IsEnabled = true;
    }
    #endregion

    #region IList<T> implementation
    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        return Pings.GetEnumerator();
    }

    /// <inheritdoc />
    /*IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)Pings).GetEnumerator();
    }*/

    private void InnerAdd(T item)
    {
        BasePingReply reply = item;

        SentCount++;
        if (reply.IsSucceeded)
        {
            SucceedCount++;
            ConsecutiveSucceedCount++;
            ConsecutiveFailedCount = 0;
            if (ConsecutiveSucceedCount > MaxConsecutiveSucceedCount)
            {
                MaxConsecutiveSucceedCount = ConsecutiveSucceedCount;
            }
            LastSucceedDateTime = reply.SentDateTime;

            if (double.IsFinite(reply.Time))
            {
                if (!double.IsFinite(MaximumTime) || reply.Time > MaximumTime)
                {
                    MaximumTime = reply.Time;
                }

                if (!double.IsFinite(MinimumTime) || reply.Time < MinimumTime)
                {
                    MinimumTime = reply.Time;
                }

                unchecked
                {
                    TotalTimeSum += Convert.ToUInt64(reply.Time);
                }
            }
        }
        else
        {
            FailedCount++;
            ConsecutiveFailedCount++;
            ConsecutiveSucceedCount = 0;
            if (ConsecutiveFailedCount > MaxConsecutiveFailedCount)
            {
                MaxConsecutiveFailedCount = ConsecutiveFailedCount;
            }
            LastFailedDateTime = reply.SentDateTime;
        }

        // Insert first so StatusChanged, raised by the LastPing change, already sees the new reply at index 0.
        Pings.Insert(0, item);
        LastPing = item;
        TrimPingsToCacheLimit();
    }

    internal void TrimPingsToCacheLimit()
    {
        var maxReplies = App.AppSettings.PingServices.MaxRepliesCache;
        if (maxReplies > 0)
        {
            Pings.RemoveExceedingAt(maxReplies, CollectionSide.Tail);
        }
    }

    /// <inheritdoc />
    public void Add(T item)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InnerAdd(item);
        }
        else
        {
            Dispatcher.UIThread.Post(() => InnerAdd(item), DispatcherPriority.Default);
        }
    }

    protected virtual void ClearCore() { }


    /// <inheritdoc />
    public void Clear()
    {
        SentCount = 0;
        SucceedCount = 0;
        FailedCount = 0;
        ConsecutiveSucceedCount = 0;
        ConsecutiveFailedCount = 0;
        MaxConsecutiveSucceedCount = 0;
        MaxConsecutiveFailedCount = 0;
        LastSucceedDateTime = null;
        LastFailedDateTime = null;
        TotalTimeSum = 0;
        MaximumTime = double.PositiveInfinity;
        MinimumTime = double.PositiveInfinity;
        LastPing = null;
        ClearCore();

        Pings.Clear();
    }

    /// <inheritdoc />
    public bool Contains(T item)
    {
        return Pings.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex)
    {
        Pings.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc />
    public bool Remove(T item)
    {
        return Pings.Remove(item);
    }

    /// <inheritdoc />
    [JsonIgnore]
    public int Count => Pings.Count;

    /// <inheritdoc />
    [JsonIgnore]
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public int IndexOf(T item)
    {
        return Pings.IndexOf(item);
    }

    /// <inheritdoc />
    public void Insert(int index, T item)
    {
        Pings.Insert(index, item);
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        Pings.RemoveAt(index);
    }

    /// <inheritdoc />
    public T this[int index]
    {
        get => Pings[index];
        set => Pings[index] = value;
    }
    #endregion

    #region Equality
    public bool Equals(BasePingableCollectionObject<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ProtocolType == other.ProtocolType && IpAddressOrUrl == other.IpAddressOrUrl;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((BasePingableCollectionObject<T>)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine((int)ProtocolType, IpAddressOrUrl);
    }

    public static bool operator ==(BasePingableCollectionObject<T>? left, BasePingableCollectionObject<T>? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(BasePingableCollectionObject<T>? left, BasePingableCollectionObject<T>? right)
    {
        return !Equals(left, right);
    }
    #endregion
}
