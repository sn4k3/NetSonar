using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

namespace build;

public class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    [field: AllowNull, MaybeNull]
    public string SoftwareName => field ??= Solution.NetSonar_Desktop.GetProperty("ProductName")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareCompany => field ??= Solution.NetSonar_Desktop.GetProperty("Company")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareCompanyRDNS => field ??= Solution.NetSonar_Desktop.GetProperty("CompanyRDNS")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareRDNS => field ??= $"{SoftwareCompanyRDNS}.{SoftwareName}";

    [field: AllowNull, MaybeNull]
    public string SoftwareAuthors => field ??= Solution.NetSonar_Desktop.GetProperty("Authors")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareSummary => field ??= Solution.NetSonar_Desktop.GetProperty("Summary")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareDescription => field ??= Solution.NetSonar_Desktop.GetProperty("Description")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareVersion
    {
        get
        {
            if (field is null)
            {
                field ??= Solution.NetSonar_Desktop.GetProperty("Version")!;
                if (field.EndsWith("-dev")) field = field[..^4];
            }
            return field;
        }
    }

    [field: AllowNull, MaybeNull]
    public string SoftwareCopyright => field ??= Solution.NetSonar_Desktop.GetProperty("Copyright")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareLicense => field ??= Solution.NetSonar_Desktop.GetProperty("PackageLicenseExpression")!;

    [field: AllowNull, MaybeNull]
    public string SoftwareRepositoryUrl => field ??= Solution.NetSonar_Desktop.GetProperty("RepositoryUrl")!;

    [field: AllowNull, MaybeNull]
    public string SoftwarePackageTags => field ??= Solution.NetSonar_Desktop.GetProperty("PackageTags")!;

    [field: AllowNull, MaybeNull]
    public string BuildRuntimeCacheFileName => field ??= Solution.NetSonar_Desktop.GetProperty(nameof(BuildRuntimeCacheFileName))!;

    public static int Main () => Execute<Build>(x => x.Compile);

    [Solution(GenerateProjects = true)]
    internal readonly Solution Solution = null!;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    public readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("RIDs to publish separated by space, Default: 'win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64'")]
    readonly string[] RIds = [
        "win-x64", "win-arm64",
        "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64"
    ];

    [Parameter("When publish set this variable(true) to create the bundles (zip, apps, installers). Default: True")]
    readonly bool PublishCreateBundles = true;

    [Parameter("When publish set this variable(true) to keep only the bundles (zip, apps, installers), compilation folders will be removed. Default: False")]
    readonly bool PublishDiscardNonBundles;

    [Parameter("When publish set this variable(true) to bundle all arch in a single bundle, eg: x64 and arm64 together, app will then run the required arch. Default: False")]
    readonly bool PublishBundleWithMultipleArch;

    Project MainProject => Solution.NetSonar_Desktop;

    AbsolutePath ArtifactsDirectory => Solution.NetSonar_Avalonia.GetProperty("ArtifactsPath");
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish";


    public Target Print => _ => _
        .Executes(() =>
        {
            Log.Information("RootDirectory = {Value}", RootDirectory);
            Log.Information("TemporaryDirectory = {Value}", TemporaryDirectory);
            Log.Information("BuildAssemblyDirectory = {Value}", BuildAssemblyDirectory);
            Log.Information("BuildAssemblyFile = {Value}", BuildAssemblyFile);
            Log.Information("BuildProjectDirectory = {Value}", BuildProjectDirectory);
            Log.Information("BuildProjectFile = {Value}", BuildProjectFile);
            Log.Information("Solution = {Value}", Solution);
            Log.Information("SolutionDirectory = {Value}", Solution.Directory);
            Log.Information("PublishDirectory = {Value}", PublishDirectory);
            Log.Information("Version = {Value}", SoftwareVersion);
            Log.Information("IsWin = {Value}", IsWin);
            Log.Information("IsOsx = {Value}", IsOsx);
            Log.Information("IsLinux = {Value}", IsLinux);
            Log.Information("IsWsl = {Value}", IsWsl);
            Log.Information("RFID = {Value}", string.Join(", ", RIds));
        });

    public Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            DotNetClean();
            ArtifactsDirectory.DeleteDirectory();
            //ArtifactsDirectory.GlobDirectories("*/bin", "*/obj").ForEach(DeleteDirectory);
            //EnsureCleanDirectory(ArtifactsDirectory);
        });

    public Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(options => options
                .SetProjectFile(MainProject)
            );
        });

    public Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(options => options
                .SetProjectFile(MainProject)
                .SetConfiguration(Configuration.Release)
            );
        });

    public Target Publish => _ => _
        //.OnlyWhenStatic(() => Configuration == Configuration.Release)
        .Executes(() =>
        {
            var publishPaths = new Dictionary<string, AbsolutePath>();
            // Clean previous publishes
            foreach (var rid in RIds)
            {
                var path = PublishDirectory / $"{SoftwareName}_{rid}_v{SoftwareVersion}";
                publishPaths.Add(rid, path);
                path.DeleteDirectory();
            }
            DotNetPublish(_ => _
                .SetProject(MainProject)
                .SetConfiguration(Configuration.Release)
                .CombineWith(RIds, (settings, rid) => settings
                    .SetSelfContained(true)
                    .SetPublishReadyToRun(true)
                    .SetRuntime(rid)
                    .SetOutput(publishPaths[rid])
                )
            );


            // Set executable permissions
            if (IsUnix)
            {
                foreach (var rid in RIds)
                {
                    if (rid.StartsWith("win")) continue;
                    (publishPaths[rid] / SoftwareName).SetExecutable();
                    var binaries = (publishPaths[rid] / "binaries").GetFiles(depth: 4);
                    foreach (var binary in binaries)
                    {
                        if (!string.IsNullOrEmpty(binary.Extension)) continue;
                        Log.Information("Set executable permission: {fileName}", binary.Name);
                        binary.SetExecutable();
                    }
                }
            }

            // Bundle previous publishes
            if (PublishCreateBundles)
            {
                foreach (var rid in RIds)
                {
                    var publishPath = publishPaths[rid];
                    var zipPath = publishPath + ".zip";
                    zipPath.DeleteFile();

                    var arch = "x64";
                    var archAlt = "x86_64";
                    var executableArch = arch;
                    var executableArchAlt = archAlt;

                    if (rid.EndsWith("-arm64"))
                    {
                        arch = "arm64";
                        //archAlt = "aarch64";
                        archAlt = "arm_aarch64 ";
                    }

                    if (RuntimeInformation.OSArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Armv6)
                    {
                        executableArch = "arm64";
                        executableArchAlt = "aarch64";
                    }

                    var runtimeCacheFile = publishPath / BuildRuntimeCacheFileName;

                    var runtimeBuild = new BuildRuntime(rid, SoftwareVersion, true);
                    runtimeCacheFile.WriteJson(runtimeBuild);


                    if (!rid.StartsWith("osx"))
                    {
                        Log.Information("Compressing: {fileName}", zipPath.Name);
                        publishPath.ZipTo(zipPath, null, CompressionLevel.SmallestSize, FileMode.Create);
                    }

                    if (rid.StartsWith("win"))
                    {
                        if (IsWin)
                        {
                            runtimeBuild = runtimeBuild with { BundleType = BuildRuntime.BundleTypes.Installer };
                            runtimeCacheFile.WriteJson(runtimeBuild);

                            DotNetBuild(options => options
                                .SetProjectFile(Solution.NetSonar_MsiInstaller)
                                .SetPlatform(arch)
                                .SetConfiguration(Configuration.Release)
                                .SetOutputDirectory(PublishDirectory)
                            );

                            (publishPath + ".wixpdb").DeleteFile();
                        }
                        else
                        {
                            Log.Warning("Skipping Windows MSI build on non-Windows platform. wix.exe not compatible with Linux yet.");
                        }
                    }
                    else if (rid.StartsWith("osx"))
                    {
                        if (PublishBundleWithMultipleArch)
                        {
                            if (IsUnix)
                            {
                                Log.Information("Bundling macOS multi-arch for {rid} app.", rid);
                                var macOSRootAppPath = PublishDirectory / $"{SoftwareName}_osx-multiarch_v{SoftwareVersion}.app";
                                var macOSAppPath = macOSRootAppPath / $"{SoftwareName}.app";
                                var macOSAppContentsPath = macOSAppPath / "Contents";
                                var macOSMacOSBinPath = macOSAppContentsPath / "MacOS";
                                var macOSAppResourcesPath = macOSAppContentsPath / "Resources";
                                var macOSAppBinPath = macOSMacOSBinPath / rid;
                                var macOSAppEntryScriptPath = macOSMacOSBinPath / $"{SoftwareName}.sh";
                                var macOSAppInfoPListFile = macOSAppContentsPath / "Info.plist";
                                var macOSAppEntitlementsFile = macOSAppContentsPath / $"{SoftwareName}.entitlements";
                                var icnsLogoFilePath = RootDirectory / "media" / $"{SoftwareName}.icns";
                                macOSAppBinPath.CreateOrCleanDirectory();
                                macOSAppResourcesPath.CreateOrCleanDirectory();
                                icnsLogoFilePath.CopyToDirectory(macOSAppResourcesPath);

                                runtimeBuild = runtimeBuild with { Runtime = "osx-multiarch", BundleType = BuildRuntime.BundleTypes.App };
                                runtimeCacheFile.WriteJson(runtimeBuild);
                                publishPath.Copy(macOSAppBinPath, ExistsPolicy.MergeAndOverwrite);

                                macOSAppInfoPListFile.WriteAllText(MacAppBundle.GetInfoPList(SoftwareName, SoftwareVersion, SoftwareCopyright).ReplaceLineEndings("\n"));
                                macOSAppEntitlementsFile.WriteAllText(MacAppBundle.Entitlements.ReplaceLineEndings("\n"));
                                macOSAppEntryScriptPath.WriteAllText(MacAppBundle.GetMultiArchEntryScript(SoftwareName).ReplaceLineEndings("\n"));
                                macOSAppEntryScriptPath.SetExecutable();
                            }
                            else
                            {
                                Log.Warning("Skipping multi-arch bundle, non unix OS.");
                            }
                        }
                        else
                        {
                            Log.Information("Bundling macOS {rid} app.", rid);
                            var macOSRootAppPath = publishPath + ".app";
                            var macOSAppPath = macOSRootAppPath / $"{SoftwareName}.app";
                            var macOSAppContentsPath = macOSAppPath / "Contents";
                            var macOSAppBinPath = macOSAppContentsPath / "MacOS";
                            var macOSAppResourcesPath = macOSAppContentsPath / "Resources";
                            var macOSAppInfoPListFile = macOSAppContentsPath / "Info.plist";
                            var macOSAppEntitlementsFile = macOSAppContentsPath / $"{SoftwareName}.entitlements";
                            var icnsLogoFilePath = RootDirectory / "media" / $"{SoftwareName}.icns";
                            macOSRootAppPath.CreateOrCleanDirectory();
                            macOSAppResourcesPath.CreateOrCleanDirectory();
                            icnsLogoFilePath.CopyToDirectory(macOSAppResourcesPath);

                            runtimeBuild = runtimeBuild with { BundleType = BuildRuntime.BundleTypes.App };
                            runtimeCacheFile.WriteJson(runtimeBuild);
                            publishPath.Copy(macOSAppBinPath);

                            macOSAppInfoPListFile.WriteAllText(MacAppBundle.GetInfoPList(SoftwareName, SoftwareVersion, SoftwareCopyright).ReplaceLineEndings("\n"));
                            macOSAppEntitlementsFile.WriteAllText(MacAppBundle.Entitlements.ReplaceLineEndings("\n"));

                            if (IsOsx)
                            {
                                Log.Information("Codesign {name}", macOSAppPath.Name);
                                ProcessTasks.StartProcess("codesign", $"--force --deep --sign - \"{macOSAppPath}\"").AssertWaitForExit();
                            }


                            Log.Information("Compressing: {fileName}", zipPath.Name);
                            macOSRootAppPath.ZipTo(zipPath, null, CompressionLevel.SmallestSize, FileMode.Create);

                            macOSRootAppPath.DeleteDirectory();
                        }
                    }
                    else if (rid.StartsWith("linux") || rid.StartsWith("unix"))
                    {
                        if (IsLinux)
                        {
                            runtimeBuild = runtimeBuild with { BundleType = BuildRuntime.BundleTypes.AppImage };
                            runtimeCacheFile.WriteJson(runtimeBuild);

                            var appImagePublishPath = publishPath + ".AppImage";
                            appImagePublishPath.DeleteFile();

                            var appImageToolExtractedFolderName = $"appimagetool-{executableArchAlt}";
                            var appImageToolFileName = appImageToolExtractedFolderName + ".AppImage";


                            var tempBuildPath = AbsolutePath.Create(Path.GetTempPath()) / $"{SoftwareName}_appimage_build";
                            tempBuildPath.CreateDirectory();

                            var appImageToolPath = tempBuildPath / appImageToolFileName;
                            var appImageToolExtractedPath = tempBuildPath / appImageToolExtractedFolderName;
                            var appImageAppRunBinary = appImageToolExtractedPath / "AppRun";
                            if (!appImageToolPath.FileExists())
                            {
                                // Download file here
                                var url = $"{LinuxAppBundle.AppImageGitHubUrl}/releases/download/continuous/{appImageToolFileName}";
                                Log.Information("Downloading {url} to {AppImageToolPath}", url, appImageToolPath);
                                HttpTasks.HttpDownloadFile(url ,appImageToolPath);
                                if (!appImageToolPath.FileExists())
                                {
                                    Log.Error($"Failed to download {url}");
                                    continue;
                                }
                                appImageToolPath.SetExecutable();
                            }

                            if (!appImageToolExtractedPath.DirectoryExists())
                            {
                                bool fuseAvailable = LinuxAppBundle.IsFuseAvailable();
                                if (!fuseAvailable)
                                {
                                    Log.Warning("FUSE not detected (libfuse.so.2 missing). AppImage extraction may fail.");
                                }

                                // Extract AppImage so it can be run in Docker containers and on machines that don't have FUSE installed
                                // Note: Extracting requires libglib2.0-0 to be installed
                                ProcessTasks.StartShell($"cd {tempBuildPath} && ./{appImageToolFileName} --appimage-extract", tempBuildPath).AssertWaitForExit();
                                //ProcessTasks.StartShell($"ls -la {tempBuildPath}").AssertWaitForExit();
                                var tempExtractedFolder = tempBuildPath / "squashfs-root";
                                Log.Information("{TempExtractedFolder}: {DirectoryExists}", tempExtractedFolder, tempExtractedFolder.DirectoryExists());
                                if (!tempExtractedFolder.DirectoryExists())
                                {
                                    Log.Error("Failed to extract {AppImageToolFileName} to {TempBuildPath}", appImageToolFileName, tempBuildPath);
                                    throw new InvalidOperationException($"Failed to extract {appImageToolFileName} to {tempBuildPath}");
                                }

                                tempExtractedFolder.Rename(appImageToolExtractedFolderName);

                                if (appImageAppRunBinary.FileExists())
                                {
                                    appImageAppRunBinary.SetExecutable();
                                }
                                else
                                {
                                    Log.Error("Expected AppRun binary not found at {Path}", appImageAppRunBinary);
                                    throw new InvalidOperationException("AppRun binary missing after extraction");
                                }
                            }

                            // Create AppImage structure
                            var appImageDirPath = publishPath + "_AppImage";
                            appImageDirPath.CreateOrCleanDirectory();

                            // Copy Logo
                            var svgLogoFilePath = RootDirectory / "media" / $"{SoftwareName}.svg";
                            var appImageHiIconDirPath = appImageDirPath / "usr" / "share" / "icons" / "hicolor" / "scalable" / "apps";
                            svgLogoFilePath.CopyToDirectory(appImageHiIconDirPath);
                            svgLogoFilePath.CopyToDirectory(appImageDirPath);

                            // Create entry files
                            var appImageAppRunFilePath = appImageDirPath / "AppRun";
                            appImageAppRunFilePath.WriteAllText(LinuxAppBundle.GetAppImageAppRunFile(SoftwareName).ReplaceLineEndings("\n"));
                            appImageAppRunFilePath.SetExecutable();

                            var appImageDesktopFilePath = appImageDirPath / $"{SoftwareRDNS}.desktop";
                            appImageDesktopFilePath.WriteAllText(LinuxAppBundle.GetAppImageDesktopFile(this).ReplaceLineEndings("\n"));

                            var appImageApplicationsDirPath = appImageDirPath / "usr" / "share" / "applications";
                            appImageDesktopFilePath.CopyToDirectory(appImageApplicationsDirPath);

                            var appImageAppDataXmlFilePath = appImageDirPath / "usr" / "share" / "metainfo" / $"{SoftwareRDNS}.appdata.xml";
                            appImageAppDataXmlFilePath.WriteAllText(LinuxAppBundle.GetAppImageAppDataXmlFile(this).ReplaceLineEndings("\n"));

                            // Copy application files
                            var appImageBinDirPath = appImageDirPath / "usr" / "bin";
                            publishPath.Copy(appImageBinDirPath);

                            // Create AppImage
                            ProcessTasks.StartShell($"ARCH={archAlt} \"{appImageAppRunBinary}\" \"{appImageDirPath}\" \"{appImagePublishPath}\"").AssertWaitForExit();
                            appImagePublishPath.SetExecutable();

                            appImageDirPath.DeleteDirectory();
                        }
                        else
                        {
                            Log.Warning("Skipping Linux AppImage build on non-Linux platform.");
                        }
                    }

                    if (PublishDiscardNonBundles)
                    {
                        publishPath.DeleteDirectory();
                    }
                }

                if (PublishBundleWithMultipleArch)
                {
                    if (IsUnix)
                    {
                        if (Enumerable.Contains(RIds, "osx-x64") && Enumerable.Contains(RIds, "osx-arm64"))
                        {
                            Log.Information("Bundling macOS multi-arch app.");
                            var macOSRootAppPath = PublishDirectory / $"{SoftwareName}_osx-multiarch_v{SoftwareVersion}.app";
                            var macOSAppPath = macOSRootAppPath / $"{SoftwareName}.app";
                            var macOSAppContentsPath = macOSAppPath / "Contents";
                            var macOSAppResourcesPath = macOSAppContentsPath / "Resources";
                            var macOSAppX64BinPath = macOSAppContentsPath / "MacOS" / "osx-x64";
                            var macOSAppArm64BinPath = macOSAppContentsPath / "MacOS" / "osx-arm64";
                            var macOSAppSharedBinPath = macOSAppContentsPath / "MacOS" / "shared";
                            macOSAppSharedBinPath.CreateOrCleanDirectory();
                            macOSAppResourcesPath.CreateOrCleanDirectory();

                            var files = macOSAppX64BinPath.GlobFiles("**");
                            Parallel.ForEach(files, x64File =>
                            {
                                AbsolutePath arm64File = x64File.ToString().Replace(macOSAppX64BinPath, macOSAppArm64BinPath);

                                if (!arm64File.Exists()) return;

                                var x64Hash = x64File.GetFileHash();
                                var arm64Hash = arm64File.GetFileHash();
                                if (!x64Hash.Equals(arm64Hash, StringComparison.Ordinal)) return;
                                //Log.Information($"Same hash for: {x64File}");

                                AbsolutePath sharedFile = x64File.ToString().Replace(macOSAppX64BinPath, macOSAppSharedBinPath);
                                x64File.Move(sharedFile);
                                arm64File.DeleteFile();

                                sharedFile.AddUnixSymlink(x64File);
                                sharedFile.AddUnixSymlink(arm64File);
                            });

                            if (IsOsx)
                            {
                                Log.Information("Codesign {name}", macOSAppPath.Name);
                                ProcessTasks.StartProcess("codesign", $"--force --deep --sign - \"{macOSAppPath}\"").AssertWaitForExit();
                            }

                            var zipPath = PublishDirectory / $"{SoftwareName}_osx-multiarch_v{SoftwareVersion}.zip";
                            zipPath.DeleteFile();
                            Log.Information("Compressing: {fileName}", zipPath.Name);
                            macOSRootAppPath.ZipTo(zipPath, null, CompressionLevel.SmallestSize, FileMode.Create);

                            macOSRootAppPath.DeleteDirectory();
                        }
                    }
                    else
                    {
                        Log.Warning("Skipping multi-arch bundle, non unix OS.");
                    }
                }
            }

            // Clean publish objects
            Log.Information("Cleaning objects.");
            ArtifactsDirectory.GlobDirectories("**/release_*", "**/debug_*").ForEach(path =>
            {
                try
                {
                    path.DeleteDirectory();
                }
                catch (Exception e)
                {
                    Log.Warning(e.Message);
                }
            });

        });
}