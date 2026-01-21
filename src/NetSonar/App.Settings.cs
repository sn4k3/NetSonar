using System.Diagnostics.CodeAnalysis;
using System;
using System.IO;
using NetSonar.Avalonia.Settings;

namespace NetSonar.Avalonia;

public partial class App
{
    [field: AllowNull, MaybeNull]
    public static string ProfilePath
    {
        get
        {
            if (field is null)
            {
                if (OperatingSystem.IsWindows())
                {
                    field = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Software);
                }
                else if (OperatingSystem.IsLinux())
                {
                    field = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Software);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    field = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library",
                        "Application Support",
                        Software);
                }
                else
                {
                    field = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Software);
                }
            }

            return field;
        }
    }

    public static string LogsPath => Path.Combine(ProfilePath, "logs");
    public static string LogFile => Path.Combine(LogsPath, "app.log");
    public static string CrashReportsFile => Path.Combine(LogsPath, "crash_reports.json");

    public static string BackupsPath => Path.Combine(ProfilePath, "backups");

    public static string ConfigPath => Path.Combine(ProfilePath, "settings");

    /// <summary>
    /// Immediately saves the current application settings to persistent storage.
    /// </summary>
    /// <remarks>This method is intended for emergency scenarios where settings must be saved without delay,
    /// such as during unexpected shutdowns or critical failures. It does not perform validation or prompt for user
    /// confirmation.</remarks>
    public static void PanicSaveSettings()
    {
        AppSettings.SaveInstance();
        PingableServicesFile.SaveInstance();
        PingableServicesFile.SavePingRepliesInstance();
    }
}