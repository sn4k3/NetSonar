using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cysharp.Diagnostics;
using Material.Icons;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Models;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.ViewModels.Dialogs;
using NetSonar.Avalonia.Views;
using NetSonar.Avalonia.Views.Fragments;
using ObservableCollections;
using StageKit.Primitives;
using StageKit.Primitives.System;
using SukiUI.Dialogs;
using ZLinq;
using ZLogger;

namespace NetSonar.Avalonia.Network;

/// <summary>
/// The operating system data of one network interface, read once so the bridge getters do not query the OS again.
/// </summary>
/// <remarks>
/// Created by <see cref="NetworkInterfaceBridge.Capture"/>, which is safe to call from a background thread, and
/// applied on the UI thread with <see cref="NetworkInterfaceBridge.Apply"/>.
/// </remarks>
internal sealed class NetworkInterfaceSnapshot
{
    public required NetworkInterface Interface { get; init; }
    public required IPInterfaceProperties Properties { get; init; }
    public IPv4InterfaceProperties? IPv4Properties { get; init; }
    public IPv6InterfaceProperties? IPv6Properties { get; init; }
    public IPInterfaceStatistics? Statistics { get; init; }
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public bool SupportsIPv4 { get; init; }
    public bool SupportsIPv6 { get; init; }
    public string PhysicalAddress { get; init; } = string.Empty;

    /// <summary>
    /// The reflected property rows, merged into <see cref="NetworkInterfaceBridge.TabularData"/> on apply.
    /// </summary>
    public Dictionary<string, GroupNameValue> TabularData { get; } = [];
}

public partial class NetworkInterfaceBridge : ObservableObject, IDisposable
{
    private NetworkInterfaceSnapshot _data = null!;
    private NetworkInterfaceSnapshot? _pendingSnapshot;
    private string? _macNetworkServiceName;

    [SetsRequiredMembers]
    public NetworkInterfaceBridge(NetworkInterface network)
        : this(network, null) { }

    [SetsRequiredMembers]
    internal NetworkInterfaceBridge(NetworkInterfaceSnapshot snapshot)
        : this(snapshot.Interface, snapshot) { }

