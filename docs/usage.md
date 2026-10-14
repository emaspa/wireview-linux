# Using the app

How the desktop app connects to the device, what its pages do, and how it is built. Back to the [README](../README.md).

## Connection modes

The app supports these ways of communicating with the device:

| Mode | How it works | Features |
|------|-------------|----------|
| **Direct serial** | App talks to the device over `/dev/ttyACM*` | Full control (default) |
| **hwmon + daemon** | App reads sensors from `/sys/class/hwmon/`, sends commands via the [wireviewd](https://github.com/emaspa/wireview-hwmon) daemon socket | Full control, plus sensor data available to system monitoring tools |
| **Through WireView Mini** | App reads and controls the device through [`wireview-mini`](wireview-mini.md) on this PC, over `127.0.0.1` | Full control; alerts and shutdown on fault keep working with the app closed |

The app auto-detects the connection mode at startup. If the [wireview-hwmon](https://github.com/emaspa/wireview-hwmon) kernel module is loaded, the app uses hwmon for sensor data and connects to the daemon's Unix socket (`/run/wireviewd.sock`) for commands: configuration read/write, fault clearing, screen control, and device info all work through the daemon. If the daemon is not running, the app still displays sensor data in read-only mode.

Commands that change the device through the daemon (configuration writes, NVM store/reset, firmware flashing, and the serial handover used by log reads and the theme editor) need root or membership of the `wireview` group, which the wireview-hwmon packages create: `sudo usermod -aG wireview $USER`. Reading sensors needs no group. Without it the app reports the refused action with that hint, and the automatic theme preview is skipped. Use wireview-hwmon 1.7.0 or later: 1.6.0 refused `wireview` group members when wireviewd ran under systemd, and only 1.7.0 reports the product, so with an older daemon a Noctua Edition is shown as a WireView Pro II.

If the kernel module is not loaded, the app looks for the [WireView Mini](wireview-mini.md) on this PC, then falls back to direct serial communication. How the app talks to WireView Mini is described in [How the app uses it](wireview-mini.md#how-the-app-uses-it).

The app can also read and control devices on other machines: see [LAN monitoring](lan-monitoring.md).

## Pages

The app has five pages accessible from the left sidebar:

| Page | Description |
|------|-------------|
| **Overview** | Summary of total current, power, voltage, cable rating, temperatures (sensors that are not connected are hidden), and a fault status/log table with per-fault clear |
| **Monitoring** | Real-time charts for voltage, current, power, and temperature; custom series selection, colors, and Y scaling |
| **Logging** | Read device logs per power cycle and export to CSV |
| **Device** | Device info, full device configuration (fan, display, alarms, thresholds), display theme editor, and firmware updates |
| **Settings** | App theme (Auto, Light, Dark, Noctua Light, Noctua Dark; Auto follows the desktop's light/dark setting and switches to the Noctua colors while a WireView Pro II Noctua Edition is selected), startup behavior, background, and LAN settings (remote hosts, publish toggle/port, network secret, log retention) |

`wireview-linux --page monitoring` opens on a given page (overview, monitoring, logging, device, settings) instead of Overview.

The app keeps its settings in `settings.json` in the `PowerMonitor` folder: `~/.config/PowerMonitor/settings.json` on Linux, `%APPDATA%\PowerMonitor\settings.json` on Windows. [WireView Mini](wireview-mini.md) reads and writes the same file.

## Real-time monitoring

Voltage, current, and power readings across all 6 pins with live charts, plus custom charts: pick any telemetry series, per-series colors, manual or auto Y scaling.

## Device configuration and profiles

The **Device** page edits the device configuration: fan speed, display settings, fault alarms, thresholds.

On the **Device** page, you can save the current device configuration as a named profile, load it later, and manage the saved profiles. Profiles are stored as JSON files in `~/.config/PowerMonitor/profiles/` (`%APPDATA%\PowerMonitor\profiles\` on Windows).

## Theme editor

Customize the device display: background images, text and highlight colors, display inversion, with a live preview, in the app or in [WireView Mini](wireview-mini.md#theme)'s control panel. Theme files (.wv2t) are compatible with the official Windows app.

The Noctua preset is the WireView Pro II Noctua Edition's own look: Thermal Grizzly's Dark background with Noctua colors. It works on any WireView Pro II and is also available as [themes/Noctua-Edition.wv2t](../themes/Noctua-Edition.wv2t) for the Windows app.

## Data logging

On-device log readback and CSV export, browsable per power cycle, on the **Logging** page.

## Notifications and shutdown on fault

- **Desktop notifications** via `notify-send`.
- **Software shutdown on fault**: optional system shutdown when a fault alarm triggers, for eGPU or setups where the hardware shutdown header cannot be connected.

[WireView Mini](wireview-mini.md) does both with the app closed.

## Firmware updates

The **Device** page flashes the bundled firmware over USB DFU. See [Firmware updates](firmware.md) for the warning, the checks and the requirements.

## Rendering backend

On Linux the app draws with Avalonia's software renderer by default. It keeps Mesa out of the process (about 120 MB less resident memory, 55 MB less proportional), and it avoids the GL-under-XWayland glitches some Nvidia setups showed. It costs CPU only on the Monitoring page at fast polling (at 50 ms, about 10 % of one core instead of 4 % on a 1200x800 window; larger windows cost proportionally more). **Settings → Startup → GPU rendering** switches to the GPU backend at the next start. `WIREVIEW_RENDER_MODE` overrides the setting with a comma-separated preference list:

```bash
WIREVIEW_RENDER_MODE=egl,software wireview-linux   # Linux: software, egl, glx, vulkan
set WIREVIEW_RENDER_MODE=software                   # Windows: software, angle, wgl, vulkan
```

Windows defaults to GPU rendering (ANGLE on Direct3D); the same setting turns it off.

## USB device IDs

| Mode | VID | PID | Description |
|------|-----|-----|-------------|
| Normal | `0483` | `5740` | STM32 CDC/ACM virtual serial port |
| DFU bootloader | `0483` | `df11` | STM32 bootloader (during firmware updates) |

## Project structure

```text
wireview-linux/
├── WireView2/                  # Main Avalonia UI application
│   ├── Views/                  # AXAML views
│   ├── ViewModels/             # MVVM view models
│   ├── Services/               # App settings, profiles, notifications
│   └── Assets/                 # Icons, backgrounds
├── WireViewDeviceLib/          # Device communication library
│   └── Device/                 # Serial + hwmon + network devices, port finder
├── WireViewNet/                # LAN layer - /sensors publisher, remote device client, HMAC auth
├── WireViewMini/               # WireView Mini - tray icon, control panel, always-on service
├── udev/                       # udev rules for USB permissions
└── install.sh                  # Installation script
```

## Tech stack

- **.NET 8.0** - Runtime and build system
- **Avalonia UI 12.0** - Cross-platform MVVM UI framework
- **CommunityToolkit.Mvvm 8.4** - MVVM source generators
- **Custom chart controls** - Lightweight line/gauge/bar charts drawn directly with Avalonia (no charting dependency)
- **System.IO.Ports** - Serial communication with the device
