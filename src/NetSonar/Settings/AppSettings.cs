using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Models;
using NetSonar.Avalonia.Network;
using ObservableCollections;
using StageKit;
using SukiUI.Enums;

namespace NetSonar.Avalonia.Settings;

public partial class PingServicesSettings : SubSettings
{
    public enum PingServicesGroupBy
    {
        [Description("None")] None,

        [Description("Protocol type")] ProtocolType,

        [Description("Status")] LastStatus,

        [Description("Group")] Group
    }

    [ObservableProperty] public partial PingServicesGroupBy GridGroupBy { get; set; } = PingServicesGroupBy.Group;

    public Dictionary<string, int> GridColumnOrder { get; init; } = [];


    public double DefaultPingEverySeconds
    {
        get;
        set => SetProperty(ref field,
            Math.Clamp(value, PingableService.MinPingEverySeconds, PingableService.MaxPingEverySeconds));
    } = PingableService.DefaultPingEverySeconds;

    public double DefaultTimeoutSeconds
    {
        get;
        set => SetProperty(ref field,
            Math.Clamp(value, PingableService.MinTimeoutSeconds, PingableService.MaxTimeoutSeconds));
    } = PingableService.DefaultTimeoutSeconds;

    public int DefaultBufferSize
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, PingableService.MinBufferSize, PingableService.MaxBufferSize));
    } = PingableService.DefaultBufferSize;

    public byte DefaultTtl
    {
        get;
        set => SetProperty(ref field, Math.Max((byte)1, value));
    } = PingableService.DefaultTtl;

    [ObservableProperty] public partial bool DefaultDontFragment { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of ping replies to keep in the list.
    /// </summary>
    public int MaxRepliesCache
    {
        get;
        set => SetProperty(ref field, Math.Max(0, value));
    } = 1_000;

    /// <summary>
    /// Gets or sets the maximum number of ping replies to show in the graph.
    /// </summary>
    public int MaxRepliesGraphCache
    {
        get;
        set => SetProperty(ref field, Math.Max(0, value));
    } = 100;

    [ObservableProperty] public partial bool ResilientReplies { get; set; }
}

public partial class NetworkInterfacesSettings : SubSettings
{
    public const int RefreshEveryMinSeconds = 1;
    public const int RefreshEveryDefaultSeconds = 5;
    public const int RefreshEveryMaxSeconds = 3600;


    public const int CardMinWidth = 400;
    public const int CardDefaultWidth = 500;
    public const int CardMaxWidth = 1000;

    public const int CardMinHeight = 400;
    public const int CardDefaultHeight = 600;
    public const int CardMaxHeight = 2000;


    public NetworkInterfacesSettings()
    {
    }

    [ObservableProperty] public partial bool AutoRefresh { get; set; } = true;

    public int RefreshEverySeconds
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, RefreshEveryMinSeconds, RefreshEveryMaxSeconds));
    } = RefreshEveryDefaultSeconds;

    public int CardWidth
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, CardMinWidth, CardMaxWidth));
    } = CardDefaultWidth;

    public int CardHeight
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, CardMinHeight, CardMaxHeight));
    } = CardDefaultHeight;

    [ObservableProperty] public partial bool EnableFilterTypes { get; set; } = false;

    [ObservableProperty]
    public partial ObservableDictionary<NetworkInterfaceType, EnumViewFilter> FilterTypes { get; set; } = new();

    [ObservableProperty] public partial bool EnableFilterStatus { get; set; } = true;

    [ObservableProperty]
    public partial ObservableDictionary<OperationalStatus, EnumViewFilter> FilterStatus { get; set; } = new();

    [ObservableProperty] public partial bool EnableFilterOthers { get; set; } = true;

    [ObservableProperty] public partial bool? FilterByVirtual { get; set; } = false;

    [ObservableProperty] public partial bool? FilterByHavePhysicalAddress { get; set; }

    [ObservableProperty] public partial bool? FilterByHaveIPAddress { get; set; }

    [ObservableProperty] public partial bool? FilterByIsTransmittingData { get; set; }

    protected override void OnLoaded(bool fromFile)
    {
        var isInit = FilterTypes.Count == 0;
        var interfaceTypes = EnumExtensions.GetAllValues<NetworkInterfaceType>(true);
        foreach (var value in interfaceTypes)
        {
            FilterTypes.TryAdd(value, new EnumViewFilter(value));
            FilterTypes[value].IconKind = NetworkInterfaceBridge.GetNetworkInterfaceTypeIcon(value);
        }

        FilterTypes.TryAdd((NetworkInterfaceType)53, new EnumViewFilter("Proprietary virtual/internal"));
        FilterTypes[(NetworkInterfaceType)53].IconKind =
            NetworkInterfaceBridge.GetNetworkInterfaceTypeIcon((NetworkInterfaceType)53);

        if (isInit)
        {
            foreach (var value in FilterTypes)
            {
                value.Value.Include = value.Key
                    is NetworkInterfaceType.Ethernet3Megabit
                    or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx
                    or NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.VeryHighSpeedDsl
                    or NetworkInterfaceType.AsymmetricDsl
                    or NetworkInterfaceType.MultiRateSymmetricDsl
                    or NetworkInterfaceType.RateAdaptDsl
                    or NetworkInterfaceType.SymmetricDsl;
            }
        }


        //isInit = FilterStatus.Count == 0;
        var statusValues = EnumExtensions.GetAllValues<OperationalStatus>(true);
        foreach (var value in statusValues)
        {
            FilterStatus.TryAdd(value, new EnumViewFilter(value));
            FilterStatus[value].IconKind = NetworkInterfaceBridge.GetStatusIcon(value);
        }

        /*if (isInit)
        {
            foreach (var value in FilterStatus)
            {
                value.Value.Include = value.Key
                    is OperationalStatus.Up
                    or OperationalStatus.Testing;
            }
        }*/
    }
}

