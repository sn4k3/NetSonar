using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ZLogger;

namespace NetSonar.Avalonia.SystemOS;

/// <summary>
/// Manages the per-user "run on system login" entry on Windows, Linux and macOS.
/// Source of truth is the OS itself — never the settings file — so it stays in sync
/// when the user toggles autostart externally (Task Manager, Login Items, etc.).
/// </summary>
public static class Autostart
{
    private const string AppKey = "NetSonar";
    private const string LaunchAgentLabel = $"pt.ptrtech.{AppKey}";
    private const string LaunchArgs = "--minimized";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsIsEnabled();
                if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopFile);
                if (OperatingSystem.IsMacOS()) return File.Exists(MacPlistFile);
            }
            catch (Exception e)
            {
                App.Logger.ZLogError(e, $"Autostart.IsEnabled failed");
            }
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows()) WindowsSet(enabled);
            else if (OperatingSystem.IsLinux()) LinuxSet(enabled);
            else if (OperatingSystem.IsMacOS()) MacSet(enabled);
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, $"Autostart.SetEnabled({enabled}) failed");
        }
    }

    /// <summary>
    /// If autostart is currently enabled, rewrites the entry with the current executable path.
    /// Call once at startup so a moved/updated binary doesn't leave a stale autostart pointer.
    /// </summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            if (IsEnabled) SetEnabled(true);
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "Autostart.RefreshIfEnabled failed");
        }

    }

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable path.");

    #region Windows

    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: false);
        return key?.GetValue(AppKey) is string s && !string.IsNullOrEmpty(s);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsSet(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true);
        if (enabled)
        {
            key.SetValue(AppKey, $"\"{ExecutablePath}\" {LaunchArgs}");
        }
        else
        {
            key.DeleteValue(AppKey, throwOnMissingValue: false);
        }
    }

    #endregion

    #region Linux

    private static string LinuxDesktopFile
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
            {
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(configHome, "autostart", $"{AppKey.ToLowerInvariant()}.desktop");
        }
    }

    private static void LinuxSet(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(LinuxDesktopFile)) File.Delete(LinuxDesktopFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopFile)!);
        File.WriteAllText(LinuxDesktopFile,
            $"""
             [Desktop Entry]
             Type=Application
             Name={AppKey}
             Exec="{ExecutablePath}" {LaunchArgs}
             Terminal=false
             X-GNOME-Autostart-enabled=true
             """);
    }

    #endregion

    #region macOS

    private static string MacPlistFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    private static void MacSet(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(MacPlistFile)) File.Delete(MacPlistFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MacPlistFile)!);
        File.WriteAllText(MacPlistFile,
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
             <plist version="1.0">
             <dict>
                 <key>Label</key>
                 <string>{LaunchAgentLabel}</string>
                 <key>ProgramArguments</key>
                 <array>
                     <string>{ExecutablePath}</string>
                     <string>{LaunchArgs}</string>
                 </array>
                 <key>RunAtLoad</key>
                 <true/>
             </dict>
             </plist>
             """);
    }

    #endregion
}
