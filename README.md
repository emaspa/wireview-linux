# WireView Pro II - Linux Unofficial Client

Unofficial Linux port of the [Thermal Grizzly WireView Pro II](https://www.thermal-grizzly.com/en/wireview-pro-ii-gpu/s-tg-wv-p2) desktop application. Built with .NET 8.0 and [Avalonia UI](https://avaloniaui.net/).

![Screenshot](docs/screenshot-v2.png)

## Features

- **Real-time monitoring** — Voltage, current, and power readings across all 6 pins with live charts
- **Device configuration** — Fan speed, display settings, fault alarms, thresholds
- **Configuration profiles** — Save, load, and manage named device configurations
- **Data logging** — On-device log readback and CSV export
- **Desktop notifications** — Via `notify-send`
- **Software shutdown on fault** — Optional system shutdown when a fault alarm triggers, for eGPU or setups where the hardware shutdown header cannot be connected

> **DFU firmware updates** are available on the [`dfu-enabled`](https://github.com/emaspa/wireview-linux/tree/dfu-enabled) branch. This feature has not been fully tested and could potentially brick your device, so it is excluded from the main branch and the pre-built binary.

> **hwmon integration**: If you want sensor data exposed to `sensors`, Grafana, conky, btop, and other monitoring tools via `/sys/class/hwmon/`, see [wireview-hwmon](https://github.com/emaspa/wireview-hwmon). The kernel module and daemon work standalone without this app, and this app can also use them as an alternative to direct serial communication (see below). That project also includes `wireviewctl`, a CLI tool for scripting device commands from the terminal.

## Connection modes

The app supports two ways of communicating with the device:

| Mode | How it works | Features |
|------|-------------|----------|
| **Direct serial** | App talks to the device over `/dev/ttyACM*` | Full control (default) |
| **hwmon + daemon** | App reads sensors from `/sys/class/hwmon/`, sends commands via the [wireviewd](https://github.com/emaspa/wireview-hwmon) daemon socket | Full control, plus sensor data available to system monitoring tools |

The app auto-detects the connection mode at startup. If the [wireview-hwmon](https://github.com/emaspa/wireview-hwmon) kernel module is loaded, the app uses hwmon for sensor data and connects to the daemon's Unix socket (`/run/wireviewd.sock`) for commands — configuration read/write, fault clearing, screen control, and device info all work through the daemon. If the daemon is not running, the app still displays sensor data in read-only mode.

If the kernel module is not loaded, the app falls back to direct serial communication automatically.

## Requirements

- Linux with USB support (tested on Ubuntu 24.04+ and Arch Linux)
- A Thermal Grizzly WireView Pro II device connected via USB

## Installation

### Ubuntu 24.04 (PPA)

```bash
sudo add-apt-repository ppa:sparvoli/wireview-hwmon
sudo apt update
sudo apt install wireview-linux
```

To also install the hwmon kernel module and daemon for system-wide sensor integration:

```bash
sudo apt install wireview-hwmon wireview-hwmon-dkms
```

### Arch Linux (AUR)

An [AUR package](https://aur.archlinux.org/packages/wireview-linux) is available, maintained by arakmar:

```bash
yay -S wireview-linux
```

### Option 1: Pre-built binary (no .NET required)

```bash
# Download and extract the latest release
mkdir -p ~/wireview-linux
curl -sL $(curl -s https://api.github.com/repos/emaspa/wireview-linux/releases/latest | grep -o 'https://.*linux-x64.tar.gz') | tar xz -C ~/wireview-linux

# Set up USB permissions
sudo cp ~/wireview-linux/udev/99-wireview.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG dialout $USER
sudo usermod -aG plugdev $USER

# Log out and back in, then run
~/wireview-linux/WireView2
```

### Option 2: Build from source

Requires [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

```bash
git clone https://github.com/emaspa/wireview-linux.git
cd wireview-linux
sudo ./install.sh
```

The install script will:
1. Install udev rules for automatic USB device permissions
2. Add your user to the `dialout` and `plugdev` groups
3. Build the application

**You must log out and back in** for the group changes to take effect.

Or manually step by step:

```bash
git clone https://github.com/emaspa/wireview-linux.git
cd wireview-linux

# Install udev rules (grants access to the WireView USB device)
sudo cp udev/99-wireview.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger

# Add yourself to the required groups
sudo usermod -aG dialout $USER
sudo usermod -aG plugdev $USER

# Log out and back in, then build and run
dotnet build -c Release
dotnet run --project WireView2/ -c Release
```

### Quick permissions fix (no reboot)

If you just want to test without logging out:

```bash
sudo chmod 666 /dev/ttyACM0
```

This is temporary and resets when the device is unplugged.

## Usage

The app has five pages accessible from the left sidebar:

| Page | Description |
|------|-------------|
| **Overview** | Summary of total current, power, voltage, and cable rating |
| **Monitoring** | Real-time charts for voltage, current, power, and temperature |
| **Logging** | Read device logs and export to CSV |
| **Device** | Device info and full device configuration (fan, display, alarms, thresholds) |
| **Settings** | App theme, startup behavior, background customization |

### Configuration profiles

On the **Device** page, you can save the current device configuration as a named profile and load it later. Profiles are stored as JSON files in `~/.local/share/PowerMonitor/profiles/`.

## USB device IDs

| Mode | VID | PID | Description |
|------|-----|-----|-------------|
| Normal | `0483` | `5740` | STM32 CDC/ACM virtual serial port |

## Project structure

```
wireview-linux/
├── WireView2/                  # Main Avalonia UI application
│   ├── Views/                  # AXAML views
│   ├── ViewModels/             # MVVM view models
│   ├── Services/               # App settings, profiles, notifications
│   └── Assets/                 # Icons, backgrounds
├── WireViewDeviceLib/          # Device communication library
│   └── Device/                 # Serial protocol, port finder
├── udev/                       # udev rules for USB permissions
└── install.sh                  # Installation script
```

## Tech stack

- **.NET 8.0** — Runtime and build system
- **Avalonia UI 11.3** — Cross-platform MVVM UI framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators
- **LiveChartsCore + SkiaSharp** — Real-time chart rendering
- **System.IO.Ports** — Serial communication with the device

## Troubleshooting

### Device not detected

1. Check that the device is connected: `lsusb | grep 0483`
2. Check that `/dev/ttyACM0` exists: `ls -la /dev/ttyACM*`
3. Check permissions: `groups` should include `dialout`
4. If using a VM, ensure USB passthrough is configured for the VID/PID pair

### Permission denied on /dev/ttyACM0

```bash
# Temporary fix:
sudo chmod 666 /dev/ttyACM0

# Permanent fix:
sudo cp udev/99-wireview.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG dialout $USER
# Then log out and back in
```

## Disclaimer

This software is an unofficial, community-made Linux port of the WireView Pro II application. It is **not affiliated with, endorsed by, or supported by Thermal Grizzly or ElmorLabs**. All trademarks belong to their respective owners.

Use at your own risk. This software interacts with hardware, while every effort has been made to ensure correctness, the authors are not responsible for any damage to your device.

## License

This project contains code decompiled from the original WireView Pro II Windows application and code from the [WireViewDeviceLib](https://github.com/ElmorLabs-ThermalGrizzly/WireViewDeviceLib) repository. Please respect the original authors' rights. This port is provided for personal use and interoperability purposes.
