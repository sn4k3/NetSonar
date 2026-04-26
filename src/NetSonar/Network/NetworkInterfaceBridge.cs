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
using NetSonar.Avalonia.SystemOS;
using NetSonar.Avalonia.ViewModels.Dialogs;
using NetSonar.Avalonia.Views;
using NetSonar.Avalonia.Views.Fragments;
using ObservableCollections;
using SukiUI.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using ZLinq;
using ZLogger;

namespace NetSonar.Avalonia.Network;

public partial class NetworkInterfaceBridge : ObservableObject, IDisposable
{
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
    public bool IsDisabled => Interface.OperationalStatus is OperationalStatus.Down && !HaveIPAddress;

    /// <summary>
    /// Gets a value that indicates whether the network interface is enabled.
    /// </summary>
    public bool IsEnabled => !IsDisabled;

    /// <summary>
    /// Gets a value that indicates whether the network interface is active.
    /// </summary>
    public bool IsActive => Interface.OperationalStatus is OperationalStatus.Up or OperationalStatus.Testing;

    /// <summary>
    /// Gets a value that indicates whether the network interface has a physical address.
    /// </summary>
    public bool HavePhysicalAddress => Interface.GetPhysicalAddress().GetAddressBytes().Length > 0;

    /// <summary>
    /// Gets the physical address of this network interface
    /// </summary>
    /// <returns>The interface's physical address.</returns>
    public string PhysicalAddressString => Interface.GetPhysicalAddress().GetAddressBytes()
        .AsValueEnumerable().Select(x => x.ToString("X2")).JoinToString(':');

    /// <summary>
    /// Gets the IP properties for this network interface.
    /// </summary>
    /// <returns>The interface's IP properties.</returns>
    public IPInterfaceProperties Properties => Interface.GetIPProperties();

    /// <summary>Provides Internet Protocol version 4 (IPv4) configuration data for this network interface.</summary>
    /// <exception cref="T:System.Net.NetworkInformation.NetworkInformationException">The interface does not support the IPv4 protocol.</exception>
    /// <returns>An <see cref="T:System.Net.NetworkInformation.IPv4InterfaceProperties" /> object that contains IPv4 configuration data.</returns>
    public IPv4InterfaceProperties IPv4Properties => Properties.GetIPv4Properties();

    /// <summary>Provides Internet Protocol version 6 (IPv6) configuration data for this network interface.</summary>
    /// <exception cref="T:System.Net.NetworkInformation.NetworkInformationException">The interface does not support the IPv6 protocol.</exception>
    /// <returns>An <see cref="T:System.Net.NetworkInformation.IPv6InterfaceProperties" /> object that contains IPv6 configuration data.</returns>
    public IPv6InterfaceProperties IPv6Properties => Properties.GetIPv6Properties();

    /// <summary>
    /// Provides Internet Protocol (IP) statistical data for this network interface.
    /// </summary>
    /// <returns>The interface's IP statistics.</returns>
    public IPInterfaceStatistics Statistics => Interface.GetIPStatistics();

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
    public string IPAddressesStr => Properties.UnicastAddresses
        .AsValueEnumerable()
        .Select(ip => ip.Address.ToString())
        .JoinToString('\n');

    /// <summary>
    /// Gets the IPv4 address information that is associated with the current network interface.
    /// </summary>
    public IPAddress? IPv4Address => Properties.UnicastAddresses
        .AsValueEnumerable()
        .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(ip => ip.Address).LastOrDefault();

    /// <summary>
    /// Gets the IPv6 address information that is associated with the current network interface.
    /// </summary>
    public IPAddress? IPv6Address => Properties.UnicastAddresses
        .AsValueEnumerable()
        .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
        .Select(ip => ip.Address).LastOrDefault();

    /// <summary>
    /// Gets a value that indicates whether the network interface supports Internet Protocol version 4 (IPv4).
    /// </summary>
    public bool SupportsIPv4 => Interface.Supports(NetworkInterfaceComponent.IPv4);

