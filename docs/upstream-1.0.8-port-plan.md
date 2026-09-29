# Upstream WireView2 1.0.8 port plan (wireview-linux and wireview-hwmon)

Analysis date: 2026-09-29. Source: decompiled `~/WireView2-SW_1.0.8` vs `~/WireView2-SW_1.0.7`
(BundleExtractor + ilspycmd 11.1, both trees decompiled with the same tool version). Note that
`https://tgrizzly.com/WireView2-SW-latest` still redirected to the 1.0.7 zip on that date; the
1.0.8 zip is linked from the product page.

## Maintainer decisions (2026-09-29)

- Port everything except WireView II / Phanteks Edition (product ids 7 and 8): deferred, no hardware.
- Ship the Noctua logo and background images (downscaled, see below).
- Theme editing stays enabled on the Noctua Edition (product id 6).
- A daemon that reports no product is treated as a Pro II (EF05).
- Backgrounds are downscaled to at most 3840 px wide before shipping.

## Shared interface: how wireviewd reports the product

- `GET_DEVICE_INFO` (0x01) reply is extended compatibly: `fw_version u8, config_version u8,
  uid[12], build_string NUL-terminated, vendor_id u8, product_id u8`. Clients that stop at the NUL
  (GUI <= 1.2.5.0, wireviewctl <= 1.6.0) are unaffected. A reply without the two trailing bytes
  means an older daemon: assume vendor 0xEF, product 0x05.
