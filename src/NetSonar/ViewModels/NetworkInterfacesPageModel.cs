using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NetSonar.Avalonia.Models;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.SystemOS;
using ObservableCollections;
using StageKit.Primitives.System;
using ZLinq;
using ZLogger;

namespace NetSonar.Avalonia.ViewModels;

public partial class NetworkInterfacesPageModel : PageViewModelBase
{
    public override int Index => 1;
    public override string DisplayName => App.Localization["Navigation.Interfaces"];
    public override MaterialIconKind Icon => MaterialIconKind.Network;

    private readonly DispatcherTimer _timer = new();
    private bool _isRefreshing;

    public ISynchronizedView<
        KeyValuePair<string, NetworkInterfaceBridge>,
        NetworkInterfaceBridge
    > InterfacesView { get; }

    public ObservableDictionary<string, NetworkInterfaceBridge> Interfaces { get; } = [];

    public NotifyCollectionChangedSynchronizedViewList<NetworkInterfaceBridge> InterfacesViewCollection { get; }

    public NotifyCollectionChangedSynchronizedViewList<EnumViewFilter> InterfaceTypeFiltersCollection { get; }
    public NotifyCollectionChangedSynchronizedViewList<EnumViewFilter> InterfaceStatusFiltersCollection { get; }

    [ObservableProperty]
    public partial int InterfaceCount { get; private set; }

    [ObservableProperty]
    public partial int OfflineInterfaceCount { get; private set; }

    [ObservableProperty]
    public partial int OnlineInterfaceCount { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    public NetworkInterfacesPageModel()
    {
        InterfacesView = Interfaces.CreateView(pair => pair.Value);
        InterfacesViewCollection = InterfacesView.ToNotifyCollectionChanged(
            SynchronizationContextCollectionEventDispatcher.Current
        );

        var interfaceTypeFilters = AppSettings.NetworkInterfaces.FilterTypes.CreateView(pair =>
            pair.Value
        );
        InterfaceTypeFiltersCollection = interfaceTypeFilters.ToNotifyCollectionChanged(
            SynchronizationContextCollectionEventDispatcher.Current
        );

        var interfaceStatusFilters = AppSettings.NetworkInterfaces.FilterStatus.CreateView(pair =>
            pair.Value
        );
        InterfaceStatusFiltersCollection = interfaceStatusFilters.ToNotifyCollectionChanged(
            SynchronizationContextCollectionEventDispatcher.Current
        );

        AppSettings.NetworkInterfaces.PropertyChanged += NetworkInterfacesOnPropertyChanged;

        foreach (var filter in InterfaceTypeFiltersCollection)
        {
            filter.PropertyChanged += Filter_PropertyChanged;
        }

        foreach (var filter in InterfaceStatusFiltersCollection)
        {
            filter.PropertyChanged += Filter_PropertyChanged;
        }

        _timer.Tick += TimerTick;
    }

    protected internal override async void OnInitialized()
    {
        _timer.Interval = TimeSpan.FromSeconds(AppSettings.NetworkInterfaces.RefreshEverySeconds);
        _timer.IsEnabled = AppSettings.NetworkInterfaces.AutoRefresh;
        await Refresh();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterText))
        {
            ReAttachFilters();
        }

