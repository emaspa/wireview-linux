# WireView Pro II - Linux Unofficial Client

Unofficial Linux port of the [Thermal Grizzly WireView Pro II](https://www.thermal-grizzly.com/en/wireview-pro-ii-gpu/s-tg-wv-p2) desktop application, plus **WireView Mini**, a small always-on companion with a tray icon and a browser control panel. A **Windows** build (**WireView Plus**) is also available on the [Releases](https://github.com/emaspa/wireview-linux/releases) page. Built with .NET 8.0 and [Avalonia UI](https://avaloniaui.net/). Not affiliated with, endorsed by, or supported by Thermal Grizzly or ElmorLabs. For sensors in `/sys/class/hwmon/` (`sensors`, Grafana, conky, btop) and the `wireviewctl` CLI, see [wireview-hwmon](https://github.com/emaspa/wireview-hwmon).

![Screenshot](docs/screenshot-v2.png)

## Features

- **Real-time monitoring**: voltage, current and power on all 6 pins, live charts and custom charts (any series, per-series colors, manual or auto Y scaling)
- **Device configuration**: fan, display, fault alarms and thresholds, saved and loaded as named profiles
- **Data logging**: on-device log readback per power cycle, CSV export
- **Alerts**: desktop notifications via `notify-send`, and optional software shutdown on fault for eGPUs or setups without the hardware shutdown header
- **LAN monitoring**: read WireViews on other machines and edit their configuration (HMAC-authenticated). See [LAN monitoring](docs/lan-monitoring.md)
- **Theme editor**: background images, colors, inversion and a live preview; `.wv2t` files work with the official Windows app. See [usage](docs/usage.md#theme-editor)
- **Firmware updates**: flash the bundled firmware (v05, build 20260902_0741) over USB DFU with `dfu-util`. See [firmware](docs/firmware.md)
- **WireView Mini**: alerts, shutdown on fault and the device on the LAN with the app closed. See [WireView Mini](docs/wireview-mini.md)
- **hwmon integration**: works with the [wireview-hwmon](https://github.com/emaspa/wireview-hwmon) kernel module and daemon as an alternative to direct serial

**Firmware flashing can leave the device unbootable if it is interrupted. Flash at your own risk and read the [full warning](docs/firmware.md#warning) first.**

## Two apps

| | Desktop app (`WireView2`, WireView Plus on Windows) | WireView Mini (`wireview-mini`) |
|---|---|---|
| What it is | The full window: Overview, Monitoring, Logging, Device and Settings pages | A program with no window: a tray icon and a control panel in the browser, about 40 MB resident on Linux |
| When it runs | When you want to look at the device | All the time, started at login |
| What it does | Monitoring, charts, device log, configuration and profiles, theme editor, firmware updates, LAN monitoring | Monitoring, fault alerts, shutdown on fault, device configuration, firmware updates, theme editor, charts, device log, CSV export, LAN publishing |

Both read the same `settings.json`. While WireView Mini runs, the app connects through it instead of opening the serial port. Details: [docs/wireview-mini.md](docs/wireview-mini.md).

## Install

| Channel | Commands |
|---|---|
| Windows (x64) | Download `wireview-plus-<version>-windows-x64.zip` from [Releases](https://github.com/emaspa/wireview-linux/releases), extract it, run the executable |
| Ubuntu 24.04 / 26.04 LTS (PPA) | `sudo add-apt-repository ppa:sparvoli/wireview-hwmon`<br>`sudo apt install wireview-linux` |
| Ubuntu / Debian (.deb) | `sudo apt install ./wireview-linux_*_amd64.deb` (from [Releases](https://github.com/emaspa/wireview-linux/releases)) |
| Fedora (COPR) | `sudo dnf copr enable emaspa/wireview-linux`<br>`sudo dnf install wireview-linux` |
| Flatpak (Bazzite, Silverblue) | `flatpak install ./wireview-linux-*.flatpak` (from [Releases](https://github.com/emaspa/wireview-linux/releases)), then the [host udev rule](docs/install.md#flatpak) |
| Arch / CachyOS / EndeavourOS (AUR) | `yay -S wireview-linux-bin` (or `yay -S wireview-linux` to build from source) |
| NixOS (flake) | `nix run github:emaspa/wireview-linux`, or the [NixOS module](docs/install.md#nixos-flake) |
| Pre-built tarball | Extract the `linux-x64.tar.gz` release, then `./install.sh` and `./WireView2` |
| Build from source | `git clone https://github.com/emaspa/wireview-linux.git`<br>`cd wireview-linux && ./install.sh` |

The packages install the udev rule, so serial access works out of the box for the user at the local seat. The long version (requirements, serial access and groups, the hwmon packages, NixOS, Flatpak, building, troubleshooting) is in [docs/install.md](docs/install.md).

## Connection modes

| Mode | How it works | Features |
|------|-------------|----------|
| **Direct serial** | App talks to the device over `/dev/ttyACM*` | Full control (default) |
| **hwmon + daemon** | App reads sensors from `/sys/class/hwmon/`, sends commands via the [wireviewd](https://github.com/emaspa/wireview-hwmon) daemon socket | Full control, plus sensor data available to system monitoring tools |
| **Through WireView Mini** | App reads and controls the device through [`wireview-mini`](docs/wireview-mini.md) on this PC, over `127.0.0.1` | Full control; alerts and shutdown on fault keep working with the app closed |

The app picks the mode at startup: hwmon when the wireview-hwmon module is loaded, then WireView Mini, then direct serial. Writes through the daemon need the `wireview` group and wireview-hwmon 1.7.0 or later. Details: [docs/usage.md](docs/usage.md#connection-modes).

## Quick usage

```bash
wireview-linux                    # start the app (./WireView2 from the tarball)
wireview-linux --page monitoring  # open on overview, monitoring, logging, device or settings
flatpak run io.github.emaspa.WireViewLinux   # the Flatpak

wireview-mini              # run WireView Mini in the foreground (Ctrl+C to stop)
wireview-mini --install    # start it at login
wireview-mini --open       # open its control panel in the browser
wireview-mini --status     # connected device, fault state and version
```

Settings live in `~/.config/PowerMonitor/settings.json` on Linux and `%APPDATA%\PowerMonitor\settings.json` on Windows, shared by the app and WireView Mini.

## Documentation

- [Installation](docs/install.md): every channel, serial access, udev, building from source, troubleshooting
- [Using the app](docs/usage.md): connection modes, pages, profiles, theme editor, logging, rendering, USB IDs, project structure
- [WireView Mini](docs/wireview-mini.md): tray, control panel, serial handover, ports, building, settings
- [LAN monitoring](docs/lan-monitoring.md): remote hosts, publishing, the network secret
- [Firmware updates](docs/firmware.md): the warning, checks, `dfu-util`, udev, Windows
- Design notes: [footprint plan](docs/footprint-plan.md), [upstream 1.0.7 port plan](docs/upstream-1.0.7-port-plan.md), [upstream 1.0.8 port plan](docs/upstream-1.0.8-port-plan.md)

## License and credits

Thanks to [Thermal Grizzly](https://www.thermal-grizzly.com/) for providing a WireView Pro II Noctua Edition unit for development.

This software is an unofficial, community-made Linux port of the WireView Pro II application. It is **not affiliated with, endorsed by, or supported by Thermal Grizzly or ElmorLabs**. All trademarks belong to their respective owners.

Use at your own risk. This software interacts with hardware, while every effort has been made to ensure correctness, the authors are not responsible for any damage to your device.

This project contains code decompiled from the original WireView Pro II Windows application and code from the [WireViewDeviceLib](https://github.com/ElmorLabs-ThermalGrizzly/WireViewDeviceLib) repository. Please respect the original authors' rights. This port is provided for personal use and interoperability purposes.
