using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using ObservableCollections;
using StageKit.Primitives;
using StageKit.Primitives.System;
using StageKit.Runtime.System;

namespace NetSonar.Avalonia.ViewModels;

public partial class SpeedTestPageModel : PageViewModelBase
{
    private readonly DispatcherTimer _timer = new();

    private CancellationTokenSource? _cancellationTokenSource;


    private DataGrid _speedTestDataGrid = null!;

    // Set while the server list is rebuilt, so the selection changes it causes do not overwrite the stored server id.
    private bool _isRestoringServer;

    public SpeedTestPageModel()
    {
        ResultsView = Results.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
        Results.CollectionChanged += (in args) =>
        {
            RemoveAllResultsCommand.NotifyCanExecuteChanged();
            ExportAllResultsToJsonCommand.NotifyCanExecuteChanged();
        };

        _timer.Tick += TimerOnTick;
        UpdateAutoSpeedTestTimer();

        AppSettings.SpeedTest.PropertyChanged += SpeedTestOnPropertyChanged;

        if (Design.IsDesignMode)
        {
            IsExecutableAvailable = true;
        }
    }

    public override int Index => 3;
    public override string DisplayName => App.Localization["Navigation.SpeedTest"];
    public override MaterialIconKind Icon => MaterialIconKind.SpeedometerMedium;

    public static ObservableList<SpeedTestResult> Results => SpeedTestsFile.Instance.Items;

