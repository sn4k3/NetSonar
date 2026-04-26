# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

NetSonar is a cross-platform (Windows/macOS/Linux, x64/arm64) Avalonia desktop network-diagnostics tool: ICMP/TCP/UDP/HTTP pings, network interface management, subnet/service discovery, speed tests.

- TFM: `net10.0`, `LangVersion=preview`, signed assembly (`NetSonar.snk`).
- UI: Avalonia 11.3 + SukiUI (Fluent), LiveCharts, Material.Icons, Svg.Controls.
- MVVM: CommunityToolkit.Mvvm (source-gen `[ObservableProperty]`/`[RelayCommand]`). `INotifyPropertyChanging` support is **disabled** (`MvvmToolkitEnableINotifyPropertyChangingSupport=false`) — do not rely on it.
- DI: `Microsoft.Extensions.DependencyInjection`.
- Logging: ZLogger.
- Compiled bindings are on by default (`AvaloniaUseCompiledBindingsByDefault=true`); XAML must declare `x:DataType`.

## Build / Run

The repo uses Nuke. Top-level scripts bootstrap a local dotnet if needed and forward args to `build/build.csproj`.

- `./build.cmd <target>` (Windows) / `./build.sh <target>` (Unix) / `./build.ps1 <target>`
- Targets (defined in `build/Build.cs`): `Print`, `Clean`, `Restore`, `Compile` (default), `Publish`.
- For day-to-day dev just use `dotnet build` / `dotnet run --project src/NetSonar.Desktop` against the solution.
- `Publish` produces zips/MSI/.app/AppImage per RID into `artifacts/publish/`. Defaults to all six RIDs (`win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64`); override with `--rids "win-x64"`. MSI requires Windows host; AppImage requires Linux host; macOS `.app` codesigns only on macOS. Skipped silently otherwise.
- There is **no test project** in the solution.

## Solution layout

- `src/NetSonar/` — `NetSonar.Avalonia.csproj`. Library project containing all app code, views, view-models, settings, network logic. This is where edits almost always go.
- `src/NetSonar.Desktop/` — `WinExe` entry point. Mostly empty; references `NetSonar.Avalonia` and copies platform-native binaries from `dependencies/<rid>/` into `binaries/` at build time (e.g. `speedtest`).
- `src/NetSonar.MsiInstaller/` — WiX installer (Windows only).
- `build/` — Nuke build project.
- `Directory.Build.props` — central `<Version>`, package metadata, Avalonia version, OS-detection MSBuild props (`IsWindows`/`IsOSX`/`IsLinux`) and `DefineConstants` (`_WINDOWS_`/`_OSX_`/`_LINUX_`).

## App architecture

`App` is a single class split across many `App.*.cs` partials in `src/NetSonar/`:

- `App.axaml.cs` — lifecycle: `Initialize` → `OnFrameworkInitializationCompleted`. Branches on `--crash-report` arg to show `CrashReportDialogView`, otherwise enforces single-instance (named `Mutex`, bypassed under `#if DEBUG`) and falls back to `InstanceAlreadyRunningDialogView`. Wires up unhandled-exception handlers, builds `MainWindow` from `MainViewModel` via the view registry, hooks `desktop.Exit += DesktopOnExit` (which calls `PanicSaveSettings`).
- `App.Views.cs` — DI/view-registry bootstrap (`SetupViews`, `ConfigureViews`, `ConfigureServices`).
- `App.Globals.cs` — shared static services: `RuntimeGlobals`, `AppSettings`, `HttpClient` (with `SocketsHttpHandler`), `JsonSerializerOptions` (with custom IP converters).
- `App.Updater.cs`, `App.Logger.cs`, `App.Theme.cs`, `App.Information.cs`, `App.Messages.cs`, `App.Resources.cs`, `App.Utilities.cs` — focused responsibilities; check the matching partial when touching that area instead of grepping the whole class.

### View ↔ ViewModel resolution

Pattern lives in `src/NetSonar/Common/AppViews.cs` + `Common/ViewLocator.cs`:

- Register pairs in `App.Views.cs` via `.AddView<TView, TViewModel>(services)`. This both maps VM-type → View-type and registers the VM in DI.
- VMs that derive from `PageViewModelBase` are registered as `AddSingleton(typeof(PageViewModelBase), viewModelType)` so the navigation host can `GetServices<PageViewModelBase>()` to enumerate pages. Other VMs are registered as singletons of their own type.
- `ViewLocator` is added to `Application.DataTemplates` and matches any `ObservableObject`. It caches built controls per-VM-instance, so VMs are effectively bound 1:1 to a single Control instance.
- To add a new page/dialog: create `FooPage.axaml(.cs)` + `FooPageModel.cs` (inheriting `PageViewModelBase` for pages, `ViewModelBase`/`DialogViewModelBase` otherwise) and add a single `.AddView<FooPage, FooPageModel>(services)` line in `App.Views.cs`.

### Settings persistence

`AppSettings` is a singleton (`AppSettings.Instance`) composed of `SubSettings` partials. `App.PanicSaveSettings()` is invoked from `DesktopOnExit` — UI code that already triggers a normal shutdown should **not** also call `PanicSaveSettings` itself (it would double-save). Exit the app via `IClassicDesktopStyleApplicationLifetime.Shutdown()` rather than `Environment.Exit` so this path runs.

### Crash reports

Unhandled exceptions are routed to `HandleUnhandledException`, which spawns a new instance of the app with `--crash-report <hash>` so the dialog runs in a clean process. `CrashReports.Instance` keeps the in-memory store keyed by hash.

## Conventions

- Any new XAML control: include `x:DataType` (compiled bindings are on by default).
- Native external binaries (e.g. speedtest CLI) live under `dependencies/<rid>/` and are copied to `binaries/` by `NetSonar.Desktop.csproj` — add new ones there, not via code.
- Use the OS define constants (`_WINDOWS_`/`_OSX_`/`_LINUX_`) for platform-specific code paths; runtime checks live in `SystemOS/`.
- XAML formatting is governed by `Settings.XamlStyler` at the repo root.
