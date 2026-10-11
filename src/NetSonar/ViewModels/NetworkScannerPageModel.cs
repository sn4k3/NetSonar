using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Models;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.ViewModels.Dialogs;
using ObservableCollections;
using StageKit.Primitives;
using StageKit.Primitives.System;
using StageKit.Runtime.System;
using SukiUI.Dialogs;

namespace NetSonar.Avalonia.ViewModels;

public partial class NetworkScannerPageModel : PageViewModelBase
{
    /// <summary>
    /// The group assigned to the monitoring services imported from this page.
    /// </summary>
    public const string MonitoringGroup = "Network Scanner";

    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(100);
    private readonly DispatcherTimer _autoRescanTimer = new();
    private readonly List<string> _detectedTargets = [];

    private readonly ObservableCollection<NetworkScannerTreeNode> _hostNodes = [];

    private CancellationTokenSource? _cancellationTokenSource;
    private string _rawOutput = string.Empty;
    private string? _nmapPath;

    public NetworkScannerPageModel()
    {
        TargetsView = Targets.ToNotifyCollectionChangedSlim(
            SynchronizationContextCollectionEventDispatcher.Current
        );

        HostsView = Hosts.ToNotifyCollectionChangedSlim(
            SynchronizationContextCollectionEventDispatcher.Current
        );

        BuildHostsSource();

        App.Localization.PropertyChanged += LocalizationOnPropertyChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;

        _autoRescanTimer.Tick += AutoRescanTimerOnTick;
        UpdateAutoRescanTimer();
    }

    public override int Index => 2;
    public override string DisplayName => App.Localization["Navigation.NetworkScanner"];
    public override MaterialIconKind Icon => MaterialIconKind.Radar;

    /// <summary>
    /// Gets the persisted scanner configuration the page binds to.
    /// </summary>
    public static NetworkScannerSettings Settings => AppSettings.NetworkScanner;

    public ObservableList<string> Targets { get; } = [];
    public NotifyCollectionChangedSynchronizedViewList<string> TargetsView { get; }
    public ObservableList<NetworkScannerHost> Hosts { get; } = [];

    public NotifyCollectionChangedSynchronizedViewList<NetworkScannerHost> HostsView { get; }

    [ObservableProperty]
    public partial HierarchicalTreeDataGridSource<NetworkScannerTreeNode> HostsSource
    {
        get;
        private set;
    } = null!;

    [ObservableProperty]
    public partial int OpenPortCount { get; private set; }

    [ObservableProperty]
    public partial int FilterHostCount { get; private set; }