    [SetsRequiredMembers]
    private NetworkInterfaceBridge(NetworkInterface network, NetworkInterfaceSnapshot? snapshot)
    {
        _pendingSnapshot = snapshot;
        Interface = network;

        TabularDataView = TabularData.ToNotifyCollectionChanged(
            SynchronizationContextCollectionEventDispatcher.Current
        );
        TabularDataGroupView = new DataGridCollectionView(TabularDataView);
        TabularDataGroupView.GroupDescriptions.Add(
            new DataGridPathGroupDescription($"Value.{nameof(GroupNameValue.Group)}")
        );
    }

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(HavePhysicalAddress))]
    [NotifyPropertyChangedFor(nameof(PhysicalAddressString))]
    [NotifyPropertyChangedFor(nameof(Properties))]
    [NotifyPropertyChangedFor(nameof(IPv4Properties))]
    [NotifyPropertyChangedFor(nameof(IPv6Properties))]
    [NotifyPropertyChangedFor(nameof(Statistics))]
    [NotifyPropertyChangedFor(nameof(IPv4Statistics))]
    [NotifyPropertyChangedFor(nameof(IsVirtual))]
    [NotifyPropertyChangedFor(nameof(HaveIPAddress))]
    [NotifyPropertyChangedFor(nameof(IPAddressesStr))]
    [NotifyPropertyChangedFor(nameof(IPv4Address))]
    [NotifyPropertyChangedFor(nameof(IPv6Address))]
    [NotifyPropertyChangedFor(nameof(SupportsIPv4))]
    [NotifyPropertyChangedFor(nameof(SupportsIPv6))]
    [NotifyPropertyChangedFor(nameof(SupportsAnyIpProtocol))]
    [NotifyPropertyChangedFor(nameof(IsDHCPv4))]
    [NotifyPropertyChangedFor(nameof(IsStaticIPv4))]
    [NotifyPropertyChangedFor(nameof(IsTransmittingData))]
    [NotifyPropertyChangedFor(nameof(BytesReceived))]
    [NotifyPropertyChangedFor(nameof(BytesSent))]
    [NotifyPropertyChangedFor(nameof(TypeIcon))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReleaseIpCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenewIpCommand))]
    public required partial NetworkInterface Interface { get; set; }

    /// <summary>
    /// Gets a value that indicates whether the network interface is disabled.
    /// </summary>
    public bool IsDisabled =>
        Interface.OperationalStatus is OperationalStatus.Down && !HaveIPAddress;

    /// <summary>
    /// Gets a value that indicates whether the network interface is enabled.
    /// </summary>
    public bool IsEnabled => !IsDisabled;

    /// <summary>
    /// Gets a value that indicates whether the network interface is active.
    /// </summary>
    public bool IsActive =>
        Interface.OperationalStatus is OperationalStatus.Up or OperationalStatus.Testing;

    /// <summary>
    /// Gets a value that indicates whether the network interface has a physical address.
    /// </summary>
    public bool HavePhysicalAddress => PhysicalAddressString.Length > 0;

    /// <summary>
    /// Gets the physical address of this network interface
    /// </summary>
    /// <returns>The interface's physical address.</returns>
    public string PhysicalAddressString => _data.PhysicalAddress;

    /// <summary>
    /// Gets the IP properties for this network interface, as read when <see cref="Interface"/> was last set.
    /// </summary>
    /// <returns>The interface's IP properties.</returns>
    public IPInterfaceProperties Properties => _data.Properties;

    /// <summary>Provides Internet Protocol version 4 (IPv4) configuration data for this network interface.</summary>
    /// <returns>An <see cref="T:System.Net.NetworkInformation.IPv4InterfaceProperties" /> object that contains IPv4 configuration data, or null when the interface does not support IPv4.</returns>
    public IPv4InterfaceProperties? IPv4Properties => _data.IPv4Properties;

    /// <summary>Provides Internet Protocol version 6 (IPv6) configuration data for this network interface.</summary>
    /// <returns>An <see cref="T:System.Net.NetworkInformation.IPv6InterfaceProperties" /> object that contains IPv6 configuration data, or null when the interface does not support IPv6.</returns>
    public IPv6InterfaceProperties? IPv6Properties => _data.IPv6Properties;

    /// <summary>
    /// Provides Internet Protocol (IP) statistical data for this network interface.
    /// </summary>
    /// <returns>The interface's IP statistics, or null when the interface cannot report them (for example while dormant).</returns>
    public IPInterfaceStatistics? Statistics => _data.Statistics;

    /// <summary>
    /// Provides Internet Protocol (IP) statistical data for this network interface.
    /// Despite the naming, the results are not IPv4 specific.
    /// Do not use this method, use GetIPStatistics instead.
    /// </summary>
    /// <returns>The interface's IP statistics.</returns>
    public IPv4InterfaceStatistics IPv4Statistics => Interface.GetIPv4Statistics();

    /// <summary>
    /// Gets a value that indicates whether the network interface is a virtual interface.
    /// </summary>
    public bool IsVirtual => IsVirtualInterface(Interface);

    /// <summary>
    /// Gets a value that indicates whether the network interface has an IP address.
    /// </summary>
    public bool HaveIPAddress => Properties.UnicastAddresses.Count > 0;

    /// <summary>
    /// Gets the IP addresses that are associated with the current network interface.
    /// </summary>
    public string IPAddressesStr =>
        Properties
            .UnicastAddresses.AsValueEnumerable()
            .Select(ip => ip.Address.ToString())
            .JoinToString('\n');

    /// <summary>
    /// Gets the IPv4 address information that is associated with the current network interface.
    /// </summary>
    public IPAddress? IPv4Address =>
        Properties
            .UnicastAddresses.AsValueEnumerable()
            .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => ip.Address)
            .LastOrDefault();

    /// <summary>
    /// Gets the IPv6 address information that is associated with the current network interface.
    /// </summary>
    public IPAddress? IPv6Address =>
        Properties
            .UnicastAddresses.AsValueEnumerable()
            .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(ip => ip.Address)
            .LastOrDefault();

    /// <summary>
    /// Gets a value that indicates whether the network interface supports Internet Protocol version 4 (IPv4).
    /// </summary>
    public bool SupportsIPv4 => _data.SupportsIPv4;

    /// <summary>
    /// Gets a value that indicates whether the network interface supports Internet Protocol version 6 (IPv6).
    /// </summary>
    public bool SupportsIPv6 => _data.SupportsIPv6;

    /// <summary>
    /// Gets a value that indicates whether the network interface supports any Internet Protocol version (v4 or v6).
    /// </summary>
    public bool SupportsAnyIpProtocol => SupportsIPv4 | SupportsIPv6;

    /// <summary>
    /// Gets a value that indicates whether the network interface is configured to use a DHCP Internet Protocol version 4 (IPv4) address.
    /// </summary>
    /// <returns>Null if current operative system lack of this information</returns>
    public bool IsDHCPv4
    {
        get
        {
            if (!SupportsIPv4)
                return false;
            if (OperatingSystem.IsWindows())
                return IPv4Properties?.IsDhcpEnabled ?? false;
            return false;
        }
    }

    /// <summary>
    /// Gets a value that indicates whether the network interface is configured to use a static Internet Protocol version 4 (IPv4) address.
    /// </summary>
    /// <returns>Null if current operative system lack of this information</returns>
    public bool IsStaticIPv4 => !IsDHCPv4;

    /// <summary>
    /// Gets a value that indicates whether the network interface is transmitting data.
    /// </summary>
    public bool IsTransmittingData => (IsActive && BytesReceived > 0) || BytesSent > 0;

    /// <summary>
    /// Gets the number of bytes received on the interface.
    /// </summary>
    public long BytesReceived => _data.BytesReceived;

    /// <summary>
    /// Gets the number of bytes sent on the interface.
    /// </summary>
    public long BytesSent => _data.BytesSent;

    public ObservableDictionary<string, GroupNameValue> TabularData { get; } = [];

    public INotifyCollectionChangedSynchronizedViewList<
        KeyValuePair<string, GroupNameValue>
    > TabularDataView { get; private set; }

    public DataGridCollectionView TabularDataGroupView { get; init; }

    public MaterialIconKind TypeIcon => GetNetworkInterfaceTypeIcon(Interface.NetworkInterfaceType);

    public MaterialIconKind StatusIcon => GetStatusIcon(Interface.OperationalStatus);

    public static NetworkInterfaceBridge? PrimaryInterface
    {
        get
        {
            var networkInterface = NetworkInterface
                .GetAllNetworkInterfaces()
                .AsValueEnumerable()
                .FirstOrDefault(@interface =>
                    @interface.NetworkInterfaceType
                        is NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.Wireless80211
                    && @interface.OperationalStatus == OperationalStatus.Up
                    && !@interface.Name.StartsWith("vEthernet")
                );
            return networkInterface is null ? null : new NetworkInterfaceBridge(networkInterface);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        TabularDataView.Dispose();
        IsDisposed = true;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Interface))
        {
            // Load the OS data before the dependent notifications below, so their getters read the new interface.
            // A snapshot captured in advance (off the UI thread) is used when there is one.
            var snapshot = _pendingSnapshot ?? Capture(Interface);
            _pendingSnapshot = null;
            _data = snapshot;
            MergeTabularData(snapshot);
        }

        base.OnPropertyChanged(e);
    }

    /// <summary>
    /// Reads the operating system data of a network interface. It does not touch the UI or any bridge state, so it
    /// can run on a background thread.
    /// </summary>
    internal static NetworkInterfaceSnapshot Capture(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        var supportsIPv4 = adapter.Supports(NetworkInterfaceComponent.IPv4);
        var supportsIPv6 = adapter.Supports(NetworkInterfaceComponent.IPv6);

        IPv4InterfaceProperties? ipv4Properties = null;
        if (supportsIPv4)
        {
            try
            {
                ipv4Properties = properties.GetIPv4Properties();
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                // The interface reports IPv4 support but exposes no IPv4 configuration.
            }
        }

        IPv6InterfaceProperties? ipv6Properties = null;
        if (supportsIPv6)
        {
            try
            {
                ipv6Properties = properties.GetIPv6Properties();
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                // The interface reports IPv6 support but exposes no IPv6 configuration.
            }
        }

        IPInterfaceStatistics? statistics = null;
        long bytesReceived = 0;
        long bytesSent = 0;
        if (adapter.OperationalStatus != OperationalStatus.Dormant)
        {
            try
            {
                statistics = adapter.GetIPStatistics();
                bytesReceived = statistics.BytesReceived;
                bytesSent = statistics.BytesSent;
            }
            catch
            {
                // Statistics are not available on every interface.
                statistics = null;
                bytesReceived = 0;
                bytesSent = 0;
            }
        }

        var snapshot = new NetworkInterfaceSnapshot
        {
            Interface = adapter,
            Properties = properties,
            IPv4Properties = ipv4Properties,
            IPv6Properties = ipv6Properties,
            Statistics = statistics,
            BytesReceived = bytesReceived,
            BytesSent = bytesSent,
            SupportsIPv4 = supportsIPv4,
            SupportsIPv6 = supportsIPv6,
            PhysicalAddress = adapter
                .GetPhysicalAddress()
                .GetAddressBytes()
                .AsValueEnumerable()
                .Select(x => x.ToString("X2"))
                .JoinToString(':'),
        };

        var tabularData = snapshot.TabularData;
        tabularData.Add(
            $"{nameof(Interface)}.{nameof(PhysicalAddress)}",
            new GroupNameValue(nameof(PhysicalAddress), snapshot.PhysicalAddress, nameof(Interface))
        );
        ReflectionExtensions.BuildTabularData(tabularData, adapter, nameof(Interface));
        ReflectionExtensions.BuildTabularData(tabularData, properties, nameof(Properties));
        ReflectionExtensions.BuildTabularData(tabularData, ipv4Properties, "IPv4 Properties");
        ReflectionExtensions.BuildTabularData(tabularData, ipv6Properties, "IPv6 Properties");
        ReflectionExtensions.BuildTabularData(tabularData, statistics, nameof(Statistics));
        return snapshot;
    }

    /// <summary>
    /// Replaces <see cref="Interface"/> with a freshly captured one. Must run on the UI thread.
    /// </summary>
    internal void Apply(NetworkInterfaceSnapshot snapshot)
    {
        if (ReferenceEquals(Interface, snapshot.Interface))
        {
            // Setting the same instance would not raise a change, so update the data and notify everything.
            _data = snapshot;
            MergeTabularData(snapshot);
            OnPropertyChanged(string.Empty);
            return;
        }

        _pendingSnapshot = snapshot;
        try
        {
            Interface = snapshot.Interface;
        }
        finally
        {
            _pendingSnapshot = null;
        }
    }

    private void MergeTabularData(NetworkInterfaceSnapshot snapshot)
    {
        foreach (var (key, row) in snapshot.TabularData)
        {
            if (TabularData.TryGetValue(key, out var existing))
                existing.Value = row.Value;
            else
                TabularData.Add(key, row);
        }
    }

    /// <summary>
    /// Resets the network interface to its current state.
    /// </summary>
    [RelayCommand]
    public async Task Reset()
    {
        var id = Interface.Id;
        var snapshot = await Task.Run(() =>
        {
            var adapter = NetworkInterface
                .GetAllNetworkInterfaces()
                .AsValueEnumerable()
                .FirstOrDefault(adapter => adapter.Id == id);
            return adapter is null ? null : Capture(adapter);
        });

        if (snapshot is not null && !IsDisposed)
            Apply(snapshot);
    }

    /// <summary>
    /// Refreshes the network interface data.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        Apply(Capture(Interface));
    }

    /// <summary>
    /// Whether changing the network configuration needs administrator rights on this platform. On Linux,
    /// NetworkManager authorizes the change through polkit, so no elevation is requested.
    /// </summary>
    private static bool RequireAdminRights => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [GeneratedRegex(@"^\(\s*(?:\d+|\*)\s*\)\s*(?<name>\S.*)$")]
    private static partial Regex MacNetworkServiceHeaderRegex();

    [GeneratedRegex(@"^\(Hardware Port:.*Device:\s*(?<device>[^,)\s]*)\s*\)$")]
    private static partial Regex MacNetworkServiceDeviceRegex();

    /// <summary>
    /// Finds the network service name that uses a BSD device in the output of
    /// <c>networksetup -listnetworkserviceorder</c>.
    /// </summary>
    /// <param name="networkServiceOrder">The command output, made of blocks such as
    /// <c>(1) Wi-Fi</c> followed by <c>(Hardware Port: Wi-Fi, Device: en0)</c>. Disabled services are prefixed
    /// with <c>*</c>, for example <c>(*) Bluetooth PAN</c>.</param>
    /// <param name="device">The BSD device name, for example <c>en0</c>.</param>
    /// <returns>The service name, or null when no service uses the device.</returns>
    public static string? ParseMacNetworkServiceName(string networkServiceOrder, string device)
    {
        if (string.IsNullOrEmpty(device))
            return null;

        string? service = null;
        foreach (
            var line in networkServiceOrder.Split(
                '\n',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var header = MacNetworkServiceHeaderRegex().Match(line);
            if (header.Success)
            {
                service = header.Groups["name"].Value.Trim();
                continue;
            }

            if (service is null)
                continue;

            var hardwarePort = MacNetworkServiceDeviceRegex().Match(line);
            if (hardwarePort.Success && hardwarePort.Groups["device"].Value == device)
                return service;
        }

        return null;
    }

    /// <summary>
    /// Gets the macOS network service name of the interface. <c>networksetup</c> takes the service name
    /// (for example <c>Wi-Fi</c>), not the BSD device name (<c>en0</c>) that <see cref="NetworkInterface.Name"/> holds.
    /// </summary>
    /// <returns>The service name, or the interface name when it cannot be resolved.</returns>
    public async Task<string> GetMacNetworkServiceNameAsync()
    {
        if (_macNetworkServiceName is not null)
            return _macNetworkServiceName;

        var device = Interface.Name;
        try
        {
            App.Logger.ZLogInformation($"Run command: networksetup -listnetworkserviceorder");
            var lines = await ProcessX.StartAsync("networksetup -listnetworkserviceorder").ToTask();
            var service = ParseMacNetworkServiceName(string.Join('\n', lines), device);
            if (service is not null)
                return _macNetworkServiceName = service;
        }
        catch (Exception e)
        {
            App.Logger.ZLogWarning(e, $"Unable to resolve the network service of {device}");
        }

        return device;
    }

    /// <summary>
    /// Gets the name the platform configuration tools expect for this interface: the network service name on
    /// macOS, the interface name elsewhere.
    /// </summary>
    public Task<string> GetConfigurationTargetAsync()
    {
        return OperatingSystem.IsMacOS()
            ? GetMacNetworkServiceNameAsync()
            : Task.FromResult(Interface.Name);
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    public async Task Disable()
    {
        if (IsDisabled)
            return;

        var toast = new ProcessXToast
        {
            Title = App.Localization.Format("Network.Disable.Title", Interface.Description),
            SuccessGenericMessage = App.Localization.Format(
                "Network.Disable.Success",
                Interface.Name
            ),
            ErrorGenericMessage = App.Localization.Format("Network.Disable.Error", Interface.Name),
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                $"netsh interface set interface \"{Interface.Name}\" disable",
                toast,
                true
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                $"ifconfig \"{Interface.Name}\" down",
                toast,
                true
            );
        }
        else
        {
            // NetworkManager manages the link: a link taken down with `ip link` can be brought back up by it, and
            // disconnecting through nmcli needs no root because NetworkManager authorizes it through polkit.
            success = await ProcessXExtensions.ExecuteHandled(
                $"nmcli device disconnect \"{Interface.Name}\"",
                toast
            );
        }

        if (success)
            await Reset();
    }

    [RelayCommand(CanExecute = nameof(IsDisabled))]
    public async Task Enable()
    {
        if (IsEnabled)
            return;

        var toast = new ProcessXToast
        {
            Title = App.Localization.Format("Network.Enable.Title", Interface.Description),
            SuccessGenericMessage = App.Localization.Format(
                "Network.Enable.Success",
                Interface.Name
            ),
            ErrorGenericMessage = App.Localization.Format("Network.Enable.Error", Interface.Name),
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                $"netsh interface set interface \"{Interface.Name}\" enable",
                toast,
                true
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                $"ifconfig \"{Interface.Name}\" up",
                toast,
                true
            );
        }
        else
        {
            // Mirrors Disable: nmcli through NetworkManager, without root.
            success = await ProcessXExtensions.ExecuteHandled(
                $"nmcli device connect \"{Interface.Name}\"",
                toast
            );
        }

        if (success)
            await Reset();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    public async Task ReleaseIp()
    {
        if (!IsActive || !HaveIPAddress)
            return;

        var toast = new ProcessXToast
        {
            Title = App.Localization.Format("Network.Release.Title", Interface.Description),
            SuccessGenericMessage = App.Localization["Network.Release.Success"],
            ErrorGenericMessage = App.Localization.Format("Network.Release.Error", Interface.Name),
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                $"ipconfig /release \"{Interface.Name}\"",
                toast,
                true
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS ipconfig takes the BSD device name.
            success = await ProcessXExtensions.ExecuteHandled(
                $"ipconfig set {Interface.Name} NONE",
                toast,
                true
            );
        }
        else
        {
            // `nmcli connection down` expects a connection profile name, not a device name.
            success = await ProcessXExtensions.ExecuteHandled(
                $"nmcli device disconnect \"{Interface.Name}\"",
                toast
            );
        }

        if (success)
            await Reset();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    public async Task RenewIp()
    {
        if (!IsActive)
            return;

        var toast = new ProcessXToast
        {
            Title = App.Localization.Format("Network.Renew.Title", Interface.Description),
            SuccessGenericMessage = App.Localization["Network.Renew.Success"],
            ErrorGenericMessage = App.Localization.Format("Network.Renew.Error", Interface.Name),
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled(
                [
                    $"ipconfig /release \"{Interface.Name}\"",
                    $"ipconfig /renew \"{Interface.Name}\"",
                ],
                toast,
                true
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS ipconfig takes the BSD device name.
            success = await ProcessXExtensions.ExecuteHandled(
                $"ipconfig set {Interface.Name} DHCP",
                toast,
                true
            );
        }
        else
        {
            success = await ProcessXExtensions.ExecuteHandled(
                [
                    $"nmcli device disconnect \"{Interface.Name}\"",
                    $"nmcli device connect \"{Interface.Name}\"",
                ],
                toast
            );
        }

        if (success)
            await Reset();
    }

    [RelayCommand]
    public void ShowManualAssignmentIPDialog()
    {
        var dialog = App
            .DialogManager.CreateDialog()
            .WithViewModel(dialog => new SetInterfaceIPDialogModel(dialog, this));
        dialog.TryShow();
    }

    /// <summary>
    /// Resolves a subnet mask given either as a prefix length (<c>24</c>) or as a mask (<c>255.255.255.0</c>).
    /// </summary>
    private static bool TryGetPrefixLength(
        string? subnetMask,
        AddressFamily family,
        out int prefixLength
    )
    {
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(subnetMask))
            return false;

        var maxPrefixLength = family == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (
            int.TryParse(
                subnetMask,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out prefixLength
            )
        )
        {
            return prefixLength <= maxPrefixLength;
        }

        if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != family)
            return false;

        prefixLength = IPAddressExtensions.MaskToCidr(mask);
        // CidrToMask is limited to 126 bits, so only IPv4 masks are checked for contiguity.
        return family == AddressFamily.InterNetworkV6
            || IPAddressExtensions.CidrToMask(prefixLength).Equals(mask);
    }

    /// <summary>
    /// Reads the DNS servers assigned statically to the interface.
    /// </summary>
    /// <param name="family">The address family to read.</param>
    /// <returns>
    /// The static servers in their configured order, an empty array when the interface takes its DNS servers from
    /// DHCP, or <see langword="null"/> when the platform cannot tell the two apart.
    /// </returns>
    /// <remarks>
    /// <see cref="IPInterfaceProperties.DnsAddresses"/> reports the effective servers without saying where they came
    /// from. Windows keeps the static ones in the <c>NameServer</c> value of the interface's TCP/IP parameters, while
    /// the DHCP ones live in <c>DhcpNameServer</c>.
    /// </remarks>
    public IPAddress[]? GetStaticDnsServers(AddressFamily family)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var service = family == AddressFamily.InterNetworkV6 ? "Tcpip6" : "Tcpip";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{service}\Parameters\Interfaces\{Interface.Id}"
            );
            if (key?.GetValue("NameServer") is not string value)
                return [];

            return ParseNameServers(value, family);
        }
        catch (Exception e)
            when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a Windows <c>NameServer</c> registry value, which separates servers with commas or spaces.
    /// </summary>
    /// <param name="value">The registry value.</param>
    /// <param name="family">The address family to keep.</param>
    /// <returns>The servers of that family, in order.</returns>
    public static IPAddress[] ParseNameServers(string? value, AddressFamily family)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var servers = new List<IPAddress>();
        foreach (
            var part in value.Split(
                [',', ' ', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (IPAddress.TryParse(part, out var address) && address.AddressFamily == family)
                servers.Add(address);
        }

        return servers.ToArray();
    }

    /// <summary>
    /// Gets the gateway addresses the interface uses now, as last read from the system.
    /// </summary>
    public IPAddress[] CurrentGateways =>
        Properties.GatewayAddresses.AsValueEnumerable().Select(gateway => gateway.Address).ToArray();

    /// <summary>
    /// Parses an optional address such as a gateway: null or blank is valid and yields null, anything else must be an
    /// address of the expected family.
    /// </summary>
    internal static bool TryParseOptionalAddress(
        string? value,
        AddressFamily family,
        out IPAddress? address
    )
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!IPAddress.TryParse(value, out address) || address.AddressFamily != family)
        {
            address = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adds the commands that assign a static address to an interface for the current platform.
    /// </summary>
    /// <param name="commands">The list to add the commands to.</param>
    /// <param name="target">The name from <see cref="GetConfigurationTargetAsync"/>.</param>
    /// <param name="address">The address to assign.</param>
    /// <param name="prefixLength">The prefix length of the subnet.</param>
    /// <param name="gateway">The default gateway, or null to leave it out.</param>
    /// <param name="currentGateways">
    /// The gateways the interface uses now. On Windows an IPv6 gateway is a default route, and adding one that already
    /// exists fails, so the routes through other gateways are deleted first and an unchanged one is left alone.
    /// </param>
    public static void AddStaticIPCommands(
        List<string> commands,
        string target,
        IPAddress address,
        int prefixLength,
        IPAddress? gateway,
        IEnumerable<IPAddress>? currentGateways = null
    )
    {
        var version = address.GetIPVersion();
        if (OperatingSystem.IsWindows())
        {
            if (version == 4)
            {
                var mask = IPAddressExtensions.CidrToMask(prefixLength);
                commands.Add(
                    $"netsh interface ipv4 set address name=\"{target}\" static {address} {mask}{(gateway is null ? string.Empty : $" {gateway}")}"
                );
            }
            else
            {
                // IPv6 takes the prefix length with the address, and the gateway is a separate default route.
                commands.Add(
                    $"netsh interface ipv6 set address interface=\"{target}\" address={address}/{prefixLength}"
                );
                if (gateway is not null)
                {
                    var hasRoute = false;
                    foreach (var current in currentGateways ?? [])
                    {
                        if (current.AddressFamily != AddressFamily.InterNetworkV6)
                            continue;

                        if (current.Equals(gateway))
                        {
                            hasRoute = true;
                            continue;
                        }

                        commands.Add(
                            $"netsh interface ipv6 delete route ::/0 interface=\"{target}\" nexthop={current}"
                        );
                    }

                    if (!hasRoute)
                    {
                        commands.Add(
                            $"netsh interface ipv6 add route ::/0 interface=\"{target}\" nexthop={gateway}"
                        );
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var gatewayArgument = gateway is null ? string.Empty : $" {gateway}";
            if (version == 4)
            {
                var mask = IPAddressExtensions.CidrToMask(prefixLength);
                commands.Add(
                    $"networksetup -setmanual \"{target}\" {address} {mask}{gatewayArgument}"
                );
            }
            else
            {
                commands.Add(
                    $"networksetup -setv6manual \"{target}\" {address} {prefixLength}{gatewayArgument}"
                );
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var gatewayArgument = gateway is null ? string.Empty : $" ipv{version}.gateway {gateway}";
            commands.Add(
                $"nmcli device modify \"{target}\" ipv{version}.addresses {address}/{prefixLength}{gatewayArgument} ipv{version}.method manual"
            );
        }
    }

    /// <summary>
    /// Adds the commands that switch the address of an interface to automatic configuration.
    /// </summary>
    /// <param name="commands">The list to add the commands to.</param>
    /// <param name="target">The name from <see cref="GetConfigurationTargetAsync"/>.</param>
    /// <param name="version">The IP version, 4 or 6.</param>
    public static void AddDhcpIPCommands(List<string> commands, string target, int version)
    {
        if (OperatingSystem.IsWindows())
        {
            if (version == 4)
            {
                commands.Add($"netsh interface ipv4 set address name=\"{target}\" source=dhcp");
            }
            else
            {
                commands.AddRange([
                    $"netsh interface ipv6 set interface \"{target}\" dhcp=enabled",
                    $"netsh interface ipv6 set interface \"{target}\" routerdiscovery=enabled",
                ]);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            commands.Add(
                version == 4
                    ? $"networksetup -setdhcp \"{target}\""
                    : $"networksetup -setv6automatic \"{target}\""
            );
        }
        else if (OperatingSystem.IsLinux())
        {
            commands.AddRange([
                $"nmcli device modify \"{target}\" ipv{version}.method auto",
                $"nmcli device modify \"{target}\" ipv{version}.gateway \"\"",
                $"nmcli device modify \"{target}\" ipv{version}.addresses \"\"",
            ]);
        }
    }

    /// <summary>
    /// Adds the commands that assign static DNS servers to an interface. The IP version is the one of the primary
    /// server.
    /// </summary>
    /// <param name="commands">The list to add the commands to.</param>
    /// <param name="target">The name from <see cref="GetConfigurationTargetAsync"/>.</param>
    /// <param name="primary">The primary DNS server.</param>
    /// <param name="secondary">The secondary DNS server of the same IP version, or null.</param>
    public static void AddStaticDnsCommands(
        List<string> commands,
        string target,
        IPAddress primary,
        IPAddress? secondary
    )
    {
        var version = primary.GetIPVersion();
        if (OperatingSystem.IsWindows())
        {
            commands.Add(
                $"netsh interface ipv{version} set dnsservers name=\"{target}\" static {primary} validate=no"
            );
            if (secondary is not null)
            {
                commands.Add(
                    $"netsh interface ipv{version} add dnsservers name=\"{target}\" {secondary} validate=no"
                );
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            commands.Add(
                $"networksetup -setdnsservers \"{target}\" {primary}{(secondary is null ? string.Empty : $" {secondary}")}"
            );
        }
        else if (OperatingSystem.IsLinux())
        {
            commands.Add(
                $"nmcli device modify \"{target}\" ipv{version}.dns \"{primary}{(secondary is null ? string.Empty : $" {secondary}")}\""
            );
            commands.Add($"nmcli device modify \"{target}\" ipv{version}.ignore-auto-dns yes");
        }
    }

    /// <summary>
    /// Adds the commands that switch the DNS servers of an interface to automatic configuration.
    /// </summary>
    /// <param name="commands">The list to add the commands to.</param>
    /// <param name="target">The name from <see cref="GetConfigurationTargetAsync"/>.</param>
    /// <param name="version">The IP version, 4 or 6.</param>
    public static void AddDhcpDnsCommands(List<string> commands, string target, int version)
    {
        if (OperatingSystem.IsWindows())
        {
            commands.Add($"netsh interface ipv{version} set dnsservers name=\"{target}\" source=dhcp");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // networksetup keeps one DNS list for both IP versions, so emptying it serves either version.
            var command = $"networksetup -setdnsservers \"{target}\" empty";
            if (!commands.Contains(command))
                commands.Add(command);
        }
        else if (OperatingSystem.IsLinux())
        {
            commands.Add($"nmcli device modify \"{target}\" ipv{version}.ignore-auto-dns no");
            commands.Add($"nmcli device modify \"{target}\" ipv{version}.dns \"\"");
        }
    }

    /// <summary>
    /// Assigns a static address to the interface.
    /// </summary>
    /// <param name="ipAddress">The IPv4 or IPv6 address.</param>
    /// <param name="subnetMask">The subnet mask (<c>255.255.255.0</c>) or the prefix length (<c>24</c>).</param>
    /// <param name="gateway">The default gateway of the same IP version, or null.</param>
    public async Task SetStaticIP(string ipAddress, string subnetMask, string? gateway = null)
    {
        if (
            !IPAddress.TryParse(ipAddress, out var address)
            || !TryGetPrefixLength(subnetMask, address.AddressFamily, out var prefixLength)
            || !TryParseOptionalAddress(gateway, address.AddressFamily, out var gatewayAddress)
        )
        {
            App.Logger.ZLogError(
                $"Invalid static IP for {Interface.Name}: address '{ipAddress}', subnet mask '{subnetMask}', gateway '{gateway}'"
            );
            App.ShowToast(
                NotificationType.Error,
                Interface.Description,
                App.Localization.Format("Network.StaticIp.Invalid", Interface.Name)
            );
            return;
        }

        var toast = new ProcessXToast
        {
            Title = Interface.Description,
            SuccessGenericMessage = App.Localization.Format(
                "Network.StaticIp.Success",
                ipAddress,
                subnetMask,
                gateway
            ),
            ErrorGenericMessage = App.Localization.Format(
                "Network.StaticIp.Error",
                address.GetIPVersion(),
                Interface.Name
            ),
        };

        List<string> commands = [];
        AddStaticIPCommands(
            commands,
            await GetConfigurationTargetAsync(),
            address,
            prefixLength,
            gatewayAddress,
            CurrentGateways
        );

        if (await ProcessXExtensions.ExecuteHandled(commands, toast, RequireAdminRights))
            await Reset();
    }

    [RelayCommand]
    public async Task SetDhcpIP(IPVersion ipVersion)
    {
        int[] versions = ipVersion switch
        {
            IPVersion.V4 => [4],
            IPVersion.V6 => [6],
            IPVersion.V4_V6 => [4, 6],
            _ => throw new ArgumentOutOfRangeException(nameof(ipVersion), ipVersion, null),
        };

        var versionStr = versions.AsValueEnumerable().JoinToString('+');

        var toast = new ProcessXToast
        {
            Title = Interface.Description,
            SuccessGenericMessage = App.Localization.Format("Network.DhcpIp.Success", versionStr),
            ErrorGenericMessage = App.Localization.Format(
                "Network.DhcpIp.Error",
                versionStr,
                Interface.Name
            ),
        };

        var target = await GetConfigurationTargetAsync();
        List<string> commands = [];
        foreach (var version in versions)
        {
            AddDhcpIPCommands(commands, target, version);
        }

        if (await ProcessXExtensions.ExecuteHandled(commands, toast, RequireAdminRights))
            await Reset();
    }

    /// <summary>
    /// Assigns static DNS servers to the interface.
    /// </summary>
    /// <param name="dnsAddress1">The primary DNS server, IPv4 or IPv6.</param>
    /// <param name="dnsAddress2">The secondary DNS server of the same IP version, or null.</param>
    public async Task SetStaticDns(string dnsAddress1, string? dnsAddress2 = null)
    {
        IPAddress? secondary = null;
        if (
            !IPAddress.TryParse(dnsAddress1, out var primary)
            || (
                !string.IsNullOrWhiteSpace(dnsAddress2)
                && (
                    !IPAddress.TryParse(dnsAddress2, out secondary)
                    || secondary.AddressFamily != primary.AddressFamily
                )
            )
        )
        {
            App.Logger.ZLogError(
                $"Invalid DNS servers for {Interface.Name}: primary '{dnsAddress1}', secondary '{dnsAddress2}'"
            );
            App.ShowToast(
                NotificationType.Error,
                App.Localization.Format("Network.Dns.Title", Interface.Name),
                App.Localization["Network.Dns.Invalid"]
            );
            return;
        }

        List<string> commands = [];
        AddStaticDnsCommands(commands, await GetConfigurationTargetAsync(), primary, secondary);

        var success = await ProcessXExtensions.ExecuteHandled(
            commands,
            new ProcessXToast
            {
                Title = App.Localization.Format("Network.Dns.Title", Interface.Name),
                SuccessGenericMessage = App.Localization.Format(
                    "Network.Dns.Success",
                    primary,
                    secondary
                ),
                ErrorGenericMessage = App.Localization["Network.Dns.Error"],
            },
            RequireAdminRights
        );

        if (success)
            await Reset();
    }

    [RelayCommand]
    public async Task SetDhcpDns(IPVersion ipVersion)
    {
        var target = await GetConfigurationTargetAsync();
        List<string> commands = [];

        if (ipVersion is IPVersion.V4 or IPVersion.V4_V6)
            AddDhcpDnsCommands(commands, target, 4);
        if (ipVersion is IPVersion.V6 or IPVersion.V4_V6)
            AddDhcpDnsCommands(commands, target, 6);

        var success = await ProcessXExtensions.ExecuteHandled(
            commands,
            new ProcessXToast
            {
                Title = App.Localization.Format("Network.Dns.Title", Interface.Name),
                SuccessGenericMessage = App.Localization["Network.DhcpDns.Success"],
                ErrorGenericMessage = App.Localization["Network.DhcpDns.Error"],
            },
            RequireAdminRights
        );

        if (success)
            await Reset();
    }

    [RelayCommand]
    public async Task ExportToCsv()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                ShowOverwritePrompt = true,
                SuggestedFileName = FileUtilities.SanitizeFileName(
                    $"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.csv"
                ),
                DefaultExtension = "csv",
                FileTypeChoices = AvaloniaExtensions.FilePickerCsv,
            }
        );

        if (file is null)
            return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null)
                return;
            await using var stream = await file.OpenWriteAsync();
            await using var textWriter = new StreamWriter(stream);
            await textWriter.WriteLineAsync(
                string.Format(
                    "{0};{1};{2}",
                    nameof(GroupNameValue.Group),
                    nameof(GroupNameValue.Name),
                    nameof(GroupNameValue.Value)
                )
            );

            foreach (var nameValueGroup in TabularData)
            {
                await textWriter.WriteLineAsync(
                    string.Format(
                        "{0};{1};{2}",
                        nameValueGroup.Value.Group,
                        nameValueGroup.Value.Name,
                        nameValueGroup.Value.Value.ReplaceLineEndings("|")
                    )
                );
            }

            App.ShowToast(
                NotificationType.Success,
                App.Localization.Format("Export.Interface.Title", "CSV"),
                App.Localization.Format("Export.Interface.Success", Interface.Name, file.Name),
                new ToastActionButton(
                    App.Localization["Common.OpenFile"],
                    toast =>
                    {
                        HostSystem.OpenFile(filePath);
                    }
                ),
                new ToastActionButton(
                    App.Localization["Common.OpenFolder"],
                    toast =>
                    {
                        HostSystem.ShowFileInFileManager(filePath);
                    }
                )
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(
                e,
                App.Localization.Format("Export.Interface.Title", "CSV"),
                App.Localization["Export.Interface.Error"]
            );
        }
    }

    [RelayCommand]
    public async Task ExportToJson()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                ShowOverwritePrompt = true,
                SuggestedFileName = FileUtilities.SanitizeFileName(
                    $"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"
                ),
                DefaultExtension = "json",
                FileTypeChoices = AvaloniaExtensions.FilePickerJson,
            }
        );

        if (file is null)
            return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null)
                return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, TabularData, App.JsonSerializerOptions);
            App.ShowToast(
                NotificationType.Success,
                App.Localization.Format("Export.Interface.Title", "JSON"),
                App.Localization.Format("Export.Interface.Success", Interface.Name, file.Name),
                new ToastActionButton(
                    App.Localization["Common.OpenFile"],
                    toast =>
                    {
                        HostSystem.OpenFile(filePath);
                    }
                ),
                new ToastActionButton(
                    App.Localization["Common.OpenFolder"],
                    toast =>
                    {
                        HostSystem.ShowFileInFileManager(filePath);
                    }
                )
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(
                e,
                App.Localization.Format("Export.Interface.Title", "JSON"),
                App.Localization["Export.Interface.Error"]
            );
        }
    }

    [RelayCommand]
    public async Task ExportToIni()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                ShowOverwritePrompt = true,
                SuggestedFileName = FileUtilities.SanitizeFileName(
                    $"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.ini"
                ),
                DefaultExtension = "ini",
                FileTypeChoices = AvaloniaExtensions.FilePickerIni,
            }
        );

        if (file is null)
            return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null)
                return;
            await using var stream = await file.OpenWriteAsync();
            await using var textWriter = new StreamWriter(stream);

            foreach (var group in TabularData.GroupBy(pair => pair.Value.Group))
            {
                await textWriter.WriteLineAsync($"[{group.Key}]");
                foreach (var nameValueGroup in group)
                {
                    await textWriter.WriteLineAsync(
                        $"{nameValueGroup.Value.Name}={nameValueGroup.Value.Value.ReplaceLineEndings("|")}"
                    );
                }

                await textWriter.WriteLineAsync();
            }

            App.ShowToast(
                NotificationType.Success,
                App.Localization.Format("Export.Interface.Title", "INI"),
                App.Localization.Format("Export.Interface.Success", Interface.Name, file.Name),
                new ToastActionButton(
                    App.Localization["Common.OpenFile"],
                    toast =>
                    {
                        HostSystem.OpenFile(filePath);
                    }
                ),
                new ToastActionButton(
                    App.Localization["Common.OpenFolder"],
                    toast =>
                    {
                        HostSystem.ShowFileInFileManager(filePath);
                    }
                )
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(
                e,
                App.Localization.Format("Export.Interface.Title", "INI"),
                App.Localization["Export.Interface.Error"]
            );
        }
    }

    [RelayCommand]
    public void OpenInNewWindow()
    {
        const int margin = 15;
        var window = new GenericWindow
        {
            Title =
                $"{App.SoftwareWithVersion} - {Interface.Description} ({Interface.NetworkInterfaceType})",
            CanPin = true,
            SizeToContent = SizeToContent.Width,
            MinWidth = AppSettings.Instance.NetworkInterfaces.CardWidth + margin * 2,
            Content = new Border
            {
                Margin = new Thickness(margin),
                Child = new NetworkInterfaceFragment { DataContext = this },
            },
        };

        window.Show(App.MainWindow);
    }

    public static MaterialIconKind GetNetworkInterfaceTypeIcon(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Unknown => MaterialIconKind.QuestionMark,
            NetworkInterfaceType.Ethernet => MaterialIconKind.Ethernet,
            NetworkInterfaceType.TokenRing => MaterialIconKind.CircleOutline,
            NetworkInterfaceType.Fddi => MaterialIconKind.LaserPointer,
            NetworkInterfaceType.BasicIsdn => MaterialIconKind.PhoneClassic,
            NetworkInterfaceType.PrimaryIsdn => MaterialIconKind.PhoneVoip,
            NetworkInterfaceType.Ppp => MaterialIconKind.Link,
            NetworkInterfaceType.Loopback => MaterialIconKind.Loop,
            NetworkInterfaceType.Ethernet3Megabit => MaterialIconKind.EthernetCable,
            NetworkInterfaceType.Slip => MaterialIconKind.SerialPort,
            NetworkInterfaceType.Atm => MaterialIconKind.LanConnect,
            NetworkInterfaceType.GenericModem => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.FastEthernetT => MaterialIconKind.Ethernet,
            NetworkInterfaceType.Isdn => MaterialIconKind.PhoneVoip,
            NetworkInterfaceType.FastEthernetFx => MaterialIconKind.Ethernet,
            NetworkInterfaceType.Wireless80211 => MaterialIconKind.Wireless,
            NetworkInterfaceType.AsymmetricDsl => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.RateAdaptDsl => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.SymmetricDsl => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.VeryHighSpeedDsl => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.IPOverAtm => MaterialIconKind.LanConnect,
            NetworkInterfaceType.GigabitEthernet => MaterialIconKind.Ethernet,
            NetworkInterfaceType.Tunnel => MaterialIconKind.Tunnel,
            NetworkInterfaceType.MultiRateSymmetricDsl => MaterialIconKind.RouterNetwork,
            NetworkInterfaceType.HighPerformanceSerialBus => MaterialIconKind.SerialPort,
            NetworkInterfaceType.Wman => MaterialIconKind.Wan,
            NetworkInterfaceType.Wwanpp => MaterialIconKind.Antenna,
            NetworkInterfaceType.Wwanpp2 => MaterialIconKind.Antenna,
            (NetworkInterfaceType)53 => MaterialIconKind.VirtualPrivateNetwork, // // Proprietary virtual/internal
            _ => MaterialIconKind.QuestionMarkRhombusOutline,
        };
    }

    public static MaterialIconKind GetStatusIcon(OperationalStatus status)
    {
        return status switch
        {
            OperationalStatus.Up => MaterialIconKind.NetworkOutline,
            OperationalStatus.Down => MaterialIconKind.NetworkOffOutline,
            OperationalStatus.Testing => MaterialIconKind.NetworkPos,
            OperationalStatus.Unknown => MaterialIconKind.QuestionNetworkOutline,
            OperationalStatus.Dormant => MaterialIconKind.Sleep,
            OperationalStatus.NotPresent => MaterialIconKind.CloseNetworkOutline,
            OperationalStatus.LowerLayerDown => MaterialIconKind.CloseCircle,
            _ => MaterialIconKind.QuestionNetwork,
        };
    }

    /// <summary>
    /// Returns the active real (non-virtual, non-loopback, non-tunnel) network interfaces
    /// — typically the physical Ethernet/Wi-Fi/cellular adapters that have an IP address assigned.
    /// </summary>
    public static NetworkInterfaceBridge[] GetActiveRealInterfaces()
    {
        return NetworkInterface
            .GetAllNetworkInterfaces()
            .AsValueEnumerable()
            .Where(IsRealActiveInterface)
            .Select(@interface => new NetworkInterfaceBridge(@interface))
            .ToArray();
    }

    /// <summary>
    /// Returns true when the IP address is a link-local address (169.254.x.x).
    /// </summary>
    /// <param name="address">The IP address to check.</param>
    /// <returns>True if the address is link-local; otherwise, false.</returns>
    public static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>
    /// Returns true when the interface is operationally up, of a physical-style type,
    /// not virtual, and has at least one unicast IP address assigned.
    /// </summary>
    public static bool IsRealActiveInterface(NetworkInterface @interface)
    {
        if (@interface.OperationalStatus != OperationalStatus.Up)
            return false;

        if (
            @interface.NetworkInterfaceType
            is not (
                NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2
            )
        )
            return false;

        if (IsVirtualInterface(@interface))
            return false;

        return @interface.GetIPProperties().UnicastAddresses.Count > 0;
    }

    /// <summary>
    /// Returns true when the interface name/description matches one of the well-known
    /// virtual adapter heuristics (Hyper-V, VMware, VirtualBox, WAN Miniport, filters, …).
    /// </summary>
    public static bool IsVirtualInterface(NetworkInterface @interface)
    {
        return @interface.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.StartsWith("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.StartsWith("VMware", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.StartsWith("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || @interface.Name.Contains("QoS", StringComparison.OrdinalIgnoreCase)
            || @interface.Description.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || @interface.Description.Contains(" Virtual ", StringComparison.OrdinalIgnoreCase)
            || @interface.Description.Contains(" Debug ", StringComparison.OrdinalIgnoreCase);
    }
}
