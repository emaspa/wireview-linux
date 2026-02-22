# WireView Pro II - Linux Unofficial Client

Unofficial Linux port of the [Thermal Grizzly WireView Pro II](https://www.thermal-grizzly.com/en/products/wireview) desktop application. Built with .NET 8.0 and [Avalonia UI](https://avaloniaui.net/).

![Screenshot](docs/screenshot.png?v=2)

## Features

- **Real-time monitoring** — Voltage, current, and power readings across all 6 pins with live charts
- **Device configuration** — Fan speed, display settings, fault alarms, thresholds
- **Configuration profiles** — Save, load, and manage named device configurations
- **Data logging** — On-device log readback and CSV export
- **DFU firmware updates** — Update device firmware directly from the app
- **Desktop notifications** — Via `notify-send`

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Linux with USB support (tested on Ubuntu 24.04+)
- A Thermal Grizzly WireView Pro II device connected via USB

## Installation

### Quick start

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

### Manual installation

If you prefer to do it step by step:

```bash
# Clone the repo
git clone https://github.com/emaspa/wireview-linux.git
cd wireview-linux

# Install udev rules (grants access to the WireView USB device)
sudo cp udev/99-wireview.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger

# Add yourself to the required groups
sudo usermod -aG dialout $USER
sudo usermod -aG plugdev $USER

# Log out and back in, then build
dotnet build -c Release
```

### Quick permissions fix (no reboot)

If you just want to test without logging out:

```bash
sudo chmod 666 /dev/ttyACM0
```

This is temporary and resets when the device is unplugged.

## Running

```bash
dotnet run --project WireView2/ -c Release
```

## Usage

The app has five pages accessible from the left sidebar:

| Page | Description |
|------|-------------|
| **Overview** | Summary of total current, power, voltage, and cable rating |
| **Monitoring** | Real-time charts for voltage, current, power, and temperature |
| **Logging** | Read device logs and export to CSV |
| **Device** | Device info, firmware update, and full device configuration (fan, display, alarms, thresholds) |
| **Settings** | App theme, startup behavior, background customization |

### Configuration profiles

On the **Device** page, you can save the current device configuration as a named profile and load it later. Profiles are stored as JSON files in `~/.local/share/PowerMonitor/profiles/`.

## USB device IDs

| Mode | VID | PID | Description |
|------|-----|-----|-------------|
| Normal | `0483` | `5740` | STM32 CDC/ACM virtual serial port |
| DFU | `0483` | `df11` | STM32 DFU bootloader |

## Project structure

```
wireview-linux/
├── WireView2/                  # Main Avalonia UI application
│   ├── Views/                  # AXAML views
│   ├── ViewModels/             # MVVM view models
│   ├── Services/               # App settings, profiles, notifications
│   └── Assets/                 # Icons, backgrounds, firmware
├── WireViewDeviceLib/          # Device communication library
│   └── Device/                 # Serial protocol, port finder, DFU
├── tools/                      # Asset extraction utilities
├── udev/                       # udev rules for USB permissions
└── install.sh                  # Installation script
```

## Tech stack

- **.NET 8.0** — Runtime and build system
- **Avalonia UI 11.3** — Cross-platform MVVM UI framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators
- **LiveChartsCore + SkiaSharp** — Real-time chart rendering
- **System.IO.Ports** — Serial communication with the device
- **LibUsbDotNet / LibUsbDfu** — USB device access for DFU firmware updates

## Troubleshooting

### Device not detected

1. Check that the device is connected: `lsusb | grep 0483`
2. Check that `/dev/ttyACM0` exists: `ls -la /dev/ttyACM*`
3. Check permissions: `groups` should include `dialout`
4. If using a VM, ensure USB passthrough is configured for both VID/PID pairs (normal + DFU mode)

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

Use at your own risk. This software interacts with hardware — while every effort has been made to ensure correctness, the authors are not responsible for any damage to your device.

## License

This project contains code decompiled from the original WireView Pro II Windows application and code from the [WireViewDeviceLib](https://github.com/ElmorLabs-ThermalGrizzly/WireViewDeviceLib) repository. Please respect the original authors' rights. This port is provided for personal use and interoperability purposes.