- `GET /sensors`: `"hwRev"` carries `"EF05"` / `"EF06"` (upstream's `{Vendor:X2}{Product:X2}`), and
  `"name"` the edition name: "WireView Pro II" or "WireView Pro II Noctua Edition".
- Firmware: one image serves both editions. Device product 6 aliases to firmware product 5
  (upstream `FirmwareProductIdAliases`). Flashing is allowed only when the image vendor matches and
  the image product equals the aliased device product; an image older than the device's build
  (version first, then the `yyyyMMdd_HHmm` build timestamp) is refused unless forced.

## wireview-hwmon side

1. wireviewd accepts product 6 besides 5 (same welcome string, protocol, sensor frame and config).
2. wireviewd reports vendor/product as specified above; `/metrics` `wireview_firmware_info` gains a
   `product` label.
3. `wireviewctl info` prints product and edition; `top` and `sensors --json` show the edition name.
4. `wireviewctl flash`: product gate with the alias, downgrade gate by version then build date,
   `--force` to override; prints device build vs image build.
5. Bundled firmware updated to `TG-WV-PRO2-FW_20260902_0741` in both repos.
6. Not needed: averaging options (config is opaque to the daemon), sensor frame (unchanged), USB ids
   and DFU (unchanged), hidden sensors (hwmon already reports N/A), disconnect detection (daemon
   already reconnects), CPU usage (daemon never busy-polled).

---

The application-side analysis follows.

## 1. Device edition identification (product id aware device layer). Foundation for §2, §4, §10

**Upstream evidence**
- `WireViewDeviceLib/.../DeviceAutoConnector.cs`: probes each port with the new
  `WireViewBasicDevice` (welcome prefix `"Thermal Grizzly WireView"` + `CMD_READ_VENDOR_DATA`),
  disconnects it, then instantiates by product id:
  `case 5: new WireViewPro2Device(...)`, `case 6: new WireViewPro2NoctuaDevice(...)`,
  `case 7: new WireView2Device(...)`, `case 8: new WireView2PhanteksDevice(...)` (vendor 239 = 0xEF).
  `_device` is now `IWireViewDevice?`.
- `WireViewPro2Device` ctor gained `welcomeMessage, deviceName, vendorId, productId` parameters;
  `WelcomeMessage` and `DeviceName` became instance properties; `Connect()` compares
  `VendorId == VendorId && ProductId == ProductId` instead of hard-coded `239/5`.
- `WireViewPro2NoctuaDevice : WireViewPro2Device` is a 9-line subclass:
  `base(portName, baud, "Thermal Grizzly WireView Pro II", "WireView Pro II Noctua Edition", 239, 6)`.
  Same welcome string, same protocol; only the product id and the display name differ.
- App consumers: `ConnectionStatusViewModel.ApplyEditionFromCurrentDevice()` (type checks, Noctua first),
  `DeviceViewModel.TryParseHardwareRevision("EF06")` for the firmware match.

**Linux mapping and current state**
- `WireViewDeviceLib/.../WireViewPro2Device.cs:59` hard-codes `VendorId == 0xEF && ProductId == 0x05`;
  `:11` `const WelcomeMessage`, `:22` `DeviceName => "WireView Pro II"`. A Noctua device is rejected on
  direct serial today.
- `DeviceManager.DefaultProbe` (`DeviceManager.cs:69`) constructs `WireViewPro2Device(port)` per port;
  there is no probe step, and none is needed for the Noctua Edition because the welcome string is
  identical. Simplest port: let `WireViewPro2Device` accept product ids {5, 6}, set `DeviceName` from
  the product id, expose `ProductId` (or keep deriving it from `HardwareRevision`). Adopt the
  `WireViewBasicDevice` two-step probe only if §10 (WireView II) is ported.
- `HwmonDevice.cs:52-55`: `DeviceName` is hard-coded "WireView Pro II (hwmon + daemon)", and
  `HardwareRevision => string.Empty`. `TryConnectDaemon()` parses `WCMD_GET_DEVICE_INFO`
  (fw, cfgver, uid[12], build string) with no vendor/product id. wireviewd's `query_device_info`
  (`wireview-hwmon/wireviewd.c:583`) rejects anything but `0xEF 0x05`. **[lead]**: wireviewd must
  accept PID 6 and report vid/pid (e.g. append two bytes after the build string, or a new command).
  App-side fallback: an old daemon only ever attaches EF05, so "hwmon, no PID reported" can safely be
  treated as EF05.
- LAN: `WireViewSensorDto.HwRev` already exists and `NetworkDevice` copies it into
  `HardwareRevision`, so remote viewers can derive the edition from `HwRev == "EF06"` with no schema
  change. But hwmon devices currently publish `HwRev = "A0"` (the `DeviceData.HardwareRevision`
  default in `IWireViewDevice.cs:23`), because `HwmonDevice.ReadSensors()` never sets it. Fix that
  when the daemon reports vid/pid. The C daemon's own `/sensors` JSON should carry the same field
  **[lead]**.
- Product name strings that stay generic (tray tooltip "WireView Pro II", window title, desktop
  entries, metainfo) are fine; upstream does not rebrand them either. Optionally mention the Noctua
  Edition in the Flatpak metainfo description.

**Effort** M (lib S, hwmon path depends on the daemon change). **Risk** medium: identity is the input
to theming, the firmware gate and the flash alias. **Verification**: needs a Noctua Edition device for
the real thing; a fake device injected through `DeviceManager(probe)` / `RegisterProbe` can exercise
the app logic without hardware.

## 2. Edition theming: EditionThemeService, Noctua Light/Dark themes, themable chart colors

**Upstream evidence**
- `WireView2.Services/AppSettings.cs`: `enum ThemeMode { Auto, Light, Dark, NoctuaLight, NoctuaDark }`
  (appended, serialized as int, so old settings files stay valid).
- New `WireView2.Services/EditionThemeService.cs` (static):
  - `enum WireViewDeviceTheme { WireViewPro2, WireViewPro2Noctua }`; `SetEdition`/`ClearEdition`
    called from `ConnectionStatusViewModel` on connect/disconnect.
  - `GetThemeVariant(mode)`: NoctuaLight→Light, NoctuaDark→Dark. `GetPaletteSet(mode)`: `Auto` follows
    the connected device's edition, `NoctuaLight/Dark` force the Noctua palette on any device,
    Light/Dark force the default palette.
  - `ApplyCurrentThemePalette()` (marshals to UI thread, subscribes once to
    `Application.ActualThemeVariantChanged`) writes top-level `app.Resources` keys, which override the
    `ThemeDictionaries`: `SystemAccentColor*`, `NavPaneBackgroundBrush`, `NavPaneBorderBrush`,
    `SideNavHoverBrush`, `SideNavPressedBrush`, `OverlayPanelBrush`, `AppBackgroundBrush` (ImageBrush
    from a `BackgroundCache` of decoded bitmaps; removed when the palette has no image so the theme
    dictionary's CPU_WELLE brush shows through), and the chart keys below.
  - Palettes: `NoctuaLight` accent/nav `#FF653025` (Noctua brown), overlay `#F2E9E9EE`, bg opacity 0.75,
    image `thermal_grizzly_wireview_pro_II_noctua_edition_software_background_light_v2.jpg`;
    `NoctuaDark` accent `#FFE9E9EE`, nav `#FF1E0D09`, overlay `#22000000`, opacity 0.75, the `_dark_v2.jpg`.
    Chart palettes: light `BarLow #653025 / BarHigh #E9E9EE / GaugeTrack #E9E9EE / GaugeAccent #653025`,
    dark `BarLow #E9E9EE / BarHigh #1E0D09 / GaugeTrack #E9E9EE / GaugeAccent #1E0D09`.
    `DefaultLight`/`DefaultDark` duplicate upstream's App.axaml values (accent `#FFE54225`).
  - `ApplyBackgroundColor(mode)` moved here from SettingsViewModel; Auto now uses the palette's
    `BackgroundColor` (white/black, same as before for the default palettes).
- `App.cs` (compiled App.axaml): new merged dictionary with defaults `OverviewBarLowColor #00BFFF`,
  `OverviewBarHighColor #FF0000`, `OverviewGaugeTrackBrush #DCDCDC`, `OverviewGaugeTotalCurrentBrush
  #00BFFF`, `...TotalPowerBrush #673AB7`, `...AvgVoltageBrush #FFC107`, `...TempInBrush #2196F3`,
  `...TempOutBrush #4CAF50`, `...External1Brush #FF9800`, `...External2Brush #F44336`.
  `App.ApplyTheme` → `EditionThemeService.GetThemeVariant` + `ApplyCurrentThemePalette`.
- `SettingsViewModel.ApplyTheme` → same, then `BackgroundOpacity = EditionThemeService.ActiveBackgroundOpacity`.
  The theme ComboBox binds `Enum.GetValues(ThemeMode)`, so the new entries show as raw
  `NoctuaLight`/`NoctuaDark`.
- Controls: `SimpleBarChart` gained `LowColor`/`HighColor` styled properties (gradient was hard-coded
  `DeepSkyBlue→Red`); `SimpleGaugeChart` gained `TrackBrush` (was hard-coded `#DCDCDC`) and now draws
  the track only over the unfilled part of the arc (small gap), so a translucent or dark track no
  longer blends under the value arc. `OverviewView` binds all of these via `DynamicResource`.
- Upstream quirk: on an edition change with theme Auto, `ApplyBackground` uses the user's
  `AppSettings.BackgroundOpacity`, not the palette's 0.75; the palette opacity is only applied when the
  theme is changed in Settings.

**Assets** (from the `-AvaloniaResources` index; all other entries byte-identical to 1.0.7):

| Path | Size | Used by code |
|---|---|---|
| `/Assets/Backgrounds/thermal_grizzly_wireview_pro_II_noctua_edition_software_background_light_v2.jpg` | 4,381,228 B, 5980x3000 | yes (NoctuaLight) |
| `/Assets/Backgrounds/thermal_grizzly_wireview_pro_II_noctua_edition_software_background_dark_v2.jpg` | 4,060,216 B, 5980x3000 | yes (NoctuaDark) |
| `/Assets/Backgrounds/BACKGROUND_NOCTUA_LIGHT.png` | 10,321,551 B, 6000x3375 | no |
| `/Assets/Backgrounds/BACKGROUND_NOCTUA_DARK.png` | 10,869,067 B, 6000x3375 | no |
| `/Assets/Backgrounds/CPU_WELLE_BW.png` | 2,686,330 B | no |
| `/Assets/Backgrounds/CPU_WELLE_BW_inv.png` | 2,416,721 B | no |

No icon, font, or fan asset changed. Extracted copies are in `res108/Assets/Backgrounds/`.
The light JPG carries a visible "noctua" logo (Noctua trademark); the dark one is a close-up of a
Noctua fan.

**Linux mapping**
- `WireView2/Services/AppSettings.cs:17` `ThemeMode` (append two values).
- `WireView2/App.axaml`: theme dictionaries with **port-specific** values (light accent `#FFE02A25`,
  light nav `#FFF9E275`), so the port's `DefaultLight/DefaultDark` palettes must be built from our
  App.axaml, not copied from upstream.
- `WireView2/App.axaml.cs:601` `ApplyTheme` and `WireView2/ViewModels/SettingsViewModel.cs:393`
  `ApplyTheme` / `:418 ApplyBackgroundOpacity` / `:426 ApplyBackgroundColor` → route through a ported
  `Services/EditionThemeService.cs`. `ApplyBackgroundOpacity` mutates whatever `TryFindResource`
  returns, which will be the top-level override brush once the service sets one; apply opacity after
  the palette.
- Edition source: `ConnectionStatusViewModel.OnConnectionChanged` (`:166`) fires on selection changes
  too (DeviceAutoConnector forwards `DeviceManager.SelectedChanged`), so the edition follows the
  selected device in the multi-device picker. Resolve the edition from `HardwareRevision == "EF06"`
  (works for serial, hwmon once §1 lands, and LAN via `HwRev`) instead of upstream's type checks.
- Controls: our `Controls/SimpleBarChart.cs:375` lerps from each series' own `Fill`
  (`s.Fill ?? DeepSkyBlue`) to `Colors.Red`, because our Overview keeps separate V/A/W series. Port as
  a `HighColor` property plus an optional low-color override for the current series only (see open
  questions). `Controls/SimpleGaugeChart.cs:140` hard-codes the track pen: add `TrackBrush` and the
  non-overlapping arc. `Views/OverviewView.axaml:68-120` hard-codes `AccentBrush` per gauge
  (MediumPurple, DarkTurquoise, Gold, DodgerBlue, MediumSeaGreen, Orange, IndianRed): switch to
  `DynamicResource` keys whose defaults are our current colors.
- Settings UI: show "Noctua Light" / "Noctua Dark" instead of raw enum names (small converter or a
  display-name list); no em dashes.
- Packaging: assets are embedded by `<AvaloniaResource Include="Assets\**" />`; no deb/rpm/AUR/Flatpak
  file list changes. Ship only the two JPGs, not the unused 26 MB of PNGs. Binary grows by ~8.4 MB.
- Linux-specific performance: a 5980x3000 bitmap decodes to ~72 MB RGBA, and upstream's
  `BackgroundCache` keeps every decoded background alive (four with CPU_WELLE). Under the
  software/XWayland render fallback (see issue #3 memory), scaling it every frame costs CPU. Recommend
  decoding with `Bitmap.DecodeToWidth(stream, 2560 or 3840)` or pre-scaling the shipped files, for the
  existing CPU_WELLE images too.

**Effort** M. **Risk** low to medium (resource precedence between top-level keys and theme
dictionaries; our diverged light palette). **Verification**: NoctuaLight/NoctuaDark can be checked
without hardware (forced modes; headless render per the sandbox memory). Auto-follows-edition needs a
Noctua device or a fake-PID probe.

## 3. Firmware v05 averaging options (up to 5.7 s)

**Upstream evidence**
- `WireViewPro2Device.AVG` gained `AVG_2834MS` (7) and `AVG_5668MS` (8).
- `DeviceViewModel`: `AllAveragingOptions` (all 9), `LegacyAveragingOptions` (`(int)a <= 6`),
  `ExtendedAveragingMinProFirmwareVersion = 5`. `AveragingOptions` is now an INPC property set in
  `TryReloadConfig`: `(_lastKnownDeviceFirmwareVersionNumber >= 5) ? AllAveragingOptions : LegacyAveragingOptions`.
  The config field is unchanged: `DeviceConfigStructV3.Average` (one byte).

**Linux mapping and a live bug**
- `WireViewDeviceLib/.../WireViewPro2Device.Protocol.cs:201` ends the enum with `AVG_NUM` (= 7), which
  upstream 1.0.7 did not have. `DeviceViewModel.cs:116`
  `AveragingOptions = Enum.GetValues<WireViewPro2Device.AVG>()` therefore shows "AVG_NUM" in the
  ComboBox today (`Views/DeviceView.axaml:188`). On a v05 device that value writes 2834 ms by accident;
  on v04 it is out of range. Replace `AVG_NUM` with `AVG_2834MS, AVG_5668MS` (no other code references
  `AVG_NUM`).
- Gate by firmware version exactly like upstream; `_lastKnownDeviceFirmwareVersionNumber` is already
  populated for serial, hwmon (daemon `GET_DEVICE_INFO`) and LAN (`FwVer`) in
  `DeviceViewModel.OnConnectionChanged` (`:768`). Make `AveragingOptions` notify.
- `Services/DeviceProfileService.cs:32` stores `Averaging` as int and `DeviceViewModel.cs:1329` casts it
  back: clamp to 6 when applying a profile to a device below v05.
- Writes go through `DeviceWriteConfigAsync` (serial / wireviewd `WriteConfig` / LAN relay). The daemon
  path needs the `wireview` group as today; no serial handover. Whether wireviewd validates the
  averaging byte range is **[lead]**.
- Optional: human labels ("22 ms" ... "5.7 s") via a converter.

**Effort** S. **Risk** low. **Verification**: a v05 device (the one on the bench) to confirm 7/8 are
accepted and shown on the device; v04 behaviour only needs the gating logic, testable without hardware.

## 4. Firmware date in the update check, product match, flash alias

**Upstream evidence** (`DeviceViewModel`)
- `BuildTimestampRegex = new Regex("_(\\d{8})_(\\d{4})(?!\\d)")`, `TryParseBuildTimestamp` parses
  `yyyyMMddHHmm` (InvariantCulture) from build strings like `TG-WV-PRO2-FW_20260902_0741`.
- `CompareBundledFirmwareToDevice()`: compare version numbers first; if equal, compare build
  timestamps; unknown on either side → `null` (no notice, no downgrade warning).
- `TryReadBundledFirmwareMetadataFromHex` now also returns vendor/product id from BuildStruct
  `+0/+1` (base+192/193).
- `FirmwareProductIdAliases = { [(239, 6)] = 5 }`: a Noctua device matches the Pro II image.
  `UpdateBundledFirmwareComparison()`: `flag = bundled vid/pid known && device vid/pid known && same
  vendor && alias(pid) == bundled pid`; `IsFirmwareUpdateSupported = !IsConnected | flag`;
  `IsBundledFirmwareNewerThanDevice = flag && Compare(...) > 0`.
- `DeviceView`: "Bundled FW" notice and the update/bootloader panels bound to `IsFirmwareUpdateSupported`;
  `UpdateFirmware()` refuses with "Bundled firmware does not match this device."
- Downgrade dialog uses `CompareBundledFirmwareToDevice() < 0` and now prints both build strings.
- Device build time comes from `ReadBuildStringAsync` (async, after connect).

**Concrete numbers** (parsed with `hexinfo.py` in this scratchpad):

| Image | vid | pid | fw | build |
|---|---|---|---|---|
| upstream 1.0.7 `TG-WV-PRO2-FW.hex` | 0xEF | 5 | 5 | `TG-WV-PRO2-FW_20260706_1047` |
| upstream 1.0.8 `TG-WV-PRO2-FW.hex` | 0xEF | 5 | 5 | `TG-WV-PRO2-FW_20260902_0741` |
| wireview-linux `WireView2/Firmware/TG-WV-PRO2-FW.hex` | 0xEF | 5 | 5 | `TG-WV-PRO2-FW_20260706_1047` |

Both are v05, so without the date check neither upstream nor our port would ever flag 0902 as newer.
The firmware string diff between the two images is only the build string plus a handful of code
bytes (+148 bytes); no product or theme strings were added, so Noctua support is a runtime switch in
the same image.

**Linux mapping**
- `WireView2/Services/FirmwareHexInfo.cs:TryRead` → add `out byte vendorId, out byte productId`
  (base+192/+193) and a shared `TryParseBuildTimestamp` helper.
- `WireView2/ViewModels/DeviceViewModel.cs`: `UpdateBundledFirmwareComparison` (`:459`),
  `LoadBundledFirmwareVersion` (`:467`), the downgrade gate in `UpdateFirmware` (`:525-538`),
  `CanStartFirmwareUpdate` (`:209`, add the product match), and `OnConnectionChanged` (`:736`, firmware number parsed at `:768`).
  Unlike upstream we do not need to wait for `ReadBuildStringAsync`: `IWireViewDevice.BuildString`
  is already set at connect for serial (cached in `Connect()`), hwmon (daemon `GET_DEVICE_INFO`) and
  LAN (DTO), so the comparison can run synchronously on connect.
- Device vid/pid: from `HardwareRevision` (serial, LAN) or the daemon (§1). With an old daemon, assume
  EF05 as argued in §1.
- The flash confirm dialog (`"Flash firmware {BundledFirmwareVersion} to {DeviceName}?"`) should show
  the build string or date too, since the version alone is identical.
- Flash alias safety (port-specific): our 1.2.5.0 image (0706) predates "Support Noctua Edition".
  Allow the Noctua alias only when the bundled build is >= 2026-09-02, so an older app build never
  offers a pre-Noctua image to a Noctua device.
- FIRMWARE SYNC (from the 1.0.7 plan): update the hex in both repos in the same cycle
  (`wireview-linux/WireView2/Firmware/TG-WV-PRO2-FW.hex`, `wireview-hwmon/firmware/TG-WV-PRO2-FW.hex`);
  `ext_flash.bin` is byte-identical and needs no update. wireviewctl's own version check should get
  the same date comparison **[lead]**.

**Effort** S. **Risk** low. **Verification**: parsing and comparison are unit-testable without hardware.
End-to-end: flash 0902 on the bench device (notice disappears), then offer the 0706 image to confirm
the downgrade dialog.

## 5. Disconnect detection ("disconnects wouldn't detect")

**Upstream evidence** (`WireViewPro2Device`)
- `PollLoop` is now `async Task`: `if (!sensorStruct.HasValue) { Disconnect(); break; }` (1.0.7 just
  slept and retried forever); `OperationCanceledException` is caught separately; the delay accounts
  for elapsed time: `Task.Delay(max(0, _pollIntervalMs - elapsed), ct)`.
- `ReadExact` and `SpiFlashReadResult` now use blocking `_port.Read` with `ReadTimeout` and treat
  `TimeoutException` / `0` bytes as failure (see §6).
- `DeviceAutoConnector` disposes and nulls `_device` on disconnect for every device type.

**Linux mapping and current state**
- `WireViewDeviceLib/.../WireViewPro2Device.cs:288` `PollLoop` has the 1.0.7 behaviour: a null read is
  ignored. When the USB device disappears, `SharedSerialPort.Open()` catches the `base.Open()`
  failure and returns false, `SendData` returns null, and the device stays `Connected = true`
  forever. `DeviceManager.Prune()` (`DeviceManager.cs:303`) only drops entries whose `Connected` is
  false, so in direct-serial mode (no wireviewd) an unplugged device leaves a zombie entry. The hwmon
  path is already fine: `HwmonDevice.PollLoop` checks `Directory.Exists(_hwmonPath)` and disconnects
  after more than 5 consecutive failures.
- Do not copy "disconnect on the first null" verbatim. In our port `ReadSensorValues()` also returns
  null for a corrupt frame (fan/pad sanity check, `WireViewPro2Device.cs:365-373`, commit 425c5ce) and
  when the global bus mutex is busy for 2 s (`SharedSerialPort.Open`). Use a consecutive-failure
  threshold like HwmonDevice (3 to 5), or disconnect immediately only when the port node is gone
  (`File.Exists(_portName)`) and count otherwise.
- `Disconnect()` called from inside `PollLoop` waits up to 1 s on its own worker (`_worker?.Wait(1000)`);
  HwmonDevice has the same pattern. It is harmless but adds a 1 s stall; guard it with a
  "called from worker" check if it matters.

**Effort** S. **Risk** medium: false disconnects mean reconnect churn and UI flicker; interaction with
`DeviceManager.Prune` retiring direct-serial entries when the daemon comes back. **Verification**:
hardware; stop wireviewd, run on direct serial, unplug and replug, and also run a long log read
(DirectSerialSession) to confirm no false disconnect. Coordinate with the lead.

## 6. CPU during device communication

**Upstream evidence**
- `ReadExact`: 1.0.7 spun on `if (_port.BytesToRead > 0)` for up to 1000 ms with no sleep; 1.0.8 calls
  `_port.Read(array, i, size - i)` and relies on `ReadTimeout`.
- `SpiFlashReadResult`: busy loop on `BytesToRead` replaced by blocking 1-byte reads with
  `ReadTimeout` set to the remaining time (restored in `finally`); a non-zero byte ends the wait.
- `SensorStructSize` cached (`Marshal.SizeOf` once); `PollLoop` period compensation (§5).

**Linux mapping**
- `WireViewPro2Device.cs:448` `ReadExact` has the busy spin; it serves every serial command including
  `WireViewPro2Device.Logging.cs` (log reads) and the theme SPI traffic. On Linux each `BytesToRead`
  is an `ioctl(FIONREAD)`, so every wait is a syscall storm on one core. `ReadTimeout = 1000` is
  already set in `Connect()` and survives our per-transaction Open/Close.
- `WireViewPro2Device.Theme.cs:50` `SpiFlashReadResult` already sleeps 1 ms per iteration; lower value.
- Impact on Linux is mostly direct-serial users and DirectSerialSession handovers; the default hwmon
  path reads sysfs and is unaffected.

**Effort** S. **Risk** medium: the log read and theme SPI writes are timing-sensitive (device-paced,
documented in DeviceViewModel.Theme.cs comments). **Verification**: hardware (log read, theme upload,
factory restore) plus `pidstat` before and after at a 50 ms poll. Overlaps with the lead's protocol
review.

## 7. Idle CPU/GPU overhead in charts and views

**Upstream evidence**
- `ViewModels/SimpleChartViewModel.cs`: `Series.Points` changed from `ObservableCollection<DataPoint>`
  to an immutable `IReadOnlyList<DataPoint>` snapshot with `SetPoints(list)` (one `PropertyChanged`);
  `AddPoint` and `RaiseChanged` removed; `EnsureSeries` returns the series; new `GetSeries(key)`.
- `Controls/SimpleLineChart.cs` (~430 changed lines):
  - Subscribes to `Series.PropertyChanged` instead of per-point `CollectionChanged`, so one
    invalidation per series update instead of one per added/removed point.
  - Static `AxisPen`, `TickPen`, `HoverPen`, `LegendBorderPen`, `SwatchBorderPen`, `LegendBackground`,
    `FallbackColors`; per-color pen/brush caches (`_seriesPens`, `_swatchBrushes`); `FormattedText`
    tick-label cache `_tickLabels` invalidated when `(XMin, XMax, YMin, YMax, Width, Height)` changes.
  - Visible range found by index scan from both ends instead of `Points.Where(...).ToList()`.
  - Min/max-per-pixel-column decimation (`DrawDecimated`, up to 4 points per column: first, min,
    max, last) when the visible point count exceeds `2 * plot.Width`.
  - Hover: invalidates only when entering/leaving the plot or when the snapped X changes
    (`_hasHoverSnap`, `_lastHoverSnapX`); 1.0.7 repainted on every mouse move inside the plot.
    `OnPointerExited` invalidates only if something was shown.
  - Nearest-point search is a loop instead of `Aggregate`; legend box size capped (see §8).
  - No timers or render loop in the control itself.
- `MonitoringViewModel`: "effective visibility" = attached to visual tree AND main window visible
  (`UpdateEffectiveVisibility`); window visibility no longer overwrites `IsViewVisible`, so restoring
  from the tray while on another page no longer restarts the graph loop. Buffer trim with one
  `RemoveRange` instead of repeated `RemoveAt(0)`; Y autoscale is a single min/max pass without LINQ
  lists; the graph tick pushes `SetPoints(buffer.ToArray())` per enabled series.
- `DeviceViewModel` + `DeviceView`: new `IsViewVisible` set from `AttachedToVisualTree` /
  `DetachedFromVisualTree`, combined with `App.MainWindowVisibilityChanged`; the 100 ms fan-preview
  `DispatcherTimer` stops when the Device page is not visible or the window is hidden, and resumes from
  the cached frames.
- `LoggingViewModel`: `Chart.EnsureSeries(key, label).SetPoints(list)` instead of Clear + Add loop.

**Linux mapping and current state**
- `WireView2/ViewModels/SimpleChartViewModel.cs` and `WireView2/Controls/SimpleLineChart.cs` are still
  the 1.0.7 versions (commit 6284312) with our additions: series toggles (the `SeriesColors` map
  doubles as the enabled filter), CSV live export. All the render-path costs above apply: new
  `Pen`/`SolidColorBrush`/`FormattedText` per frame (`SimpleLineChart.cs:264-320`), `Where().ToList()`
  per series (`:312`), `Aggregate` in the legend (`:356`), repaint on every mouse move (`:181`).
- `WireView2/ViewModels/MonitoringViewModel.cs:652` calls `Chart.AddPoint` for every enabled series on
  every sample (on the UI thread), which raises `CollectionChanged` for the add and each `RemoveAt(0)`,
  and trims its own buffer with a `RemoveAt(0)` loop (`:650`). Y autoscale uses
  `SelectMany(...).ToList()` + `Min()`/`Max()` (`:586-596`). `RebuildChartSeriesFromBuffer` (`:550`) and
  `LoggingViewModel.cs:400,498` do Clear + Add loops. Our Monitoring has no graph loop: points go
  straight to the chart per sample. Porting the snapshot model means either a throttled push (e.g.
  once per `MonitoringUpdateIntervalMs` or per frame) or `SetPoints` per sample; the former is the
  real win at 50 ms polling.
- `WireView2/ViewModels/DeviceViewModel.Theme.cs:170,314` fan-preview timer runs whenever cached frames
  exist, including while the window sits in the tray. Port the visibility gating (DeviceView.axaml.cs
  attach/detach + `App.MainWindowVisibilityChanged`, unsubscribe in `DeviceViewModel.Dispose` `:1346`).
- `OverviewViewModel` already gates on window visibility (`:277-305`, stashes the latest sample); it
  does not gate on page visibility. Low value; optional.
- The window background image (5980x3000) is part of every frame; see the downscale note in §2.

**Effort** M (control + VM model change, Monitoring push strategy). **Risk** medium: the controls
diverged (toggles, CSV export `writeOnlyNewPoints`, hover filter), and Monitoring's CSV export reads
the same buffers. **Verification**: headless render tests (sandbox memory) for the control;
`pidstat -u -p <pid> 1` at 50 ms and 1000 ms poll with all series enabled, before and after;
hover behaviour needs a real session.

## 8. Crash fixes worth taking

1. **Legend `Math.Clamp` throw (take first).** 1.0.7 computes
   `Math.Clamp(x + 10, plot.Left, plot.Right - boxWidth)` and the same for Y; when the hover legend is
   wider or taller than the plot (many enabled series, small window), `min > max` makes `Math.Clamp`
   throw `ArgumentException` inside `Render`. 1.0.8 caps `boxWidth = Math.Min(w + 12, plot.Width)` and
   `boxHeight = Math.Min(h + 12, plot.Height)`. Our code has the unfixed lines at
   `WireView2/Controls/SimpleLineChart.cs:371-372`, and our Monitoring page can enable ~20 series.
   Effort S, no hardware.
2. `PollLoop` catches `OperationCanceledException` separately (relevant only with the `Task.Delay` change).
3. `ReadExact` / `SpiFlashReadResult` catch `TimeoutException` (relevant only with §6).
4. `DeviceViewModel`: `_disposed` guard in `StartFanPreview`/`UpdateEffectiveVisibility`; `Dispose`
   unsubscribes `App.MainWindowVisibilityChanged` (port together with §7).
5. `OverviewViewModel.OnConnectionChanged` clears the stashed pending sample on connect/disconnect so a
   stale frame from the previous device is not applied. Our Overview stashes too (`_pendingDeviceData`);
   worth the same reset on `SelectedChanged`.
6. `DeviceAutoConnector` disposes and nulls `_device` on disconnect (our DeviceManager already does).

## 9. Hide not-connected temperature sensors

**Upstream evidence** (`OverviewViewModel`, `OverviewView`)
- `IsOnboardTempInUsed`, `IsOnboardTempOutUsed`, `IsExternalTemp1Used`, `IsExternalTemp2Used`;
  `IsTempSensorUsed(t) => t > -100.0`. Evaluated once per connection change and when the view becomes
  visible (`_tempSensorCheckPending`), not per sample, so layout does not jump on a glitch.
- Each temperature gauge `Border` has `IsVisible` bound; the gauges live in a `UniformGrid`, which
  reflows around hidden children.

**Linux mapping**: largely covered. `OverviewViewModel.cs:114`
`IsTempValid(t) => t > -100.0 && t < 200.0` drives `TempInAvailable` etc., which hide the gauge in
`Views/OverviewView.axaml:93-120`, but the column stays with its title and an "N/A" label, and
evaluation is per sample. hwmon returns ENODATA for absent sensors (`wireview_hwmon.c` `temp_mc ==
S32_MIN`), which `HwmonDevice.ReadTempFile` turns into NaN, also "invalid". Optional polish: hide the
whole column (switch `Grid ColumnDefinitions="*,*,*,*"` to a one-row `UniformGrid`), make it sticky per
connection like upstream, hide the Temperatures panel when all four are absent, and default absent
temperature series to off in Monitoring. **Effort** S. **Risk** low. **Verification**: any device
with an external probe unplugged.

## 10. Future WireView devices (WireView II pid 7, Phanteks Edition pid 8)

**Upstream evidence**
- New `WireView2Device` (647 lines, `IWireViewDevice`): welcome `"Thermal Grizzly WireView II"`, its
  own `DeviceConfigStructV0` (friendly name, fault masks, thresholds, `Average`; no fan, display, UI or
  theme fields), `ReadBuildString`, `EnterBootloader`, `NvmCmd`, `ClearFaults`; no SPI/logging.
  `WireView2PhanteksDevice` is a name/pid subclass.
- `DeviceViewModel`: `IsWireView2Device`; `IsFanSupported`, `IsDisplaySupported`,
  `IsDeviceLoggingSupported` = `!IsWireView2Device`; `ApplyToEditor(DeviceConfigStructV0)`,
  `BuildWireView2ConfigFromEditor()` (reads config, patches fields, writes, `NVM_CMD_STORE`),
  `MapWireView2AverageToPro`/`MapProAverageToWireView2` (clamp 0..8), WireView2 branches in
  `TryReloadConfig`, `ApplyConfig`, `StoreConfig`, `ResetConfig` (`NVM_CMD_RESET` + reload).
  `AveragingOptions = AllAveragingOptions` for WireView II.
- `DeviceView`: fan section, display settings, the fault-matrix "Display" column (9 bindings) and the
  "Device Logging (s)" slider hidden via those flags; firmware panels hidden via
  `IsFirmwareUpdateSupported` (the bundled pid-5 image does not match pid 7/8).

**Linux mapping**: nothing exists. It would need a device-lib class, the probe step, wireviewd and
hwmon driver support **[lead]**, DeviceView/LoggingView/theme-editor gating, and LAN DTO handling
(config relay uses the Pro II struct today). **Recommendation**: defer until the hardware exists; do
§1 and the product match in §4 so that an unknown pid is never flashed with the Pro II image and the
add-on is cheap later. **Effort** L. **Risk** high and unverifiable without hardware.

## 11. Minor and unannounced items

- Gauge track drawn only over the unfilled arc (§2). Cosmetic, S.
- Downgrade dialog prints build strings (§4).
- `ReadBuildStringAsync` updates `_lastKnownDeviceFirmwareBuildTime` and re-runs the comparison (§4).
- **Upstream regression, do not port**: 1.0.8 `LoggingViewModel` has `"�C"` (U+FFFD replacement
  character) where 1.0.7 had `"°C"`, in the `YC` axis unit, the four temperature series labels and the
  unit switch (`"�C" => 3`). A source-encoding accident; only LoggingViewModel is affected
  (MonitoringViewModel still has `°C`). Our `LoggingViewModel.cs:217-220` uses `°` escapes and
  is safe. Could be reported to TG.

## Removed upstream

| Removed | Where | Port impact |
|---|---|---|
| `SimpleChartViewModel.AddPoint`, `Series.RaiseChanged`, `ObservableCollection` points | ViewModels | Our Monitoring/Logging use them; replaced by `SetPoints`/`GetSeries` in §7 |
| Per-point `CollectionChanged` subscriptions in SimpleLineChart | Controls | §7 |
| `const WelcomeMessage`, fixed `DeviceName` in WireViewPro2Device | DeviceLib | §1 |
| Hard-coded vid 239 / pid 5 check in `Connect()` | DeviceLib | §1 |
| `Thread.Sleep` poll loop, `BytesToRead` spin loops | DeviceLib | §5, §6 |
| `ThemeVariant` switch and background-color logic in `SettingsViewModel`/`App` | Services | moved to EditionThemeService (§2) |
| Hard-coded gauge accents, gauge track color, bar gradient colors | OverviewView, controls | became resources (§2) |
| `AveragingOptions` as a get-only field | DeviceViewModel | §3 |
| `°C` in LoggingViewModel (replaced by U+FFFD) | LoggingViewModel | do not port (§11) |

No user-facing feature was removed.

## Already covered or not applicable on Linux

| Upstream 1.0.8 item | Status in wireview-linux |
|---|---|
| Hide absent temperature sensors | Mostly covered (`OverviewViewModel.IsTempValid`, gauges hidden); polish in §9 |
| Overview background-loop gating by window visibility | Covered (`OverviewViewModel.OnMainWindowVisibilityChanged` stashes the latest sample) |
| `App.MainWindowVisibilityChanged` event | Exists (`App.axaml.cs:55`) |
| Buffer trimming in `DeviceManager` / multi-device disposal on disconnect | Covered by `DeviceManager.Drop/Prune` |
| Firmware flashing path (DfuFirmwareUpdater, WinUSB driver prep, DFU_Driver/) | Not applicable; we use `DfuUtilFlasher` (dfu-util). Unchanged upstream |
| `ext_flash.bin` update | Not needed: byte-identical between 1.0.7 and 1.0.8 |
| `-AvaloniaResources` icons, fonts, fan templates | Unchanged |
| `BACKGROUND_NOCTUA_*.png`, `CPU_WELLE_BW*.png` | Not referenced by upstream code; do not ship |
| Tray icon/menu | Unchanged upstream (ours diverged anyway) |
| LoggingViewModel `�C` | Upstream bug, skip |
| Serial handover / `wireview` group | No new 1.0.8 app feature needs direct serial; averaging and config go through the daemon's config write (group as today) |

## Recommended order

1. **Quick fixes, no hardware**: §8.1 legend clamp crash; §3 replace `AVG_NUM` with the two v05 values
   and gate by firmware version (fixes a live bug).
2. **Firmware date check** (§4) together with updating the bundled hex to 20260902_0741 in both repos
   (FIRMWARE SYNC), otherwise the new logic has nothing to report.
3. **Serial robustness** (§5 with a failure threshold, §6 blocking reads), coordinated with the lead,
   verified on hardware incl. a log read and a theme upload.
4. **Chart and view overhead** (§7), plus §8.4/§8.5.
5. **Edition identification** (§1) once the lead settles the wireviewd vid/pid report.
6. **Edition theming and assets** (§2); forced Noctua modes can ship even before §1.
7. §9 polish.
8. §10 deferred.

## Open questions for the maintainer

1. Are we allowed to ship the Noctua backgrounds? The light image shows the Noctua logo; the firmware
   memory note covers TG's firmware image, not Noctua brand artwork. Alternative: ship the Noctua
   palette without the images.
2. Downscale the backgrounds (new JPGs and existing CPU_WELLE PNGs) to ~2560 or 3840 px wide to save
   ~8 MB per image in the binary and ~60 MB RAM each decoded?
3. Should theme Auto follow the connected edition like upstream (it also means OS light/dark), and in
   multi-device mode follow the selected device?
4. Overview bar colors: keep our per-series V/A/W base colors in the default palette and apply the
   edition palette only to the current series, or switch to upstream's single low/high gradient?
5. Disconnect threshold for direct serial: disconnect at once when the tty node is gone and after N
   consecutive null reads otherwise? What N (HwmonDevice uses more than 5)?
6. Minimum daemon: is "no vid/pid from wireviewd means EF05" acceptable, or should the app require the
   new daemon for Noctua devices?
7. Flash alias: restrict flashing a Noctua device to bundled builds >= 2026-09-02?
8. Noctua device-side theme: firmware says "Added Noctua theme", but no app enum (`Theme`,
   `THEME_BACKGROUND`, `UiThemePreset`) changed. On a Noctua device, does the config report a theme
   value our enums do not know (our config write could clobber it), and does "Restore defaults" from
   `ext_flash.bin` (TG artwork only, `DeviceViewModel.Theme.cs:1058`) overwrite Noctua artwork in SPI
   flash? Upstream 1.0.8 does not special-case either. Needs a config dump and an SPI read-back from a
   Noctua unit before enabling the theme editor on pid 6.
9. Port WireView II / Phanteks support now (dormant) or wait for hardware?
10. Monitoring: adopt a throttled chart push (upstream-style graph tick) instead of per-sample updates,
    given `MonitoringUpdateIntervalMs` can go down to 50 ms?
