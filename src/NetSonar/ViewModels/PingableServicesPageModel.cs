using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Extensions;
using NetSonar.Avalonia.Localization;
using NetSonar.Avalonia.Network;
using NetSonar.Avalonia.Settings;
using NetSonar.Avalonia.SystemOS;
using NetSonar.Avalonia.ViewModels.Dialogs;
using NetSonar.Avalonia.ViewModels.Fragments;
using NetSonar.Avalonia.Views;
using ObservableCollections;
using StageKit.Primitives;
using StageKit.Primitives.System;
using SukiUI.Dialogs;
using ZLinq;
using Timer = System.Timers.Timer;

namespace NetSonar.Avalonia.ViewModels;

public partial class PingableServicesPageModel : PageViewModelBase
{
    public const string NumericUpDownTimeFormat = "#,#0.##";
    public const double NumericUpDownPingIncrement = 0.50;
    public const double NumericUpDownTimeoutIncrement = 0.50;
    private readonly ConcurrentDictionary<Guid, byte> _activePings = new();
    private readonly ConcurrentQueue<PingableService> _completedServices = new();
    private int _recountPending;

    private readonly Timer _timer = new(500);


    private DataGrid _servicesDataGrid = null!;
    private DataGrid _servicesPingsDataGrid = null!;


