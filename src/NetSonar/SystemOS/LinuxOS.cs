using System.IO;

namespace NetSonar.Avalonia.SystemOS;

public static class LinuxOS
{
    public enum LinuxPackageManager
    {
        Unknown,
        Apt,
        Dnf,
        Pacman,
        Zypper
    }


    private static LinuxPackageManager? _packageManager;
    /// <summary>
    /// Gets the default Linux package manager detected for the current system.
    /// </summary>
    /// <remarks>The returned package manager is determined based on the operating system environment. This
    /// property is read-only and always returns a valid instance representing the detected package manager.</remarks>
    public static LinuxPackageManager PackageManager => _packageManager ??= GetPackageManager();

    /// <summary>
    /// Gets the command-line name of the current package manager.
    /// </summary>
    public static string PackageManagerCommand { get; } = PackageManager.ToString().ToLowerInvariant();

    /// <summary>
    /// Detects and returns the type of Linux package manager available on the current system.
    /// </summary>
    /// <remarks>This method checks for the presence of common package manager executables in standard
    /// locations. It does not guarantee that the detected package manager is fully functional or configured on the
    /// system.</remarks>
    /// <returns>A value from the <see cref="LinuxPackageManager"/> enumeration indicating the detected package manager. Returns
    /// <see cref="LinuxPackageManager.Unknown"/> if no known package manager is found.</returns>
    private static LinuxPackageManager GetPackageManager()
    {
        if (File.Exists("/usr/bin/apt")) return LinuxPackageManager.Apt;
        if (File.Exists("/usr/bin/dnf")) return LinuxPackageManager.Dnf;
        if (File.Exists("/usr/bin/pacman")) return LinuxPackageManager.Pacman;
        if (File.Exists("/usr/bin/zypper")) return LinuxPackageManager.Zypper;
        return LinuxPackageManager.Unknown;
    }
}