    [ObservableProperty]
    public partial string Target { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoInstallDependencyCommand))]
    [NotifyPropertyChangedFor(nameof(CanAutoInstall))]
    [NotifyPropertyChangedFor(nameof(IsUsingBuiltinEngine))]
    public partial bool IsNmapAvailable { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoInstallDependencyCommand))]
    public partial bool IsInstalling { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedPortsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanAllPortsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanSelectedHostsCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineText))]
    public partial DateTime? LastScanTimestamp { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineText))]
    [NotifyCanExecuteChangedFor(nameof(SaveRawOutputCommand))]
    public partial NetworkScanEngine Engine { get; private set; }

    /// <summary>
    /// Gets the engine the last scan used, with the timestamp of that scan.
    /// </summary>
    public string EngineText =>
        LastScanTimestamp is null
            ? string.Empty
            : App.Localization.Format(
                "NetworkScanner.LastScan",
                Engine == NetworkScanEngine.Nmap
                    ? App.Localization["NetworkScanner.Engine.Nmap"]
                    : App.Localization["NetworkScanner.Engine.Builtin"],
                LastScanTimestamp.Value.ToString("g", CultureInfo.CurrentCulture)
            );

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasSelectedHosts))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedInBrowserCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedMacAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedPortsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanSelectedHostsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToMonitoringCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedPortsToMonitoringCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedHostsCommand))]
    public partial NetworkScannerTreeNode? SelectedNode { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the next scan runs on the built-in engine, because nmap is missing or
    /// because the user turned it off.
    /// </summary>
    /// <remarks>
    /// The missing-dependency banner binds to <see cref="IsNmapAvailable"/> instead: turning nmap off is a
    /// deliberate choice, not a missing dependency to install.
    /// </remarks>
    public bool IsUsingBuiltinEngine => !IsNmapAvailable || !Settings.UseNmapWhenAvailable;

    [ObservableProperty]
    public partial string? NmapVersion { get; private set; }

    public bool CanAutoInstall => GetInstallCommand() is not null;

    public bool HasSelection => SelectedNode is not null;

    // A selected port row stands for its parent host, exactly as GetSelectedHosts maps it.
    public bool HasSelectedHosts =>
        HostsSource.RowSelection?.SelectedItems.Any(node => (node?.Host ?? node?.Parent?.Host) is not null)
        == true;

    public bool HasResults => Hosts.Count > 0;

    public bool HasRawOutput => _rawOutput.Length > 0;

    /// <summary>
    /// Gets the target the displayed hosts belong to.
    /// </summary>
    private static string CurrentTarget =>
        string.IsNullOrWhiteSpace(Settings.LastTarget) ? string.Empty : Settings.LastTarget;

    protected internal override void OnInitialized()
    {
        base.OnInitialized();
        _ = ReloadDependency();
        RefreshTargets();
        RestoreLastScan();
    }

    protected internal override void OnLoaded()
    {
        base.OnLoaded();
        _ = ReloadDependency();
    }

    [RelayCommand]
    public void ClearFilters()
    {
        FilterText = string.Empty;
    }

    [RelayCommand]
    public async Task ReloadDependency()
    {
        try
        {
            // Locating nmap and asking it for its version touches the disk and spawns a process.
            var (path, version) = await Task.Run(async () =>
            {
                if (!NmapScannerService.TryFindExecutable(out var executablePath))
                    return (Path: string.Empty, Version: (string?)null);

                return (Path: executablePath, Version: await NmapScannerService.GetVersionAsync());
            });

            IsNmapAvailable = path.Length > 0;
            NmapVersion = version;

            // The vendor database sits next to nmap, and re-reading it is expensive, so it is only dropped
            // when nmap moved, appeared, or went away.
            if (!string.Equals(path, _nmapPath, StringComparison.Ordinal))
            {
                _nmapPath = path;
                MacVendorLookup.Invalidate();
            }
        }
        catch (Exception exception)
        {
            App.ShowExceptionToast(exception, App.Localization["Navigation.NetworkScanner"]);
        }
    }

    [RelayCommand]
    public void RefreshTargets()
    {
        var currentTarget = Target;
        _detectedTargets.Clear();

        try
        {
            _detectedTargets.AddRange(NmapScannerService.GetLocalTargets());
        }
        catch (Exception exception)
        {
            App.ShowExceptionToast(exception, App.Localization["Navigation.NetworkScanner"]);
        }

        Targets.Clear();
        Targets.AddRange(_detectedTargets);
        foreach (var custom in Settings.CustomTargets)
        {
            if (!_detectedTargets.Contains(custom, StringComparer.OrdinalIgnoreCase))
            {
                Targets.Add(custom);
            }
        }

        Target =
            !string.IsNullOrWhiteSpace(currentTarget) ? currentTarget
            : !string.IsNullOrWhiteSpace(Settings.LastTarget) ? Settings.LastTarget
            : Targets.FirstOrDefault() ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    public Task ScanNetwork()
    {
        var target = NetworkTargetList.Normalize(Target);
        Target = target;
        return ScanTarget(target, true);
    }

    /// <summary>
    /// Runs host discovery against a target list.
    /// </summary>
    /// <param name="target">The target list.</param>
    /// <param name="remember">Whether the target becomes the last and a remembered custom target.</param>
    private async Task ScanTarget(string target, bool remember)
    {
        target = NetworkTargetList.Normalize(target);
        if (!NmapScannerService.IsValidTarget(target))
        {
            App.ShowToast(
                NotificationType.Warning,
                App.Localization["Navigation.NetworkScanner"],
                App.Localization["NetworkScanner.InvalidTarget"]
            );
            return;
        }

        if (remember)
        {
            Settings.LastTarget = target;
            Settings.RememberCustomTarget(target, _detectedTargets);
            if (!Targets.Contains(target))
                Targets.Add(target);
        }

        // The options read the localization, so they are built here on the UI thread.
        var preferNmap = Settings.UseNmapWhenAvailable;
        var options = Settings.ToScanOptions();

        await RunScan(
            async (progress, token) =>
            {
                // The engines do process, DNS, and parsing work that must not run on the UI thread.
                var result = await Task.Run(
                    () =>
                        NetworkScannerService.DiscoverAsync(
                            preferNmap,
                            target,
                            options,
                            progress,
                            token
                        ),
                    token
                );

                ApplyResult(target, result, false);
            }
        );
    }

    [RelayCommand(CanExecute = nameof(CanScanSelectedPorts))]
    public async Task ScanSelectedPorts()
    {
        var selected = GetSelectedHosts();
        if (selected.Length == 0)
            return;

        await ScanPorts(selected.Select(host => host.Address).ToArray());
    }

    [RelayCommand(CanExecute = nameof(CanScanAllPorts))]
    public async Task ScanAllPorts()
    {
        // With -Pn nmap waits for the timeout on every host, so a host that stopped answering is not worth it.
        var addresses = Hosts.Where(host => !host.IsMissing).Select(host => host.Address).ToArray();
        if (addresses.Length == 0)
            return;

        await ScanPorts(addresses);
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    public async Task CancelScan()
    {
        var cancellationTokenSource = Interlocked.CompareExchange(
            ref _cancellationTokenSource,
            null,
            null
        );
        if (cancellationTokenSource is null)
            return;

        try
        {
            await cancellationTokenSource.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished between reading the source and cancelling it.
        }
    }

    [RelayCommand(CanExecute = nameof(HasResults))]
    public void ClearResults()
    {
        if (Hosts.Count == 0)
            return;

        CreateMessageBoxYesNo(
                NotificationType.Warning,
                App.Localization.Format("NetworkScanner.ClearResults.Title", Hosts.Count),
                App.Localization.Format("NetworkScanner.ClearResults.Message", Hosts.Count),
                _ =>
                {
                    Hosts.Clear();
                    _rawOutput = string.Empty;
                    Engine = NetworkScanEngine.Builtin;
                    LastScanTimestamp = null;
                    OnPropertyChanged(nameof(HasRawOutput));
                    SaveRawOutputCommand.NotifyCanExecuteChanged();
                    RebuildHostTree();

                    // The stored scan has to go with it, or the next start restores what was just cleared.
                    PersistHosts();
                }
            )
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHosts))]
    public void AddSelectedToMonitoring()
    {
        ImportToMonitoring(false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHosts))]
    public void AddSelectedPortsToMonitoring()
    {
        ImportToMonitoring(true);
    }

    [RelayCommand(CanExecute = nameof(CanRescanSelectedHosts))]
    public async Task RescanSelectedHosts()
    {
        var selected = GetSelectedHosts();
        if (selected.Length == 0)
            return;

        await ScanPorts(selected.Select(host => host.Address).ToArray());
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHosts))]
    public void RemoveSelectedHosts()
    {
        var selected = GetSelectedHosts()
            .Select(host => host.Address)
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
            return;

        CreateMessageBoxYesNo(
                NotificationType.Warning,
                App.Localization.Format("NetworkScanner.RemoveSelected.Title", selected.Count),
                App.Localization.Format("NetworkScanner.RemoveSelected.Message", selected.Count),
                _ =>
                {
                    for (var index = Hosts.Count - 1; index >= 0; index--)
                    {
                        if (selected.Contains(Hosts[index].Address))
                            Hosts.RemoveAt(index);
                    }

                    RebuildHostTree();
                    PersistHosts();
                }
            )
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHosts))]
    public Task CopySelectedAddress()
    {
        var addresses = GetSelectedHosts().Select(host => host.Address).ToArray();
        return addresses.Length == 0
            ? Task.CompletedTask
            : App.CopyToClipboard(string.Join(Environment.NewLine, addresses), true);
    }

    [RelayCommand(CanExecute = nameof(CanCopySelectedMacAddress))]
    public Task CopySelectedMacAddress()
    {
        var macAddresses = GetSelectedMacAddresses();
        return macAddresses.Length == 0
            ? Task.CompletedTask
            : App.CopyToClipboard(string.Join(Environment.NewLine, macAddresses), true);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedInBrowser))]
    public async Task OpenSelectedInBrowser()
    {
        foreach (var url in GetSelectedWebUrls())
        {
            await App.LaunchUriAsync(url);
        }
    }

    [RelayCommand(CanExecute = nameof(HasResults))]
    public async Task ExportResultsToJson()
    {
        var file = await PickSaveFile("json", AvaloniaExtensions.FilePickerJson);
        if (file is null)
            return;

        await WriteExport(
            file,
            async path =>
            {
                await using var stream = File.Create(path);
                await JsonSerializer.SerializeAsync(
                    stream,
                    Hosts.ToArray(),
                    App.JsonSerializerOptions
                );
            }
        );
    }

    [RelayCommand(CanExecute = nameof(HasResults))]
    public async Task ExportResultsToCsv()
    {
        var file = await PickSaveFile("csv", AvaloniaExtensions.FilePickerCsv);
        if (file is null)
            return;

        await WriteExport(file, path => File.WriteAllTextAsync(path, BuildCsv()));
    }

    [RelayCommand(CanExecute = nameof(HasRawOutput))]
    public async Task SaveRawOutput()
    {
        var file = await PickSaveFile("xml", AvaloniaExtensions.FilePickerXml);
        if (file is null)
            return;

        await WriteExport(file, path => File.WriteAllTextAsync(path, _rawOutput));
    }

    [RelayCommand(CanExecute = nameof(CanAutoInstallDependency))]
    public async Task AutoInstallDependency()
    {
        var command = GetInstallCommand();
        if (command is null || IsInstalling)
            return;

        IsInstalling = true;
        try
        {
            await ProcessXExtensions.ExecuteHandled(
                command.Value.Command,
                new ProcessXToast(
                    App.Localization["NetworkScanner.Install.Title"],
                    App.Localization["NetworkScanner.Install.Success"],
                    App.Localization["NetworkScanner.Install.Error"]
                )
                {
                    ShowOnlySuccessGenericMessage = true,
                },
                command.Value.RequireElevation
            );
        }
        finally
        {
            IsInstalling = false;
            await ReloadDependency();
        }
    }

    private bool CanStartScan()
    {
        return !IsScanning;
    }

    private bool CanCancelScan()
    {
        return IsScanning;
    }

    private bool CanScanAllPorts()
    {
        return !IsScanning && HasResults;
    }

    private bool CanScanSelectedPorts()
    {
        return !IsScanning && HasSelectedHosts;
    }

    private bool CanRescanSelectedHosts()
    {
        return CanScanSelectedPorts();
    }

    private bool CanAutoInstallDependency()
    {
        return !IsInstalling && CanAutoInstall;
    }

    private bool CanCopySelectedMacAddress()
    {
        return GetSelectedMacAddresses().Length > 0;
    }

    private bool CanOpenSelectedInBrowser()
    {
        return GetSelectedWebUrls().Length > 0;
    }

    private async Task ScanPorts(IReadOnlyList<string> addresses)
    {
        var target = string.IsNullOrWhiteSpace(Settings.LastTarget)
            ? NetworkTargetList.Normalize(Target)
            : Settings.LastTarget;

        var preferNmap = Settings.UseNmapWhenAvailable;
        var options = Settings.ToPortScanOptions();

        // A bad specification would make nmap fail and the built-in engine find nothing, and a scan that finds
        // nothing erases the ports already known for every requested host.
        if (
            options.Range == NetworkPortScanRange.Custom
            && !NetworkPortCatalog.IsValidPortSpec(options.CustomPorts)
        )
        {
            App.ShowToast(
                NotificationType.Warning,
                App.Localization["Navigation.NetworkScanner"],
                App.Localization["NetworkScanner.InvalidPorts"]
            );
            return;
        }

        await RunScan(
            async (progress, token) =>
            {
                var result = await Task.Run(
                    () =>
                        NetworkScannerService.ScanPortsAsync(
                            preferNmap,
                            addresses,
                            options,
                            progress,
                            token
                        ),
                    token
                );

                ApplyResult(target, result, true, addresses);
            }
        );
    }

    private async Task RunScan(Func<IProgress<NetworkScanProgress>, CancellationToken, Task> scan)
    {
        if (IsScanning)
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        Interlocked.Exchange(ref _cancellationTokenSource, cancellationTokenSource);
        IsScanning = true;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        ProgressText = string.Empty;

        // The built-in engine reports once per probe, from many threads, so the throttle has to happen before
        // the value is posted to the UI thread.
        var progress = new ThrottledProgress(
            new Progress<NetworkScanProgress>(OnProgress),
            ProgressThrottle
        );

        try
        {
            await scan(progress, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // The user cancelled the active scan.
        }
        catch (Exception exception)
        {
            App.ShowExceptionToast(exception, App.Localization["Navigation.NetworkScanner"]);
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _cancellationTokenSource,
                null,
                cancellationTokenSource
            );
            cancellationTokenSource.Dispose();
            IsScanning = false;
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            ProgressText = string.Empty;
        }
    }

    private void OnProgress(NetworkScanProgress progress)
    {
        // A value posted just before the scan ended can arrive after the reset and would leave stale text.
        if (!IsScanning)
            return;

        ProgressPercent = Math.Clamp(progress.Percent, 0, 100);
        IsProgressIndeterminate = !progress.HasPercent;

        var percent = ProgressPercent.ToString("0.0", CultureInfo.CurrentCulture);
        ProgressText = progress.Eta is { } eta
            ? App.Localization.Format(
                "NetworkScanner.ProgressEta",
                progress.Task,
                percent,
                FormatEta(eta)
            )
            : App.Localization.Format("NetworkScanner.Progress", progress.Task, percent);
    }

    /// <summary>
    /// Formats a remaining time as a duration instead of a raw number of seconds.
    /// </summary>
    /// <param name="eta">The time the engine expects the running task to still need.</param>
    /// <returns>The duration, as <c>h:mm:ss</c> or <c>m:ss</c>.</returns>
    private static string FormatEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero)
            eta = TimeSpan.Zero;

        return eta.TotalHours >= 1
            ? eta.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : eta.ToString(@"m\:ss", CultureInfo.CurrentCulture);
    }

    private void ApplyResult(
        string target,
        NetworkScanResult result,
        bool isPortScan,
        IReadOnlyList<string>? scannedAddresses = null
    )
    {
        Engine = result.Engine;
        _rawOutput = result.RawOutput;
        OnPropertyChanged(nameof(HasRawOutput));
        SaveRawOutputCommand.NotifyCanExecuteChanged();

        var previous = NetworkScansFile.Instance.FindLatest(target)?.Hosts ?? [];
        var previousByAddress = previous.ToDictionary(
            host => host.Address,
            StringComparer.OrdinalIgnoreCase
        );

        List<NetworkScannerHost> hosts;
        if (isPortScan)
        {
            var scannedByAddress = result.Hosts.ToDictionary(
                host => host.Address,
                StringComparer.OrdinalIgnoreCase
            );
            var requested = (scannedAddresses ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

            hosts = new List<NetworkScannerHost>(Hosts.Count);
            foreach (var host in Hosts)
            {
                // A requested host that reported nothing is no longer serving those ports, so its
                // previous port list must not survive the scan.
                hosts.Add(
                    requested.Contains(host.Address)
                        ? host.WithPortScan(scannedByAddress.GetValueOrDefault(host.Address))
                        : host
                );
            }

            var listed = hosts
                .Select(host => host.Address)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var scanned in result.Hosts)
            {
                if (listed.Add(scanned.Address))
                    hosts.Add(scanned);
            }
        }
        else
        {
            var discovered = result
                .Hosts.Select(host =>
                    host.WithCarriedPorts(previousByAddress.GetValueOrDefault(host.Address))
                )
                .ToArray();

            // A host the previous scan saw stays listed for a few misses, because a missed probe is not the
            // same as a device that left the network.
            hosts = NetworkScanRetention.KeepRecentlySeen(previous, discovered);
        }

        hosts.Sort((left, right) => left.GetSortKey().CompareTo(right.GetSortKey()));

        // Hosts already counted as missing must not be reported as newly gone on every following scan.
        var diff = NetworkScanDiff.Compare(
            previous.Where(host => !host.IsMissing).ToArray(),
            hosts.Where(host => !host.IsMissing).ToArray()
        );

        Hosts.Clear();
        Hosts.AddRange(hosts);
        LastScanTimestamp = DateTime.Now;

        NetworkScansFile.Instance.Store(
            new NetworkScanSnapshot
            {
                Target = target,
                Timestamp = LastScanTimestamp.Value,
                Engine = result.Engine,
                Hosts = hosts,
            }
        );

        RebuildHostTree(scannedAddresses);
        ReportChanges(diff);
    }

    private void ReportChanges(NetworkScanDiff diff)
    {
        if (!Settings.NotifyChanges || !diff.HasChanges)
            return;

        App.ShowToast(
            NotificationType.Information,
            App.Localization["NetworkScanner.Changes.Title"],
            App.Localization.Format(
                "NetworkScanner.Changes.Message",
                diff.NewHosts.Count,
                diff.GoneHosts.Count,
                diff.ChangedHosts.Count
            )
        );
    }

    private void ImportToMonitoring(bool includePorts)
    {
        var services = new List<NewPingService>();
        foreach (var node in GetSelectedNodes())
        {
            // Every selected port row imports exactly that port, whichever command was used. A host row imports
            // all of its ports for the ports command, and an ICMP monitor of the host otherwise.
            if (node.Port is { } selectedPort && node.Parent?.Host is { } parent)
            {
                services.Add(
                    NetworkPortCatalog.CreatePingService(parent, selectedPort, MonitoringGroup)
                );
            }
            else if (includePorts && node.Host is { OpenPortCount: > 0 } host)
            {
                services.AddRange(
                    host.Ports.Select(port =>
                        NetworkPortCatalog.CreatePingService(host, port, MonitoringGroup)
                    )
                );
            }
            else if (node.Host is { } hostOnly)
            {
                services.Add(NetworkPortCatalog.CreatePingService(hostOnly, null, MonitoringGroup));
            }
        }

        var distinct = services.Distinct().ToArray();
        if (distinct.Length == 0)
            return;

        DialogManager
            .CreateDialog()
            .WithViewModel(dialog => AddPingServicesDialogModel.CreateForImport(dialog, distinct))
            .TryShow();
    }

    private NetworkScannerTreeNode[] GetSelectedNodes()
    {
        return HostsSource.RowSelection?.SelectedItems.OfType<NetworkScannerTreeNode>().ToArray()
            ?? [];
    }

    private NetworkScannerHost[] GetSelectedHosts()
    {
        var selected = HostsSource
            .RowSelection?.SelectedItems.Where(node => node is not null)
            .Select(node => node!.Host ?? node.Parent?.Host)
            .Where(host => host is not null)
            .Cast<NetworkScannerHost>()
            .DistinctBy(host => host.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return selected ?? [];
    }

    private string[] GetSelectedMacAddresses()
    {
        return GetSelectedHosts()
            .Select(host => host.MacAddress)
            .Where(mac => !string.IsNullOrEmpty(mac))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] GetSelectedWebUrls()
    {
        return GetSelectedNodes()
            .Select(GetWebUrl)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetWebUrl(NetworkScannerTreeNode node)
    {
        var address = node.Host?.Address ?? node.Parent?.Host?.Address;
        if (string.IsNullOrWhiteSpace(address))
            return null;

        if (node.Port is { } port)
        {
            // The user picked this exact port, so an unknown one is still opened as plain HTTP.
            if (NetworkPortCatalog.TryGetWebUrl(address, port.Number, out var portUrl))
                return portUrl;

            var literal = address.Contains(':') ? $"[{address}]" : address;
            return $"http://{literal}:{port.Number}";
        }

        var host = node.Host;
        if (host is null)
            return null;

        // HTTPS first, then HTTP, then whichever other web port the host lists first.
        foreach (var preferred in (ReadOnlySpan<int>)[443, 80])
        {
            if (
                host.Ports.Any(value => value.Number == preferred)
                && NetworkPortCatalog.TryGetWebUrl(address, preferred, out var preferredUrl)
            )
                return preferredUrl;
        }

        foreach (var value in host.Ports)
        {
            if (NetworkPortCatalog.TryGetWebUrl(address, value.Number, out var webUrl))
                return webUrl;
        }

        return null;
    }

    private void RebuildHostTree(IEnumerable<string>? selectedAddresses = null)
    {
        // Without an explicit selection, keep the rows the user has selected through a refilter or a rescan.
        var selected = (
            selectedAddresses ?? GetSelectedHosts().Select(host => host.Address)
        ).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filter = FilterText.Trim();

        HostsSource.RowSelection?.Clear();
        _hostNodes.Clear();

        var openPorts = 0;
        foreach (var host in Hosts)
        {
            openPorts += host.OpenPortCount;
            if (!Matches(host, filter))
                continue;
            _hostNodes.Add(NetworkScannerTreeNode.ForHost(host));
        }

        OpenPortCount = openPorts;
        FilterHostCount = _hostNodes.Count;

        HostsSource.ExpandAll();
        for (var index = 0; index < _hostNodes.Count; index++)
        {
            if (selected.Contains(_hostNodes[index].Host!.Address))
            {
                HostsSource.RowSelection?.Select(new IndexPath(index));
            }
        }

        SummaryText = App.Localization.Format(
            "NetworkScanner.Summary",
            Hosts.Count,
            openPorts,
            _hostNodes.Count
        );

        OnPropertyChanged(nameof(HasResults));
        ExportResultsToJsonCommand.NotifyCanExecuteChanged();
        ExportResultsToCsvCommand.NotifyCanExecuteChanged();
        ClearResultsCommand.NotifyCanExecuteChanged();
        ScanAllPortsCommand.NotifyCanExecuteChanged();
        UpdateSelectionState();
    }

    private static bool Matches(NetworkScannerHost host, string filter)
    {
        if (filter.Length == 0)
            return true;

        if (
            host.Address.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || host.Hostname.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || host.MacAddress.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || host.Vendor.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || host.OperatingSystem.Contains(filter, StringComparison.OrdinalIgnoreCase)
        )
            return true;

        return host.Ports.Any(port =>
            port.Service.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || port.ProductVersion.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || port.Number.ToString(CultureInfo.InvariantCulture)
                .Contains(filter, StringComparison.Ordinal)
        );
    }

    private void UpdateSelectionState()
    {
        SelectedNode = HostsSource.RowSelection?.SelectedItem;

        // With a multi-selection the primary item often stays put while rows join or leave the selection, so
        // the SelectedNode setter alone does not refresh what depends on the whole selection.
        OnPropertyChanged(nameof(HasSelectedHosts));
        ScanSelectedPortsCommand.NotifyCanExecuteChanged();
        RescanSelectedHostsCommand.NotifyCanExecuteChanged();
        AddSelectedToMonitoringCommand.NotifyCanExecuteChanged();
        AddSelectedPortsToMonitoringCommand.NotifyCanExecuteChanged();
        RemoveSelectedHostsCommand.NotifyCanExecuteChanged();
        CopySelectedAddressCommand.NotifyCanExecuteChanged();
        CopySelectedMacAddressCommand.NotifyCanExecuteChanged();
        OpenSelectedInBrowserCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Writes the displayed hosts back to the scan history, so an edit of the list survives a restart.
    /// </summary>
    /// <remarks>
    /// Only a scan writes a new timestamp; editing the list keeps the timestamp of the scan it came from.
    /// An emptied list drops the stored scan instead of storing an empty one.
    /// </remarks>
    private void PersistHosts()
    {
        var target = CurrentTarget;
        if (target.Length == 0)
            return;

        if (Hosts.Count == 0)
        {
            NetworkScansFile.Instance.RemoveTarget(target);
            return;
        }

        NetworkScansFile.Instance.Store(
            new NetworkScanSnapshot
            {
                Target = target,
                Timestamp = LastScanTimestamp ?? DateTime.Now,
                Engine = Engine,
                Hosts = Hosts.ToArray(),
            }
        );
    }

    private void RestoreLastScan()
    {
        var snapshot = NetworkScansFile.Instance.FindLatest(Settings.LastTarget);
        if (snapshot is null)
            return;

        Hosts.Clear();
        Hosts.AddRange(snapshot.Hosts);
        Engine = snapshot.Engine;
        LastScanTimestamp = snapshot.Timestamp;
        RebuildHostTree();
    }

    private void BuildHostsSource()
    {
        var source = new HierarchicalTreeDataGridSource<NetworkScannerTreeNode>(_hostNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<NetworkScannerTreeNode>(
                    new TextColumn<NetworkScannerTreeNode, string>(
                        App.Localization["Ui.ScannerHost"],
                        node => node.Name,
                        options: new TextColumnOptions<NetworkScannerTreeNode>
                        {
                            // The text would sort 192.168.1.10 before 192.168.1.2, so sort by number.
                            CompareAscending = NetworkScannerTreeNode.CompareByAddress,
                            CompareDescending = (left, right) =>
                                NetworkScannerTreeNode.CompareByAddress(right, left),
                        }
                    ),
                    node => node.Children
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerChange"],
                    node => node.ChangeText
                ),
                // Colored like the status column of the other grids; the cell template lives in the view.
                new TemplateColumn<NetworkScannerTreeNode>(
                    App.Localization["Ui.Status"],
                    "NetworkScannerStateCell",
                    options: new TemplateColumnOptions<NetworkScannerTreeNode>
                    {
                        CompareAscending = (left, right) =>
                            string.Compare(left?.State, right?.State, StringComparison.OrdinalIgnoreCase),
                        CompareDescending = (left, right) =>
                            string.Compare(right?.State, left?.State, StringComparison.OrdinalIgnoreCase),
                    }
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerMacAddress"],
                    node => node.MacAddress
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerVendor"],
                    node => node.Vendor
                ),
                new TextColumn<NetworkScannerTreeNode, int?>(
                    App.Localization["Ui.ScannerPorts"],
                    node => node.PortCount
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerProtocol"],
                    node => node.Protocol
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerService"],
                    node => node.Service
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerVersion"],
                    node => node.Version
                ),
                new TextColumn<NetworkScannerTreeNode, string>(
                    App.Localization["Ui.ScannerOperatingSystem"],
                    node => node.OperatingSystem
                ),
            },
        };

        source.RowSelection!.SingleSelect = false;
        source.RowSelection.SelectionChanged += (_, _) => UpdateSelectionState();
        HostsSource = source;
    }

    private void LocalizationOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(App.Localization.Culture))
            return;

        OnPropertyChanged(nameof(EngineText));

        // The column headers are plain strings captured when the source is built, so the grid has to be
        // rebuilt for a language change to reach them. The new grid starts empty, so carry the selection over.
        var selected = GetSelectedHosts().Select(host => host.Address).ToArray();
        BuildHostsSource();
        RebuildHostTree(selected);
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NetworkScannerSettings.AutoRescanMinutes):
                UpdateAutoRescanTimer();
                break;
            case nameof(NetworkScannerSettings.UseNmapWhenAvailable):
                OnPropertyChanged(nameof(IsUsingBuiltinEngine));
                break;
        }
    }

    private void UpdateAutoRescanTimer()
    {
        var minutes = Settings.AutoRescanMinutes;
        if (minutes <= 0)
        {
            _autoRescanTimer.IsEnabled = false;
            return;
        }

        _autoRescanTimer.Interval = TimeSpan.FromMinutes(minutes);
        _autoRescanTimer.IsEnabled = true;
    }

    private void AutoRescanTimerOnTick(object? sender, EventArgs e)
    {
        if (IsScanning || !ScanNetworkCommand.CanExecute(null))
            return;

        // Rescan the target of the displayed results, not whatever is being typed in the target box.
        var target = CurrentTarget.Trim();
        if (!NmapScannerService.IsValidTarget(target))
            return;

        _ = ScanTarget(target, false);
    }

    partial void OnFilterTextChanged(string value)
    {
        RebuildHostTree();
    }

    private async Task<IStorageFile?> PickSaveFile(string extension, FilePickerFileType[] fileTypes)
    {
        return await TopLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                ShowOverwritePrompt = true,
                SuggestedFileName = FileUtilities.SanitizeFileName(
                    $"NetworkScan#{Hosts.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.{extension}"
                ),
                DefaultExtension = extension,
                FileTypeChoices = fileTypes,
            }
        );
    }

    private static async Task WriteExport(IStorageFile file, Func<string, Task> write)
    {
        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null)
                return;

            await write(filePath);
            App.ShowToast(
                NotificationType.Success,
                App.Localization["Export.Results.Title"],
                App.Localization.Format("Export.Results.Success", 1, file.Name),
                new ToastActionButton(
                    App.Localization["Common.OpenFile"],
                    _ => HostSystem.OpenFile(filePath)
                ),
                new ToastActionButton(
                    App.Localization["Common.OpenFolder"],
                    _ => HostSystem.ShowFileInFileManager(filePath)
                )
            );
        }
        catch (Exception exception)
        {
            App.ShowExceptionToast(
                exception,
                App.Localization["Export.Results.Title"],
                App.Localization["Export.Results.Error"]
            );
        }
        finally
        {
            file.Dispose();
        }
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder(Hosts.Count * 128);
        builder.AppendLine(
            "Address,Hostname,MacAddress,Vendor,State,OperatingSystem,LastSeen,Port,Protocol,Service,Version"
        );

        foreach (var host in Hosts)
        {
            if (host.OpenPortCount == 0)
            {
                AppendCsvRow(builder, host, null);
                continue;
            }

            foreach (var port in host.Ports)
                AppendCsvRow(builder, host, port);
        }

        return builder.ToString();
    }

    private static void AppendCsvRow(
        StringBuilder builder,
        NetworkScannerHost host,
        NetworkScannerPort? port
    )
    {
        builder.Append(CsvField(host.Address)).Append(',');
        builder.Append(CsvField(host.Hostname)).Append(',');
        builder.Append(CsvField(host.MacAddress)).Append(',');
        builder.Append(CsvField(host.Vendor)).Append(',');
        builder.Append(CsvField(host.State)).Append(',');
        builder.Append(CsvField(host.OperatingSystem)).Append(',');
        builder
            .Append(CsvField(host.LastSeen.ToString("s", CultureInfo.InvariantCulture)))
            .Append(',');
        builder
            .Append(port?.Number.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
            .Append(',');
        builder.Append(CsvField(port?.Protocol ?? string.Empty)).Append(',');
        builder.Append(CsvField(port?.Service ?? string.Empty)).Append(',');
        builder.AppendLine(CsvField(port?.ProductVersion ?? string.Empty));
    }

    private static string CsvField(string value)
    {
        if (value.Length == 0)
            return value;

        // Host names, vendors, and service banners come from the network; a spreadsheet would run one that
        // starts like a formula.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Returns the package-manager command that installs nmap on this platform.
    /// </summary>
    /// <returns>The command and whether it needs elevation, or <see langword="null"/> when no manager was found.</returns>
    private static (string Command, bool RequireElevation)? GetInstallCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return HostSystem.TryFindExecutable("winget.exe", out _)
                ? (
                    "winget.exe install --id \"Insecure.Nmap\" --exact --source winget --silent "
                        + "--disable-interactivity --accept-package-agreements --accept-source-agreements",
                    false
                )
                : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return HostSystem.TryFindExecutable("brew", out _)
                ? ("brew install nmap", false)
                : null;
        }

        if (!OperatingSystem.IsLinux())
            return null;

        return LinuxRuntime.PackageManager switch
        {
            LinuxPackageManager.Apt => ("apt install -y nmap", true),
            LinuxPackageManager.Dnf or LinuxPackageManager.Dnf5 or LinuxPackageManager.Yum => (
                $"{LinuxRuntime.PackageManager.CommandName} install -y nmap",
                true
            ),
            LinuxPackageManager.Pacman => ("pacman -S --noconfirm nmap", true),
            _ => null,
        };
    }
}

