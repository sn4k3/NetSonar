using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using Material.Icons;
using NetSonar.Avalonia.Localization;
using NetSonar.Avalonia.Network;
using ObservableCollections;
using SkiaSharp;
using SukiUI.Models;
using ZLinq;

namespace NetSonar.Avalonia.ViewModels.Fragments;

public partial class PingableServiceGraphFragmentModel : ViewModelBase, IDisposable
{
    private const int RollingAverageWindowSize = 10;
    private const double MultiGraphDataLabelGapRatio = 0.07;
    private static readonly SKTypeface BoldTypeface = SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold);
    private readonly Dictionary<PingableService, PingableServiceReply[]> _frozenReplies = [];

    private readonly ObservableList<ServiceComparisonPoint> _multiGraphValues = [];
    private readonly NotifyCollectionChangedSynchronizedViewList<ServiceComparisonPoint> _multiGraphValuesCollection;
    private readonly Axis _multiGraphYAxis;

    private readonly ObservableList<string> _repliesYAxes = [];

    private readonly ObservableList<string> _singleGraphLabels = [];
    private readonly NotifyCollectionChangedSynchronizedViewList<string> _singleGraphLabelsCollection;

    private readonly ObservableList<PingChartPoint> _singleGraphValues = [];
    private readonly NotifyCollectionChangedSynchronizedViewList<PingChartPoint> _singleGraphValuesCollection;
    private readonly Axis _singleGraphYAxis;

    private bool _isDisposed;
    private int _refreshPending;

    private GraphWindowSizeOption _selectedWindowSize = null!;
    private PingableService[] _services = [];


    public PingableServiceGraphFragmentModel()
    {
        WindowSizeOptions = CreateWindowSizeOptions(AppSettings.PingServices.MaxRepliesGraphCache);
        _selectedWindowSize = FindInitialWindowSize(WindowSizeOptions, AppSettings.PingServices.MaxRepliesGraphCache);

        _singleGraphValuesCollection =
            _singleGraphValues.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
        _singleGraphLabelsCollection =
            _singleGraphLabels.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
        _multiGraphValuesCollection =
            _multiGraphValues.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);
        RepliesYAxesCollection =
            _repliesYAxes.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);


        SingleGraphSeries =
        [
            new ColumnSeries<PingChartPoint>
            {
                Name = App.Localization["Graph.Latency"],
                Values = _singleGraphValuesCollection,
                Mapping = MapLatency,
                Padding = 0,
                YToolTipLabelFormatter = point => FormatLatencyTooltip(point.Model),
                Fill = new SolidColorPaint(new SKColor(
                    App.Theme.ActiveColorTheme!.Primary.R,
                    App.Theme.ActiveColorTheme.Primary.G,
                    App.Theme.ActiveColorTheme.Primary.B)),
                MaxBarWidth = 18
            },
            new ScatterSeries<PingChartPoint, DiamondGeometry>
            {
                Name = App.Localization["Graph.AboveScale"],
                Values = _singleGraphValuesCollection,
                Mapping = MapCappedLatency,
                YToolTipLabelFormatter = point => FormatCappedLatencyTooltip(point.Model),
                Fill = new SolidColorPaint(SKColors.Gold),
                Stroke = new SolidColorPaint(SKColors.DarkOrange, 2),
                GeometrySize = 12,
                MinGeometrySize = 12,
                IsVisibleAtLegend = false
            },
            new LineSeries<PingChartPoint>
            {
                Name = $"{App.Localization["Graph.RollingAverage"]} ({RollingAverageWindowSize})",
                Values = _singleGraphValuesCollection,
                Mapping = MapRollingAverage,
                YToolTipLabelFormatter = point => FormatRollingAverageTooltip(point.Model),
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(Brushes.DarkOrange.Color.ToUInt32()), 3),
                GeometryStroke = null,
                GeometryFill = null,
                GeometrySize = 12,
                LineSmoothness = 0
            },
            new ScatterSeries<PingChartPoint, CrossGeometry>
            {
                Name = App.Localization["Graph.Failure"],
                Values = _singleGraphValuesCollection,
                Mapping = MapFailure,
                YToolTipLabelFormatter = point => FormatFailureTooltip(point.Model?.Reply),
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(Brushes.Red.Color.ToUInt32()), 3),
                GeometrySize = 16,
                MinGeometrySize = 16
            }
        ];

        SingleGraphXAxes =
        [
            new Axis
            {
                Labels = _singleGraphLabelsCollection,
                LabelsDensity = 1,
                LabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255)),
                NamePaint = new SolidColorPaint(new SKColor(255, 255, 255))
            }
        ];

        _singleGraphYAxis = new Axis
        {
            MinLimit = 0,
            Labeler = value => value < 0 ? string.Empty : $"{value:0.##} ms",
            LabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255)),
            NamePaint = new SolidColorPaint(new SKColor(255, 255, 255))
        };
        SingleGraphYAxes = [_singleGraphYAxis];


        MultiGraphSeries =
        [
            new RowSeries<ServiceComparisonPoint>
            {
                Name = App.Localization["Graph.Maximum"],
                Values = _multiGraphValuesCollection,
                Mapping = MapMaximumServiceValue,
                IgnoresBarPosition = true,
                XToolTipLabelFormatter = point => FormatServiceName(point.Model),
                YToolTipLabelFormatter = point => FormatServiceComparisonTooltip(point.Model),
                Fill = new SolidColorPaint(new SKColor(127, 127, 127, 150)),
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => FormatChartValue(point.Model?.Maximum),
                DataLabelsTranslate = new LvcPoint(-1, 0)
            },
            new RowSeries<ServiceComparisonPoint>
            {
                Name = App.Localization["Graph.Average"],
                Values = _multiGraphValuesCollection,
                Mapping = MapAverageServiceValue,
                IgnoresBarPosition = true,
                IsHoverable = false,
                Fill = new SolidColorPaint(new SKColor(Brushes.DarkOrange.Color.ToUInt32())),
                DataLabelsPaint = new SolidColorPaint(new SKColor(0, 0, 0)),
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => point.Model?.AverageLabel ?? string.Empty,
                DataLabelsTranslate = new LvcPoint(-1, 0)
            },
            new ScatterSeries<ServiceComparisonPoint, VerticalMarkerGeometry, OutlinedLabelGeometry>
            {
                Name = App.Localization["Graph.Current"],
                Values = _multiGraphValuesCollection,
                Mapping = MapCurrentServiceMarker,
                IsHoverable = false,
                Fill = new SolidColorPaint(new SKColor(0, 188, 212)),
                Stroke = new SolidColorPaint(SKColors.White, 2),
                GeometrySize = 28,
                MinGeometrySize = 28,
                ZIndex = 10,
                DataLabelsPaint = new SolidColorPaint(SKColors.White)
                {
                    SKTypeface = BoldTypeface
                },
                DataLabelsSize = 14,
                DataLabelsPosition = DataLabelsPosition.Right,
                DataLabelsFormatter = point => FormatChartValue(point.Model?.Current),
                DataLabelsTranslate = new LvcPoint(-0.25, 0)
            },
            new RowSeries<ServiceComparisonPoint>
            {
                Name = App.Localization["Graph.Minimum"],
                Values = _multiGraphValuesCollection,
                Mapping = MapMinimumServiceValue,
                IgnoresBarPosition = true,
                IsHoverable = false,
                Fill = new SolidColorPaint(new SKColor(Brushes.DarkGreen.Color.ToUInt32())),
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => point.Model?.MinimumLabel ?? string.Empty,
                DataLabelsTranslate = new LvcPoint(-1, 0)
            },
            new ScatterSeries<ServiceComparisonPoint, CrossGeometry>
            {
                Name = App.Localization["Graph.Failed"],
                Values = _multiGraphValuesCollection,
                Mapping = MapFailedServiceValue,
                XToolTipLabelFormatter = point => FormatServiceName(point.Model),
                YToolTipLabelFormatter = point => FormatServiceComparisonTooltip(point.Model),
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(Brushes.Red.Color.ToUInt32()), 3),
                GeometrySize = 16,
                MinGeometrySize = 16,
                DataLabelsPaint = new SolidColorPaint(new SKColor(Brushes.Red.Color.ToUInt32())),
                DataLabelsPosition = DataLabelsPosition.Right,
                DataLabelsFormatter = point => StatusLocalization.GetText(point.Model?.Service.LastStatus)
            }
        ];

        MultiGraphXAxes =
        [
            new Axis
            {
                Labeler = value => value < 0 ? string.Empty : $"{value:#,##0.##}\nms",
                LabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255))
                // SeparatorsPaint = new SolidColorPaint(new SKColor(220, 220, 220))
            }
        ];

        _multiGraphYAxis = new Axis
        {
            Labels = RepliesYAxesCollection,
            Position = AxisPosition.End,
            MinStep = 1,
            LabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255))
        };
        MultiGraphYAxes = [_multiGraphYAxis];

        App.Theme.OnBaseThemeChanged += OnBaseThemeChanged;
        App.Theme.OnColorThemeChanged += OnColorThemeChanged;
        App.Localization.PropertyChanged += LocalizationOnPropertyChanged;

        OnBaseThemeChanged(App.Theme.ActiveBaseTheme);
        OnColorThemeChanged(App.Theme.ActiveColorTheme);
    }

    public PingableServiceGraphFragmentModel(PingableService[] services, bool showGraphOptions = false) : this()
    {
        ShowGraphOptions = showGraphOptions;
        Services = services;
    }

    public PingableServiceGraphFragmentModel(PingableService service, bool showGraphOptions = false) : this([service],
        showGraphOptions)
    {
    }

    public PingableService[] Services
    {
        get => _services;
        set
        {
            foreach (var service in _services)
            {
                service.PingCompleted -= ServiceOnPingCompleted;
                service.Pings.CollectionChanged -= ServicePingsOnCollectionChanged;
                service.PropertyChanged -= ServiceOnPropertyChanged;
            }

            if (value.Length <= 1)
            {
                _services = value;
            }
            else
            {
                _services = value
                    .AsValueEnumerable()
                    .OrderByDescending(service => service.AverageTime)
                    .ThenByDescending(service => service.LastTime)
                    .ToArray();
            }

            foreach (var service in _services)
            {
                service.PingCompleted += ServiceOnPingCompleted;
                service.Pings.CollectionChanged += ServicePingsOnCollectionChanged;
                service.PropertyChanged += ServiceOnPropertyChanged;
            }

            if (IsGraphFrozen) CaptureFrozenReplies();

            Rebuild();

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasService));
            OnPropertyChanged(nameof(HasSingleService));
            OnPropertyChanged(nameof(HasMultiServices));
            OnPropertyChanged(nameof(PingEverySeconds));
            OnPropertyChanged(nameof(TimeoutSeconds));
            NotifyServicesEnabledStateChanged();
        }
    }

    public bool HasService => Services.Length > 0;
    public bool HasSingleService => Services.Length == 1;
    public bool HasMultiServices => Services.Length > 1;
    public bool ShowGraphOptions { get; }
    public string ServicesEnabledActionText => App.Localization[AreAllServicesEnabled ? "Ui.Disable" : "Ui.Enable"];

    public MaterialIconKind ServicesEnabledActionIcon => AreAllServicesEnabled
        ? MaterialIconKind.Pause
        : MaterialIconKind.Play;

    public string ServicesEnabledActionToolTip => AreAllServicesEnabled
        ? App.Localization["Graph.DisableAll.ToolTip"]
        : App.Localization["Graph.EnableAll.ToolTip"];

    public bool AreAllServicesEnabled
    {
        get
        {
            if (_services.Length == 0) return false;

            foreach (var service in _services)
            {
                if (!service.IsEnabled) return false;
            }

            return true;
        }
    }

    public double? PingEverySeconds
    {
        get
        {
            if (_services.Length == 0) return null;

            var value = _services[0].PingEverySeconds;
            for (var i = 1; i < _services.Length; i++)
            {
                if (_services[i].PingEverySeconds != value) return null;
            }

            return value;
        }
        set
        {
            if (value is null) return;

            foreach (var service in _services)
            {
                service.PingEverySeconds = value.Value;
            }

            OnPropertyChanged();
        }
    }

    public double? TimeoutSeconds
    {
        get
        {
            if (_services.Length == 0) return null;

            var value = _services[0].TimeoutSeconds;
            for (var i = 1; i < _services.Length; i++)
            {
                if (_services[i].TimeoutSeconds != value) return null;
            }

            return value;
        }
        set
        {
            if (value is null) return;

            foreach (var service in _services)
            {
                service.TimeoutSeconds = value.Value;
            }

            OnPropertyChanged();
        }
    }

    public ISeries[] SingleGraphSeries { get; }

    public ICartesianAxis[] SingleGraphXAxes { get; }

    public ICartesianAxis[] SingleGraphYAxes { get; }

    public ISeries[] MultiGraphSeries { get; }

    public ICartesianAxis[] MultiGraphXAxes { get; }

    public ICartesianAxis[] MultiGraphYAxes { get; }

    public NotifyCollectionChangedSynchronizedViewList<string> RepliesYAxesCollection { get; }

    public Func<float, float>? EasingFunction => null;

    [ObservableProperty]
    public partial Paint ThemePaint { get; set; } = new SolidColorPaint(new SKColor(255, 255, 255));

    [ObservableProperty]
    public partial string CurrentTimeText { get; private set; } = $"{App.Localization["Ui.Ping"]}: -";

    [ObservableProperty] public partial string MedianTimeText { get; private set; } = "P50: -";
    [ObservableProperty] public partial string P95TimeText { get; private set; } = "P95: -";

    [ObservableProperty]
    public partial string JitterText { get; private set; } = $"{App.Localization["Graph.Jitter"]}: -";

    [ObservableProperty]
    public partial string WindowLossText { get; private set; } = $"{App.Localization["Graph.Loss"]}: -";

    [ObservableProperty] public partial bool IsFullScale { get; set; }

    public string ScaleModeText => App.Localization[IsFullScale ? "Graph.FullScale" : "Graph.AutoScale"];

    public string ScaleModeToolTip => IsFullScale
        ? App.Localization["Graph.AutoScale.ToolTip"]
        : App.Localization["Graph.FullScale.ToolTip"];

    public GraphWindowSizeOption[] WindowSizeOptions { get; private set; }

    public GraphWindowSizeOption SelectedWindowSize
    {
        get => _selectedWindowSize;
        set
        {
            if (value is null || Equals(_selectedWindowSize, value)) return;
            _selectedWindowSize = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    [ObservableProperty] public partial bool IsGraphFrozen { get; set; }

    public MaterialIconKind FreezeIcon => IsGraphFrozen
        ? MaterialIconKind.SnowflakeOff
        : MaterialIconKind.Snowflake;

    public string FreezeActionText => IsGraphFrozen
        ? App.Localization["Ui.Resume"]
        : App.Localization["Graph.Freeze"];

    public string FreezeToolTip => IsGraphFrozen
        ? App.Localization["Graph.Resume.ToolTip"]
        : App.Localization["Graph.Freeze.ToolTip"];

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        App.Theme.OnBaseThemeChanged -= OnBaseThemeChanged;
        App.Theme.OnColorThemeChanged -= OnColorThemeChanged;
        App.Localization.PropertyChanged -= LocalizationOnPropertyChanged;

        Services = [];
        _singleGraphValuesCollection.Dispose();
        _singleGraphLabelsCollection.Dispose();
        _multiGraphValuesCollection.Dispose();
        RepliesYAxesCollection.Dispose();
    }

    [RelayCommand]
    private void ToggleServicesEnabled()
    {
        var isEnabled = !AreAllServicesEnabled;
        foreach (var service in _services)
        {
            service.IsEnabled = isEnabled;
        }

        NotifyServicesEnabledStateChanged();
    }

    partial void OnIsFullScaleChanged(bool value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(ScaleModeToolTip));
        Rebuild();
    }

    partial void OnIsGraphFrozenChanged(bool value)
    {
        OnPropertyChanged(nameof(FreezeIcon));
        OnPropertyChanged(nameof(FreezeActionText));
        OnPropertyChanged(nameof(FreezeToolTip));

        if (value)
        {
            CaptureFrozenReplies();
        }
        else
        {
            _frozenReplies.Clear();
            Rebuild();
        }
    }

    public void Rebuild()
    {
        _singleGraphValues.Clear();
        _singleGraphLabels.Clear();
        _singleGraphYAxis.MinLimit = 0;
        _singleGraphYAxis.MaxLimit = null;
        _multiGraphValues.Clear();
        _repliesYAxes.Clear();
        _multiGraphYAxis.MinLimit = null;
        _multiGraphYAxis.MaxLimit = null;
        ResetWindowStatistics();

        if (HasSingleService)
        {
            var service = Services[0];

            var replies = GetRepliesForGraph(service);

            if (replies.Length == 0) return;

            Array.Reverse(replies);
            RebuildSingleGraphValues(replies);
        }
        else
        {
            if (!HasService) return;
            RebuildMultiGraphValues();
        }
    }

    private void ServiceOnPingCompleted(object? sender,
        BasePingableCollectionObject<PingableServiceReply>.PingCompletedEventArgs e)
    {
        if (sender is not PingableService) return;
        if (IsGraphFrozen) return;

        // Pings complete on worker threads, several at once. The chart's axes and value lists must only change on the
        // UI thread: changing them while LiveCharts lays out, or while it releases its Skia resources after the page is
        // left, can crash the renderer natively. Background priority also runs after the post that adds the reply.
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
        Dispatcher.UIThread.Post(RefreshAfterPing, DispatcherPriority.Background);
    }

    private void RefreshAfterPing()
    {
        Volatile.Write(ref _refreshPending, 0);
        if (_isDisposed || IsGraphFrozen) return;

        if (HasSingleService)
        {
            Rebuild();
        }
        else
        {
            Sort();
        }
    }

    private void ServicePingsOnCollectionChanged(in NotifyCollectionChangedEventArgs<PingableServiceReply> e)
    {
        if (IsGraphFrozen) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Rebuild();
        }
    }

    private void ServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PingableService.PingEverySeconds))
        {
            OnPropertyChanged(nameof(PingEverySeconds));
        }
        else if (e.PropertyName == nameof(PingableService.TimeoutSeconds))
        {
            OnPropertyChanged(nameof(TimeoutSeconds));
        }
        else if (e.PropertyName == nameof(PingableService.IsEnabled))
        {
            NotifyServicesEnabledStateChanged();
        }
    }

    private void LocalizationOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(App.Localization.Culture)) return;

        SingleGraphSeries[0].Name = App.Localization["Graph.Latency"];
        SingleGraphSeries[1].Name = App.Localization["Graph.AboveScale"];
        SingleGraphSeries[2].Name = $"{App.Localization["Graph.RollingAverage"]} ({RollingAverageWindowSize})";
        SingleGraphSeries[3].Name = App.Localization["Graph.Failure"];
        MultiGraphSeries[0].Name = App.Localization["Graph.Maximum"];
        MultiGraphSeries[1].Name = App.Localization["Graph.Average"];
        MultiGraphSeries[2].Name = App.Localization["Graph.Current"];
        MultiGraphSeries[3].Name = App.Localization["Graph.Minimum"];
        MultiGraphSeries[4].Name = App.Localization["Graph.Failed"];

        var selectedWindowSize = _selectedWindowSize.Count;
        WindowSizeOptions = CreateWindowSizeOptions(AppSettings.PingServices.MaxRepliesGraphCache);
        _selectedWindowSize = FindInitialWindowSize(WindowSizeOptions, selectedWindowSize);

        OnPropertyChanged(nameof(WindowSizeOptions));
        OnPropertyChanged(nameof(SelectedWindowSize));
        OnPropertyChanged(nameof(ServicesEnabledActionText));
        OnPropertyChanged(nameof(ServicesEnabledActionToolTip));
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(ScaleModeToolTip));
        OnPropertyChanged(nameof(FreezeActionText));
        OnPropertyChanged(nameof(FreezeToolTip));
        Rebuild();
    }

    private void NotifyServicesEnabledStateChanged()
    {
        OnPropertyChanged(nameof(AreAllServicesEnabled));
        OnPropertyChanged(nameof(ServicesEnabledActionText));
        OnPropertyChanged(nameof(ServicesEnabledActionIcon));
        OnPropertyChanged(nameof(ServicesEnabledActionToolTip));
    }

    private static Coordinate MapLatency(PingChartPoint point, int index)
    {
        return point.Reply.IsSucceeded && double.IsFinite(point.Reply.Time)
            ? new Coordinate(index, point.PlotLatency)
            : Coordinate.Empty;
    }

    private static Coordinate MapRollingAverage(PingChartPoint point, int index)
    {
        return double.IsFinite(point.PlotRollingAverage)
            ? new Coordinate(index, point.PlotRollingAverage)
            : Coordinate.Empty;
    }

    private static Coordinate MapCappedLatency(PingChartPoint point, int index)
    {
        return point.IsLatencyCapped
            ? new Coordinate(index, point.CappedMarker)
            : Coordinate.Empty;
    }

    private static Coordinate MapFailure(PingChartPoint point, int index)
    {
        return point.Reply.IsFailed
            ? new Coordinate(index, point.FailureMarker)
            : Coordinate.Empty;
    }

    private static Coordinate MapMaximumServiceValue(ServiceComparisonPoint comparison, int index)
    {
        return double.IsFinite(comparison.Maximum)
            ? new Coordinate(index, comparison.Maximum)
            : Coordinate.Empty;
    }

    private static Coordinate MapAverageServiceValue(ServiceComparisonPoint comparison, int index)
    {
        return double.IsFinite(comparison.Average)
            ? new Coordinate(index, comparison.Average)
            : Coordinate.Empty;
    }

    private static Coordinate MapCurrentServiceMarker(ServiceComparisonPoint comparison, int index)
    {
        return comparison.Service.WasLastPingSucceeded
               && double.IsFinite(comparison.Current)
            ? new Coordinate(comparison.Current, index)
            : Coordinate.Empty;
    }

    private static Coordinate MapMinimumServiceValue(ServiceComparisonPoint comparison, int index)
    {
        return double.IsFinite(comparison.Minimum)
            ? new Coordinate(index, comparison.Minimum)
            : Coordinate.Empty;
    }

    private static Coordinate MapFailedServiceValue(ServiceComparisonPoint comparison, int index)
    {
        return comparison.Service.WasLastPingFailed
            ? new Coordinate(0, index)
            : Coordinate.Empty;
    }

    private void RebuildSingleGraphValues(PingableServiceReply[] replies)
    {
        var successfulValues = replies
            .AsValueEnumerable()
            .Where(static reply => reply.IsSucceeded && double.IsFinite(reply.Time))
            .Select(static reply => reply.Time)
            .ToArray();
        Array.Sort(successfulValues);

        var median = GetPercentile(successfulValues, 0.50);
        var p95 = GetPercentile(successfulValues, 0.95);
        var axisMaximum = IsFullScale
            ? GetFullAxisMaximum(successfulValues)
            : GetRobustAxisMaximum(successfulValues, median, p95);
        _singleGraphYAxis.MinLimit = -axisMaximum * 0.08;
        _singleGraphYAxis.MaxLimit = axisMaximum;
        var failedCount = replies.Length - successfulValues.Length;

        var jitterSum = 0d;
        var jitterPairCount = 0;
        var previousTime = 0d;
        var hasPreviousTime = false;
        foreach (var reply in replies)
        {
            if (!reply.IsSucceeded || !double.IsFinite(reply.Time))
            {
                hasPreviousTime = false;
                continue;
            }

            if (hasPreviousTime)
            {
                jitterSum += Math.Abs(reply.Time - previousTime);
                jitterPairCount++;
            }

            previousTime = reply.Time;
            hasPreviousTime = true;
        }

        var latestReply = replies[^1];
        CurrentTimeText = $"{App.Localization["Ui.Ping"]}: {FormatMilliseconds(latestReply.Time)}";
        MedianTimeText = $"P50: {FormatMilliseconds(median)}";
        P95TimeText = $"P95: {FormatMilliseconds(p95)}";
        JitterText = jitterPairCount > 0
            ? $"{App.Localization["Graph.Jitter"]}: {FormatMilliseconds(jitterSum / jitterPairCount)}"
            : $"{App.Localization["Graph.Jitter"]}: ∞";
        WindowLossText = $"{App.Localization["Graph.Loss"]}: {failedCount * 100d / replies.Length:0.##}%";

        Span<double> rollingWindow = stackalloc double[RollingAverageWindowSize];
        var rollingCount = 0;
        var rollingIndex = 0;
        var rollingSum = 0d;

        foreach (var reply in replies)
        {
            var rollingAverage = double.PositiveInfinity;
            if (reply.IsSucceeded && double.IsFinite(reply.Time))
            {
                if (rollingCount == RollingAverageWindowSize)
                {
                    rollingSum -= rollingWindow[rollingIndex];
                }
                else
                {
                    rollingCount++;
                }

                rollingWindow[rollingIndex] = reply.Time;
                rollingIndex = (rollingIndex + 1) % RollingAverageWindowSize;
                rollingSum += reply.Time;
                rollingAverage = Math.Round(rollingSum / rollingCount, 2, MidpointRounding.AwayFromZero);
            }

            var isLatencyCapped = !IsFullScale
                                  && reply.IsSucceeded
                                  && double.IsFinite(reply.Time)
                                  && reply.Time > axisMaximum;
            var plotLatency = reply.IsSucceeded && double.IsFinite(reply.Time)
                ? isLatencyCapped ? axisMaximum * 0.92 : reply.Time
                : double.PositiveInfinity;
            var plotRollingAverage = double.IsFinite(rollingAverage)
                ? IsFullScale ? rollingAverage : Math.Min(rollingAverage, axisMaximum * 0.98)
                : double.PositiveInfinity;
            var failureMarker = -axisMaximum * 0.04;
            var cappedMarker = axisMaximum * 0.96;
            _singleGraphValues.Add(new PingChartPoint(
                reply,
                rollingAverage,
                plotLatency,
                plotRollingAverage,
                failureMarker,
                cappedMarker,
                isLatencyCapped));
            _singleGraphLabels.Add(reply.SentDateTime.ToString("HH:mm:ss"));
        }
    }

    private void RebuildMultiGraphValues(ServiceComparisonPoint[]? comparisons = null)
    {
        _multiGraphValues.Clear();
        _repliesYAxes.Clear();

        comparisons ??= Services
            .AsValueEnumerable()
            .Select(BuildServiceComparison)
            .ToArray();

        var graphMaximum = 0d;
        foreach (var comparison in comparisons)
        {
            if (double.IsFinite(comparison.Maximum))
            {
                graphMaximum = Math.Max(graphMaximum, comparison.Maximum);
            }
        }

        var dataLabelMinimumGap = Math.Max(1, graphMaximum * MultiGraphDataLabelGapRatio);

        for (var i = 0; i < comparisons.Length; i++)
        {
            var comparison = comparisons[i];
            var current = comparison.Service.WasLastPingSucceeded
                ? comparison.Current
                : double.NaN;
            var averageLabel = HasDataLabelSpace(comparison.Average, comparison.Minimum, dataLabelMinimumGap)
                               && HasDataLabelSpace(comparison.Average, current, dataLabelMinimumGap)
                               && HasDataLabelSpace(comparison.Average, comparison.Maximum, dataLabelMinimumGap)
                ? FormatChartValue(comparison.Average)
                : string.Empty;
            var minimumLabel = HasDataLabelSpace(comparison.Minimum, current, dataLabelMinimumGap)
                               && HasDataLabelSpace(comparison.Minimum, comparison.Maximum, dataLabelMinimumGap)
                ? FormatChartValue(comparison.Minimum)
                : string.Empty;
            comparison = comparison with
            {
                AverageLabel = averageLabel,
                MinimumLabel = minimumLabel
            };
            comparisons[i] = comparison;
            var service = comparison.Service;
            _multiGraphValues.Add(comparison);
            _repliesYAxes.Add(string.IsNullOrWhiteSpace(service.HostName) ? service.IpEndPointStr : service.HostName);
        }

        _multiGraphYAxis.MinLimit = -0.5;
        _multiGraphYAxis.MaxLimit = Math.Max(0.5, comparisons.Length - 0.5);
    }

    private ServiceComparisonPoint BuildServiceComparison(PingableService service)
    {
        IReadOnlyList<PingableServiceReply> replies = IsGraphFrozen
                                                      && _frozenReplies.TryGetValue(service, out var frozenReplies)
            ? frozenReplies
            : service.Pings;
        var maxReplies = SelectedWindowSize.Count;
        var sampleCount = maxReplies > 0 ? Math.Min(replies.Count, maxReplies) : replies.Count;
        var failedCount = 0;
        var successCount = 0;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        var sum = 0d;

        for (var i = 0; i < sampleCount; i++)
        {
            var reply = replies[i];
            if (!reply.IsSucceeded || !double.IsFinite(reply.Time))
            {
                failedCount++;
                continue;
            }

            successCount++;
            sum += reply.Time;
            minimum = Math.Min(minimum, reply.Time);
            maximum = Math.Max(maximum, reply.Time);
        }

        return new ServiceComparisonPoint(
            service,
            sampleCount,
            failedCount,
            minimum,
            successCount > 0
                ? Math.Round(sum / successCount, 2, MidpointRounding.AwayFromZero)
                : double.PositiveInfinity,
            successCount > 0 ? maximum : double.PositiveInfinity,
            service.LastTime);
    }

    private static double GetPercentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return double.PositiveInfinity;
        if (sortedValues.Length == 1) return sortedValues[0];

        var position = (sortedValues.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex) return sortedValues[lowerIndex];

        var fraction = position - lowerIndex;
        return Math.Round(
            sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction,
            2,
            MidpointRounding.AwayFromZero);
    }

    private static double GetRobustAxisMaximum(double[] sortedValues, double median, double p95)
    {
        if (sortedValues.Length == 0) return 1;

        var actualMaximum = sortedValues[^1];
        var typicalMaximum = Math.Max(10, Math.Max(median * 3, p95 * 1.5));
        return Math.Ceiling(Math.Max(1, Math.Min(actualMaximum * 1.1, typicalMaximum)));
    }

    private static double GetFullAxisMaximum(double[] sortedValues)
    {
        return sortedValues.Length == 0
            ? 1
            : Math.Ceiling(Math.Max(1, sortedValues[^1] * 1.1));
    }

    private static string FormatMilliseconds(double value)
    {
        return double.IsFinite(value) ? $"{value:N0} ms" : "∞";
    }

    private static string FormatLatencyTooltip(PingChartPoint? point)
    {
        return point is null
            ? string.Empty
            : $"{point.Reply.SentDateTime:G}\n" +
              $"{App.Localization["Graph.Latency"]}: {FormatMilliseconds(point.Reply.Time)}\n" +
              $"{App.Localization["Graph.RollingAverage"]}: {FormatMilliseconds(point.RollingAverage)}\n" +
              $"{App.Localization["Ui.Status"]}: {StatusLocalization.GetText(point.Reply.Status)}";
    }

    private static string FormatRollingAverageTooltip(PingChartPoint? point)
    {
        return point is null
            ? string.Empty
            : $"{App.Localization["Graph.RollingAverage"]}: {FormatMilliseconds(point.RollingAverage)}";
    }

    private static string FormatCappedLatencyTooltip(PingChartPoint? point)
    {
        return point is null
            ? string.Empty
            : $"{point.Reply.SentDateTime:G}\n" +
              $"{App.Localization["Graph.Latency"]}: {FormatMilliseconds(point.Reply.Time)}\n" +
              App.Localization["Graph.AboveAutomaticScale"];
    }

    private static string FormatFailureTooltip(PingableServiceReply? reply)
    {
        return reply is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(reply.ErrorMessage)
                ? $"{reply.SentDateTime:G}\n{App.Localization["Ui.Status"]}: {StatusLocalization.GetText(reply.Status)}"
                : $"{reply.SentDateTime:G}\n{App.Localization["Ui.Status"]}: {StatusLocalization.GetText(reply.Status)}\n{reply.ErrorMessage}";
    }

    private static string FormatChartValue(double? value)
    {
        return value is not null && double.IsFinite(value.Value)
            ? $"{value.Value:#,##0}"
            : string.Empty;
    }

    private static bool HasDataLabelSpace(
        double value,
        double otherValue,
        double minimumGap)
    {
        return !double.IsFinite(otherValue)
               || Math.Abs(value - otherValue) >= minimumGap;
    }

    private static string FormatServiceName(ServiceComparisonPoint? comparison)
    {
        if (comparison is null) return string.Empty;

        var service = comparison.Service;
        return string.IsNullOrWhiteSpace(service.HostName) ? service.IpEndPointStr : service.HostName;
    }

    private static string FormatServiceComparisonTooltip(ServiceComparisonPoint? comparison)
    {
        if (comparison is null) return string.Empty;

        var service = comparison.Service;
        var loss = comparison.SampleCount > 0
            ? comparison.FailedCount * 100d / comparison.SampleCount
            : 0;
        var current = service.WasLastPingSucceeded && double.IsFinite(comparison.Current)
            ? FormatMilliseconds(comparison.Current)
            : "-";

        return $"{App.Localization["Graph.Minimum"]}: {FormatMilliseconds(comparison.Minimum)}\n" +
               $"{App.Localization["Graph.Average"]}: {FormatMilliseconds(comparison.Average)}\n" +
               $"{App.Localization["Graph.Current"]}: {current}\n" +
               $"{App.Localization["Graph.Maximum"]}: {FormatMilliseconds(comparison.Maximum)}\n" +
               $"{App.Localization["Graph.Loss"]}: {loss:0.##}% ({comparison.FailedCount}/{comparison.SampleCount})\n" +
               $"{App.Localization["Ui.Status"]}: {StatusLocalization.GetText(service.LastStatus)}";
    }

    private void ResetWindowStatistics()
    {
        CurrentTimeText = $"{App.Localization["Ui.Ping"]}: -";
        MedianTimeText = "P50: -";
        P95TimeText = "P95: -";
        JitterText = $"{App.Localization["Graph.Jitter"]}: -";
        WindowLossText = $"{App.Localization["Graph.Loss"]}: -";
    }

    private PingableServiceReply[] GetRepliesForGraph(PingableService service)
    {
        var maxReplies = SelectedWindowSize.Count;
        return IsGraphFrozen && _frozenReplies.TryGetValue(service, out var frozenReplies)
            ? frozenReplies.AsValueEnumerable().Take(maxReplies > 0 ? maxReplies : int.MaxValue).ToArray()
            : service.Pings.AsValueEnumerable().Take(maxReplies > 0 ? maxReplies : int.MaxValue).ToArray();
    }

    private void CaptureFrozenReplies()
    {
        _frozenReplies.Clear();
        foreach (var service in Services)
        {
            _frozenReplies[service] = service.Pings.AsValueEnumerable().ToArray();
        }
    }

    private static GraphWindowSizeOption[] CreateWindowSizeOptions(int configuredSize)
    {
        List<int> sizes = [50, 100, 250];
        if (configuredSize > 0 && !sizes.Contains(configuredSize)) sizes.Add(configuredSize);
        sizes.Sort();

        var options = new GraphWindowSizeOption[sizes.Count + 1];
        for (var i = 0; i < sizes.Count; i++)
        {
            options[i] = new GraphWindowSizeOption(
                sizes[i],
                App.Localization.Format("Graph.Samples", sizes[i]));
        }

        options[^1] = new GraphWindowSizeOption(0, App.Localization["Graph.AllSamples"]);
        return options;
    }

    private static GraphWindowSizeOption FindInitialWindowSize(
        GraphWindowSizeOption[] options,
        int configuredSize)
    {
        foreach (var option in options)
        {
            if (option.Count == configuredSize) return option;
        }

        return options[0];
    }

    public void Sort()
    {
        if (!HasMultiServices) return;

        var comparisons = _services
            .AsValueEnumerable()
            .Select(service => BuildServiceComparison(service))
            .OrderByDescending(static comparison => comparison.Average)
            .ThenByDescending(static comparison => comparison.Current)
            .ToArray();
        _services = comparisons
            .AsValueEnumerable()
            .Select(static comparison => comparison.Service)
            .ToArray();
        RebuildMultiGraphValues(comparisons);
    }


    private void OnBaseThemeChanged(ThemeVariant theme)
    {
        var color = App.SukiTextResource;
        var paint = new SolidColorPaint(new SKColor(color.R, color.G, color.B, color.A));

        foreach (var axis in SingleGraphXAxes)
        {
            axis.LabelsPaint = paint;
            axis.NamePaint = paint;
        }

        foreach (var axis in SingleGraphYAxes)
        {
            axis.LabelsPaint = paint;
            axis.NamePaint = paint;
        }

        foreach (var axis in MultiGraphXAxes)
        {
            axis.LabelsPaint = paint;
            axis.NamePaint = paint;
        }

        foreach (var axis in MultiGraphYAxes)
        {
            axis.LabelsPaint = paint;
            axis.NamePaint = paint;
        }
    }

    private void OnColorThemeChanged(SukiColorTheme theme)
    {
        if (SingleGraphSeries.Length == 0) return;
        if (SingleGraphSeries[0] is ColumnSeries<PingChartPoint> column)
        {
            column.Fill = new SolidColorPaint(new SKColor(
                theme.Primary.R,
                theme.Primary.G,
                theme.Primary.B));
        }
    }

    public sealed record GraphWindowSizeOption(int Count, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record PingChartPoint(
        PingableServiceReply Reply,
        double RollingAverage,
        double PlotLatency,
        double PlotRollingAverage,
        double FailureMarker,
        double CappedMarker,
        bool IsLatencyCapped);

    private sealed record ServiceComparisonPoint(
        PingableService Service,
        int SampleCount,
        int FailedCount,
        double Minimum,
        double Average,
        double Maximum,
        double Current)
    {
        public string AverageLabel { get; init; } = string.Empty;
        public string MinimumLabel { get; init; } = string.Empty;
    }

    private sealed class VerticalMarkerGeometry : BoundedDrawnGeometry, IDrawnElement<SkiaSharpDrawingContext>
    {
        private const float MarkerWidth = 7;

        public void Draw(SkiaSharpDrawingContext context)
        {
            var width = MathF.Min(MarkerWidth, Width);
            var radius = width * 0.5f;
            context.Canvas.DrawRoundRect(
                X + (Width - width) * 0.5f,
                Y,
                width,
                Height,
                radius,
                radius,
                context.ActiveSkiaPaint);
        }
    }

    private sealed class OutlinedLabelGeometry : LabelGeometry
    {
        public override void Draw(SkiaSharpDrawingContext context)
        {
            var paint = context.ActiveSkiaPaint;
            var color = paint.Color;
            var style = paint.Style;
            var strokeWidth = paint.StrokeWidth;
            var strokeJoin = paint.StrokeJoin;

            paint.Color = SKColors.Black;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 3;
            paint.StrokeJoin = SKStrokeJoin.Round;
            base.Draw(context);

            paint.Color = color;
            paint.Style = SKPaintStyle.Fill;
            base.Draw(context);

            paint.Style = style;
            paint.StrokeWidth = strokeWidth;
            paint.StrokeJoin = strokeJoin;
        }
    }
}