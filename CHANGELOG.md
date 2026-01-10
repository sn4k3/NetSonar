# v0.2.0 (10/01/2026)
- Add a option to save and restore ping replies after program restart (#6) (Default: Disable)
- Add Speed Test module to measure internet speed
- Save and restore pingable services DataGrid column order
- Change the pingable services DataGrid column order
- Change the default ping cache from 10000 to 1000
- Insert pings at the top of the DataGrid instead of the bottom
- Fix the issue where is not possible to add multiple services at once in the dialog
- Fix the issue where pingable services hostname were not loaded between sessions
- Fix when chaining theme the base color is reset
- Ignore the following task exceptions to prevent app from crash: (#2)
  - org.freedesktop.DBus.Error.ServiceUnknown
  - org.freedesktop.DBus.Error.UnknownMethod
- Upgrade AvaloniaUI from 11.3.5 to 11.3.10
- Upgrade .NET from 9.0.9 to 10.0.1

# v0.1.2 (12/09/2025)
- Add a GridSplitter to be able to resize the layout of services page (#6)
- Upgrade AvaloniaUI from 11.3.3 to 11.3.5
- Upgrade .NET from 9.0.8 to 9.0.9

# v0.1.1 (08/08/2025)
- Fix settings not being saved if app crashes
- macOS: Fix missing app icon
- Windows: Fix the community forum links in support button
- Upgrade .NET from 9.0.6 to 9.0.8
- Upgrade AvaloniaUI from 11.3.2 to 11.3.3

# v0.1.0 (01/07/2025)
- First release