/// <summary>
/// A row of the scanner grid: either a host or one of its open ports.
/// </summary>
public sealed class NetworkScannerTreeNode
{
    private NetworkScannerTreeNode(
        NetworkScannerHost? host,
        NetworkScannerPort? port,
        NetworkScannerTreeNode? parent,
        IReadOnlyList<NetworkScannerTreeNode> children
    )
    {
        Host = host;
        Port = port;
        Parent = parent;
        Children = children;
    }

    public NetworkScannerHost? Host { get; }
    public NetworkScannerPort? Port { get; }
    public NetworkScannerTreeNode? Parent { get; }
    public IReadOnlyList<NetworkScannerTreeNode> Children { get; }

    public string Name =>
        Host is not null
            ? string.IsNullOrWhiteSpace(Host.Hostname)
                ? Host.Address
                : $"{Host.Address} ({Host.Hostname})"
            : $"{Port!.Number}/{Port.Protocol}"
                + (
                    string.IsNullOrWhiteSpace(Port.Service)
                        ? string.Empty
                        : $" ({Port.Service.ToUpperInvariant()})"
                );

    public string State => Host?.State ?? Port?.State ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether the host is up or the port is open.
    /// </summary>
    public bool IsStateGood =>
        State.Equals("up", StringComparison.OrdinalIgnoreCase)
        || State.Equals("open", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the host is down or the port is closed or filtered.
    /// </summary>
    public bool IsStateBad =>
        State.Equals("down", StringComparison.OrdinalIgnoreCase)
        || State.Equals("closed", StringComparison.OrdinalIgnoreCase)
        || State.Equals("filtered", StringComparison.OrdinalIgnoreCase);

    public bool HasStateStatus => IsStateGood || IsStateBad;
    public string MacAddress => Host?.MacAddress ?? string.Empty;
    public string Vendor => Host?.Vendor ?? string.Empty;
    public int? PortCount => Host?.OpenPortCount;
    public string Protocol => Port?.Protocol ?? string.Empty;
    public string Service => Port?.Service ?? string.Empty;
    public string Version => Port?.ProductVersion ?? string.Empty;
    public string OperatingSystem => Host?.OperatingSystem ?? string.Empty;

    public string ChangeText =>
        Host?.Change switch
        {
            NetworkScannerChange.New => App.Localization["NetworkScanner.Change.New"],
            NetworkScannerChange.PortsChanged => App.Localization[
                "NetworkScanner.Change.PortsChanged"
            ],
            NetworkScannerChange.Gone => App.Localization["NetworkScanner.Change.Gone"],
            _ => string.Empty,
        };

    /// <summary>
    /// Orders host rows by their numeric address and port rows by port number, then protocol.
    /// </summary>
    public static int CompareByAddress(NetworkScannerTreeNode? left, NetworkScannerTreeNode? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        if (left.Host is not null && right.Host is not null)
        {
            var byKey = left.Host.GetSortKey().CompareTo(right.Host.GetSortKey());
            return byKey != 0
                ? byKey
                : string.Compare(left.Host.Address, right.Host.Address, StringComparison.OrdinalIgnoreCase);
        }

        if (left.Port is not null && right.Port is not null)
        {
            var byNumber = left.Port.Number.CompareTo(right.Port.Number);
            return byNumber != 0
                ? byNumber
                : string.Compare(left.Port.Protocol, right.Port.Protocol, StringComparison.OrdinalIgnoreCase);
        }

        // Hosts and ports are never siblings; keep a consistent order should it happen.
        return left.Host is not null ? -1 : 1;
    }

    public static NetworkScannerTreeNode ForHost(NetworkScannerHost host)
    {
        var children = new List<NetworkScannerTreeNode>(host.Ports.Count);
        var node = new NetworkScannerTreeNode(host, null, null, children);
        foreach (var port in host.Ports)
            children.Add(new NetworkScannerTreeNode(null, port, node, []));

        return node;
    }
}