    /// <summary>
    /// Gets a value that indicates whether the network interface supports Internet Protocol version 6 (IPv6).
    /// </summary>
    public bool SupportsIPv6 => Interface.Supports(NetworkInterfaceComponent.IPv6);

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
            if (!SupportsIPv4) return false;
            if (OperatingSystem.IsWindows()) return IPv4Properties.IsDhcpEnabled;
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
    public bool IsTransmittingData => IsActive && BytesReceived > 0 || BytesSent > 0;

    /// <summary>
    /// Gets the number of bytes received on the interface.
    /// </summary>
    public long BytesReceived
    {
        get
        {
            if (Interface.OperationalStatus != OperationalStatus.Dormant)
            {
                try
                {
                    return Statistics.BytesReceived;
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Gets the number of bytes sent on the interface.
    /// </summary>
    public long BytesSent
    {
        get
        {
            if (Interface.OperationalStatus != OperationalStatus.Dormant)
            {
                try
                {
                    return Statistics.BytesSent;
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
    }


    public ObservableDictionary<string, GroupNameValue> TabularData { get; } = [];

    public INotifyCollectionChangedSynchronizedViewList<KeyValuePair<string, GroupNameValue>> TabularDataView { get; private set; }

    public DataGridCollectionView TabularDataGroupView { get; init; }

    public MaterialIconKind TypeIcon => GetNetworkInterfaceTypeIcon(Interface.NetworkInterfaceType);

    public MaterialIconKind StatusIcon => GetStatusIcon(Interface.OperationalStatus);

    [SetsRequiredMembers]
    public NetworkInterfaceBridge(NetworkInterface network)
    {
        Interface = network;

        TabularDataView = TabularData.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current);
        TabularDataGroupView = new DataGridCollectionView(TabularDataView);
        TabularDataGroupView.GroupDescriptions.Add(new DataGridPathGroupDescription($"Value.{nameof(GroupNameValue.Group)}"));
    }

    ~NetworkInterfaceBridge()
    {
        Dispose();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Interface))
        {
            Refresh();
        }
        base.OnPropertyChanged(e);
    }

    /// <summary>
    /// Resets the network interface to its current state.
    /// </summary>
    [RelayCommand]
    public void Reset()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .AsValueEnumerable()
            .FirstOrDefault(adapter => adapter.Id == Interface.Id);

        if (adapter is null) return;
        Interface = adapter;
    }

    /// <summary>
    /// Refreshes the network interface data.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        var key = $"{nameof(Interface)}.{nameof(PhysicalAddress)}";
        if (TabularData.TryGetValue(key, out var macValueGroup)) macValueGroup.Value = PhysicalAddressString;
        else TabularData.Add(key, new GroupNameValue(nameof(PhysicalAddress), PhysicalAddressString, nameof(Interface)));
        TabularData.TryAdd($"{nameof(Interface)}.{nameof(PhysicalAddress)}", new GroupNameValue(nameof(PhysicalAddress), PhysicalAddressString, "Interface"));
        ReflectionExtensions.BuildTabularData(TabularData, Interface, nameof(Interface));
        ReflectionExtensions.BuildTabularData(TabularData, Properties, nameof(Properties));
        if (Interface.Supports(NetworkInterfaceComponent.IPv4))
        {
            ReflectionExtensions.BuildTabularData(TabularData, IPv4Properties, "IPv4 Properties");
        }

        if (Interface.Supports(NetworkInterfaceComponent.IPv6))
        {
            ReflectionExtensions.BuildTabularData(TabularData, IPv6Properties, "IPv6 Properties");
        }

        if (Interface.OperationalStatus != OperationalStatus.Dormant)
        {
            try
            {
                ReflectionExtensions.BuildTabularData(TabularData, Statistics, nameof(Statistics));
            }
            catch
            {
                // ignored
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    public async Task Disable()
    {
        if (IsDisabled) return;

        var toast = new ProcessXToast
        {
            Title = $"Disable {Interface.Description}",
            SuccessGenericMessage = $"Interface \"{Interface.Name}\" successfully disabled.",
            ErrorGenericMessage = $"Unable to disable \"{Interface.Name}\"."
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled($"netsh interface set interface \"{Interface.Name}\" disable", toast, true);
        }
        else if (OperatingSystem.IsMacOS())
        {
            success = await ProcessXExtensions.ExecuteHandled($"ifconfig \"{Interface.Name}\" down", toast, true);
        }
        else
        {
            success = await ProcessXExtensions.ExecuteHandled($"ip link set \"{Interface.Name}\" down", toast, true);
        }

        if (success) Reset();
    }

    [RelayCommand(CanExecute = nameof(IsDisabled))]
    public async Task Enable()
    {
        if (IsEnabled) return;

        var toast = new ProcessXToast
        {
            Title = $"Enable {Interface.Description}",
            SuccessGenericMessage = $"Interface \"{Interface.Name}\" successfully enabled.",
            ErrorGenericMessage = $"Unable to enable \"{Interface.Name}\"."
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled($"netsh interface set interface \"{Interface.Name}\" enable", toast, true);
        }
        else if (OperatingSystem.IsMacOS())
        {
            success = await ProcessXExtensions.ExecuteHandled($"ifconfig \"{Interface.Name}\" up", toast, true);
        }
        else
        {
            success = await ProcessXExtensions.ExecuteHandled($"nmcli device up \"{Interface.Name}\"", toast);
        }

        if (success) Reset();
    }


    [RelayCommand(CanExecute = nameof(IsActive))]
    public async Task ReleaseIp()
    {
        if (!IsActive || !HaveIPAddress) return;

        var toast = new ProcessXToast
        {
            Title = $"Release {Interface.Description}",
            SuccessGenericMessage = "IP address successfully released.",
            ErrorGenericMessage = $"Unable to release \"{Interface.Name}\"."
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled($"ipconfig /release \"{Interface.Name}\"", toast);
        }
        else
        {
            success = await ProcessXExtensions.ExecuteHandled($"nmcli connection down \"{Interface.Name}\"", toast);
        }

        if (success) Reset();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    public async Task RenewIp()
    {
        if (!IsActive) return;

        var toast = new ProcessXToast
        {
            Title = $"Renew {Interface.Description}",
            SuccessGenericMessage = "IP address successfully renewed.",
            ErrorGenericMessage = $"Unable to renew \"{Interface.Name}\"."
        };

        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = await ProcessXExtensions.ExecuteHandled(
            [
                        $"ipconfig /release \"{Interface.Name}\"",
                        $"ipconfig /renew \"{Interface.Name}\""
            ], toast);
        }
        else if (OperatingSystem.IsMacOS())
        {
            success = await ProcessXExtensions.ExecuteHandled(
            [
                $"ifconfig \"{Interface.Name}\" down",
                $"ifconfig \"{Interface.Name}\" up"
            ], toast);
        }
        else
        {
            success = await ProcessXExtensions.ExecuteHandled(
            [
                $"nmcli device disconnect \"{Interface.Name}\"",
                $"nmcli device connect \"{Interface.Name}\""
            ], toast);
        }

        if (success) Reset();
    }

    [RelayCommand]
    public void ShowManualAssignmentIPDialog()
    {
        var dialog = App.DialogManager
            .CreateDialog()
            .WithViewModel(dialog => new SetInterfaceIPDialogModel(dialog, this));
        dialog.TryShow();
    }

    public async Task SetStaticIP(string ipAddress, string subnetMask, string? gateway = null)
    {
        if (!IPAddress.TryParse(ipAddress, out var ipaddress))
        {
            App.Logger.ZLogError($"Invalid IP address format: {ipaddress}");
            return;
        }
        if(!IPAddress.TryParse(subnetMask, out var mask))
        {
            App.Logger.ZLogError($"Invalid subnet mask format: {subnetMask}");
            return;
        }

        if (gateway is not null && !IPAddress.TryParse(gateway, out _))
        {
            App.Logger.ZLogError($"Invalid gateway address format: {gateway}");
            return;
        }

        bool isIPv4 = ipaddress.AddressFamily == AddressFamily.InterNetwork;
        int version = isIPv4 ? 4 : 6;

        var toast = new ProcessXToast
        {
            Title = Interface.Description,
            SuccessGenericMessage = $"IP address successfully set to {ipAddress} {subnetMask} {gateway}",
            ErrorGenericMessage = $"Unable set static IPv{version} for \"{Interface.Name}\"."
        };

        if (OperatingSystem.IsWindows())
        {
            var ipVersion = isIPv4 ? "ipv4" : "ipv6";
            await ProcessXExtensions.ExecuteHandled($"netsh interface {ipVersion} set address name=\"{Interface.Name}\" static {ipAddress} {subnetMask} {gateway}", toast, true);
        }
        else if (OperatingSystem.IsLinux())
        {

            var maskCidr = IPAddressExtensions.MaskToCidr(mask);
            await ProcessXExtensions.ExecuteHandled($"nmcli device modify \"{Interface.Name}\" ipv4.addresses {IPv4Address}/{maskCidr} ipv4.gateway \"{gateway}\" ipv4.method manual", toast);

        }
    }

    [RelayCommand]
    public async Task SetDhcpIP(IPVersion ipVersion)
    {
        int[] versions = ipVersion switch
        {
            IPVersion.V4 => [4],
            IPVersion.V6 => [6],
            IPVersion.V4_V6 => [4, 6],
            _ => throw new ArgumentOutOfRangeException(nameof(ipVersion), ipVersion, null)
        };

        var versionStr = versions.AsValueEnumerable().JoinToString('+');

        var toast = new ProcessXToast
        {
            Title = Interface.Description,
            SuccessGenericMessage = $"IP address successfully set to DHCPv{versionStr}",
            ErrorGenericMessage = $"Unable set DHCPv{versionStr} for \"{Interface.Name}\"."
        };

        List<string> commands = [];
        var requireAdminRights = false;

        foreach (var version in versions)
        {
            if (OperatingSystem.IsWindows())
            {
                requireAdminRights = true;
                if (version == 4) commands.Add($"netsh interface ipv{version} set address name=\"{Interface.Name}\" source=dhcp");
                else if (version == 6)
                {
                    commands.AddRange([
                        $"netsh interface ipv6 set interface \"{Interface.Name}\" dhcp=enabled",
                        $"netsh interface ipv6 set interface \"{Interface.Name}\" routerdiscovery=enabled"
                    ]);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                requireAdminRights = true;
                if (version == 4) commands.Add($"networksetup -setdhcp \"{Interface.Name}\"");
                else if (version == 6) commands.Add($"networksetup -setv6automatic \"{Interface.Name}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                commands.AddRange([
                    $"nmcli device modify \"{Interface.Name}\" ipv{version}.method auto",
                    $"nmcli device modify \"{Interface.Name}\" ipv{version}.gateway \"\"",
                    $"nmcli device modify \"{Interface.Name}\" ipv{version}.address \"\""
                ]);

            }
        }

        await ProcessXExtensions.ExecuteHandled(commands, toast, requireAdminRights);

        Reset();

    }


    public async Task SetStaticDns(string dnsAddress1, string? dnsAddress2 = null)
    {
        var ipaddress1 = IPAddress.Parse(dnsAddress1);
        var ipVersion = ipaddress1.GetIPVersion();


        if (dnsAddress2 is not null)
        {
            _ = IPAddress.Parse(dnsAddress2);
        }

        var commands = new List<string>();
        bool requireAdminRights = false;

        if (OperatingSystem.IsWindows())
        {
            commands.Add($"netsh interface ipv{ipVersion} set dnsservers name=\"{Interface.Name}\" static {dnsAddress1} validate=no");
            requireAdminRights = true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            commands.Add($"networksetup -setdnsservers \"{Interface.Name}\" {dnsAddress1} {dnsAddress2}");
            requireAdminRights = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            commands.Add($"nmcli device modify \"{Interface.Name}\" ipv{ipVersion}.dns \"{dnsAddress1} {dnsAddress2}\"");
            commands.Add($"nmcli device modify \"{Interface.Name}\" ipv{ipVersion}.ignore-auto-dns yes");
        }


        if (dnsAddress2 is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                commands.Add($"netsh interface ipv4 add dnsservers name=\"{Interface.Name}\" {dnsAddress2} validate=no");
            }
        }

        await ProcessXExtensions.ExecuteHandled(commands, new ProcessXToast()
        {
            Title = $"DNS assignment for \"{Interface.Name}\"",
            SuccessGenericMessage = $"DNS servers successfully assigned: {dnsAddress1} {dnsAddress2}",
            ErrorGenericMessage = "Unable to assign DNS.",
        }, requireAdminRights);
    }


    [RelayCommand]
    public async Task SetDhcpDns(IPVersion ipVersion)
    {
        var commands = new List<string>();
        bool requireAdminRights = false;

        if (OperatingSystem.IsWindows())
        {
            if (ipVersion is IPVersion.V4 or IPVersion.V4_V6)
            {
                commands.Add($"netsh interface ipv4 set dnsservers name=\"{Interface.Name}\" source=dhcp");
            }
            if (ipVersion is IPVersion.V6 or IPVersion.V4_V6)
            {
                commands.Add($"netsh interface ipv6 set dnsservers name=\"{Interface.Name}\" source=dhcp");
            }

            requireAdminRights = true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (ipVersion is IPVersion.V4 or IPVersion.V4_V6)
            {
                commands.Add($"networksetup -setdnsservers \"{Interface.Name}\" empty");
            }

            requireAdminRights = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (ipVersion is IPVersion.V4 or IPVersion.V4_V6)
            {
                commands.Add($"nmcli device modify \"{Interface.Name}\" ipv4.ignore-auto-dns no");
                commands.Add($"nmcli device modify \"{Interface.Name}\" ipv4.dns \"\"");
            }
            if (ipVersion is IPVersion.V6 or IPVersion.V4_V6)
            {
                commands.Add($"nmcli device modify \"{Interface.Name}\" ipv6.ignore-auto-dns no");
                commands.Add($"nmcli device modify \"{Interface.Name}\" ipv6.dns \"\"");
            }
        }

        await ProcessXExtensions.ExecuteHandled(commands, new ProcessXToast()
        {
            Title = $"DNS assignment for \"{Interface.Name}\"",
            SuccessGenericMessage = "DHCP DNS successfully assigned",
            ErrorGenericMessage = "Unable to assign DHCP DNS.",
        }, requireAdminRights);
    }

    [RelayCommand]
    public async Task ExportToCsv()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.csv"),
            DefaultExtension = "csv",
            FileTypeChoices = AvaloniaExtensions.FilePickerCsv
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var textWriter = new StreamWriter(stream);
            await textWriter.WriteLineAsync(string.Format("{0};{1};{2}",
                nameof(GroupNameValue.Group),
                nameof(GroupNameValue.Name),
                nameof(GroupNameValue.Value)
            ));

            foreach (var nameValueGroup in TabularData)
            {
                await textWriter.WriteLineAsync(string.Format("{0};{1};{2}",
                    nameValueGroup.Value.Group,
                    nameValueGroup.Value.Name,
                    StringExtensions.ReplaceLinebreak(nameValueGroup.Value.Value, "|")
                ));
            }

            App.ShowToast(NotificationType.Success,
                "Export interface to CSV",
                $"The interface \"{Interface.Name}\" were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
                );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export interface to CSV", "Error while trying to export the interface:");
        }
    }

    [RelayCommand]
    public async Task ExportToJson()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, TabularData, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                "Export interface to JSON",
                $"The interface \"{Interface.Name}\" were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
                );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export interface to JSON", "Error while trying to export the interface:");
        }
    }

    [RelayCommand]
    public async Task ExportToIni()
    {
        using var file = await App.TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"{Interface.Name}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.ini"),
            DefaultExtension = "ini",
            FileTypeChoices = AvaloniaExtensions.FilePickerIni
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var textWriter = new StreamWriter(stream);

            foreach (var group in TabularData.GroupBy(pair => pair.Value.Group))
            {

                await textWriter.WriteLineAsync($"[{group.Key}]");
                foreach (var nameValueGroup in group)
                {
                    await textWriter.WriteLineAsync($"{nameValueGroup.Value.Name}={StringExtensions.ReplaceLinebreak(nameValueGroup.Value.Value, "|")}");
                }
                await textWriter.WriteLineAsync();
            }

            App.ShowToast(NotificationType.Success,
                "Export interface to INI",
                $"The interface \"{Interface.Name}\" were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
                );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export interface to INI", "Error while trying to export the interface:");
        }
    }

    [RelayCommand]
    public void OpenInNewWindow()
    {
        const int margin = 15;
        var window = new GenericWindow
        {
            Title = $"{App.SoftwareWithVersion} - {Interface.Description} ({Interface.NetworkInterfaceType})",
            CanPin = true,
            SizeToContent = SizeToContent.Width,
            MinWidth = AppSettings.Instance.NetworkInterfaces.CardWidth + margin * 2,
            Content = new Border
            {
                Margin = new Thickness(margin),
                Child = new NetworkInterfaceFragment
                {
                    DataContext = this
                }
            },
        };

        window.Show(App.MainWindow);
    }

    private void Dispose(bool disposing)
    {
        if (IsDisposed) return;

        if (disposing)
        {
            TabularDataView.Dispose();
        }

        // Free unmanaged resources (unmanaged objects) and override a finalizer below.
        // Set large fields to null.

        IsDisposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
            _ => MaterialIconKind.QuestionMarkRhombusOutline
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
            _ => MaterialIconKind.QuestionNetwork
        };
    }

    public static NetworkInterfaceBridge? PrimaryInterface
    {
        get
        {
            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                .AsValueEnumerable()
                .FirstOrDefault(@interface =>
                @interface.NetworkInterfaceType
                    is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211
                && @interface.OperationalStatus == OperationalStatus.Up
                && !@interface.Name.StartsWith("vEthernet"));
            return networkInterface is null ? null : new NetworkInterfaceBridge(networkInterface);
        }

    }

    /// <summary>
    /// Returns the active real (non-virtual, non-loopback, non-tunnel) network interfaces
    /// — typically the physical Ethernet/Wi-Fi/cellular adapters that have an IP address assigned.
    /// </summary>
    public static NetworkInterfaceBridge[] GetActiveRealInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .AsValueEnumerable()
            .Where(IsRealActiveInterface)
            .Select(@interface => new NetworkInterfaceBridge(@interface))
            .ToArray();
    }

    /// <summary>
    /// Returns true when the interface is operationally up, of a physical-style type,
    /// not virtual, and has at least one unicast IP address assigned.
    /// </summary>
    public static bool IsRealActiveInterface(NetworkInterface @interface)
    {
        if (@interface.OperationalStatus != OperationalStatus.Up) return false;

        if (@interface.NetworkInterfaceType is not (
                NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2))
            return false;

        if (IsVirtualInterface(@interface)) return false;

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