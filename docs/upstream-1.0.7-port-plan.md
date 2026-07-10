# Upstream WireView2 1.0.7 → wireview-linux port plan

Analysis date: 2026-07-09. Source: decompiled `~/WireView2-SW_1.0.7` vs `~/WireView2-SW_1.0.6`
(BundleExtractor + ilspycmd, same layout as the 1.0.6 decompile). Linux port baseline: v1.1.0.1
(architecturally at upstream 1.0.6 + our own additions).

Status (updated 2026-07-09):
- DONE §1 nav-pane modes (GraphUpdateInterval skipped — our `MonitoringUpdateIntervalMs`,
  clamped 50–5000 and editable from the Monitoring page, already covers it).
- DONE §2 bundled firmware info (`Services/FirmwareHexInfo.cs`, verified against the real
  v04/v05 hex images) + downgrade gate.
- DONE §5-robustness SPI read retry (10×, 10 ms, flush-then-retry).
- DONE (beyond upstream parity): in-app firmware flashing on Linux via
  `Services/DfuUtilFlasher.cs` (EnterBootloader → poll `dfu-util -l` for 0483:df11 →
  `dfu-util -a 0 -s 0x08000000:leave`), v05 hex bundled via csproj (single-file
  self-extract), dfu-util declared in rpm/AUR, bundled as flatpak modules
  (libusb 1.0.27 + dfu-util 0.11). Untested against real hardware — needs a
  device-attached smoke test before release.
- DONE §3 custom charts (Simple{Line,Gauge,Bar}Chart + SimpleChartViewModel under
  WireView2/Controls; Monitoring/Logging/Overview migrated off LiveCharts rendering;
  Linux extras kept: series toggles, CSV live export, per-bar value labels, restored
  V/A/W bar toggles, fault table on Overview). Verified under real 293 W GPU load.
- DONE §4 power-cycle log separation (parser boundaries + voltage gate, cycle picker,
  Logging chart + progress + file pickers). Log read under the hwmon daemon works via
  a serial handover: wireviewd gained WCMD_SUSPEND/RESUME_SERIAL (wireview-hwmon repo)
  and the app a DirectSerialSession helper with heartbeat re-arm — the same mechanism
  the theme editor's SPI writes will use.
- DONE §5 theme editor suite (WireViewPro2Device.Theme.cs SPI write/erase + asset
  API; DeviceViewModel.Theme.cs + DeviceView UI: live preview w/ fan animation,
  V2 preset/color UI, background import with pure-Avalonia rasterizer replacing
  upstream's GDI+, .wv2t files, factory restore from bundled ext_flash.bin).
  Verified on hardware incl. byte-level flash read-back. Gotchas encoded in code
  comments: preview reads/uploads suspend the daemon (order device relay commands
  BEFORE property updates that trigger a preview refresh); the firmware re-blits
  backgrounds only on screen ENTRY with device-paced timing (~1 s settle, ~3 s on
  the detour screen); the protocol cannot query the current screen.
- DONE §6 dependency migration (2026-07-10): Avalonia 11.3.0 → 12.0.2 (matching
  upstream exactly; SkiaSharp 3.119.4-preview + HarfBuzz 8.3.1.3 come transitively,
  same stack as the Windows client). LiveCharts was REMOVED rather than upgraded
  (SimpleBarChart got its own SimpleBarSeries/SimpleAxis types). Breaks were small:
  NativeMenuItemToggleType → MenuItemToggleType, BindingPlugins workaround deleted,
  Watermark → PlaceholderText, two pragma'd obsolete Bitmap.Save calls (replacement
  API is later-12.x only). NOTE: Avalonia 12.1+ generators need a newer Roslyn than
  SDK 8.0.421 ships (CS9057 → InitializeComponent missing); stay on 12.0.x until the
  SDK is upgraded. Still X11/XWayland on Linux (no native Wayland).

THE 1.0.7 PORT IS COMPLETE — all sections done and hardware-verified.
- RELEASE CHECKLIST (v1.2.0.0): when assembling the PPA source package, extend
  debian/control to `Recommends: wireview-hwmon, wireview-hwmon-dkms, dfu-util`
  (rpm Recommends / AUR optdepends / flatpak modules already committed here).

