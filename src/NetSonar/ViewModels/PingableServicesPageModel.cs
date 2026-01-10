using System;
using System.Threading.Tasks;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Network;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Material.Icons;
using NetSonar.Avalonia.ViewModels.Dialogs;
using SukiUI.Dialogs;
using Timer = System.Timers.Timer;
using System.IO;
using System.Text.Json;
using Avalonia.Collections;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.SystemOS;
using ObservableCollections;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.Presenters;
using NetSonar.Avalonia.ViewModels.Fragments;
using NetSonar.Avalonia.Views;
using ZLinq;

namespace NetSonar.Avalonia.ViewModels;

public partial class PingableServicesPageModel : PageViewModelBase
{
    public const string NumericUpDownTimeFormat = "#,#0.##";
    public const double NumericUpDownPingIncrement = 0.50;
    public const double NumericUpDownTimeoutIncrement = 0.50;
    public override int Index => 0;
    public override string DisplayName => "Pings";
    public override MaterialIconKind Icon => MaterialIconKind.Radar;

    private readonly Timer _timer = new(500);

    public static int ServicesCount => Services.Count;
    [ObservableProperty] public partial int ServicesFailedCount { get; private set; }
    [ObservableProperty] public partial int ServicesSucceededCount { get; private set; }


    [ObservableProperty] public partial string FilterText { get; set; } = string.Empty;

    public static ObservableList<PingableService> Services => PingableServicesFile.Instance.Items;

    public IWritableSynchronizedView<PingableService, PingableService> ServicesView { get; }
    public NotifyCollectionChangedSynchronizedViewList<PingableService> ServicesViewCollection { get; }

    public DataGridCollectionView ServicesGroupView { get; }