    public NotifyCollectionChangedSynchronizedViewList<SpeedTestResult> ResultsView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedResultsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedResultsToJsonCommand))]
    public partial SpeedTestResult? SelectedResult { get; set; }

    [ObservableProperty] public partial SpeedTestResult? DisplayResult { get; set; } = new();


    [ObservableProperty] public partial bool IsExecutableAvailable { get; private set; }

    [ObservableProperty] public partial bool IsExecutableInstalling { get; private set; }

    public bool CanExecutableAutoInstall
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return HostSystem.TryFindExecutable("brew", out _);
            }

            if (OperatingSystem.IsWindows())
            {
                return HostSystem.TryFindExecutable("winget.exe", out _);
            }

            // Linux repositories only carry the unrelated Python speedtest-cli; the Ookla CLI needs its own
            // repository, so it is installed manually.
            return false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedResultsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedResultsToJsonCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllResultsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAllResultsToJsonCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial ObservableList<SpeedTestResultServer?> Servers { get; private set; } = [];

    [ObservableProperty] public partial SpeedTestResultServer? SelectedServer { get; set; }

    [ObservableProperty] public partial string? SpeedTestVersion { get; private set; }

    [ObservableProperty]
    public partial int AngularMeterMaxValue { get; set; } = AppSettings.SpeedTest.InitialSpeedGaugeRange;

    protected internal override void OnInitialized()
    {
        base.OnInitialized();
        _ = CheckSpeedTestAvailable();

        _speedTestDataGrid.KeyUp += SpeedTestDataGridOnKeyUp;
    }

    private void SpeedTestDataGridOnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            if (e.Key == Key.Delete)
            {
                if (RemoveSelectedResultsCommand.CanExecute(null))
                {
                    RemoveSelectedResultsCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            return;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(SelectedResult))
        {
            DisplayResult = SelectedResult;
        }
        else if (e.PropertyName == nameof(DisplayResult))
        {
            if (DisplayResult is null)
            {
                AngularMeterMaxValue = AppSettings.SpeedTest.InitialSpeedGaugeRange;
                return;
            }

            var maxSpeed = Math.Max(DisplayResult.Download.BandwidthMbps, DisplayResult.Upload.BandwidthMbps);
            var increment = AppSettings.SpeedTest.SpeedGaugeRangeIncrement;
            var fittedRange = Math.Max(
                AppSettings.SpeedTest.InitialSpeedGaugeRange,
                (int)(Math.Ceiling(maxSpeed / increment) * increment)
            );

            // A live test only widens the gauge, so it does not shrink back between updates; a selected result
            // gets a range that fits it, so a slow result is not drawn on the scale of a faster one.
            if (!IsRunning || fittedRange > AngularMeterMaxValue)
            {
                AngularMeterMaxValue = fittedRange;
            }
        }
    }

    private void SpeedTestOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.SpeedTest.AutoSpeedTest))
        {
            UpdateAutoSpeedTestTimer();
        }
        else if (e.PropertyName == nameof(AppSettings.SpeedTest.AutoSpeedTestIntervalSeconds))
        {
            UpdateAutoSpeedTestTimer();
        }
    }

    private void UpdateAutoSpeedTestTimer()
    {
        var intervalSeconds = AppSettings.SpeedTest.AutoSpeedTestIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            _timer.IsEnabled = false;
            return;
        }

        // The setting is only bounded by the UI; a hand-edited file must not start a test every second.
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, SpeedTestSettings.DefaultMinimumAutoSpeedTestIntervalSeconds));
        _timer.IsEnabled = AppSettings.SpeedTest.AutoSpeedTest;
    }

    private async void TimerOnTick(object? sender, EventArgs e)
    {
        // An automatic test must never prompt: it stays idle until the user accepted Ookla's terms.
        if (!AppSettings.SpeedTest.OoklaTermsAccepted) return;

        // The timer runs before the page was ever opened, so the CLI may not have been looked up yet.
        if (SpeedTestService.ExecutablePath is null)
        {
            await CheckSpeedTestAvailable();
        }

        if (!IsExecutableAvailable) return;
        await RunSpeedTestAsync();
    }

    /// <summary>
    /// Runs <paramref name="onAccepted"/> once the user accepted Ookla's terms, asking first when they were not
    /// accepted yet. Declining or dismissing the question does nothing.
    /// </summary>
    private void EnsureTermsAccepted(Action onAccepted)
    {
        if (AppSettings.SpeedTest.OoklaTermsAccepted)
        {
            onAccepted();
            return;
        }

        CreateMessageBoxYesNo(NotificationType.Information,
                App.Localization["SpeedTest.Terms.Title"],
                App.Localization["SpeedTest.Terms.Message"],
                _ =>
                {
                    AppSettings.SpeedTest.OoklaTermsAccepted = true;
                    onAccepted();
                })
            .TryShow();
    }

    /// <summary>
    /// Withdraws the acceptance of Ookla's terms, so the next test or server lookup asks again.
    /// </summary>
    [RelayCommand]
    public void RevokeOoklaTerms()
    {
        AppSettings.SpeedTest.OoklaTermsAccepted = false;
    }

    [RelayCommand]
    public async Task CheckSpeedTestAvailable()
    {
        if (Design.IsDesignMode) return;

        try
        {
            // Detection starts the CLI to read its version, which must not block the UI thread.
            var version = await Task.Run(() => SpeedTestService.DetectAsync());
            SpeedTestVersion = version;
            IsExecutableAvailable = version is not null;
        }
        catch (Exception e)
        {
            IsExecutableAvailable = false;
            App.ShowExceptionToast(e, App.Localization["SpeedTest.Error"]);
        }

        // Listing servers runs the CLI with Ookla's terms accepted, so it waits for the user's consent.
        if (IsExecutableAvailable && Servers.Count == 0 && AppSettings.SpeedTest.OoklaTermsAccepted)
        {
            await LoadServerListAsync();
        }
    }

    [RelayCommand]
    public async Task AutoInstallDependency()
    {
        if (IsExecutableInstalling) return;
        IsExecutableInstalling = true;
        ProcessXToast toast = new()
        {
            Title = App.Localization["SpeedTest.Install.Title"],
            ShowOnlySuccessGenericMessage = true,
            SuccessGenericMessage = App.Localization["SpeedTest.Install.Success"],
            ErrorGenericMessage = App.Localization["SpeedTest.Install.Error"]
        };

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                await ProcessXExtensions.ExecuteHandled(
                    [
                        "brew tap teamookla/speedtest",
                        "brew install speedtest --force"
                    ],
                    toast,
                    true);
            }
            else if (OperatingSystem.IsWindows())
            {
                await ProcessXExtensions.ExecuteHandled(
                    "winget.exe install --id \"Ookla.Speedtest.CLI\" --exact --source winget --accept-source-agreements --accept-package-agreements  --disable-interactivity --silent --force",
                    toast,
                    true);
            }
        }
        finally
        {
            IsExecutableInstalling = false;
        }

        await CheckSpeedTestAvailable();
    }

    [RelayCommand]
    public void UpdateServerList()
    {
        EnsureTermsAccepted(() => _ = LoadServerListAsync());
    }

    private async Task LoadServerListAsync()
    {
        var servers = await SpeedTestService.GetServerList();

        // The list is replaced rather than refilled, as the bound ComboBox does not observe an ObservableList.
        ObservableList<SpeedTestResultServer?> list = [null];
        list.AddRange(servers);

        var serverId = AppSettings.SpeedTest.ServerId;
        _isRestoringServer = true;
        try
        {
            Servers = list;
            SelectedServer = serverId == 0 ? null : servers.FirstOrDefault(server => server.Id == serverId);
        }
        finally
        {
            _isRestoringServer = false;
        }
    }

    partial void OnSelectedServerChanged(SpeedTestResultServer? value)
    {
        if (_isRestoringServer) return;
        AppSettings.SpeedTest.ServerId = value?.Id ?? 0;
    }

    [RelayCommand]
    public void StartSpeedTest()
    {
        if (IsRunning) return;

        EnsureTermsAccepted(() => _ = StartAcceptedSpeedTestAsync());
    }

    private async Task StartAcceptedSpeedTestAsync()
    {
        // The first test right after accepting the terms has no server list yet, so the remembered server is
        // restored before it starts.
        if (Servers.Count == 0)
        {
            await LoadServerListAsync();
        }

        await RunSpeedTestAsync();
    }

    private async Task RunSpeedTestAsync()
    {
        if (IsRunning) return;

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        IsRunning = true;

        try
        {
            await foreach (var result in SpeedTestService.StartSpeedTest(SelectedServer, cancellationTokenSource.Token))
            {
                if (result.HasError)
                {
                    App.ShowToast(NotificationType.Error, App.Localization["SpeedTest.Error"], result.Error);
                    continue;
                }

                if (!Enum.TryParse(result.Type.AsSpan(), true, out SpeedTestType resultType)) continue;
                switch (resultType)
                {
                    case SpeedTestType.TestStart:
                        AngularMeterMaxValue = AppSettings.SpeedTest.InitialSpeedGaugeRange;
                        DisplayResult = result;
                        break;
                    // Without a testStart line, or with the display cleared, the update starts from itself.
                    case SpeedTestType.Ping:
                        DisplayResult = (DisplayResult ?? result) with
                        {
                            Type = result.Type,
                            ISP = result.ISP,
                            PacketLoss = result.PacketLoss,
                            Timestamp = result.Timestamp,
                            Error = result.Error,
                            Ping = result.Ping
                        };
                        break;
                    case SpeedTestType.Download:
                        DisplayResult = (DisplayResult ?? result) with
                        {
                            Type = result.Type,
                            ISP = result.ISP,
                            PacketLoss = result.PacketLoss,
                            Timestamp = result.Timestamp,
                            Error = result.Error,
                            Download = result.Download
                        };
                        break;
                    case SpeedTestType.Upload:
                        DisplayResult = (DisplayResult ?? result) with
                        {
                            Type = result.Type,
                            ISP = result.ISP,
                            PacketLoss = result.PacketLoss,
                            Timestamp = result.Timestamp,
                            Error = result.Error,
                            Upload = result.Upload
                        };
                        break;
                    case SpeedTestType.Result:
                        Results.Insert(0, result);
                        SelectedResult = result;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // The active speed test was stopped by the user.
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["SpeedTest.Error"]);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
            {
                _cancellationTokenSource = null;
            }

            cancellationTokenSource.Dispose();
            IsRunning = false;
        }
    }

    [RelayCommand]
    public async Task StopSpeedTest()
    {
        var cancellationTokenSource = _cancellationTokenSource;
        if (cancellationTokenSource is null) return;

        await cancellationTokenSource.CancelAsync();
    }

    private bool CanModifySelectedResults() => SelectedResult is not null && !IsRunning;

    private bool CanModifyAllResults() => Results.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanModifySelectedResults))]
    public async Task ExportSelectedResultsToJson()
    {
        if (_speedTestDataGrid.SelectedIndex == -1) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = FileUtilities.SanitizeFileName(
                $"Speedtests#{_speedTestDataGrid.SelectedItems.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, _speedTestDataGrid.SelectedItems, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Results.Title"],
                App.Localization.Format("Export.Results.Success", _speedTestDataGrid.SelectedItems.Count, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"], toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Results.Title"],
                App.Localization["Export.Results.Error"]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyAllResults))]
    public async Task ExportAllResultsToJson()
    {
        if (Results.Count == 0) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName =
                FileUtilities.SanitizeFileName($"Speedtests#{Results.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, Results, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Results.Title"],
                App.Localization.Format("Export.Results.Success", Results.Count, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"], toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Results.Title"],
                App.Localization["Export.Results.Error"]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelectedResults))]
    public void RemoveSelectedResults()
    {
        if (_speedTestDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("SpeedTest.RemoveSelected.Title", _speedTestDataGrid.SelectedItems.Count),
                App.Localization.Format("SpeedTest.RemoveSelected.Message", _speedTestDataGrid.SelectedItems.Count),
                _ => Results.RemoveRange(_speedTestDataGrid.SelectedItems))
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(CanModifyAllResults))]
    public void RemoveAllResults()
    {
        if (Results.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("SpeedTest.RemoveAll.Title", Results.Count),
                App.Localization.Format("SpeedTest.RemoveAll.Message", Results.Count), _ => Results.Clear())
            .TryShow();
    }

    public void SetControls(DataGrid speedTestDataGrid)
    {
        _speedTestDataGrid = speedTestDataGrid;
        _speedTestDataGrid.ExtendDataGridShortcuts();
    }
}