Upstream 1.0.7 changelog items map to five feature areas plus dependency bumps. The serial wire
protocol is **unchanged** (UsbCmd/NVM_CMD/SCREEN_CMD byte-identical), so we are already
wire-compatible with firmware v05 for everything we currently do.

---

## 1. Navigation pane behavior (Auto / Minimal / Expanded) — small, self-contained

- `Services/AppSettings.cs`: add `public enum NavPaneMode { Auto, Minimal, Expanded }` +
  `public NavPaneMode NavPane { get; set; }` (default Auto, serializes as int, property name `NavPane`).
- `ViewModels/SettingsViewModel.cs`: `NavPaneModes` (Enum.GetValues), `NavPaneMode` property
  (persists + `SaveCurrent()`, raises `IsNavMinimal`/`IsNavExpanded`), ctor init, `OnSettingsSaved`
  re-sync (mirror the `ScreenAfterConnection` pattern).
- `Views/SettingsView.axaml`: "Navigation Pane" label + ComboBox (`NavPaneModes`/`NavPaneMode`, Width 150).
- `Views/MainWindow.axaml(.cs)`: replace hover-only `ExpandNav` with mode-aware `UpdateNavWidth`:
  Expanded→180, Minimal→48, Auto→hover ? 180 : 48. Call on ctor, PointerEntered/Exited, and
  subscribe `AppSettings.Saved`. Add `Classes.nav-minimal="{Binding Settings.IsNavMinimal}"` /
  `Classes.nav-expanded="{Binding Settings.IsNavExpanded}"` on the `NavPane` Border. Label
  visibility: Minimal never shows labels (even hovered), Expanded always shows. Our axaml gates
  labels per-button via `#NavPane.IsPointerOver`; extend those bindings or switch to the upstream
  descendant-selector styles.

Unannounced adjacent setting: `GraphUpdateInterval` (int ms, default 1000, clamped 100–2000 in the
VM) — new in AppSettings + SettingsViewModel. Port along with this slice if we want it.

## 2. Bundled firmware info + up/downgrade checks — medium, self-contained

All in `DeviceViewModel` (upstream); we have none of it. Portable logic:

- Intel-HEX parser `TryReadBundledFirmwareMetadataFromHex`: builds addr→byte map (record types
  0/1/2/4), then reads BuildStruct at `minAddr + 192`: version byte at `+2` (i.e. base+194),
  32-byte NUL-terminated ASCII build string at `+35` (base+227).
- `LoadBundledFirmwareVersion()` reads `TG-WV-PRO2-FW.hex` from `AppContext.BaseDirectory`
  (missing file → "-"); called from ctor.
- Bound props: `BundledFirmwareVersion` ("vNN", PadLeft(2,'0')), `BundledBuildString` ("(…)"),
  `IsBundledFirmwareNewerThanDevice`, `BundledFirmwareUpdateNotice` ("Newer firmware version available.").
- On connect: `int.TryParse(_device.FirmwareVersion)` → `_lastKnownDeviceFirmwareVersionNumber`,
  re-run comparison (our connect handler: DeviceViewModel.cs ~line 459).
