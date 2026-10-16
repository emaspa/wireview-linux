# Memory and CPU footprint plan (wireview-linux and WireView Plus)

Status: phase 1 (section 3) implemented 2026-10-13, measured below. Phases 2 and 3 are proposals.

## 1. Where the resources go today

Measured on the development machine (Intel Arrow Lake iGPU + RTX 5080, Mesa, XWayland), GUI 1.3.1.1
from the PPA, running 85 hours, wireviewd 1.7.x alongside:

| Process | RSS | PSS | Threads | Idle CPU |
|---|---|---|---|---|
| `WireView2` (Avalonia GUI) | 295 MB | 226 MB | 23 | 0.2 % of one core (1 tick / 5 s) |
| `wireviewd` (C daemon) | 2.3 MB | 2.3 MB | 1 | 0 |

GUI RSS by mapping (`/proc/<pid>/smaps`):

| Mapping | RSS | What it is |
|---|---|---|
| anonymous | 94 MB | .NET GC heap, Skia surfaces, Mesa buffers |
| `libLLVM.so.21` + `libgallium` | 53 MB | Mesa's GL driver (Intel iris here) and the LLVM it links; resident whether or not the driver uses it |
| `/memfd:doublemapper` | 40 MB | .NET W^X double mapping of JIT code (`DOTNET_EnableWriteXorExecute`) |
| `[heap]` | 23 MB | native malloc (Skia, HarfBuzz, X11) |
| `System.Private.CoreLib.dll` | 11 MB | runtime |
| `libicu*` | 8 MB | globalization data, unused by an English-only UI |
| other managed DLLs (`WireView2`, Avalonia, `System.Net.Http`, crypto) | ~15 MB | mapped from `~/.cache/dotnet_bundle_extract` because the deb runs the single-file launcher |
| window background JPG | up to 30 MB | 3840x1926 decoded at full size (`EditionThemeService.cs:258`, `App.axaml:42,61`), scaled every frame |

Conclusions:

- **CPU is not the problem.** After the 1.3.0.0 chart work (upstream 1.0.8 §6/§7: snapshot series,
  decimation, pen caches, blocking serial reads) the GUI idles at 0.2 % of a core. A Rust GUI idles
  at the same ~0. Only the Monitoring page at 50 ms polling still costs real CPU, and that is
  rendering work any toolkit pays.
- **Memory is the problem, and most of it is not "because C#".** Roughly 130 MB of the 295 MB is
  the rendering stack, the JIT double mapping, ICU and a full-size background bitmap. All four go
  away with configuration or small code changes inside the current codebase. The .NET GC heap for
  this app is a few tens of MB.
- **The headless layer already exists on Linux** and it is C: wireviewd + wireviewctl + the hwmon
  module. It does polling, hwmon export, LAN publishing (HTTP + HMAC, Prometheus, mDNS), remote
  control and serial handover, in 2.3 MB. It does not do fault alerts or shutdown-on-fault; those
  live only in the GUI (`ConnectionStatusViewModel.cs:195-230`).

## 2. The Rust port question

A full port means rewriting ~17.5k lines of C# (11.3k GUI, 5.1k device library, 1.0k network),
including the parts that took the longest to get right: serial handover with wireviewd, DFU flashing,
SPI theme flash timing, the Windows driver installer (SetupAPI), tray icons with their SNI quirks,
the theme editor with image codecs, and the custom charts. Realistic effort is several months for
feature parity, then every distribution channel (PPA, COPR, AUR, Flatpak, Nix, Windows zip) rebuilt
around a cargo toolchain with vendored crates (Launchpad builders have no network).

The payoff: a Rust GUI (egui, iced or Slint) lands around 40 to 80 MB RSS, most of it GPU driver.
A tuned Avalonia build (section 3) is expected around 120 to 160 MB. So the rewrite buys perhaps
60 to 100 MB on a desktop that has at least 16 GB, at the price of months of work and one more
thing the numbers above do not show: this project tracks the upstream Windows client release by
release (docs/upstream-1.0.7-port-plan.md, docs/upstream-1.0.8-port-plan.md). Those ports are
largely copy-and-adapt today because both sides are C#. With a Rust codebase every upstream change
becomes a translation.

