# WireView Mini

`wireview-mini` is a small program with no window that does the always-on jobs, so the app only needs to run when you want to look at it. Back to the [README](../README.md).

## What it does

- It owns the device (hwmon through wireviewd when that runs, otherwise the serial port, the same choice the app makes).
- It raises the fault notification (Windows toast, `notify-send` on Linux) for this PC's device and for every host in **Remote hosts**.
- It shuts the PC down on a fault of this PC's device when **Shutdown on fault** is on (a remote host's fault never powers this PC off).
- It publishes the device on port `9876` (the app's publish port): on the LAN when **Publish this host on the LAN** is on, on `127.0.0.1` only when it is off. See [LAN publishing and ports](#lan-publishing-and-ports).

WireView Mini plus its control panel are a small client of their own. With the tray menu and the control panel you can watch the device, chart its readings, read its on-device log, switch its screen, clear faults, edit its configuration and alarms, edit its display theme, update its firmware, and set up LAN monitoring and shutdown on fault without installing or opening the app.

On Linux it needs neither the wireview-hwmon kernel module nor wireviewd: with no hwmon device it opens the WireView's serial port (`/dev/ttyACM*`) itself, like the app's direct serial mode. Your user needs access to the port, which the udev rule in `udev/99-wireview.rules` gives (the packages install it; see the [Flatpak section](install.md#flatpak) for installing it by hand).

It reads the app's own `settings.json`, so the app and the control panel configure the same settings, and changes apply without restarting WireView Mini. It is meant for Windows and for Linux without wireviewd (Flatpak, immutable distros); with wireviewd running it still gives alerts and shutdown, and leaves publishing to the daemon.

## Running it

```bash
wireview-mini              # run in the foreground (Ctrl+C to stop), logs to stderr
wireview-mini --install    # start it at login
wireview-mini --uninstall  # stop starting it at login
wireview-mini --status     # connected device, fault state and version
wireview-mini --open       # print the control panel's address and open it in the browser
wireview-mini --no-tray    # run without the tray icon
wireview-mini --version
wireview-mini --help
```

`--install` writes `~/.config/autostart/wireview-mini.desktop` on Linux and the `WireView Mini` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` on Windows (no administrator rights needed). It logs to `wireview-mini-YYYY-MM-DD.log` in the `logs` folder next to `settings.json`. A second copy says it is already running and exits with 1.

### Exit codes of --status

| Code | Meaning |
|------|---------|
| 0 | A device is connected with no active fault |
| 3 | WireView Mini is not running |
| 4 | It runs without a connected device |
| 5 | An active fault |

### Windows notes

On Windows WireView Mini has no console window, so starting it at login shows nothing. Its options still print in a terminal; to wait for the output and get the exit code, use `start /wait wireview-mini --status` in cmd, or `wireview-mini --status | Out-Host` in PowerShell.

## Tray menu

WireView Mini puts the WireView icon in the notification area. Its menu, top to bottom:

- the live state (`WireView Pro II: 123.4 W`, the fault while one is active, *Serial port lent to the app* during a handover, or *No device*),
- **Launch WireView Desktop** (starts the app, or on Windows brings the running app's window forward; shown only where the app is installed),
- **Settings and controls...**, which opens the control panel in the browser,
- **Screen**, a submenu: Main, Simple, Current, Temperature, Status, then **Pause updates** and **Resume updates** (the device's display),
- **Clear faults**, enabled while a fault is active (clears the active faults and keeps the fault log, like the app's fault banner),
- **Remote hosts**, a submenu shown when the settings list remote hosts, with one line per host: `host: 123.4 W`, the host's fault, or `host: offline`,
- the **Publish on the LAN**, **Shutdown on fault** and **Start at login** checkboxes (with wireviewd, a disabled *LAN publishing: wireviewd, port 9876* line, or *LAN publishing: wireviewd, off*, takes the place of the first),
- **Quit**.

The tooltip carries the same live line. The two settings checkboxes write `settings.json` and apply at once, like the same switches in the app; *Start at login* is `--install`/`--uninstall`. The tray shows *Firmware update available*, which opens the panel, when the bundled firmware is newer than the device's.

On Linux the icon is a StatusNotifierItem: KDE, XFCE, Cinnamon and LXQt show it out of the box, GNOME needs the AppIndicator/KStatusNotifierItem extension. On a desktop without a tray (or with no session bus) WireView Mini logs one line, keeps running without the icon and adds it when a tray shows up later. `--no-tray` turns it off.

## Control panel

A single page served by WireView Mini on its own port, opened in the default browser by a click on the tray icon (left click on Windows, the tray's activate action on Linux), by the tray's **Settings and controls...** or by `wireview-mini --open` (which also prints the address, for desktops without a tray).

It works offline and answers this PC only: from any other address it returns 403, even while the port is open on the LAN. Each run of WireView Mini makes a new random access token; the tray and `--open` put it in the address (after `#t=`), and the page sends it with every request, so other web pages and other users cannot use it.

The page is refreshed every second. Charts, Device log, Device configuration, Theme, Firmware and wireviewd's settings are collapsed until you click their header, and are read only then; the page remembers which ones you left open.

Settings changed on the page are saved to `settings.json` and applied at once, the same path as the tray's switches. Device writes go to the device the same way as a command from the LAN, so they work over hwmon with wireviewd and over direct serial alike. `GET /sensors` on the same port is unchanged.

### Status

Device, firmware, source (`hwmon` or `serial:/dev/ttyACM0`, `COM3`), total power, current and voltage, each pin's voltage, current and power as a table with the temperatures under it, the app's per-wire bar chart beside them (voltage, current and power toggles; 12 V, 10 A and 120 W full scale, growing when a reading is more than 5 percent over; gridlines at every quarter of full scale labelled for each series shown; value labels on the bars with one series on), the fan duty, and the fault state with a **Clear faults** button.

### Remote hosts

The same readings for each host in the settings, read only.

### Screen

The five screens, pause and resume.

### Charts

A live line chart of the readings WireView Mini keeps, with the app's series and colors (total power and current, each pin's voltage and current, the four temperatures) plus each pin's power and the fan duty, the app's default selection (total power and current), a window of 1, 5 or 15 minutes, an hour or everything kept, an automatic or manual Y range per unit, a legend with the newest values, the values under the pointer, and **Export CSV**.

WireView Mini keeps the readings of this PC's device of the last `HistoryMinutes` minutes in memory, one sample a second (see [Settings](#settings)). The history is not saved to disk, so it starts empty with WireView Mini, and it pauses while the serial port is lent to the app or busy with a flash, a theme transfer or a log read. The Charts card reads it only while it is open and the page is visible, and the new samples then come with the status poll, so an open chart adds no requests.

**Export CSV** downloads every sample kept in the columns and formats of the app's CSV log (Timestamp, Connected, HW, FW, the totals, the four temperatures, then V1, I1 to V6, I6; times in UTC).

### Device log

**Read from device**, the power cycles found (labeled as in the app's Logging page), the same chart for the selected cycle (the Logging page's default series: the six currents and the onboard temperatures) and **Export CSV** for that cycle.

**Read from device** reads the log the device keeps in its flash, as the app's Logging page does: the whole 8 MB log area over the serial port (about a minute and a half; with wireviewd WireView Mini borrows the port, which needs the `wireview` group), split into power cycles with the app's code. The log has no clock, so, as in the app, each cycle's time counts from its power-on (from 2026-01-01 00:00 in the CSV). The result stays in WireView Mini's memory until the next read (a full log is at most 7.5 MB); a cycle's **Export CSV** has the columns of the app's Logging export. While the log is read, the other device controls, the theme editor and the firmware flash wait, and the app cannot borrow the port.

### Device configuration

Grouped like the app's Device page: General (friendly name, backlight), Fan (mode, temperature source, duty and temperature limits), Display (current and power scale, theme on older firmware, rotation, timeout mode, cycle screens, cycle time, timeout, averaging, default screen), the fault alarm table (display, buzzer, soft off, hard off per fault type) and the fault thresholds.

**Apply** writes only the fields you changed, the way the app does; **Store to device memory** keeps them after a power cycle; **Reload from device**, **Reload from device memory** and **Reset to defaults** (asks first) are there too. Out-of-range values are refused with a message.

**Profiles** at the top of the card are the app's device profiles, the same files (`~/.config/PowerMonitor/profiles/`, `%APPDATA%\PowerMonitor\profiles\` on Windows), so a profile saved in the app shows here and the other way round. **Save current as profile** saves the form's values, unsaved edits included, and like the app turns characters a file name cannot hold into `_`; **Load** fills the form and nothing reaches the device until you press **Apply**, as in the app. A profile field the device's configuration does not have (the legacy theme on firmware with the color theme, averaging on the oldest firmware) is left out, as in the app, and an averaging window older firmware does not have becomes its longest one.

### Theme

For firmware with the color theme (config V3), opened on demand: the app's presets, the four colors, display inversion, the background slot (the fan animation follows it, as in the app), a custom background image with the app's fit, scale and offsets, a live preview of the main screen, **Apply**, **Reload from device**, **Save theme file...** and **Load theme file...** (.wv2t) and **Restore factory artwork**.

The card does what the app's [theme editor](usage.md#theme-editor) does. Opening it, **Reload from device** and picking a slot not read yet read the device's colors from its configuration and the slot's artwork from its flash over the serial port (about a second; with wireviewd WireView Mini borrows the port from the daemon for it, which needs the `wireview` group; without it the preview shows no artwork). The status poll never reads the artwork. **Apply** writes the colors, inversion and slots the way the configuration's Apply does (Store to device memory keeps them).

A custom background image is scaled in the browser to 320x170 with the app's fit modes, converted to the device's RGB565 and written to the selected slot's flash with the highlight-tinted fan frames the app makes, then the screen is redrawn; the transfer takes a minute or two, with a progress bar. Theme files are written and read with the app's code, so they open in the app and in the official Windows app. **Restore factory artwork** writes the selected slot and its fan frames back from `ext_flash.bin` (shipped next to `wireview-mini`, the same file as the app's, and also the Noctua Edition's factory artwork). While a theme transfer runs, the other device controls and the firmware flash wait, and the app cannot borrow the serial port.

### Firmware

The device's and the bundled firmware (version and build), what a flash would do, and **Update firmware**. The flash, its checks and its requirements are described in [Firmware updates](firmware.md#in-wireview-mini).

### Alerts and shutdown

Shutdown on fault, Start at login (fault notifications are always on).

### LAN monitoring

Publish on the LAN, the port (the page follows WireView Mini to a new port), the remote hosts to watch, and the network secret; with wireviewd, the daemon's own settings instead of the first two (see [wireviewd settings](#wireviewd-settings)). The LAN features themselves are described in [LAN monitoring](lan-monitoring.md).

### wireviewd settings

When the device is reached through hwmon with wireviewd, the daemon does the LAN publishing, so the **LAN monitoring** card says so with what answers on the daemon's port (*answering on port 9876* or *off*) and edits the daemon's settings in `/etc/wireview/config` in place of WireView Mini's own switch and port:

- **Publish on the LAN** (`remote_enabled`),
- **Port**,
- **Bind address** (empty for all addresses),
- **Secret** (the passphrase other hosts sign their writes to this PC with),
- **Log retention** (days, 0 keeps every log).

**Save** writes only the changed keys through `wireviewctl config set` (the secret on its standard input, never on a command line) and wireviewd reloads them without a restart; the result line shows the daemon's answer, such as `config reloaded: listener on [::]:9876`, or wireviewctl's message when a value is refused or the file is not writable.

**Use the same secret for remote hosts**, under WireView Mini's network secret, copies the daemon's secret there on Save, so this PC signs its writes to the other hosts with the same one.

This needs wireview-hwmon 1.8 or newer (older versions have no `wireviewctl config`, and the card says so) and membership of the `wireview` group (`sudo usermod -aG wireview $USER`, then log in again). WireView Mini moves its own loopback listener off a port you give the daemon, and the page follows it. `WIREVIEW_CTL` overrides which `wireviewctl` is run.

### Tools

Launch WireView Desktop (where the app is installed) and where WireView Mini's log and settings are.

### About

The version, the author, the same disclaimer as the app's Settings page and the thanks to Thermal Grizzly for the development unit. `wireview-mini --help` ends with the same lines.

## How the app uses it

At startup the app asks `http://127.0.0.1:9876/sensors` before it opens the serial port (after hwmon on Linux). When WireView Mini answers, the app connects through it, shown as `mini` in the device picker and *Connected via WireView Mini* in the status bar, and does not open the port itself: two programs on one serial port corrupt each other's data. The app signs its commands to WireView Mini with a key WireView Mini keeps next to `settings.json` (`mini.key`), so writes work with no network secret set. The app does not show a second fault notification while WireView Mini runs.

## Serial handover

Firmware flashing, reading the device log and the theme editor need the serial port itself. For those the app asks WireView Mini to let go of the port, does the transfer and hands the port back, like the handover with wireviewd. Meanwhile WireView Mini keeps publishing the last reading, marked not connected. If the app never hands the port back (it crashed), WireView Mini takes it back on its own after at most 10 minutes. The handover is accepted only from this PC.

## LAN publishing and ports

WireView Mini publishes on port `9876`, the app's publish port: on the LAN when **Publish this host on the LAN** is on, on `127.0.0.1` only when it is off. While it runs, it publishes instead of the app (see [Publishing this host](lan-monitoring.md#publishing-this-host-windows)).

With wireviewd running it leaves publishing to the daemon: its own listener stays on `127.0.0.1` and off the daemon's port, whatever **Publish on the LAN** says (it logs this once).

When the publish port is taken (on Linux wireviewd holds `9876`), WireView Mini serves the panel, `/sensors` and the app's connection on a loopback-only fallback port instead (the next free one of the publish port +1 to +10, else any free port), logs `control panel on http://127.0.0.1:<port>`, and moves back to the publish port once it is free (it retries every 30 s); the tray, `--open` and `--status` use whichever port it is on. A control panel page left open follows it: while the page cannot reach WireView Mini, it asks the publish port and the ten after it (on this PC only, without the token) which one answers as the same run, and goes there. A restarted WireView Mini has a new token, so that page says to open the panel again from the tray.

## Building

Build it from source with:

```bash
dotnet publish WireViewMini -c Release -r linux-x64
dotnet publish WireViewMini -c Release -r win-x64
```

On Linux this is a NativeAOT build: one native `wireview-mini` executable of about 11 MB that needs no .NET runtime, plus `libSystem.IO.Ports.Native.so` next to it for the serial port. It idles at about 40 MB resident instead of about 90 MB for the JIT build. It needs a C toolchain and zlib (`gcc` or `clang` plus `zlib1g-dev` on Debian and Ubuntu, `zlib-devel` on Fedora). Without them, add `-p:PublishAot=false` to get the self-contained single-file JIT build, which behaves the same.

On Windows the default is the single-file JIT build, and it also builds from Linux. A NativeAOT build can only be made on Windows, with the Visual Studio Build Tools and their "Desktop development with C++" workload:

```powershell
dotnet publish WireViewMini -c Release -r win-x64 -p:PublishAot=true
```

Expect the Windows AOT build to lose its fault toasts: the toast library registers the app through COM and code generated at run time, and NativeAOT supports neither.

WireView Mini ships the firmware image `TG-WV-PRO2-FW.hex` and the factory artwork `ext_flash.bin` next to `wireview-mini`.

## Environment variables

| Variable | Effect |
|----------|--------|
| `WIREVIEW_CTL` | Overrides which `wireviewctl` is run (for [wireviewd settings](#wireviewd-settings)) |
| `WIREVIEW_DFU_UTIL` | Overrides which `dfu-util` is run (for [firmware updates](firmware.md)) |

## Settings

WireView Mini uses the app's `settings.json` (`~/.config/PowerMonitor/settings.json` on Linux, `%APPDATA%\PowerMonitor\settings.json` on Windows). Next to it are WireView Mini's `logs` folder and `mini.key`, the key the app signs its commands with.

`HistoryMinutes` sets how much chart history WireView Mini keeps in memory: 60 minutes by default (about 350 KB), at most 1440 (a day, about 8.5 MB), 0 turns it off. Set it in `settings.json` (`"HistoryMinutes": 120`); the key is written only when you set it, and a change applies at once.

## Limitations

- If the app is open at the same time, its next settings save writes back its own copy of the **Publish on the LAN** and **Shutdown on fault** switches (and of the settings changed on the control panel), so change them in one place while both run.
- On GNOME the tray icon needs the AppIndicator/KStatusNotifierItem extension.
- The chart history lives in memory only and starts empty with each run.
- A remote host's fault raises a notification but never powers this PC off.
- The control panel answers this PC only.
- The Windows NativeAOT build has no fault toasts (see [Building](#building)).
- While a flash, a theme transfer or a log read runs, the other device controls wait and the app cannot borrow the serial port.