- `DeviceView.axaml`: "Bundled FW:" row + orange (#FFA500) update notice, visible on
  `IsBundledFirmwareNewerThanDevice`.
- Downgrade gate (bundled < device → YesNo "Older firmware warning" dialog before flashing):
  upstream puts it in `UpdateFirmware()`; our flashing is external (dfu-util instructions), so
  attach wherever we trigger/document flashing. MessageBox YesNo already exists in our MsgBox.
- Packaging: ship firmware v05 `TG-WV-PRO2-FW.hex` (and see §5 for `ext_flash.bin`) with the app
  if we want the feature to light up.

## 3. Custom charts (replaces LiveCharts *rendering*, keeps its data types) — large

Upstream replaced `CartesianChart`/`PieChart` rendering with three hand-written Avalonia controls
drawing via `DrawingContext` (perf + the rc-quality LiveCharts issues). LiveCharts stays as a
dependency: `SimpleBarChart` still consumes `ISeries`/`ColumnSeries<double>`/`Axis`.

New files (no equivalents in port; create `WireView2/Controls/`):
- `Controls/SimpleLineChart.cs` — time-series w/ nice ticks, s/m/h X labels, hover crosshair +
  tooltip legend, 10-color fallback palette, `Chart` (SimpleChartViewModel) + `SeriesColors` props.
- `Controls/SimpleGaugeChart.cs` — 180° half-gauge (Value/Max/Label/Unit/AccentBrush), replaces
  Overview PieChart gauges. Repaint throttle: skip identical values except every 100th.
- `Controls/SimpleBarChart.cs` — grouped bars, DeepSkyBlue→Red gradient by value ratio, consumes
  LiveCharts `ColumnSeries<double>` + `Axis` labels.
- `ViewModels/SimpleChartViewModel.cs` — series dict (`EnsureSeries`/`AddPoint` w/ X-window
  auto-trim, `SetXWindow`/`SetYRange`/`AutoScaleY`), `DataPoint(double X, double Y)`.

ViewModel rewrites to match:
- `MonitoringViewModel`: drop `ISeries`/`ObservablePoint`; `TelemetryItem` loses its LineSeries,
  gains Color/EnabledChanged + snapshot provider; VM gains `Chart` + `SeriesColorMap` +
  per-key buffers. View: `<controls:SimpleLineChart Chart="{Binding Chart}" SeriesColors="{Binding SeriesColorMap}"/>`.
- `OverviewViewModel`: visibility-gated background render loop (`IsViewVisible` starts/stops a
  task; device data stashed under a gate; UI applies latest only when visible); bar chart collapsed
  to single current series with in-place `List<double>` value updates; X labels `"{v:0.0} A"`.
  View: SimpleBarChart + SimpleGaugeCharts.
- `LoggingViewModel`: chart migration same as Monitoring (see also §4).

## 4. Power-cycle separation of logged data — medium

- `DeviceLogParser` (device lib): `ENTRY_TYPE_POWER_ON` becomes a cycle *boundary* (not a data
  row); MCU tick wraparound (negative delta) also starts a new cycle; new voltage sanity gate on
  MCU_TICK rows: `60 < Σ(6 voltage channels) < 900`. Return type becomes list-of-cycles
  (`List<List<DeviceData>>`). **Reconcile with our diverged parser** (we add POWER_ON rows and use
  `HpwrSense > 3` discard + 0xFFFFFFFF empty-run detection) — adopt cycle boundaries + voltage
  gate on top of our checks.
- `LoggingViewModel`: `MeasurementCycleItem(Index, SampleCount, StartUtc, EndUtc)` records with
  `"Cycle #N (123 samples, 1:05)"` labels; `MeasurementCycles` collection; `SelectedMeasurementCycle`
  drives `ApplySelectedPowerCycle` (rebuild chart for that cycle; auto-select last); status
  `"Loaded {N} samples in {M} power cycle(s)."`
- `LoggingView.axaml`: cycle ComboBox (ItemsSource/SelectedItem/IsEnabled=HasMeasurementCycles).
  NOTE: our LoggingView has **no chart** (buttons + DataGrid only) — decide whether to add a
  SimpleLineChart while at it; the cycle picker can also just filter the DataGrid.

## 5. Theme editor suite (unannounced in changelog; biggest item) — large

We have the 1.0.6-level theme model (presets TG1–TG6, colors, THEME_BACKGROUND/THEME_FAN) in the
VM, but our DeviceView.axaml only ever surfaced the legacy theme combo. 1.0.7 adds a full editor.

Device lib prerequisites (absent in our port — we only have SPI *read* for the datalogger):
- SPI write/erase primitives: `CMD_SPI_FLASH_WRITE_PAGE=9` (header `[9, addr LE32, len LE32]` +
  data, 64-byte chunks, 1-byte status ==1, timeout len*100ms, max 256B/page) and
  `CMD_SPI_FLASH_ERASE_SECTOR=11` (`[11, addr LE32, len LE32]`, timeout (len/4096)*100ms).
- `WriteSpiFlashBytesPreserveSectorsAsync` — read-modify-erase-write over 4096B sectors / 256B
  pages, wrapped in screen pause/resume `{12,240}`…`{12,241}`, bound 16 MiB.
- `Read/WriteThemeBackgroundRgb565Async` (exactly 108800 B = 320×170 RGB565) and
  `Read/WriteThemeFanRgb565Async` (2 frames × 10658 B = 73×73 RGB565).
- On-device SPI offsets: bg Orange=12288, Dark=121088; fan (f1/f2) Orange=353140/374460,
  Dark=363800/385120, BlackWhite=395780/406440.
- Also: upstream `SpiFlashReadBytesAsync` now retries short reads 10× (10 ms sleep) instead of
  throwing — small robustness port for our `WireViewPro2Device.Logging.cs` (throws first failure).

App layer:
- Live preview + fan animation: read current assets, `Rgb565ToImage` (column-major storage,
  horizontal mirror on decode), compose 320×170 mock screen (fan at Rect(239,47,73,73)),
  100 ms DispatcherTimer frame flip, inverted variants for TG4–6. All Avalonia — ports as-is.
- Background import (Stretch/Uniform/UniformToFill + scale/offset sliders, debounced regen):
  **upstream uses System.Drawing/GDI+ — PlatformNotSupportedException on Linux; reimplement the
  rasterizer with SkiaSharp/Avalonia.** RGB565 packing / fit math / column-major+mirror layout
  reuse verbatim. Fan tint needs `Assets/DeviceAssets/fan1.png`/`fan2.png` templates.
- `Services/ThemeFile.cs` — `.wv2t` JSON theme files (Version=1, ARGB colors, inversion, bitmap
  ids, optional Base64 RGB565 background) + Save/Load commands + StorageProvider pickers. Portable.
- Factory restore from bundled `ext_flash.bin` (new 1.0.7 release artifact, 403 KB): slice offsets
  (differ from on-device!): bg Orange=0, Dark=108800 (len 108800); fan f1/f2 Orange=340852/362172,
  Dark=351512/372832, BlackWhite=383492/394152 (len 10658). Ship `ext_flash.bin` in our packages.
- DeviceView.axaml: preview image, import fit/scale/offset controls, select-background /
  load-theme / save-theme / restore-defaults buttons — plus finally surfacing the V2
  preset/color-picker/bitmap UI our VM already supports.

## 6. Dependencies (decide separately)

Upstream 1.0.7 (from deps.json): Avalonia 11.3.11 → **12.0.2**, LiveChartsCore 2.0.0-rc6.1 →
**2.0.2 stable**, SkiaSharp 2.88.9 → **3.119.4-preview**. We're on Avalonia 11.3.0 +
LiveCharts 2.0.0-rc3.3. None of the features above strictly require Avalonia 12; the custom chart
controls reduce LiveCharts exposure. Options: (a) stay on 11.3.x (bump to latest patch), port
features first; (b) full Avalonia 12 migration as its own change (watch our tray-icon SNI quirks,
font handling, and X11/render-mode workarounds). Recommend (a) then (b).

## Not applicable to Linux (verified, safe to skip)

- DFU WinUSB driver prep (`RemoveGuiStDfuDevDriverIfPresentAsync`, `WaitForWinUsbDeviceAsync`),
  DFU_Driver/, WindowsDriverHelper changes (localized sc.exe parsing, UAC catch, pnputil regex).
- Serial hardening (finally-close, TickCount64, ≤255 config loop) — we already have equivalents.
- SharedSerialPort mutex rework — ours is already equivalent-or-stronger.
- MessageBox: no API change; our MsgBox already has YesNo.
- DFU download protocol itself unchanged (VID 1155 / PID 57105, SetAddressPointer 0x08000000).

## Suggested order

1. Quick wins: nav-pane modes (+GraphUpdateInterval), bundled-FW info + downgrade notice,
   SPI read-retry robustness.
2. Log parser power-cycles + LoggingViewModel/View cycle picker.
3. Custom chart controls + Monitoring/Overview migration.
4. Device-lib SPI write/erase + theme APIs, then the theme editor UI (SkiaSharp rasterizer).
5. Avalonia 12 / LiveCharts 2.0.2 migration as a standalone change.

Firmware v05 itself (averaging range 5.7 s, in/out temp swap fix, new display driver IC, etc.) is
device-side; nothing to port, but update any README/docs that mention firmware version and ship the
new hex + ext_flash.bin.