public partial class SpeedTestSettings : SubSettings
{
    public const int DefaultMinimumAutoSpeedTestIntervalSeconds = 30;
    public const int DefaultMaximumAutoSpeedTestIntervalSeconds = 60 * 60 * 24;
    public const int DefaultAutoSpeedTestIntervalSeconds = 60 * 10;

    /// <summary>
    /// Gets or sets a value indicating whether automatic speed testing is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoSpeedTest { get; set; }

    /// <summary>
    /// Gets or sets the interval, in seconds, between automatic speed tests.
    /// </summary>
    /// <remarks>Specify a positive value to enable periodic speed testing. Setting the interval to zero or a
    /// negative value disables automatic speed tests.</remarks>
    public float AutoSpeedTestIntervalSeconds
    {
        get;
        set => SetProperty(ref field, Math.Max(0, value));
    } = DefaultAutoSpeedTestIntervalSeconds;

    /// <summary>
    /// Gets or sets the initial range value for the speed gauge.
    /// </summary>
    public int InitialSpeedGaugeRange
    {
        get;
        set => SetProperty(ref field, Math.Max(0, value));
    } = 250;

    /// <summary>
    /// Gets or sets the increment value for the speed gauge range when it reaches the maximum.
    /// </summary>
    public int SpeedGaugeRangeIncrement
    {
        get;
        set => SetProperty(ref field, Math.Max(1, value));
    } = 250;

    /// <summary>
    /// Gets or sets a value indicating whether the user accepted the Ookla Speedtest CLI terms (EULA, Terms of Use
    /// and Privacy Policy). Speed tests and server lookups only run once this is <see langword="true"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool OoklaTermsAccepted { get; set; }

    /// <summary>
    /// Gets or sets the Ookla server id used for speed tests, or <c>0</c> to let the CLI pick the server.
    /// </summary>
    [ObservableProperty]
    public partial int ServerId { get; set; }
}

public partial class AppSettings : RootSettingsFile<AppSettings>
{
    #region Constants

    public const string Section = "AppSettings";

    #endregion


    public AppSettings()
    {
        AutoSave = true;
        DirectoryPath = ApplicationKit.ConfigsPath;
        FileName = "app_settings.json";
        DefaultDebounceSaveMilliseconds = 10_000;
    }

    /// <summary>
    /// Reflects whether the app is registered to start on user login.
    /// Not persisted — read from / written to the OS so it stays in sync
    /// when the user toggles autostart externally.
    /// </summary>
    [JsonIgnore]
    public bool RunOnStartup
    {
        get
        {
            try
            {
                return SystemOS.Autostart.IsEnabled;
            }
            catch (Exception e)
            {
                UnhandledExceptions.HandleSafeException(e, "RunOnStartup:get");
            }

            return false;
        }
        set
        {
            try
            {
                if (SystemOS.Autostart.IsEnabled == value) return;
                SystemOS.Autostart.SetEnabled(value);
                OnPropertyChanged();
            }
            catch (Exception e)
            {
                UnhandledExceptions.HandleSafeException(e, "RunOnStartup:set");
            }
        }
    }

    [ObservableProperty] public partial ApplicationTheme Theme { get; set; } = ApplicationTheme.Default;

    [ObservableProperty] public partial string ThemeColor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selected UI culture. An empty value lets the first startup use the system language.
    /// </summary>
    [ObservableProperty]
    public partial string Language { get; set; } = string.Empty;

    public float UiScale
    {
        get;
        set => SetProperty(ref field, Math.Clamp(MathF.Round(value, 2, MidpointRounding.AwayFromZero), 0.5f, 2f));
    } = 1f;

    [ObservableProperty] public partial bool BackgroundAnimations { get; set; } = true;

    [ObservableProperty] public partial bool BackgroundTransitions { get; set; } = true;

    [ObservableProperty]
    public partial SukiBackgroundStyle BackgroundStyle { get; set; } = SukiBackgroundStyle.GradientSoft;

    [ObservableProperty] public partial bool IsTrayVisible { get; set; } = true;

    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;

    [ObservableProperty] public partial bool CheckForUpdates { get; set; } = true;

    [ObservableProperty] public partial DateTime LastUpdateDateTimeCheck { get; set; } = ApplicationKit.Birth;

    [ObservableProperty] public partial bool IsSideMenuExpanded { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of concurrent tasks enabled by this <see cref="T:System.Threading.Tasks.ParallelOptions" /> instance.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get;
        set => SetProperty(ref field, Math.Max(-1, value));
    } = -1;

    /// <summary>
    /// Gets or sets the last window state.
    /// </summary>
    [ObservableProperty]
    public partial WindowState LastWindowState { get; set; } = WindowState.Maximized;

    [ObservableProperty] public partial PingServicesSettings PingServices { get; set; } = new();

    [ObservableProperty] public partial NetworkInterfacesSettings NetworkInterfaces { get; set; } = new();

    [ObservableProperty] public partial SpeedTestSettings SpeedTest { get; set; } = new();

    [ObservableProperty] public partial NetworkScannerSettings NetworkScanner { get; set; } = new();

    [JsonIgnore]
    public override SubSettings[] SubSettingsCollection =>
    [
        PingServices,
        NetworkInterfaces,
        SpeedTest,
        NetworkScanner
    ];
}