    public PingableServicesPageModel()
    {
        ServicesView = Services.CreateWritableView(service => service);
        ServicesViewCollection =
            ServicesView.ToWritableNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current);

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
            if (Dispatcher.UIThread.CheckAccess())
            {
                NotifyServicesCountChanged();
            }
            else
            {
                Dispatcher.UIThread.Post(NotifyServicesCountChanged);
            }
        };

        AppSettings.PingServices.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(PingServicesSettings.GridGroupBy))
            {
                RegroupServicesGrid();
            }
        };

        App.Localization.PropertyChanged += LocalizationOnPropertyChanged;
    }

    public override int Index => 0;
    public override string DisplayName => App.Localization["Navigation.Pings"];
    public override MaterialIconKind Icon => MaterialIconKind.Radar;

    public static int ServicesCount => Services.Count;
    [ObservableProperty] public partial int ServicesFailedCount { get; private set; }
    [ObservableProperty] public partial int ServicesSucceededCount { get; private set; }


    [ObservableProperty] public partial string FilterText { get; set; } = string.Empty;

    public static ObservableList<PingableService> Services => PingableServicesFile.Instance.Items;

    public IWritableSynchronizedView<PingableService, PingableService> ServicesView { get; }
    public NotifyCollectionChangedSynchronizedViewList<PingableService> ServicesViewCollection { get; }

    public DataGridCollectionView ServicesGroupView { get; }

    public bool HasServices => Services.Count > 0;

    public bool IsAnyServiceSelected => _servicesDataGrid is { SelectedIndex: not -1 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenAnyInBrowser), nameof(IsAnyServiceSelected))]
    public partial PingableService? SelectedService { get; set; }

    [ObservableProperty]
    public partial NotifyCollectionChangedSynchronizedViewList<PingableServiceReply>? SelectedServicePingReplies
    {
        get;
        set;
    }

    public PingableServiceGraphFragmentModel PingGraphModel { get; } = new();

    public bool CanOpenAnyInBrowser => _servicesDataGrid?.SelectedItems
        .AsValueEnumerable()
        .Cast<PingableService>()
        .Any(item => item.ProtocolType is ServiceProtocolType.HTTP or ServiceProtocolType.ICMP) ?? false;

    private void NotifyServicesCountChanged()
    {
        OnPropertyChanged(nameof(ServicesCount));
        OnPropertyChanged(nameof(HasServices));
        ToggleEnabledAllServicesCommand.NotifyCanExecuteChanged();
        PauseAllServicesCommand.NotifyCanExecuteChanged();
        ResumeAllServicesCommand.NotifyCanExecuteChanged();
        ResetAllServicesStatisticsCommand.NotifyCanExecuteChanged();
        RemoveAllServicesCommand.NotifyCanExecuteChanged();
        ExportAllServicesToJsonCommand.NotifyCanExecuteChanged();
    }

    private void ServicesDataGridOnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsAnyServiceSelected));
        OnPropertyChanged(nameof(CanOpenAnyInBrowser));
        EditSelectedServicesCommand.NotifyCanExecuteChanged();
        ToggleEnabledSelectedServiceCommand.NotifyCanExecuteChanged();
        PauseSelectedServiceCommand.NotifyCanExecuteChanged();
        PauseSelectedServicesCommand.NotifyCanExecuteChanged();
        ResumeSelectedServiceCommand.NotifyCanExecuteChanged();
        ResumeSelectedServicesCommand.NotifyCanExecuteChanged();
        ResetServiceStatisticsForSelectedItemCommand.NotifyCanExecuteChanged();
        ResetServiceStatisticsForSelectedItemsCommand.NotifyCanExecuteChanged();
        RemoveSelectedServicesCommand.NotifyCanExecuteChanged();
        ExportSelectedServicesToJsonCommand.NotifyCanExecuteChanged();
        ExportCurrentPingsToCsvCommand.NotifyCanExecuteChanged();
        ExportCurrentPingsToTabularCommand.NotifyCanExecuteChanged();
        ExportCurrentPingsToJsonCommand.NotifyCanExecuteChanged();
        OpenSelectedGraphInNewWindowCommand.NotifyCanExecuteChanged();
        OpenGraphInNewWindowCommand.NotifyCanExecuteChanged();
        OpenSelectedInBrowserCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedServicePingRepliesChanged(
        NotifyCollectionChangedSynchronizedViewList<PingableServiceReply>? oldValue,
        NotifyCollectionChangedSynchronizedViewList<PingableServiceReply>? newValue)
    {
        oldValue?.Dispose();
    }

    partial void OnSelectedServiceChanged(PingableService? value)
    {
        if (value is null) return;
        SelectedServicePingReplies =
            value.Pings.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
        PingGraphModel.Services = _servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray();
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
        AppSettings.PingServices.GridColumnOrder[e.Column.Header?.ToString() ?? string.Empty] =
            Math.Clamp(e.Column.DisplayIndex, 0, _servicesDataGrid.Columns.Count - 1);
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
        using (var servicesToExecute = Services
                   .AsValueEnumerable()
                   .Where(host => host.CanTimerExecute)
                   .ToArrayPool())
        {
            for (var i = 0; i < servicesToExecute.Size; i++)
            {
                var service = servicesToExecute.Array[i];
                if (!_activePings.TryAdd(service.Id, 0)) continue;

                // Probes run on their own without awaiting the batch, so a slow service never delays the others.
                _ = PingServiceAsync(service);
            }
        }

        RequestRecount();
    }

    private async Task PingServiceAsync(PingableService service)
    {
        try
        {
            await service.PingAsync();
            _completedServices.Enqueue(service);
        }
        catch (Exception e)
        {
            App.WriteLine(e);
        }
        finally
        {
            _activePings.TryRemove(service.Id, out _);
            RequestRecount();
        }
    }

    /// <summary>
    /// Schedules a single recount on the UI thread, coalescing the requests made until it runs.
    /// </summary>
    private void RequestRecount()
    {
        if (Interlocked.Exchange(ref _recountPending, 1) != 0) return;

        // Background runs after the Default priority post that adds the reply to the service.
        Dispatcher.UIThread.Post(RecountServices, DispatcherPriority.Background);
    }

    private void RecountServices()
    {
        Volatile.Write(ref _recountPending, 0);

        var failed = 0;
        var success = 0;

        foreach (var service in Services)
        {
            if (service.WasLastPingSucceeded)
            {
                success++;
            }
            else if (service.WasLastPingFailed)
            {
                failed++;
            }
        }

        ServicesFailedCount = failed;
        ServicesSucceededCount = success;

        var anyStatusChanged = false;
        while (_completedServices.TryDequeue(out var completed))
        {
            if (completed.StatusChanged) anyStatusChanged = true;
        }

        if (anyStatusChanged && !string.IsNullOrWhiteSpace(FilterText)) ReAttachFilters();
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
                ServicesGroupView.GroupDescriptions.Add(
                    new DataGridPathGroupDescription(AppSettings.PingServices.GridGroupBy.ToString()));
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
            var splitText =
                FilterText.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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

                if (StatusLocalization.GetText(service.LastStatus)
                    .Contains(word, StringComparison.CurrentCultureIgnoreCase)) return true;
                if (service.LastStatusStr.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.ProtocolType.ToString().Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.IpAddresses.AsValueEnumerable().Any(ipAddress =>
                        ipAddress.ToString().Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
                if (service.HostName.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.Group.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
                if (service.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        });
    }

    private void LocalizationOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ILocalizationService.Culture)) return;

        Dispatcher.UIThread.Post(() =>
        {
            ReAttachFilters();
            ServicesGroupView.Refresh();
            if (_servicesPingsDataGrid is not null) _servicesPingsDataGrid.CollectionView?.Refresh();
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

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void EditSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        var selectedServices = _servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray();
        if (selectedServices.Length == 0) return;

        var dialog = DialogManager
            .CreateDialog()
            .WithViewModel(dialog =>
            {
                var model = new AddPingServicesDialogModel(dialog, selectedServices);
                // Changing the protocol or the address rebuilds the service, so follow it with the selection.
                model.ServiceReplaced += (replaced, replacement) =>
                {
                    var selectedItems = _servicesDataGrid.SelectedItems;
                    var index = selectedItems.IndexOf(replaced);
                    if (index >= 0) selectedItems.RemoveAt(index);
                    selectedItems.Add(replacement);
                };
                return model;
            });
        dialog.TryShow();
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void ToggleEnabledSelectedService()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        if (_servicesDataGrid.SelectedItem is not PingableService service) return;
        service.IsEnabled = !service.IsEnabled;
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public void ToggleEnabledAllServices()
    {
        if (Services.Count == 0) return;
        foreach (var service in Services)
        {
            service.IsEnabled = !service.IsEnabled;
        }
    }


    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void PauseSelectedService()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        if (_servicesDataGrid.SelectedItem is not PingableService service) return;
        service.IsEnabled = false;
    }


    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void PauseSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        foreach (PingableService service in _servicesDataGrid.SelectedItems)
        {
            service.IsEnabled = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public void PauseAllServices()
    {
        if (Services.Count == 0) return;
        foreach (var service in Services)
        {
            service.IsEnabled = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void ResumeSelectedService()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        if (_servicesDataGrid.SelectedItem is not PingableService service) return;
        service.IsEnabled = true;
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void ResumeSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        foreach (PingableService service in _servicesDataGrid.SelectedItems)
        {
            service.IsEnabled = true;
        }
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public void ResumeAllServices()
    {
        if (Services.Count == 0) return;
        foreach (var service in Services)
        {
            service.IsEnabled = true;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void ResetServiceStatisticsForSelectedItem()
    {
        if (_servicesDataGrid.SelectedIndex == -1 ||
            _servicesDataGrid.SelectedItem is not PingableService service) return;

        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization["Ping.ResetOne.Title"],
                App.Localization.Format("Ping.ResetOne.Message", service.IpEndPoint, service.HostName),
                _ => { service.Clear(); })
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void ResetServiceStatisticsForSelectedItems()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("Ping.ResetSelected.Title", _servicesDataGrid.SelectedItems.Count),
                App.Localization.Format("Ping.ResetSelected.Message", _servicesDataGrid.SelectedItems.Count), _ =>
                {
                    foreach (PingableService service in _servicesDataGrid.SelectedItems)
                    {
                        service.Clear();
                    }
                })
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public void ResetAllServicesStatistics()
    {
        if (Services.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("Ping.ResetAll.Title", Services.Count),
                App.Localization.Format("Ping.ResetAll.Message", Services.Count), _ =>
                {
                    foreach (var ping in Services)
                    {
                        ping.Clear();
                    }
                })
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void RemoveSelectedServices()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("Ping.RemoveSelected.Title", _servicesDataGrid.SelectedItems.Count),
                App.Localization.Format("Ping.RemoveSelected.Message", _servicesDataGrid.SelectedItems.Count),
                _ => Services.RemoveRange(_servicesDataGrid.SelectedItems))
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public void RemoveAllServices()
    {
        if (Services.Count == 0) return;
        CreateMessageBoxYesNo(NotificationType.Warning,
                App.Localization.Format("Ping.RemoveAll.Title", Services.Count),
                App.Localization.Format("Ping.RemoveAll.Message", Services.Count), _ => Services.Clear())
            .TryShow();
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public async Task ExportSelectedServicesToJson()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;
        var selectedServices = _servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray();
        if (selectedServices.Length == 0) return;

        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName =
                FileUtilities.SanitizeFileName(
                    $"services#{selectedServices.Length}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, selectedServices, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Services.Title"],
                App.Localization.Format("Export.Services.Success", selectedServices.Length, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"],
                    toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Services.Title"],
                App.Localization["Export.Services.Error"]);
        }
    }

    [RelayCommand(CanExecute = nameof(HasServices))]
    public async Task ExportAllServicesToJson()
    {
        var services = Services.AsValueEnumerable().ToArray();
        if (services.Length == 0) return;

        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName =
                FileUtilities.SanitizeFileName($"services#{services.Length}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, services, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Services.Title"],
                App.Localization.Format("Export.Services.Success", services.Length, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"],
                    toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Services.Title"],
                App.Localization["Export.Services.Error"]);
        }
    }


    private static string FormatPingsHeader(char separator, bool csv)
    {
        return FormatPingsFields(separator, csv,
            nameof(PingableServiceReply.IsSucceeded),
            nameof(PingableServiceReply.Status),
            nameof(PingableServiceReply.StatusCode),
            nameof(PingableServiceReply.IpEndPoint),
            nameof(PingableServiceReply.SentDateTime),
            nameof(PingableServiceReply.Time),
            nameof(PingableServiceReply.Ttl),
            nameof(PingableServiceReply.BufferLength),
            nameof(PingableServiceReply.ErrorMessage));
    }

    /// <summary>
    /// Formats a reply with culture independent numbers and an ISO 8601 date, so the export reads the same everywhere.
    /// </summary>
    private static string FormatPingsRow(PingableServiceReply reply, char separator, bool csv)
    {
        var culture = CultureInfo.InvariantCulture;
        return FormatPingsFields(separator, csv,
            reply.IsSucceeded.ToString(culture),
            reply.Status.ToString(),
            reply.StatusCode.ToString(culture),
            reply.IpEndPoint.ToString(),
            reply.SentDateTime.ToString("o", culture),
            reply.Time.ToString(culture),
            reply.Ttl.ToString(culture),
            reply.BufferLength.ToString(culture),
            reply.ErrorMessage);
    }

    private static string FormatPingsFields(char separator, bool csv, params ReadOnlySpan<string?> fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) builder.Append(separator);
            AppendPingsField(builder, fields[i], csv);
        }

        return builder.ToString();
    }

    private static void AppendPingsField(StringBuilder builder, string? field, bool csv)
    {
        if (string.IsNullOrEmpty(field)) return;

        if (csv)
        {
            // Quote fields holding the separator, a quote or a line break, doubling the embedded quotes.
            if (field.AsSpan().IndexOfAny(";\"\r\n") < 0)
            {
                builder.Append(field);
                return;
            }

            builder.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            return;
        }

        // Tabs and line breaks would break the rows and columns when pasted into a spreadsheet.
        foreach (var c in field)
        {
            builder.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public async Task ExportCurrentPingsToCsv()
    {
        var selectedService = SelectedService;
        if (selectedService is null || !TopLevel.StorageProvider.CanSave) return;

        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = FileUtilities.SanitizeFileName(
                $"{selectedService.ProtocolType.ToString().ToLowerInvariant()}-{selectedService.HostName}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.csv"),
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
            var pings = selectedService.Pings.AsValueEnumerable().ToArray();
            await textWriter.WriteLineAsync(FormatPingsHeader(';', true));

            foreach (var reply in pings)
            {
                await textWriter.WriteLineAsync(FormatPingsRow(reply, ';', true));
            }

            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Pings.CsvTitle"],
                App.Localization.Format("Export.Pings.Success", pings.Length, selectedService.HostName, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"],
                    toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Pings.CsvTitle"],
                App.Localization["Export.Pings.Error"]);
        }
    }

    /// <summary>
    /// Exports the pings of the selected service as tab separated values, ready to paste into a spreadsheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public async Task ExportCurrentPingsToTabular()
    {
        var selectedService = SelectedService;
        if (selectedService is null || !TopLevel.StorageProvider.CanSave || selectedService.Pings.Count == 0) return;

        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = FileUtilities.SanitizeFileName(
                $"{selectedService.ProtocolType.ToString().ToLowerInvariant()}-{selectedService.HostName}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.tsv"),
            DefaultExtension = "tsv",
            FileTypeChoices = AvaloniaExtensions.FilePickerTsv
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var textWriter = new StreamWriter(stream);
            var pings = selectedService.Pings.AsValueEnumerable().ToArray();
            await textWriter.WriteLineAsync(FormatPingsHeader('\t', false));

            foreach (var reply in pings)
            {
                await textWriter.WriteLineAsync(FormatPingsRow(reply, '\t', false));
            }

            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Pings.TsvTitle"],
                App.Localization.Format("Export.Pings.Success", pings.Length, selectedService.HostName, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"],
                    toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Pings.TsvTitle"],
                App.Localization["Export.Pings.Error"]);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public async Task ExportCurrentPingsToJson()
    {
        var selectedService = SelectedService;
        if (selectedService is null || !TopLevel.StorageProvider.CanSave || selectedService.Pings.Count == 0) return;

        using var file = await TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            ShowOverwritePrompt = true,
            SuggestedFileName = FileUtilities.SanitizeFileName(
                $"{selectedService.ProtocolType.ToString().ToLowerInvariant()}-{selectedService.HostName}-{DateTime.Now:dd-MM-yyyy-HH-mm-ss}.json"),
            DefaultExtension = "json",
            FileTypeChoices = AvaloniaExtensions.FilePickerJson
        });

        if (file is null) return;

        try
        {
            var filePath = file.TryGetLocalPath();
            if (filePath is null) return;
            await using var stream = File.Create(filePath);
            var pings = selectedService.Pings.AsValueEnumerable().ToArray();
            await JsonSerializer.SerializeAsync(stream, pings, App.JsonSerializerOptions);
            App.ShowToast(NotificationType.Success,
                App.Localization["Export.Pings.JsonTitle"],
                App.Localization.Format("Export.Pings.Success", pings.Length, selectedService.HostName, file.Name),
                new ToastActionButton(App.Localization["Common.OpenFile"],
                    toast => { HostSystem.OpenFile(filePath); }),
                new ToastActionButton(App.Localization["Common.OpenFolder"],
                    toast => { HostSystem.ShowFileInFileManager(filePath); })
            );
        }
        catch (Exception e)
        {
            App.ShowExceptionToast(e, App.Localization["Export.Pings.JsonTitle"],
                App.Localization["Export.Pings.Error"]);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void OpenSelectedGraphInNewWindow()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return;

        foreach (PingableService selectedItem in _servicesDataGrid.SelectedItems)
        {
            GenericWindow window = new()
            {
                Title = $"{App.SoftwareWithVersion}  {App.Localization["Graph.Title"]}",
                CanPin = true,
                Content = new ContentPresenter
                {
                    Margin = new Thickness(20),
                    Content = new PingableServiceGraphFragmentModel(selectedItem, true)
                }
            };
            window.Show(App.MainWindow);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnyServiceSelected))]
    public void OpenGraphInNewWindow()
    {
        if (SelectedService is null) return;

        GenericWindow window = new()
        {
            Title = $"{App.SoftwareWithVersion}  {App.Localization["Graph.Title"]}",
            CanPin = true,
            Content = new ContentPresenter
            {
                Margin = new Thickness(20),
                Content = new PingableServiceGraphFragmentModel(
                    _servicesDataGrid.SelectedItems.AsValueEnumerable<PingableService>().ToArray(),
                    true)
            }
        };

        if (_servicesDataGrid.SelectedItems.Count == 1)
        {
            var label = string.IsNullOrWhiteSpace(SelectedService.HostName)
                ? SelectedService.IpAddressOrUrl
                : $"{SelectedService.HostName} ({SelectedService.IpEndPointStr}) [{SelectedService.ProtocolType}]";
            window.Title += $" - {label}";
        }
        else
        {
            window.Title +=
                $" - {App.Localization.Format("Ui.ServicesCountPlain", _servicesDataGrid.SelectedItems.Count)}";
        }

        window.Show(App.MainWindow);
    }

    [RelayCommand(CanExecute = nameof(CanOpenAnyInBrowser))]
    public Task<bool> OpenSelectedInBrowser()
    {
        if (_servicesDataGrid.SelectedIndex == -1) return Task.FromResult(false);
        var openedAny = false;

        foreach (PingableService selectedItem in _servicesDataGrid.SelectedItems)
        {
            if (selectedItem.ProtocolType is not ServiceProtocolType.HTTP and not ServiceProtocolType.ICMP) continue;
            LaunchUriAsync(GetBrowserUrl(selectedItem));
            openedAny = true;
        }

        return Task.FromResult(openedAny);
    }

    /// <summary>
    /// Gets the URL to open for a service, adding the http scheme to the bare host or IP address of an ICMP service.
    /// </summary>
    private static string GetBrowserUrl(PingableService service)
    {
        var target = service.IpAddressOrUrl;
        if (target.Contains("://", StringComparison.Ordinal)) return target;

        // An IPv6 literal must be bracketed to be a valid URL host.
        if (!target.StartsWith('[')
            && IPAddress.TryParse(target, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            target = $"[{target}]";
        }

        return $"http://{target}";
    }

    public void SetControls(DataGrid servicesDataGrid, DataGrid pingRepliesDataGrid)
    {
        _servicesDataGrid = servicesDataGrid;
        _servicesPingsDataGrid = pingRepliesDataGrid;
        _servicesDataGrid.SelectionChanged += ServicesDataGridOnSelectionChanged;
        _servicesDataGrid.ExtendDataGridShortcuts(_ => RemoveSelectedServices(), _ => RemoveAllServices());
        _servicesPingsDataGrid.ExtendDataGridShortcuts(_ => ResetServiceStatisticsForSelectedItem());
    }
}
