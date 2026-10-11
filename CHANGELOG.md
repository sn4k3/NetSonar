# v0.6.0 (11/10/2026)

- Add the Network Scanner module with local-network target discovery, host and port scanning, monitoring import,
  cross-platform dependency installation, and a built-in ICMP sweep, neighbour-cache, and TCP-connect engine for systems
  without nmap; configure engine selection, DNS resolution, service/version, UDP and OS detection, port presets and
  custom ranges, timing, host/probe timeouts, host limits, and automatic rescans; show live progress, percentage and
  ETA, searchable host/port/service results, per-host actions, scan-to-scan comparisons, persisted scan history,
  JSON/CSV exports, and raw nmap reports
- Fix network-interface refresh reapplying unchanged filters and resetting cards, which caused scroll jumps, lost hover
  state, and settings-button flicker
- Ask for acceptance of Ookla's EULA, terms of use, and privacy policy before the first speed test or server lookup,
  skip automatic speed tests until accepted, and allow revoking the acceptance from the speed test settings
- Remember the selected speed test server between sessions
- Use an Ookla speedtest CLI installed on the system when no bundled copy is available, such as on Windows ARM64, and
  show the missing-dependency panel when none is found instead of failing on start
- Translate the speed test results grid and refresh it when the language changes
- Fix speed tests failing on multi-gigabit connections that transfer more than 2 GB during a test
- Fix the speed test page freezing the UI while reading the speedtest version on first open
- Fix the Linux speed test auto-install installing the unrelated Python `speedtest-cli`; the Ookla CLI is now installed
  manually on Linux
- Fix download and upload columns of the speed test results sorting as text instead of by bandwidth
- Fix the speed test gauge keeping the range of a faster result when selecting a slower one
- Fix the speed test results menu commands not reflecting the current selection, and disable them while a test runs
- Fix the update-check cross-thread exception
- Upgrade AvaloniaUI from 12.1.2 to 12.1.4

# v0.5.1 (11/09/2026)

- Fix speedtest not working on linux and macOS systems
- Let users choose the WiX installation directory and restore the last directory on later installs, upgrades, and
  reinstalls
- Record the installed MSI product code, software version, architecture, and executable path alongside the remembered
  installation directory, while removing installed-product metadata during uninstall
- Move the optional "Start program after install" checkbox to the completion page
- Fix `install-netsonar` script to have minimum version requirement for macOS

# v0.5.0 (10/09/2026)

