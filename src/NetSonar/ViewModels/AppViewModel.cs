using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetSonar.Avalonia.ViewModels;

public partial class AppViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial PingableServicesPageModel? PingsPage { get; set; }

    [ObservableProperty]
    public partial string PingsSummary { get; set; } = string.Empty;

    public int PingsTotalCount => PingsPage is null ? 0 : PingableServicesPageModel.ServicesCount;
    public int PingsUpCount => PingsPage?.ServicesSucceededCount ?? 0;
    public int PingsDownCount => PingsPage?.ServicesFailedCount ?? 0;

    partial void OnPingsPageChanged(PingableServicesPageModel? oldValue, PingableServicesPageModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnPingsPagePropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnPingsPagePropertyChanged;

        OnPropertyChanged(nameof(PingsTotalCount));
        OnPropertyChanged(nameof(PingsUpCount));
        OnPropertyChanged(nameof(PingsDownCount));
    }

    private void OnPingsPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PingableServicesPageModel.ServicesCount):
            case nameof(PingableServicesPageModel.ServicesSucceededCount):
            case nameof(PingableServicesPageModel.ServicesFailedCount):
                PingsSummary = $"🟢 {PingsUpCount} up  ·  🔴 {PingsDownCount} down";
                break;
        }
    }

    [RelayCommand]
    public void SetThemeSystem()
    {
        App.ChangeBaseTheme(ApplicationTheme.Default);
    }

    [RelayCommand]
    public void SetThemeLight()
    {
        App.ChangeBaseTheme(ApplicationTheme.Light);
    }

    [RelayCommand]
    public void SetThemeDark()
    {
        App.ChangeBaseTheme(ApplicationTheme.Dark);
    }

    [RelayCommand]
    public void ShowApplication()
    {
        App.MainWindow.WindowState = AppSettings.LastWindowState;
        App.MainWindow.Show();
    }

    [RelayCommand]
    public void HideApplication()
    {
        App.MainWindow.WindowState = WindowState.Minimized;
        App.MainWindow.Hide();
    }

    [RelayCommand]
    public void ExitApplication()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}