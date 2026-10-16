# Installation

Every way to install the WireView Pro II Linux client, and how to get serial access to the device. Back to the [README](../README.md).

## Requirements

- Linux with USB support (tested on Ubuntu 24.04 / 26.04 LTS, Fedora 43-44, and Arch Linux; also packaged for Arch-based distros via the AUR and immutable distros like Bazzite / Silverblue via Flatpak)
- A Thermal Grizzly WireView Pro II or WireView Pro II Noctua Edition connected via USB (the Noctua Edition is tested on a real unit)
- For the hwmon + daemon mode: [wireview-hwmon](https://github.com/emaspa/wireview-hwmon) 1.7.0 or later
- Optional: `dfu-util` for in-app firmware flashing (the Flatpak bundles it; deb/rpm/AUR packages list it as a recommended/optional dependency). See [firmware updates](firmware.md)

## Windows (x64)

The desktop app is not built for Windows, where the official application covers that. [WireView Mini](wireview-mini.md) is: download `wireview-mini-<version>-windows-x64.zip` from the [Releases](https://github.com/emaspa/wireview-linux/releases) page, extract it and run `wireview-mini.exe`, or `wireview-mini --install` to start it at login. No installation or .NET runtime required.

## Ubuntu 24.04 / 26.04 LTS (PPA)

```bash
sudo add-apt-repository ppa:sparvoli/wireview-hwmon
sudo apt update
sudo apt install wireview-linux
```

To also install the hwmon kernel module and daemon for system-wide sensor integration:

```bash
sudo apt install wireview-hwmon wireview-hwmon-dkms
```

## Ubuntu and Debian (.deb package)

A pre-built `.deb` package is available on the [Releases](https://github.com/emaspa/wireview-linux/releases) page. Download it and install:

```bash
sudo apt install ./wireview-linux_*_amd64.deb
```

## Fedora (COPR)

```bash
sudo dnf copr enable emaspa/wireview-linux
sudo dnf install wireview-linux
```

Or grab the standalone `.rpm` from the [Releases](https://github.com/emaspa/wireview-linux/releases) page. A single RPM works on all current Fedora releases (tested on 42, 43, and 44):

```bash
sudo dnf install ./wireview-linux-*.x86_64.rpm
```

To also install the hwmon kernel module and daemon for system-wide sensor integration (same COPR repo):

```bash
sudo dnf install wireview-hwmon wireview-hwmon-dkms
```

The package is a self-contained binary: no .NET runtime is required. For immutable, atomic distros (Bazzite, Silverblue, Kinoite), use the [Flatpak](#flatpak) instead.

## Flatpak

For Bazzite, Silverblue and other immutable distros. A `.flatpak` bundle is available on the [Releases](https://github.com/emaspa/wireview-linux/releases) page:

```bash
flatpak install ./wireview-linux-*.flatpak
flatpak run io.github.emaspa.WireViewLinux
```

The Flatpak supports direct USB serial mode. A sandbox cannot install udev rules, so install the rule on the host once:

```bash
sudo curl -fsSL https://raw.githubusercontent.com/emaspa/wireview-linux/main/udev/99-wireview.rules \
  -o /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

## Arch Linux, CachyOS, EndeavourOS (AUR)

[`wireview-linux-bin`](https://aur.archlinux.org/packages/wireview-linux-bin) installs the pre-built release binary (no .NET SDK, no compile) and tracks the latest release:

```bash
yay -S wireview-linux-bin
```

[`wireview-linux`](https://aur.archlinux.org/packages/wireview-linux), started by [Arakmar](https://github.com/Arakmar) and now co-maintained here, builds the same release from source with the Arch .NET SDK. It is updated together with the binary package, so pick whichever you prefer:

```bash
yay -S wireview-linux        # builds from source, takes a few minutes
```

To also install the hwmon kernel module and daemon for system-wide sensor integration, load the module and enable the daemon after installing:

```bash
yay -S wireview-hwmon wireview-hwmon-dkms
sudo modprobe wireview_hwmon
sudo systemctl enable --now wireviewd
```

From the next boot onward both come up automatically (the dkms package registers the module in `modules-load.d` and the service modprobes it on start).

## NixOS (flake)

The repository is a flake. Its NixOS module installs the app, built from source, and the udev rule. Add [wireview-hwmon](https://github.com/emaspa/wireview-hwmon)'s module for the hwmon + daemon mode:

```nix
{
  inputs = {
    wireview-linux.url = "github:emaspa/wireview-linux";
    wireview-hwmon.url = "github:emaspa/wireview-hwmon";
  };

  outputs = { nixpkgs, wireview-linux, wireview-hwmon, ... }: {
    nixosConfigurations.myhost = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        ./configuration.nix
        wireview-linux.nixosModules.default
        wireview-hwmon.nixosModules.default
        {
          programs.wireview-linux.enable = true;
          services.wireview-hwmon.enable = true;
          users.users.alice.extraGroups = [ "wireview" ];
        }
      ];
    };
  };
}
```

`nix run github:emaspa/wireview-linux` starts the app without installing it, but direct serial access still needs the udev rule from the module. The package is marked unfree because it contains decompiled upstream code and Thermal Grizzly's firmware. The flake allows it on its own, so no `allowUnfree` setting is needed.

## Pre-built binary (no .NET required)

The tarball is self-contained: the app binary, the udev rule, and a one-time `install.sh` that sets up USB serial access.

```bash
# Download and extract the latest release
curl -sL $(curl -s https://api.github.com/repos/emaspa/wireview-linux/releases/latest | grep -o 'https://.*linux-x64.tar.gz') | tar xz
cd wireview-linux-*-linux-x64

# One-time USB serial setup (installs the udev rule)
./install.sh

# Run
./WireView2
```

## Build from source

Requires [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

```bash
git clone https://github.com/emaspa/wireview-linux.git
cd wireview-linux
./install.sh
```

Run it as your own user; it uses `sudo` where it needs root. The install script will:

1. Install the udev rule (serial port and DFU bootloader `0660` in the serial group, plus a `uaccess` ACL for the user logged in at the local seat). The serial group is `dialout`, or `uucp` on Arch-based distros
2. Only when there is no local seat session (SSH, for example): add your user to that group, which needs a new login to take effect
3. Build the application

Or manually step by step:

```bash
git clone https://github.com/emaspa/wireview-linux.git
cd wireview-linux

# Install udev rules (grants access to the WireView USB device)
sudo cp udev/99-wireview.rules /etc/udev/rules.d/
# Arch-based distros have no dialout group, their serial group is uucp
getent group dialout >/dev/null || sudo sed -i 's/GROUP="dialout"/GROUP="uucp"/g' /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules
sudo udevadm trigger

# Only over SSH or another session without a local seat (no uaccess ACL there):
# join the serial group, then log in again
# sudo usermod -aG dialout $USER
# Arch-based distros: sudo usermod -aG uucp $USER

# Build and run
dotnet build -c Release
dotnet run --project WireView2/ -c Release
```

WireView Mini has its own build commands: see [Building WireView Mini](wireview-mini.md#building).

## hwmon integration

If you want sensor data exposed to `sensors`, Grafana, conky, btop, and other monitoring tools via `/sys/class/hwmon/`, see [wireview-hwmon](https://github.com/emaspa/wireview-hwmon). The kernel module and daemon work standalone without this app, and this app can also use them as an alternative to direct serial communication (see [connection modes](usage.md#connection-modes)). That project also includes `wireviewctl`, a CLI tool for monitoring and scripting device commands from the terminal.

The package commands for each distro are in its section above. Commands that change the device through the daemon need root or membership of the `wireview` group, which the wireview-hwmon packages create:

```bash
sudo usermod -aG wireview $USER
```

## Serial access

### Package installs

The package installs above (PPA, `.deb`, `.rpm`, AUR) install the udev rule and the system reloads it automatically, so **serial access works out of the box** for the user logged in at the local seat. The rule makes the serial port `0660` in the distro's serial group (`dialout`, or `uucp` on Arch-based distros), the DFU bootloader `0660 root:dialout`, and gives the active session a logind `uaccess` ACL, so no group membership or logout is needed there. The rule installs unchanged on any distro. Arch-based distros have no `dialout` group, so the AUR package gives the DFU bootloader their serial group `uucp` instead.

The rule is `udev/99-wireview.rules` in this repository. It covers the device's normal mode and its DFU bootloader (see [USB device IDs](usage.md#usb-device-ids)).

### Joining the serial group

If access still fails in an unusual setup (for example over SSH, where the `uaccess` ACL doesn't apply), add yourself to the serial group and log back in:

```bash
# Debian, Ubuntu, Fedora and most other distros
sudo usermod -aG dialout "$USER"

# Arch-based distros
sudo usermod -aG uucp "$USER"
```

### Quick permissions fix (no reboot)

If you just want to test without logging out:

```bash
sudo chmod 666 /dev/ttyACM0
```

This is temporary and resets when the device is unplugged.

## Troubleshooting

### Device not detected

1. Check that the device is connected: `lsusb | grep 0483`
2. Check that `/dev/ttyACM0` exists: `ls -la /dev/ttyACM*`
3. Check permissions: in a local desktop session `getfacl /dev/ttyACM0` should list your user (the `uaccess` ACL); over SSH, `groups` should include `dialout` (`uucp` on Arch-based distros)
4. If using a VM, ensure USB passthrough is configured for the VID/PID pair

### Permission denied on /dev/ttyACM0

```bash
# Temporary fix (until the device is replugged):
sudo setfacl -m "u:$USER:rw" /dev/ttyACM0

# Permanent fix:
sudo cp udev/99-wireview.rules /etc/udev/rules.d/
# Arch-based distros have no dialout group, their serial group is uucp
getent group dialout >/dev/null || sudo sed -i 's/GROUP="dialout"/GROUP="uucp"/g' /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules
sudo udevadm trigger
# Over SSH or without a local seat session, also join the serial group and
# log in again:
sudo usermod -aG dialout $USER
# Arch-based distros: sudo usermod -aG uucp $USER
```
