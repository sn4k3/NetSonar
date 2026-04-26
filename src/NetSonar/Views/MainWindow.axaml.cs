using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.Settings;

namespace NetSonar.Avalonia.Views;

public partial class MainWindow : SukiWindowExtended
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"{App.SoftwareWithVersion}   [{RuntimeInformation.RuntimeIdentifier}]";
#if DEBUG
        Title += " [Debug]";
#endif

        KeyBindings.Add(new KeyBinding()
        {
            Gesture = new KeyGesture(Key.F11),
            Command = new RelayCommand(ToggleFullScreen)
        });
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        if (AppSettings.Instance.CheckForUpdates
            && (DateTime.Now - App.AppSettings.LastUpdateDateTimeCheck).TotalHours >= App.CheckUpdateHourInterval)
        {
            DispatcherTimer.RunOnce(() => _ = App.CheckForUpdatesAsync(false), TimeSpan.FromSeconds(5));
        }

    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(e.Property, WindowStateProperty))
        {
            var windowState = WindowState;
            if (windowState != WindowState.Minimized)
            {
                AppSettings.Instance.LastWindowState = WindowState;
                /*if (windowState == WindowState.FullScreen)
                {
                    App.ShowToast("Fullscreen mode", "Fullscreen mode is activated\nPress F11 to exit.");
                }*/
            }
        }
        base.OnPropertyChanged(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (AppSettings.Instance.IsTrayVisible && AppSettings.Instance.CloseToTray)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Hide();
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