**Recommendation: do not port the GUI.** Spend the effort on (a) shrinking the GUI where the memory
actually is and (b) WireView Mini, so the GUI no longer needs to run all the time. Rust has a
place in (b), as an optional later step with a small, well-defined scope.

## 3. Phase 1: shrink the existing GUI (no rewrite)

Target: 295 MB RSS to roughly 120 to 160 MB visible, less when hidden in the tray. Each item is
measured separately before it is kept; the ones that do not pay for themselves are dropped.

0. **Measurement script first.** `tools/footprint.sh <pid>` sampling RSS, PSS, thread count and CPU
   ticks over 60 s, run in three states: Monitoring page visible at 50 ms polling, Overview visible,
   window hidden in the tray. Windows equivalent with `Get-Process` and `Get-Counter`. Record the
   results in this file.
1. **Rendering mode.** Try `X11PlatformOptions.RenderingMode = [X11RenderingMode.Software]` in
   `Program.cs`. Skia's CPU rasterizer is more than enough for gauges and a few line charts, it
   removes Mesa and LLVM from the process (53 MB here), and it removes the GL-under-XWayland class
   of problems (issue #3 flashing, the "X connection broken" deaths on this rig). Keep the GL path
   available behind the existing `AVALONIA_X11_RENDER_MODE` environment variable. Decide the default
   from the Monitoring-at-50-ms CPU numbers. Windows keeps its default (ANGLE/D3D) unless the same
   test shows a win there.
2. **Runtime knobs** in `runtimeconfig.template.json`: `System.GC.ConserveMemory` (5 to 7),
   `System.GC.Concurrent=false` (drops the background GC thread, this app has no latency
   requirement), `System.Runtime.TieredPGO=false` (no instrumentation buffers). Plus
   `DOTNET_EnableWriteXorExecute=0`, which is environment-only: turn `/usr/bin/wireview-linux` from a
   symlink into a two-line wrapper that exports it and point the `.desktop` file at the wrapper.
   Expected: 30 to 50 MB.
3. **`InvariantGlobalization=true`.** Drops ICU. The code already formats CSV and chart labels with
   `InvariantCulture` (43 call sites); the 16 remaining culture-dependent `ToString("F")`/`"N"`
   calls become dot-decimal, which is correct for this UI. Expected: ~8 MB plus faster startup.
4. **Background bitmap.** Decode the window background with `Bitmap.DecodeToWidth` at the window's
   real width (and re-decode on resize past a threshold) instead of holding 3840x1926 and scaling
   it per frame. This is also upstream 1.0.8 §2's downscale note. Expected: 20 to 25 MB and less
   per-frame work on software rendering.
5. **Hidden-in-tray behaviour.** On window hide: drop the fan-preview frame cache and theme-editor
   bitmaps, then one compacting `GC.Collect`. Monitoring history stays (users expect it on reopen).
   Also fix the known second-launch-while-in-tray exit (wireview-hwmon README, follow-ups).
6. **Ship loose files only.** The deb and rpm install the single-file launcher plus the loose DLLs
   side by side (doubling the download, noted in the wireview-hwmon README). Shipping only the
   loose layout avoids the per-user extraction to `~/.cache/dotnet_bundle_extract` and lets the
   pages be shared between processes.
7. **Trimming / NativeAOT, last.** Avalonia 12 supports both. NativeAOT removes the JIT, the double
   mapping and tiered compilation memory, starts instantly and yields the single native binary that
   is half of the Rust pitch. Risks: `System.Text.Json` DTOs in WireViewNet need a source-generated
   `JsonSerializerContext`; the ColorPicker and any reflection binding must be checked (compiled
   bindings are already on). Do it after WireView Mini (section 4) has proven the toolchain on a
   smaller surface. COPR and Nix builds compile from source, so the ILCompiler package must be
   restorable there; the PPA repacks the release binary and is unaffected.

Effort: about a week including measurements. Ships as 1.3.2.

### Phase 1 results (2026-10-13)

`tools/footprint.sh <pid> 30 --maps`, 45 s after launch, Overview page, 1 s polling, hwmon
mode with a Noctua Edition, 1920x1080 screen, Intel iGPU + Mesa under XWayland. Self-contained
linux-x64 publish of the same commit before and after. "Minimized" is the user's
`StartMinimized` setting (window iconified, tray only); "visible" was run with a copy of the
settings under `XDG_CONFIG_HOME`.

| Build | State | RSS | PSS | Threads | Idle CPU |
|---|---|---|---|---|---|
| before | visible | 284 MB | 221 MB | 23 | 2.7 % |
| before | minimized | 227 MB | 168 MB | 23 | 0.9 % |
| after | visible | 149 MB | 138 MB | 17 | 2.3 % |
| after, default palette | visible | 155 MB | 144 MB | 17 | 2.6 % |
| after | minimized | 126 MB | 115 MB | 17 | 0.6 % |

Single knobs on the "before" build, minimized (RSS / PSS):

| Knob | Result | Kept |
|---|---|---|
| software rendering | 170 / 152 (from 227 / 168), 6 threads fewer, CPU unchanged | yes, Linux default; `WIREVIEW_RENDER_MODE` overrides |
| `DOTNET_EnableWriteXorExecute=0` | 227 / 168, no change: the double mapping only double-counts in RSS | no |
| ConserveMemory 7, non-concurrent GC, no tiered PGO | 165 / 148 (from 168 / 150) | yes, cheap |
| InvariantGlobalization | 155 / 144 (from 165 / 148) | yes |
| background decoded at screen width | 184 / 132 (from 227 / 168, with the GC knobs) | yes |

Items dropped from the list above: 6 (loose-only layout) gives no memory back, the extracted
bundle is memory-mapped like loose files would be; it stays a packaging decision. Item 7
(trimming / NativeAOT) is deferred to after WireView Mini, as planned. The remaining 37 MB of
anonymous memory in the visible state is the GC heap plus Skia surfaces; the 33 MB
`memfd:doublemapper` line is JIT code counted twice.

Verification: `Bitmap.DecodeToWidth` produced 1920x963 for both the edition background and the
default Dark dictionary brush, and the rendered frames were checked under Avalonia.Headless.

**Software versus GPU rendering by page**, same build (`--page` option, 1200x800 window, this
machine, CPU of the app process only; GL work done by the GPU and the compositor is not counted):

| Page, polling | Software | GL (egl) |
|---|---|---|
| Overview, 1 s | 2.3 % | 2.7 % |
| Overview, 50 ms | 3.4 % | 2.1 % |
| Monitoring, 1 s | 3.6 % | 1.1 % |
| Monitoring, 50 ms | 9.8 % | 3.8 % |

Memory is the same in every row: about 150 MB RSS / 138 MB PSS software, 275 / 195 GL. The
Monitoring page repaints the plot on every sample; with software rendering that repaint is CPU
work proportional to the window area, so a 4K maximized window costs roughly four times the
figures above. The background photo under the plot is about 1.1 of the 3.6 % (measured with
opacity 0); `RenderOptions.BitmapInterpolationMode=MediumQuality` on the background Border made
things far worse (17 % at 1 s, 40 % at 50 ms) and is not used.

Decision: software stays the Linux default (memory is paid all the time, the CPU only on the
Monitoring page) and **Settings → Startup → GPU rendering** flips it at the next start; Windows
defaults to GPU. `WIREVIEW_RENDER_MODE` overrides both. The `--page` command-line option was added
for these measurements and is documented for launchers.

Not pursued: decoding the background at the exact window size for a 1:1 blit (at most the
1.1 % above), and tray-hidden cache dropping beyond the compacting GC on hide.

## 4. Phase 2: WireView Mini, so the GUI runs only when wanted

The always-on jobs (fault alerts, shutdown-on-fault, LAN publishing, remote-host monitoring, the
tray power number) are what keep the GUI resident. Linux has wireviewd for publishing but not for
alerts or shutdown; Windows has nothing. Proposal:

### 4.1 Linux: extend wireviewd (C)

- `shutdown-on-fault` as a daemon option (config key + `--shutdown-on-fault`). The daemon runs as
  root under systemd, so `systemctl poweroff` is a natural fit and it works with nobody logged in,
  which the GUI version cannot do. This is the stronger implementation for the eGPU use case the
  README describes.
- A fault event on the daemon socket (new `WATCH` command or a one-line event stream), so a user
  session helper can show the desktop notification: `wireviewctl watch --notify` running as a user
  systemd service calling `notify-send`. No new process type, no .NET.
- The GUI keeps its own alert path for direct-serial mode and defers to the daemon when connected
  through hwmon (one notification, not two).

### 4.2 Windows: `WireViewMini`, a .NET console app with no Avalonia

New project `WireViewMini/` referencing only `WireViewDeviceLib` and `WireViewNet`. Both are already
free of Avalonia, and almost everything it needs exists:

| Mini job | Already in the code |
|---|---|
| find and poll the device | `DeviceManager`, `WireViewPro2Device`, `WindowsSetupApi`, `WindowsDriverHelper` |
| LAN publishing with HMAC | `SensorPublisher`, `HmacAuth`, `WireViewSensorDto` (carries `FaultStatus`/`FaultLog`) |
| watch remote hosts | `NetworkDevice`, `RemoteDeviceProbe` |
| fault edge detection, cooldown, text | `ConnectionStatusViewModel.cs:195-230`: move into a `FaultMonitor` class in WireViewNet shared by GUI and mini |
| shutdown | `shutdown /s /t 0` (Windows), `systemctl poweroff` (Linux) |
| settings | `AppSettings` moved to a shared library so the GUI configures and WireView Mini follows the same `settings.json` |

New code: a console host (~150 lines), Windows notifications (from the tray icon through
`Shell_NotifyIcon`, which Windows 10 and 11 show as toasts; `notify-send` on Linux), an `--install`/`--uninstall` pair that registers a logon task
(extend `AutoStartService`), and a serial handover command mirroring wireviewd's `SUSPEND_SERIAL`
so the GUI can still read logs, flash firmware and edit themes while WireView Mini owns the COM port.

**GUI integration, the key design point.** The GUI already speaks the LAN protocol to wireviewd and
to other copies of itself. Add a probe for `localhost:9876` ahead of the serial probe in
`DeviceManager`: when WireView Mini is running the GUI connects through it, exactly like hwmon+daemon
mode on Linux, with full remote control over HMAC (the local secret is read from the shared
settings file, so nothing to type). When WireView Mini is not running, direct serial as today. Both
platforms then have the same shape: a small always-on process owns the device, the GUI is a
viewer/editor you open when you want it.

What stays GUI-only: the live power number drawn as the tray icon itself. WireView Mini has a plain
tray icon (the app's icon) with a small menu, and shows the live power in its tooltip and in the
menu's first line; drawing a power glyph into the icon needs a rasteriser WireView Mini does not
carry. Users who want the glyph keep the GUI resident, now at Phase 1 size.

Expected footprint for WireView Mini in .NET, trimmed and invariant: 25 to 40 MB RSS on Windows,
idle CPU ~0. Measured on Linux (2026-10-14, hwmon + wireviewd, panel closed): the JIT single-file
build sits at 87 MB RSS / 75 MB PSS / 32 MB anonymous after a while; the NativeAOT build (10.7 MB
binary, zero trimming warnings, source-generated JSON) starts at 23 MB RSS and levels off at about
39 MB RSS / 33 MB PSS / 23 MB anonymous, 0.2 % of a core, so AOT is the default Linux publish.
Windows gets the AOT build too when published on a Windows machine, now that the notifications come
from the tray icon and no toast package is left (the SDK cannot compile a Windows build natively from
Linux, so the zip made here stays the JIT build). Measured on Windows 11 (2026-10-16, Task Manager,
five minutes, panel closed): the JIT single-file build at 39 to 43 MB and 0.1 %, the NativeAOT build
(9.4 MB exe, zero warnings) at 23.6 MB and 0 to 0.2 %. The complete Linux AOT build, tray icon and an hour
of chart history included, settles at 50 MB RSS / 43 MB PSS (the README's figure). Effort: about a week for WireView Mini, two to three days for the GUI probe, handover and
packaging (one zip with both executables). WireView Mini also runs on Linux as-is, which gives Flatpak
and immutable-distro users headless alerts and shutdown without the DKMS module.

### Phase 2 status (2026-10-13): 4.2 implemented

Done, in this repo:

- `AppSettings` moved to `WireViewNet/Services/` (namespace unchanged, same path and JSON),
  with `ReloadIfChanged()` (compares the file's write time) so WireView Mini follows GUI edits.
- `FaultMonitor` in `WireViewDeviceLib` (debounce, edge, 10 s cooldown, `FormatFaults`);
  `ConnectionStatusViewModel` uses it with the same behaviour. `SystemPower.Shutdown()`,
  `IToastNotifier`, `LinuxToastNotifier`, `NullToastNotifier`, `XdgAutostart` and the remote
  host filter (`RemoteHostList`) are shared too.
- `LocalPublishHost` in `WireViewNet`: the snapshot, `/config` and command relay that
  `WireViewPublishService` had, now used by both the GUI and WireView Mini. `SensorPublisher`
  takes a bind address, a local-only second secret, refuses local-only ops from other
  addresses, and asks for exclusive use of the port on Windows.
- `WireViewMini/` (`wireview-mini`): `net8.0` plus `net8.0-windows10.0.17763.0` (WinExe,
  notifications from the tray icon through `Shell_NotifyIcon`, `AttachConsole` for the CLI). Runs
  the local `DeviceManager` (hwmon first, then serial), a second one for the remote hosts,
  publishes always (all interfaces with LAN publishing on, loopback otherwise), alerts, shuts
  down on a local fault, `--install`/`--uninstall` (XDG autostart, HKCU Run key),
  `--status`, `--version`, `--help`, named-mutex single instance, clean exit on
  SIGINT/SIGTERM.
- Tray icon with no UI framework: Win32 `Shell_NotifyIcon` through P/Invoke on Windows, a
  StatusNotifierItem plus `com.canonical.dbusmenu` over the session bus on Linux
  (`Tmds.DBus.Protocol`, the low-level library, no code generation). Menu: live state line
  (device and power, fault, handover or no device), Launch WireView Desktop, the *Publish on the LAN* and
  *Shutdown on fault* switches (saved to `settings.json`, applied on the next tick like a GUI
  edit), *Start at login* (`--install`/`--uninstall`), Quit. The power is also in the tooltip.
  No tray on the desktop: one log line, WireView Mini runs on and registers when a watcher
  appears. `--no-tray` turns it off. Cost on Linux: about +5 MB RSS, +4.5 MB PSS, +1.5 MB
  anonymous; +0.4 MB on the Linux single file, +0.25 MB on the Windows one.
- WireView Mini as a mini client (everything but the graphs, themes and the device log;
  firmware flashing was added later, see the next point). Tray menu: *Settings and controls...*, a *Screen* submenu (the five screens,
  pause, resume), *Clear faults* (enabled during a fault; clears the active faults, keeps the
  log, like the app's banner), a *Remote hosts* submenu (one line per host: power, fault or
  offline). Submenus in the shared menu model and both backends (Win32 `MF_POPUP`, dbusmenu
  `children-display`).
- Firmware flashing in WireView Mini (2026-10-14): `DfuUtilFlasher`, `FirmwareHexInfo` and a
  shared `BundledFirmware` moved to `WireViewDeviceLib`; `WireViewMini/FirmwareService.cs` runs
  the app's gates and flash flow (serial: Mini closes its own port and sends the bootloader
  command directly; daemon: through wireviewd; dfu-util or WinUSB), `GET /api/firmware`,
  `POST /api/firmware/flash`, the flash state in `/api/status`, a Firmware card and a tray
  *Firmware update available* line. Idle until used: the bundled image is parsed once and the
  verdict cached per device and firmware. +0.3 MB on the Linux AOT binary.
- Theme editor in WireView Mini (2026-10-14): the .wv2t document, its reflection-free reader
  and writer (`ThemeFileFormat`, a source-generated context, byte-identical output) and the
  preset table (`DeviceThemePresets`) moved to `WireViewDeviceLib`, the app's `ThemeFile` and
  presets on top; `WireViewMini/ThemeService.cs` reads the theme (config, slot artwork over
  the serial port, borrowed from wireviewd in daemon mode) on request only, applies colors
  through the config write and custom artwork through the SPI writes, restores the factory
  artwork from a bundled `ext_flash.bin`; `GET`/`POST /api/theme`, restore, `.wv2t` download,
  upload and export; a Theme card that does the raster work in the browser (canvas). Large
  bodies only for the theme routes (loopback, with the token). The long panel cards (Device
  configuration, Theme, Firmware, wireviewd settings) are collapsed and read on opening.
  +0.42 MB on the Linux AOT binary (11,044,880 to 11,481,312 bytes).
- Charts and the device log in WireView Mini (2026-10-14): a history ring (`HistoryBuffer`, one
  sample a tick, preallocated float arrays, 99 bytes a sample: 356,400 bytes for the default
  `HistoryMinutes` 60, written to `settings.json` only when set) and an on-request device log read
  (`DeviceLogService`, the app's split into power cycles, now `DeviceLogCycles` in
  `WireViewDeviceLib`, kept as 20 bytes a sample: 7.5 MB at most for a full log, 128 KB for the
  test unit's 6,360 samples); canvas charts in the page, CSV in the app's columns (`DeviceDataCsv`,
  shared with the app's CsvLogger and Logging export), streamed. Panel closed, same age: 44.2 to
  45.0 MB RSS, 37.8 to 38.6 MB PSS; a log read leaves nothing behind (an aggressive collection after
  it). +0.39 MB on the Linux AOT binary (11,481,312 to 11,884,704 bytes).
- Control panel: one embedded page (`WireViewMini/ControlPanel.html`, inline CSS and JS)
  and a JSON API under `/api/` (status, config read and write, screen/NVM/clear-faults
  commands, mini settings, open the app), served on the publish port through a new
  extra-routes hook in `SensorPublisher` (same connection, size and rate limits). Loopback
  peers only (403 otherwise, also with LAN publishing on); `/api/` needs the per-run random
  token in `X-Mini-Token` (401 otherwise); no CORS. Opened by the tray or `--open`, which
  reads the token from the owner-only `mini-panel.token` WireView Mini writes at start.
  When the publish port is taken (wireviewd's 9876), a loopback-only fallback listener on
  PublishPort+1 to +10, else an ephemeral port, serves the same routes; its port is the
  token file's second line (read by `--open` and `--status`), and WireView Mini moves back to the
  publish port when the 30 s retry finds it free.
- `DeviceConfigModel` in `WireViewDeviceLib`: the config decoded in the Device page's units,
  layouts V1, V2 and V3, with an encode that writes only the edited fields onto the device's
  current bytes (`MergeConfigChanges`), so a round trip is byte-identical. The page's
  edit logic (`ConfigEditorValues`, `ApplyChanges`, the device-string helpers) moved from
  `DeviceViewModel` to `DeviceConfigEditor` in the same library; the view model calls it.
- Cost: page never opened, the same idle as before (RSS 64 to 68 MB, PSS 53 to 57 MB,
  anonymous 15 to 18 MB, 15 threads, about 1 % of one core); after the page has been used and
  closed, about +17 MB RSS (flat afterwards). +0.15 MB on each single file.
- wireviewd's settings in the control panel (Linux, hwmon + daemon): `DaemonConfigService` runs
  `wireviewctl config show --show-secret` / `config set` (changed keys only, the secret on stdin,
  wireview-hwmon 1.8 or newer), `GET`/`POST /api/daemon-config`, `daemon` in `/api/status` (what
  answers on the daemon's port, looked at every 3 s) and a disabled *LAN publishing: wireviewd* tray
  line; in that mode WireView Mini's listener stays on loopback and off the daemon's port. Tested with a
  fake wireviewctl and against 1.8's permission hint; the real write waits for the daemon upgrade.
- Serial handover: `suspendSerial`/`resumeSerial` commands (loopback only, signed with the
  mini's `mini.key` or the network secret). WireView Mini closes the port, keeps publishing the
  last readings with `connected: false` and `serialSuspended: true`, and takes the port back on
  resume or when the window (at most 600 s) runs out.
- GUI: the localhost probe (`LocalMiniProbe`, registered with
  `DeviceManager.SetLocalOwnerProbe`) runs after hwmon and before serial on every platform;
  while WireView Mini answers, serial is not opened and open serial entries are retired. Through
  WireView Mini: firmware flashing, device log read, theme transfers (via `DirectSerialSession`,
  now on `ISerialPortOwner`), fault clearing, config and screen commands. No GUI fault toast
  while WireView Mini runs; the GUI publisher steps aside when WireView Mini holds the port.

Measured on Linux (self-contained single file, not trimmed, hwmon device, 1 s poll, one
unreachable remote host): RSS 51 to 69 MB, PSS 45 to 58 MB, anonymous 10 to 17 MB, 15 threads,
0.8 % of one core. About 29 MB of that is the mapped single-file bundle and 9 MB the JIT's
double mapping, so trimming and NativeAOT (step 5) are where the rest of the gap to the 25 to
40 MB estimate is.

Left:

- 4.1 (wireviewd `shutdown-on-fault`, fault events, `wireviewctl watch --notify`) lives in the
  wireview-hwmon repo and is not part of this change.
- Packaging: WireView Mini is in no package or zip yet.
- Not verified on real Windows: toasts, the Run key, the windowless start, the WinUSB flash
  through the handover, the tray icon and its menu (including the submenus) and the control
  panel's browser launch. Built and published for win-x64 from Linux only.
- Direct serial on Linux verified on hardware (2026-10-14) with wireviewd stopped, both with the
  kernel module unloaded and loaded: WireView Mini opens `/dev/ttyACM0`, the panel reads and writes
  the config byte-exact, screen commands work. With the module loaded but the daemon stopped
  the manager can list a read-only hwmon entry next to the serial port; the panel and the
  command relay prefer the device that takes commands.
- The control panel's memory after use: source-generated JSON and a collection once the page
  closes could take back most of the +17 MB.
- After a flash through WireView Mini the Device page shows the old firmware version until the
  device is selected again (WireView Mini's device never disconnects from the GUI's side).
- Trimming or NativeAOT for WireView Mini.

## 5. Phase 3 (optional): WireView Mini in Rust

Only if the .NET mini's 25 to 40 MB is still judged too much. Scope is small and fully specified
by then: the serial protocol (`serialport` crate), the `/sensors` JSON API with HMAC (`axum` or
`tiny_http`, `hmac`, `sha2`), toasts (`tauri-winrt-notification`, `notify-rust`), shutdown, the
logon task. Two reference implementations (wireviewd.c and the .NET mini) and the existing
`tests/test_wireviewd.c` give a conformance target: same JSON, same HMAC, same handover. Result:
one 2 to 3 MB executable, 3 to 6 MB RSS, no runtime. Roughly 3 to 4k lines, two to three weeks.

Do not replace wireviewd with it on Linux. The C daemon is packaged, tested and 2.3 MB; a rewrite
there has no user-visible benefit.

## 6. Order of work

1. Measurement script and baseline table (section 3, item 0). Half a day.
2. Phase 1 items 1 to 6, each measured. One week. Release 1.3.2.
3. `FaultMonitor` and `AppSettings` extraction into shared code, then `WireViewMini` for Windows
   with the localhost probe in the GUI. One to two weeks. Release 1.4.0 with WireView Mini in the
   Windows zip.
4. wireviewd `shutdown-on-fault` and fault events, `wireviewctl watch --notify`. In the
   wireview-hwmon repo, a few days.
5. NativeAOT: mini first, GUI after. One week with the packaging checks.
6. Rust mini, only on evidence from step 3.

Steps 2, 3 and 4 are independent and can be interleaved; 5 and 6 depend on 3.