        base.OnPropertyChanged(e);
    }

    private void NetworkInterfacesOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null)
            return;
        if (e.PropertyName == nameof(NetworkInterfacesSettings.AutoRefresh))
        {
            _timer.IsEnabled = AppSettings.NetworkInterfaces.AutoRefresh;
        }
        else if (e.PropertyName == nameof(NetworkInterfacesSettings.RefreshEverySeconds))
        {
            _timer.Interval = TimeSpan.FromSeconds(
                AppSettings.NetworkInterfaces.RefreshEverySeconds
            );
        }
        else if (e.PropertyName.Contains("Filter"))
            ReAttachFilters();
    }

    private void Filter_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnumViewFilter.Include))
            ReAttachFilters();
    }

    private async void TimerTick(object? sender, EventArgs e)
    {
        await Refresh();
    }

    /// <summary>
    /// Reads the interfaces on a background thread and applies the result on the UI thread, keeping the existing
    /// cards. A refresh that is requested while another one is running is skipped.
    /// </summary>
    [RelayCommand]
    public async Task Refresh()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        try
        {
            var (networks, snapshots) = await Task.Run(CaptureInterfaces);
            ApplyInterfaces(networks, snapshots);
        }
        catch (Exception ex)
        {
            App.Logger.ZLogError(ex, $"Unable to refresh the network interfaces");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static (NetworkInterface[] Networks, NetworkInterfaceSnapshot?[] Snapshots) CaptureInterfaces()
    {
        var networks = NetworkInterface.GetAllNetworkInterfaces();
        var snapshots = new NetworkInterfaceSnapshot?[networks.Length];
        for (var i = 0; i < networks.Length; i++)
        {
            try
            {
                snapshots[i] = NetworkInterfaceBridge.Capture(networks[i]);
            }
            catch (Exception ex)
            {
                // A single interface that cannot be read must not stop the others from refreshing.
                App.Logger.ZLogWarning(ex, $"Unable to read the network interface {networks[i].Name}");
            }
        }

        return (networks, snapshots);
    }

    private void ApplyInterfaces(NetworkInterface[] networks, NetworkInterfaceSnapshot?[] snapshots)
    {
        var keys = new HashSet<string>(networks.Length);
        var reattachFilters = false;
        int offlineCount = 0;
        int onlineCount = 0;
        for (var i = 0; i < networks.Length; i++)
        {
            var adapter = networks[i];
            var snapshot = snapshots[i];
            keys.Add(adapter.Id);

            if (Interfaces.TryGetValue(adapter.Id, out var networkInterface))
            {
                if (snapshot is not null)
                {
                    var wasIncluded = MatchesCurrentFilters(networkInterface);
                    networkInterface.Apply(snapshot);
                    reattachFilters |= wasIncluded != MatchesCurrentFilters(networkInterface);
                }
            }
            else if (snapshot is not null)
            {
                networkInterface = new NetworkInterfaceBridge(snapshot);
                Interfaces.Add(adapter.Id, networkInterface);
                reattachFilters = true;
            }
            else
            {
                continue;
            }

            if (networkInterface.IsActive)
                onlineCount++;
            else
                offlineCount++;
        }

        // Remove gone interfaces
        using (
            var keysToRemove = Interfaces
                .AsValueEnumerable()
                .Where(adapter => !keys.Contains(adapter.Key))
                .Select(adapter => adapter.Key)
                .ToArrayPool()
        )
        {
            foreach (var key in keysToRemove.Span)
            {
                if (!Interfaces.Remove(key, out var networkInterface))
                    continue;
                networkInterface.Dispose();
            }
        }

        InterfaceCount = networks.Length;
        OfflineInterfaceCount = offlineCount;
        OnlineInterfaceCount = onlineCount;
        if (reattachFilters)
            ReAttachFilters();
    }

    [RelayCommand]
    public void ClearFilters()
    {
        FilterText = string.Empty;
    }

    [RelayCommand]
    public void AttachStatusOnlineFilter()
    {
        FilterText = "status:active";
    }

    [RelayCommand]
    public void AttachStatusOfflineFilter()
    {
        FilterText = "status:inactive";
    }

    [RelayCommand]
    public void ReAttachFilters()
    {
        InterfacesView.AttachFilter(pair => MatchesCurrentFilters(pair.Value));
    }

    private bool MatchesCurrentFilters(NetworkInterfaceBridge networkInterface)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            if (AppSettings.NetworkInterfaces.EnableFilterTypes)
            {
                if (
                    AppSettings.NetworkInterfaces.FilterTypes.TryGetValue(
                        networkInterface.Interface.NetworkInterfaceType,
                        out var filter
                    )
                )
                {
                    if (!filter.Include)
                        return false;
                }
            }

            if (AppSettings.NetworkInterfaces.EnableFilterStatus)
            {
                if (
                    AppSettings.NetworkInterfaces.FilterStatus.TryGetValue(
                        networkInterface.Interface.OperationalStatus,
                        out var filter
                    )
                )
                {
                    if (!filter.Include)
                        return false;
                }
            }

            if (AppSettings.NetworkInterfaces.EnableFilterOthers)
            {
                if (
                    AppSettings.NetworkInterfaces.FilterByVirtual.HasValue
                    && networkInterface.IsVirtual
                        != AppSettings.NetworkInterfaces.FilterByVirtual.Value
                )
                    return false;
                if (
                    AppSettings.NetworkInterfaces.FilterByHavePhysicalAddress.HasValue
                    && networkInterface.HavePhysicalAddress
                        != AppSettings.NetworkInterfaces.FilterByHavePhysicalAddress.Value
                )
                    return false;
                if (
                    AppSettings.NetworkInterfaces.FilterByHaveIPAddress.HasValue
                    && networkInterface.HaveIPAddress
                        != AppSettings.NetworkInterfaces.FilterByHaveIPAddress.Value
                )
                    return false;
                if (
                    AppSettings.NetworkInterfaces.FilterByIsTransmittingData.HasValue
                    && networkInterface.IsTransmittingData
                        != AppSettings.NetworkInterfaces.FilterByIsTransmittingData.Value
                )
                    return false;
            }
        }
        else
        {
            var splitText = FilterText.Split(
                ' ',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );
            foreach (var word in splitText)
            {
                if (
                    word.Equals("status:up", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("status:active", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("status:online", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return networkInterface.IsActive;
                }

                if (
                    word.Equals("status:down", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("status:inactive", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("status:offline", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return !networkInterface.IsActive;
                }

                if (
                    networkInterface.Interface.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
                if (
                    networkInterface.Interface.Name.Contains(
                        word,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return true;
                if (
                    networkInterface.Interface.Description.Contains(
                        word,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return true;
                if (
                    networkInterface
                        .Interface.OperationalStatus.ToString()
                        .Equals(word, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
                if (
                    networkInterface
                        .Interface.NetworkInterfaceType.ToString()
                        .Equals(word, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
                if (
                    networkInterface
                        .Properties.UnicastAddresses.AsValueEnumerable()
                        .Any(address =>
                            address
                                .Address.ToString()
                                .Contains(word, StringComparison.OrdinalIgnoreCase)
                        )
                )
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    [RelayCommand]
    public static void OpenSystemNetworkManager()
    {
        if (OperatingSystem.IsWindows())
        {
            // ReSharper disable once StringLiteralTypo
            ProcessHelper.StartShell("ncpa.cpl");
        }
        else if (OperatingSystem.IsLinux())
        {
            ProcessHelper.StartShell("nm-connection-editor");
        }
        else if (OperatingSystem.IsMacOS())
        {
            ProcessHelper.StartShell("open \"x-apple.systempreferences:com.apple.Network-Settings.extension\"");
        }
    }
}