    public PingableService? SelectedService
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            //_pingRepliesSortColumn.Sort(ListSortDirection.Descending);
            SelectedServicePingReplies = value?.Pings.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
            PingGraphModel.Services = _servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray();
        }
    }

    public NotifyCollectionChangedSynchronizedViewList<PingableServiceReply>? SelectedServicePingReplies
    {
        get;
        set
        {
            var oldValue = field;
            if (!SetProperty(ref field, value)) return;
            oldValue?.Dispose();
        }
    }


    private DataGrid _servicesDataGrid = null!;
    private DataGrid _servicesPingsDataGrid = null!;

    public PingableServiceGraphFragmentModel PingGraphModel { get; } = new();


    public PingableServicesPageModel()
    {
        ServicesView = Services.CreateWritableView(service => service);
        ServicesViewCollection = ServicesView.ToWritableNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current);

        ServicesGroupView = new DataGridCollectionView(ServicesViewCollection);

        /*App.Theme.OnColorThemeChanged += theme =>
        {
            if (RepliesGraphSeries.Length == 0) return;
            if (RepliesGraphSeries[0] is ColumnSeries<double> column)
            {
                column.Fill = new SolidColorPaint(new SKColor(
                    theme.Primary.R,
                    theme.Primary.G,
                    theme.Primary.B));
            }
        };*/

        Services.CollectionChanged += (in args) =>
        {
            OnPropertyChanged(nameof(ServicesCount));
        };

        AppSettings.PingServices.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(PingServicesSettings.GridGroupBy))
            {
                RegroupServicesGrid();
            }
        };
    }

    protected internal override void OnInitialized()
    {
        RegroupServicesGrid();
        foreach (var column in _servicesDataGrid.Columns)
        {
            var columnId = column.Header?.ToString() ?? string.Empty;

            if (AppSettings.PingServices.GridColumnOrder.TryGetValue(columnId, out var displayIndex))
            {
                column.DisplayIndex = Math.Clamp(displayIndex, 0, _servicesDataGrid.Columns.Count - 1);
            }
        }

        _servicesDataGrid.ItemsSource = ServicesGroupView;

        _servicesDataGrid.KeyUp += ServicesDataGridOnKeyUp;
        _servicesPingsDataGrid.Sorting += ServicesPingsDataGridOnSorting;
        _servicesPingsDataGrid.LoadingRow += ServicesPingsDataGridOnLoadingRow;
        _servicesDataGrid.ColumnDisplayIndexChanged += ServicesDataGridOnColumnDisplayIndexChanged;

        _timer.Elapsed += Timer_Elapsed;
        DispatcherTimer.RunOnce(_timer.Start, TimeSpan.FromSeconds(2), DispatcherPriority.ApplicationIdle);
        //Dispatcher.UIThread.Post(_timer.Start, DispatcherPriority.ApplicationIdle);


    }

    private void ServicesDataGridOnColumnDisplayIndexChanged(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        AppSettings.PingServices.GridColumnOrder[e.Column.Header?.ToString() ?? string.Empty] = Math.Clamp(e.Column.DisplayIndex, 0, _servicesDataGrid.Columns.Count - 1);
        AppSettings.DebouncedSave();
    }

    /*protected internal override void OnUnloaded()
    {
        _timer.Elapsed -= Timer_Elapsed;
        _timer.Stop();
        _timer.Dispose();
    }*/

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterText))
        {
            ReAttachFilters();
        }
        base.OnPropertyChanged(e);
    }

    private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        var failed = 0;
        var success = 0;

        foreach (var service in Services)
        {
            if (service.WasLastPingSucceeded)
            {
                success++;
            }
            else
            {
                failed++;
            }
        }

        ServicesFailedCount = failed;
        ServicesSucceededCount = success;

        var anyStatusChanged = false;

        using (var servicesToExecute = Services
                   .AsValueEnumerable()
                   .Where(host => host.CanTimerExecute)
                   .ToArrayPool())
        {
            switch (servicesToExecute.Size)
            {
                case 0:
                    return;
                case 1:
                    servicesToExecute.Array[0].Ping();
                    anyStatusChanged = servicesToExecute.Array[0].StatusChanged;
                    break;
                default:
                    Parallel.ForEach(servicesToExecute.ArraySegment, App.GetParallelOptions(), host =>
                    {
                        host.Ping();
                        if (host.StatusChanged) anyStatusChanged = true;
                    });
                    break;
            }
        }

        if (anyStatusChanged)
        {
            failed = 0;
            success = 0;

            foreach (var service in Services)
            {
                if (service.WasLastPingSucceeded)
                {
                    success++;
                }
                else
                {
                    failed++;
                }
            }

            ServicesFailedCount = failed;
            ServicesSucceededCount = success;

            if (!string.IsNullOrWhiteSpace(FilterText)) Dispatcher.UIThread.Post(ReAttachFilters);
        }
    }

    private void ServicesPingsDataGridOnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        /*var ctx = e.Row.DataContext;
        if (ctx is PingableServiceReply { IsFailed: true })
        {
            e.Row.Background = Brushes.DarkRed;
        }*/

        //e.Row.Background = Brushes.DarkRed;
        /*DataGridRow row = e.Row;
        row.Bind(TemplatedControl.BackgroundProperty, new Binding("IsFailed", BindingMode.OneWay)
        {
            Converter = new BoolErrorGridRowBackgroundConverter()
        });*/
    }

    private void ServicesDataGridOnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            if (e.Key == Key.Delete)
            {
                RemoveSelectedServices();
                e.Handled = true;
                return;
            }
            return;
        }
    }


    private void ServicesPingsDataGridOnSorting(object? sender, DataGridColumnEventArgs e)
    {
        //_pingRepliesSortColumn = e.Column;
    }


    private void RegroupServicesGrid()
    {
        ServicesGroupView.GroupDescriptions.Clear();
        switch (AppSettings.PingServices.GridGroupBy)
        {
            case PingServicesSettings.PingServicesGroupBy.None:
                break;
            default:
                ServicesGroupView.GroupDescriptions.Add(new DataGridPathGroupDescription(AppSettings.PingServices.GridGroupBy.ToString()));
                break;
        }
    }

    public void ReAttachFilters()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            ServicesView.ResetFilter();
            return;
        }

        ServicesView.AttachFilter(service =>
        {
            var splitText = FilterText.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in splitText)
            {
                if (word.Equals("status:ok", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("status:success", StringComparison.OrdinalIgnoreCase))
                {
                    return service.WasLastPingSucceeded;
                }

                if (word.Equals("status:fail", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("status:failed", StringComparison.OrdinalIgnoreCase))
                {
                    return service.WasLastPingFailed;
                }

                if (service.LastStatusStr.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.ProtocolType.ToString().Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.IpAddresses.AsValueEnumerable().Any(ipAddress => ipAddress.ToString().Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
                if (service.HostName.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.Group.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;

            }

            return false;
        });
    }

    [RelayCommand]
    public void ResetColumnsOrder()
    {
        AppSettings.PingServices.GridColumnOrder.Clear();
        for (var i = 0; i < _servicesDataGrid.Columns.Count; i++)
        {
            var column = _servicesDataGrid.Columns[i];
            column.DisplayIndex = i;
        }
    }

    [RelayCommand]
    public void ClearFilters()
    {
        FilterText = string.Empty;
    }

    [RelayCommand]
    public void AttachStatusSucceedFilter()
    {
        FilterText = "status:success";
    }

    [RelayCommand]
    public void AttachStatusFailedFilter()
    {
        FilterText = "status:failed";
    }

    [RelayCommand]
    public static void AddServices()
    {
        var dialog = DialogManager
            .CreateDialog()
            .WithViewModel(dialog => new AddPingServicesDialogModel(dialog));
        dialog.TryShow();
    }

    [RelayCommand]
    public void PauseSelectedService()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        if (_servicesDataGrid.SelectedItem is not PingableService service) return;
        service.IsEnabled = false;
    }


    [RelayCommand]
    public void PauseSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        foreach (PingableService service in _servicesDataGrid.SelectedItems)
        {
            service.IsEnabled = false;
        }
    }

    [RelayCommand]
    public static void PauseAllServices()
    {
        if (Services.Count == 0) return;
        foreach (var service in Services)
        {
            service.IsEnabled = false;
        }
    }

    [RelayCommand]
    public void ResumeSelectedService()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        if (_servicesDataGrid.SelectedItem is not PingableService service) return;
        service.IsEnabled = true;
    }

    [RelayCommand]
    public void ResumeSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        foreach (PingableService service in _servicesDataGrid.SelectedItems)
        {
            service.IsEnabled = true;
        }
    }

    [RelayCommand]
    public static void ResumeAllServices()
    {
        if (Services.Count == 0) return;
        foreach (var service in Services)
        {
            service.IsEnabled = true;
        }
    }

    [RelayCommand]
    public void ResetServiceStatisticsForSelectedItem()
    {
        if (_servicesDataGrid.SelectedIndex == -1 || _servicesDataGrid.SelectedItem is not PingableService service) return;

        CreateMessageBoxYesNo(NotificationType.Warning,
                "Are you sure you want to reset the selected ping host?",
                $"""
                 You are about to reset the selected ping host statistics.

                 IP Address: {service.IpEndPoint}
                 Host: {service.HostName}

                 Are you sure you want to continue?
                 """, _ =>
                {
                    service.Clear();
                })
            .TryShow();

    }

    [RelayCommand]
    public void ResetServiceStatisticsForSelectedItems()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to reset the {_servicesDataGrid.SelectedItems.Count} selected ping hosts?",
                $"""
                 You are about to reset the {_servicesDataGrid.SelectedItems.Count} selected ping hosts statistics.
                 Are you sure you want to continue?
                 """, _ =>
                {
                    foreach (PingableService service in _servicesDataGrid.SelectedItems)
                    {
                        service.Clear();
                    }
                })
            .TryShow();

    }

    [RelayCommand]
    public static void ResetAllServicesStatistics()
    {
        if (Services.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to reset all the {Services.Count} ping hosts?",
                $"""
                 You are about to reset all the {Services.Count} ping hosts statistics.
                 Are you sure you want to continue?
                 """, _ =>
                {
                    foreach (var ping in Services)
                    {
                        ping.Clear();
                    }
                })
            .TryShow();

    }

    [RelayCommand]
    public void RemoveSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to remove the {_servicesDataGrid.SelectedItems.Count} selected ping hosts?",
                $"""
                 You are about to remove the {_servicesDataGrid.SelectedItems.Count} selected ping hosts.
                 Are you sure you want to continue?
                 """, _ => Services.RemoveRange(_servicesDataGrid.SelectedItems))
            .TryShow();

    }

    [RelayCommand]
    public static void RemoveAllServices()
    {
        if (Services.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                $"Are you sure you want to remove all the {Services.Count} ping hosts?",
                $"""
                        You are about to remove all the {Services.Count} ping hosts.
                        Are you sure you want to continue?
                        """, _ => Services.Clear())
            .TryShow();

    }

    [RelayCommand]
    public async Task ExportSelectedServicesToJson()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"services#{_servicesDataGrid.SelectedItems.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, _servicesDataGrid.SelectedItems, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                "Export services to JSON",
                $"The {_servicesDataGrid.SelectedItems.Count} services were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
            );

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export services to JSON", "Error while trying to export services:");
        }
    }

    [RelayCommand]
    public static async Task ExportAllServicesToJson()
    {
        if (Services.Count == 0) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"services#{Services.Count}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, Services, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                "Export services to JSON",
                $"The {Services.Count} services were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
            );

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export services to JSON", "Error while trying to export services:");
        }
    }


    [RelayCommand]
    public async Task ExportCurrentPingsToCsv()
    {
        if (SelectedService is null || !TopLevel.StorageProvider.CanSave) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"{SelectedService.ProtocolType.ToString().ToLowerInvariant()}-{SelectedService.HostName}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.csv"),
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
            await textWriter.WriteLineAsync(string.Format("{0};{1};{2};{3};{4};{5};{6};{7}",
                nameof(PingableServiceReply.IsSucceeded),
                nameof(PingableServiceReply.Status),
                nameof(PingableServiceReply.StatusCode),
                nameof(PingableServiceReply.IpEndPoint),
                nameof(PingableServiceReply.SentDateTime),
                nameof(PingableServiceReply.Time),
                nameof(PingableServiceReply.Ttl),
                nameof(PingableServiceReply.BufferLength)
            ));

            var count = SelectedService.Pings.Count;
            for (var i = 0; i < SelectedService.Pings.Count; i++)
            {
                var reply = SelectedService.Pings[i];
                await textWriter.WriteLineAsync(string.Format("{0};{1};{2};{3};{4};{5};{6};{7}",
                    reply.IsSucceeded,
                    reply.Status,
                    reply.StatusCode,
                    reply.IpEndPoint,
                    reply.SentDateTime,
                    reply.Time,
                    reply.Ttl,
                    reply.BufferLength
                ));
            }

            App.ShowToast(NotificationType.Success,
                "Export ping to CSV",
                $"The {count} pings in \"{SelectedService.HostName}\" were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
                );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export pings to CSV", "Error while trying to export pings:");
        }
    }

    [RelayCommand]
    public async Task ExportCurrentPingsToJson()
    {
        if (SelectedService is null || !TopLevel.StorageProvider.CanSave || SelectedService.Pings.Count == 0) return;
        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = StringExtensions.GetSafeFilename($"{SelectedService.ProtocolType.ToString().ToLowerInvariant()}-{SelectedService.HostName}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            var count = SelectedService.Pings.Count;
            await JsonSerializer.SerializeAsync(stream, SelectedService.Pings, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                "Export ping to JSON",
                $"The {count} pings in \"{SelectedService.HostName}\" were exported to \"{file.Name}\".",
                new ToastActionButton("Open file", toast => { SystemAware.StartProcess(filePath); }),
                new ToastActionButton("Open folder", toast => { SystemAware.SelectFileOnExplorer(filePath); })
                );

        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, "Export pings to JSON", "Error while trying to export pings:");
        }
    }

    [RelayCommand]
    public void OpenGraphInNewWindow()
    {
        if (SelectedService is null) return;

        GenericWindow window = new()
        {
            Title = $"{App.SoftwareWithVersion}  Chart",
            CanPin = true,
            Content = new ContentPresenter
            {
                Margin = new Thickness(20),
                Content = new PingableServiceGraphFragmentModel(_servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray())
            }
        };

        if (_servicesDataGrid.SelectedItems.Count == 1)
        {
            var label = string.IsNullOrWhiteSpace(SelectedService.HostName) ? SelectedService.IpAddressOrUrl : $"{SelectedService.HostName} ({SelectedService.IpEndPointStr}) [{SelectedService.ProtocolType}]";
            window.Title += $" - {label}";
        }
        else
        {
            window.Title += $" - {_servicesDataGrid.SelectedItems.Count} services";

        }

        window.Show(App.MainWindow);
    }

    public void SetControls(DataGrid servicesDataGrid, DataGrid pingRepliesDataGrid)
    {
        _servicesDataGrid = servicesDataGrid;
        _servicesPingsDataGrid = pingRepliesDataGrid;
        _servicesDataGrid.ExtendDataGridShortcuts();
        _servicesPingsDataGrid.ExtendDataGridShortcuts();
    }
}