- Add batch import of services for all supported protocols from tab- or pipe-separated clipboard data, including
  explicit protocol schemes, default-port protocol inference, optional intervals, descriptions, groups, duplicate
  reporting, and IPv6 support by @dearmb (#24)
- Add editing of existing ping services from the service’s page, reusing the Add Ping Services dialog in edit mode for
  one or several selected services, applying the changes in place, and rebuilding only the services whose protocol or
  address changed while keeping their list position by @dearmb (#25)
- Reject edits that would give a service the same protocol and address as another service, leaving the whole edit
  unapplied instead of creating a duplicate
- Add commands to toggle the enabled state of the selected service and of all services
- Add export of the replies of the selected service to tab-separated values, next to the existing CSV and JSON exports
- Add opening the response-time graph of every selected service in its own window
- Add keyboard shortcuts to the service’s page for adding, editing, pausing, resuming, toggling, resetting statistics,
  and opening graphs, and show the matching gesture on the context-menu entries
- Add a report tab on the dialog with app diagnostics
- Add application RAM usage to title bar
- Show the assigned hotkey in the tooltips of the added, resume all, pause all, and open-graph buttons
- Add data grid shortcuts to delete the selected rows and all rows, and to invert the selection, while leaving keys
  alone during cell editing
- Add debian, rhel, and arch linux packages, it will now prioritize the system-wide package manager and fallback to
  AppImage
- Add macOS pkg installer for a more user-friendly installation experience
- Document installation through the remote Windows PowerShell script
- Fix the macOS gestures of pause all and resume all colliding with pause selected and resume selected, and the resume
  all context-menu entry advertising the resume selected gesture
- Refine Simplified Chinese translations by @dearmb (#26)
- Upgrade .NET from 10.0.10 to 10.0.12
- Upgrade AvaloniaUI from 12.1.1 to 12.1.2

# v0.4.1 (05/08/2026)

- Translate the application UI, dialogs, notifications, networking actions, and enum descriptions into German, Spanish,
  French, Italian, Japanese, Korean, Dutch, Polish, Brazilian and European Portuguese, Russian, Turkish, and Simplified
  Chinese (AI Translation) (closes #14)

# v0.4.0 (01/08/2026)

- Add NTP as a distinct service protocol with standard request packet creation, default port 123, request correlation,
  and response validation
- Add a catalogue of public NTP servers and an option to import them from the Add Ping Services dialog
- Add DNS as a distinct service protocol with default port 53, request correlation, response validation, text-list
  parsing, and public resolver imports
- Add SMTP probes with default port 25 and complete `220` greeting validation
- Add WebSocket probes for `ws://` and `wss://` endpoints, including custom ports and paths
- Add MQTT 3.1.1 probes with default port 1883, randomized client identifiers, and CONNACK validation
- Add TLS probes with default port 443, SNI, negotiated-version reporting, and system certificate validation
- Add SSH probes with default port 22 and SSH 2.0 identification validation
- Add STUN Binding probes with default port 3478, transaction correlation, and mapped-address validation
- Add SIP OPTIONS probes over UDP with default port 5060, transaction correlation, provisional-response handling, and
  final status validation
- Add IMAP probes with default port 143, greeting validation, and a tagged CAPABILITY exchange
- Add a shared public-host catalogue with multiple credential-free TLS, NTP, HTTP, WebSocket, SSH, SMTP, IMAP, MQTT,
  STUN, and SIP endpoints
- Add public HTTP targets for Google, Microsoft, Apple, Mozilla, Canonical, Cloudflare, and GitHub
- Expand the public-hosted catalogue with Let's Encrypt, Azure DevOps, Codeberg, Fastmail, Eclipse IoT, and Twilio
  endpoints
- Add individual public-host import actions for TLS, DNS, NTP, HTTP, WebSocket, SSH, SMTP, IMAP, MQTT, STUN, and SIP
- Add implicit TLS, certificate validation, and hostname validation to IMAP probes on port 993
- Fix public-service imports removing or retaining the wrong empty rows
- Fix Windows ICMP probes timing out before the configured deadline by accounting for the platform's 500 ms timeout
  resolution
- Fix TCP, UDP, and NTP hostname/port parsing, endpoint creation, address-family selection, and NTP default-port
  handling
- Fix UDP probes reporting success from a connectionless sending alone by waiting for a response from the remote service
- Fix HTTP replies using `0.0.0.0` after restoring reply history by resolving DNS independently of historical success
  counts and refreshing unresolved endpoints before HTTP requests
- Fix response-time charts handling timeouts and missing statistics, displaying reply history out of order, mixing
  average calculations, failing with an unlimited graph cache, and retaining stale data after statistics are reset
- Add configurable response-time graph sample windows, freeze/resume, automatic and full scaling, rolling averages,
  percentile, jitter, and displayed-window loss statistics
- Add multiservice response-time comparisons with minimum, average, current, maximum, failure status, host labels, and
  consolidated per-host tooltips
- Improve response-time chart timeout markers, multiservice current-value markers, outlined labels, and label spacing,
  value and axis formatting, tooltip deduplication, and transitions between single-service and multiservice views
- Add response-time graph controls to the main and pop-out views for sample window, freeze, scale, and synchronized
  service-enablement toggles, with collapsible pop-out controls for probe interval and timeout and multiservice batch
  editing
- Add live reply-status summaries and replace disabled summary buttons with reusable, theme-aware status badges across
  the ping and network-interface views
- Optimize HTTP probes to complete after response headers and use the declared content length without downloading or
  buffering the response body
- Prevent slow services from delaying scheduled probes for other services while still blocking overlapping probes for
  the same service
- Enforce the configured reply-cache limit both while adding replies and after restoring resilient reply history
- Fix service-list parsing so protocol prefixes, HTTP/HTTPS URLs, descriptions, groups, intervals, timeouts, buffer
  sizes, and NTP entries are handled independently
- Cap configurable probe timeouts at 65,535 seconds and reuse unchanged ICMP `PingOptions`
- Reduce probe allocations with pooled UDP/NTP buffers and allocation-free elapsed-time measurements
- Keep pooled NTP request storage alive until asynchronous sends and response validation are complete
- Replace the custom `FastObservableCollection` and MintPlayer dependencies with synchronized ObservableCollections
  views
- Refactor shared provider metadata into a common model and consolidate the public NTP catalogue into `BaseProvider`
- Add DotNext buffer packages for allocation-aware networking and data-processing improvements
- Fix the Network Interfaces page and adapter details not populating on the first refresh, including when automatic
  refresh is disabled
- Dispose removed network-interface bridges and avoid constructing duplicate interface card controls
- Run automatic speed tests on the UI dispatcher and prevent cancellation from exposing a premature ready state
- Handle disabled automatic-test intervals, correct Speedtest bandwidth conversions, and separate phase progress from
  latency details
- Upgrade AvaloniaUI from 12.1.0 to 12.1.1

# v0.3.1 (20/07/2026)

- Add `--portable` startup argument to run the app in portable mode. (#21)
- Add `--profile-path` startup argument to specify a custom profile path. (#21)
- Upgrade AvaloniaUI from 12.0.3 to 12.1.0
- Upgrade .NET from 10.0.5 to 10.0.10
- Upgrade other dependencies

# v0.3.0 (24/05/2026)

- Migrate core app infrastructure to StageKit/ApplicationKit: centralize birth/logs/config paths, application args, and
  unhandled-exception handling.
- Replace the per-user Mutex with ApplicationInstanceGuard and wire ApplicationKit.Logger.
- Remove legacy CrashReport and custom RootSettings/collection/subsettings implementations and adapt settings files
  (AppSettings, PingableServicesFile, SpeedTestsFile) to use ApplicationKit-based constructors, auto-save, and new JSON
  options.
- Update usages to UnhandledExceptions.HandleSafeException and adjust file I/O (timer, FileOptions).
- Fix Call from invalid thread for Toasts (fixes #15)
- Upgrade AvaloniaUI from 11.3.14 to 12.0.3
- Upgrade .NET from 10.0.2 to 10.0.5

# v0.2.6 (26/04/2026)

- Fix start with the system minimized was not able to show the application after clicking the tray icon
- Clicking the tray icon now toggles the visibility of the application instead of only showing it

# v0.2.5 (26/04/2026)

- Add the "Start with system" option to the settings [Default: false] (#16)
- Add the "UI Scale" option to the settings
- Add the "Close to tray" option to the settings [Default: true] (#16)
- Add Tray icon with options
- Fix unable to have uppercase queries (#18)
- Upgrade AvaloniaUI from 11.3.11 to 11.3.14
- Upgrade .NET from 10.0.2 to 10.0.6

# v0.2.4 (24/01/2026)

- Fix speedtest binary not found when the path contains spaces

# v0.2.3 (21/01/2026)

- Fix the update checker call from a different thread

# v0.2.2 (21/01/2026)

- Fix the permission issue on speedtest binary for linux and macOS systems
- Upgrade AvaloniaUI from 11.3.10 to 11.3.11
- Upgrade .NET from 10.0.1 to 10.0.2

# v0.2.1 (10/01/2026)

- Fix the speedtest path for linux and macOS systems

# v0.2.0 (10/01/2026)

- Add an option to save and restore ping replies after the program restarts (#6) (Default: Disable)
- Add a Speed Test module to measure internet speed
- Save and restore pingable services DataGrid column order
- Change the pingable services DataGrid column order
- Change the default ping cache from 10,000 to 1000
- Insert pings at the top of the DataGrid instead of the bottom
- Fix the issue where it is not possible to add multiple services at once in the dialog
- Fix the issue where pingable services hostnames were not loaded between sessions
- Fix changing the theme, the base color is reset
- Ignore the following task exceptions to prevent the app from crashing: (#2)
  - org.freedesktop.DBus.Error.ServiceUnknown
  - org.freedesktop.DBus.Error.UnknownMethod
- Upgrade AvaloniaUI from 11.3.5 to 11.3.10
- Upgrade .NET from 9.0.9 to 10.0.1

# v0.1.2 (12/09/2025)

- Add a GridSplitter to be able to resize the layout of services pages (#6)
- Upgrade AvaloniaUI from 11.3.3 to 11.3.5
- Upgrade .NET from 9.0.8 to 9.0.9

# v0.1.1 (08/08/2025)

- Fix settings not being saved if the app crashes
- macOS: Fix missing app icon
- Windows: Fix the community forum links in the support button
- Upgrade .NET from 9.0.6 to 9.0.8
- Upgrade AvaloniaUI from 11.3.2 to 11.3.3

# v0.1.0 (01/07/2025)

- First release
