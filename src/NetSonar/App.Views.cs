using Microsoft.Extensions.DependencyInjection;
using NetSonar.Avalonia.Common;
using NetSonar.Avalonia.Services;
using NetSonar.Avalonia.ViewModels;
using NetSonar.Avalonia.ViewModels.Dialogs;
using NetSonar.Avalonia.ViewModels.Fragments;
using NetSonar.Avalonia.Views;
using NetSonar.Avalonia.Views.Dialogs;
using NetSonar.Avalonia.Views.Fragments;

namespace NetSonar.Avalonia;

public partial class App
{
    public readonly ServiceCollection Services = new();
    public ServiceProvider ServicesProvider { get; private set; } = null!;
    public AppViews Views { get; private set;} = null!;

    private void SetupViews()
    {
        Views = ConfigureViews(Services);
        ServicesProvider = ConfigureServices(Services);
    }


    private static AppViews ConfigureViews(ServiceCollection services)
    {
        return new AppViews()

                // Add main view
                .AddView<MainWindow, MainViewModel>(services)

                // Add pages
                .AddView<PingableServicesPage, PingableServicesPageModel>(services)
                .AddView<NetworkInterfacesPage, NetworkInterfacesPageModel>(services)
                .AddView<SpeedTestPage, SpeedTestPageModel>(services)
                .AddView<SettingsPage, SettingsPageModel>(services)

                // Fragments
                .AddView<PingableServiceGraphFragment, PingableServiceGraphFragmentModel>(services)


                // Add additional views
                //.AddView<DialogView, DialogViewModel>(services)
                .AddView<InstanceAlreadyRunningDialogView, InstanceAlreadyRunningDialogModel>(services)
                .AddView<AboutDialogView, AboutDialogModel>(services)
                .AddView<AddPingServicesDialogView, AddPingServicesDialogModel>(services)
                .AddView<AppUpdateDialogView, AppUpdateDialogModel>(services)
                .AddView<SetInterfaceIPDialogView, SetInterfaceIPDialogModel>(services)
            //.AddView<RecursiveView, RecursiveViewModel>(services)
            //.AddView<CustomThemeDialogView, CustomThemeDialogViewModel>(services)
            ;
    }

    private static ServiceProvider ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<ClipboardService>();
        //services.AddSingleton<PageNavigationService>();

        return services.BuildServiceProvider();
    }

}