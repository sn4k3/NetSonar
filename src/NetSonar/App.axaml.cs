using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NetSonar.Avalonia.ViewModels;
using NetSonar.Avalonia.Views;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using NetSonar.Avalonia.Common;
using NetSonar.Avalonia.ViewModels.Dialogs;
using NetSonar.Avalonia.Views.Dialogs;
using System.Threading.Tasks;
using NetSonar.Avalonia.Settings;
using ZLogger;
using System.Globalization;
using Updatum;

namespace NetSonar.Avalonia;

public partial class App : Application
{
    /// <summary>
    /// Mutex to prevent multiple instances of the application from running.
    /// </summary>
    private static readonly Mutex AppMutex = new(true, $"Mutex_{Environment.UserDomainName}_{Environment.UserName}_{EntryApplication.AssemblyName}_{{8AEA6BAE-D5D5-49FA-8A8E-479FFC369D5D}}");

    public const bool IsDebug =
#if DEBUG
            true
#else
            false
#endif
        ;

    /// <summary>
    /// Flag to determine if the application is running in crash report mode.
    /// </summary>
    public static bool IsCrashReport { get; private set; }

    public static CrashReports CrashReports => CrashReports.Instance;

    /// <summary>
    /// Command line arguments passed to the application.
    /// </summary>
    public static string[] Args { get; private set; } = [];

    /// <summary>
    /// Main window of the application.
    /// </summary>
    public static TopLevel TopLevel => MainWindow;

    public static Window MainWindow { get; private set; } = null!;

    public static readonly SukiDialogManager DialogManager = new();
    public static readonly SukiToastManager ToastManager = new();

    public override void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.DefaultThreadCurrentCulture =
                OptimalCultureInfo;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        SetupLogger();
        SetupTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopCheck)
        {
            if (desktopCheck.Args?.Length > 0) Args = desktopCheck.Args;
            if (Args.Length >= 1)
            {
                switch (Args[0])
                {
                    case "--crash-report" when Args.Length >= 2:
                        IsCrashReport = true;
                        break;
                }
            }
        }

        if (IsCrashReport)
        {
            _ = long.TryParse(Args[1], out var crashReportHashCode);
            var crashReport = CrashReports.GetActual(crashReportHashCode);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow = new GenericWindow
                {
                    Title = $"{SoftwareWithVersion} - Crash Report",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    CanResize = false,
                    CanMaximize = false,
                    MaxHeightScreenRatio = 0.75,
                    MaxWidthScreenRatio = 0.75,
                    Content = new CrashReportDialogView
                    {
                        DataContext = new CrashReportDialogModel(crashReport)
                    }
                };
                desktop.MainWindow = MainWindow;
            }
            else
            {
                Logger.ZLogCritical($"{crashReport?.FormatedMessage ?? "The application crashed due an unexpected exception. (Unable to present the information in the UI"}.");
                Environment.Exit(0);
            }
        }
        else
        {
            // Line below is needed to remove Avalonia data validation.
            // Without this line you will get duplicate validations from both Avalonia and CT
            //BindingPlugins.DataValidators.RemoveAt(0);
#if DEBUG
            if(true)
#else
            if (Design.IsDesignMode || AppMutex.WaitOne(TimeSpan.Zero, true))
#endif
            {
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => HandleUnhandledException((Exception)e.ExceptionObject, "Non-UI");
                TaskScheduler.UnobservedTaskException += (sender, e) => HandleUnhandledException(e.Exception, "Task");

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    /*MainWindow = new MainWindow
                    {
                        DataContext = MainViewModel
                    };
                    desktop.MainWindow = MainWindow;
                    desktop.Exit += DesktopOnExit;*/

                    Services.AddSingleton(desktop);
                    SetupViews();

                    DataTemplates.Add(new ViewLocator(Views));

                    MainWindow = (Views.CreateView<MainViewModel>(ServicesProvider) as Window)!;
                    desktop.MainWindow = MainWindow;
                    desktop.Exit += DesktopOnExit;
                    AppUpdater.UpdateFound += AppUpdaterOnUpdateFound;
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
                {
                    /*singleViewPlatform.MainView = new MainView
                    {
                        DataContext = MainViewModel
                    };*/
                }
            }
            else
            {
#pragma warning disable CS0162 // Unreachable code detected
                AppMutex.Dispose();

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    MainWindow = new GenericWindow()
                    {
                        Title = $"{SoftwareWithVersion} - Already running",
                        SizeToContent = SizeToContent.WidthAndHeight,
                        CanResize = false,
                        CanMaximize = false,
                        MaxHeightScreenRatio = 0.75,
                        MaxWidthScreenRatio = 0.75,
                        Topmost = true,
                        Content = new InstanceAlreadyRunningDialogView
                        {
                            DataContext = new InstanceAlreadyRunningDialogModel()
                        }
                    };
                    desktop.MainWindow = MainWindow;
                }
                else
                {
                    Logger.ZLogInformation($"""
                                There is another instance of {Software} running. Only one instance of {Software} can run at a time.
                                Please find and open the running instance or close it before starting a new one. (Unable to present this information in the UI).
                                """);
                    Environment.Exit(0);
                }
#pragma warning restore CS0162 // Unreachable code detected
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DesktopOnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        PanicSaveSettings();
        //AppMutex.ReleaseMutex();
        AppMutex.Dispose();
    }
}
