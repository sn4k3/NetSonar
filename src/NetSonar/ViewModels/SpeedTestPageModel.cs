using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.SystemOS;
using ObservableCollections;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace NetSonar.Avalonia.ViewModels;

public partial class SpeedTestPageModel : PageViewModelBase
{
    public override int Index => 2;
    public override string DisplayName => "Speed Test";
    public override MaterialIconKind Icon => MaterialIconKind.SpeedometerMedium;

    private readonly Timer _timer = new(3_600_000);

    private CancellationTokenSource _cancellationTokenSource = new();

    public static ObservableList<SpeedTestResult> Results => SpeedTestsFile.Instance.Items;

    public NotifyCollectionChangedSynchronizedViewList<SpeedTestResult> ResultsView { get; }

    [ObservableProperty]
    public partial SpeedTestResult? SelectedResult { get; set; }

    [ObservableProperty]
    public partial SpeedTestResult? DisplayResult { get; set; } = new();


    [ObservableProperty]
    public partial bool IsExecutableAvailable { get; private set; }

    [ObservableProperty]
    public partial bool IsExecutableInstalling { get; private set; }

    public bool CanExecutableAutoInstall
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return SystemAware.TryFindEnvFile("brew", out _);
            }
            if (OperatingSystem.IsWindows())
            {
                return SystemAware.TryFindEnvFile("winget.exe", out _);
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxOS.PackageManager != LinuxOS.LinuxPackageManager.Unknown;
            }

            return true;
        }
    }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial ObservableList<SpeedTestResultServer?> Servers { get; private set; } = [];

    [ObservableProperty]
    public partial SpeedTestResultServer? SelectedServer { get; set; }

    [ObservableProperty]
    public partial string? SpeedTestVersion { get; private set; }

    [ObservableProperty]
    public partial int AngularMeterMaxValue { get; set; } = AppSettings.SpeedTest.InitialSpeedGaugeRange;

    [ObservableProperty]
    public partial int AngularMeterSlowSpeedSeries { get; set; } = 100;

    [ObservableProperty]
    public partial int AngularMeterMediumSpeedSeries { get; set; } = 100;

    [ObservableProperty]
    public partial int AngularMeterFastSpeedSeries { get; set; } = 100;



    private DataGrid _speedTestDataGrid = null!;

    public SpeedTestPageModel()
    {
        ResultsView = Results.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);

        AngularMeterSlowSpeedSeries = AppSettings.SpeedTest.InitialSpeedGaugeRange / 4;
        AngularMeterMediumSpeedSeries = AppSettings.SpeedTest.InitialSpeedGaugeRange / 4;
        AngularMeterFastSpeedSeries = AppSettings.SpeedTest.InitialSpeedGaugeRange - AngularMeterSlowSpeedSeries - AngularMeterMediumSpeedSeries;

        _timer.Interval = AppSettings.SpeedTest.AutoSpeedTestIntervalSeconds * 1000;
        _timer.Enabled = AppSettings.SpeedTest.AutoSpeedTest;
        _timer.Elapsed += TimerOnElapsed;

        AppSettings.SpeedTest.PropertyChanged += SpeedTestOnPropertyChanged;

        if (Design.IsDesignMode)
        {
            IsExecutableAvailable = true;
        }
    }

    protected internal override void OnInitialized()
    {
        base.OnInitialized();
        CheckSpeedTestAvailable();
        if (IsExecutableAvailable)
        {
            SpeedTestVersion = SpeedTestService.GetSpeedTestVersion().GetAwaiter().GetResult();
            _ = UpdateServerList();
        }

        _speedTestDataGrid.KeyUp += SpeedTestDataGridOnKeyUp;
    }

    private void SpeedTestDataGridOnKeyUp(object? sender, KeyEventArgs e)
    {

        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            if (e.Key == Key.Delete)
            {
                RemoveSelectedResults();
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
            if (maxSpeed >= AngularMeterMaxValue)
            {
                // Calculate the next multiple of 500 that is greater than or equal to Speed
                var nextStep = ((maxSpeed / AppSettings.SpeedTest.SpeedGaugeRangeIncrement) + 1) * AppSettings.SpeedTest.SpeedGaugeRangeIncrement;
                AngularMeterMaxValue = nextStep;
            }
        }
    }

    private void SpeedTestOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.SpeedTest.AutoSpeedTest))
        {
            _timer.Enabled = AppSettings.SpeedTest.AutoSpeedTest;
        }
        else if (e.PropertyName == nameof(AppSettings.SpeedTest.AutoSpeedTestIntervalSeconds))
        {
            _timer.Interval = AppSettings.SpeedTest.AutoSpeedTestIntervalSeconds * 1000;
        }
    }

    private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        _ = StartSpeedTest();
    }

    [RelayCommand]
    public void CheckSpeedTestAvailable()
    {
        IsExecutableAvailable = SpeedTestService.IsSpeedTestAvailable();
    }

    [RelayCommand]
    public async Task AutoInstallDependency()
    {
        if (IsExecutableInstalling) return;
        IsExecutableInstalling = true;
        ProcessXToast toast = new()
        {
            Title = "Speedtest CLI installation",
            ShowOnlySuccessGenericMessage = true,
            SuccessGenericMessage = "Speedtest CLI installed successfully.",
            ErrorGenericMessage = "Failed to install Speedtest CLI. Please install it manually from https://www.speedtest.net/apps/cli"
        };

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
        else if (OperatingSystem.IsLinux())
        {
            if (LinuxOS.PackageManager != LinuxOS.LinuxPackageManager.Unknown)
            {
                await ProcessXExtensions.ExecuteHandled(
                    $"{LinuxOS.PackageManagerCommand} install speedtest-cli",
                    toast,
                    true);
            }
        }
        CheckSpeedTestAvailable();
        IsExecutableInstalling = false;
    }

    [RelayCommand]
    public async Task UpdateServerList()
    {
        var servers = await SpeedTestService.GetServerList();
        Servers.Clear();
        Servers.Add(null);
        Servers.AddRange(servers);
    }

    [RelayCommand]
    public async Task StartSpeedTest()
    {
        if (IsRunning) return;
        IsRunning = true;

        try
        {
            await foreach (var result in SpeedTestService.StartSpeedTest(SelectedServer, _cancellationTokenSource.Token))
            {
                if (result.HasError)
                {
                    App.ShowToast(NotificationType.Error, "Error while speed testing", result.Error);
                    continue;
                }

                if (!Enum.TryParse(result.Type.AsSpan(), true, out SpeedTestType resultType)) continue;
                switch (resultType)
                {
                    case SpeedTestType.TestStart:
                        AngularMeterMaxValue = AppSettings.SpeedTest.InitialSpeedGaugeRange;
                        DisplayResult = result;
                        break;
                    case SpeedTestType.Ping:
                        DisplayResult = DisplayResult! with
                        {
                            Type = result.Type,
                            ISP = result.ISP,
                            PacketLoss = result.PacketLoss,
                            Timestamp = result.Timestamp,
                            Error = result.Error,
                            Ping = result.Ping,
                        };
                        break;
                    case SpeedTestType.Download:
                        DisplayResult = DisplayResult! with
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
                        DisplayResult = DisplayResult! with
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
        catch (OperationCanceledException)
        {
            //App.ShowToast(NotificationType.Information, "Speed test canceleed");
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Error while speed testing");
        }
        finally
        {
            IsRunning = false;
        }

    }

    [RelayCommand]
    public async Task StopSpeedTest()
    {
        await _cancellationTokenSource.CancelAsync();
        IsRunning = false;
    }

    [RelayCommand]
    public async Task ExportSelectedResultsToJson()
    {
        if (_speedTestDataGrid.SelectedIndex == -1) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"Speedtests#{_speedTestDataGrid.SelectedItems.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
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
                "Export results to JSON",
                $"The {_speedTestDataGrid.SelectedItems.Count} results were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
            );

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export results to JSON", "Error while trying to export results:");
        }
    }

    [RelayCommand]
    public static async Task ExportAllResultsToJson()
    {
        if (Results.Count == 0) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"Speedtests#{Results.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
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
                "Export results to JSON",
                $"The {Results.Count} results were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
            );

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export results to JSON", "Error while trying to export results:");
        }
    }

    [RelayCommand]
    public void RemoveSelectedResults()
    {
        if (_speedTestDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to remove the {_speedTestDataGrid.SelectedItems.Count} selected results?",
                $"""
                 You are about to remove the {_speedTestDataGrid.SelectedItems.Count} selected results.
                 Are you sure you want to continue?
                 """, _ => Results.RemoveRange(_speedTestDataGrid.SelectedItems))
            .TryShow();

    }

    [RelayCommand]
    public static void RemoveAllResults()
    {
        if (Results.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to remove all the {Results.Count} results?",
                $"""
                 You are about to remove all the {Results.Count} results.
                 Are you sure you want to continue?
                 """, _ => Results.Clear())
            .TryShow();

    }

    public void SetControls(DataGrid speedTestDataGrid)
    {
        _speedTestDataGrid = speedTestDataGrid;
        _speedTestDataGrid.ExtendDataGridShortcuts();
    }
}