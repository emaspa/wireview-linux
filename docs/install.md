# Installation

Every way to install the WireView Pro II Linux client, and how to get serial access to the device. Back to the [README](../README.md).

## Requirements

- Linux with USB support (tested on Ubuntu 24.04 / 26.04 LTS, Fedora 43-44, and Arch Linux; also packaged for Arch-based distros via the AUR and immutable distros like Bazzite / Silverblue via Flatpak)
- A Thermal Grizzly WireView Pro II or WireView Pro II Noctua Edition connected via USB (the Noctua Edition is tested on a real unit)
- For the hwmon + daemon mode: [wireview-hwmon](https://github.com/emaspa/wireview-hwmon) 1.7.0 or later; 1.8.1 or later alongside WireView Mini, whose wireviewd takes the LAN port back from it on its own
- Optional: `dfu-util` for in-app firmware flashing (the Flatpak bundles it and the Nix package puts it on the PATH; the deb, rpm and AUR `wireview-linux-bin` packages list it as a recommended or optional dependency, and the source-built AUR `wireview-linux` depends on it). See [firmware updates](firmware.md)

## WireView Mini

[WireView Mini](wireview-mini.md) (`wireview-mini`) comes with the app in the PPA, `.deb`, COPR/`.rpm` and AUR packages, installed as `/usr/bin/wireview-mini` with its files in `/usr/lib/wireview-linux/` (`/opt/wireview-linux/` for the source-built AUR `wireview-linux`), and sits next to `WireView2` in the pre-built tarball. The packages add a **WireView Mini** entry to the application launcher, which starts it and opens its control panel in the browser, or, when it already runs, opens that one's control panel; the tarball's `install.sh` adds it for the current user. Nothing starts it on its own: run `wireview-mini --install` once to start it at login (an entry in `~/.config/autostart/`), then the launcher entry or `wireview-mini` to start it now. The [Flatpak](#flatpak) and the [Nix flake](#nixos-flake) carry the app only.

It is one native executable (no .NET runtime) built against glibc 2.35, so it runs on any distro with that glibc or a newer one (tested on Ubuntu 22.04, 24.04 and 26.04, Fedora 43 and 44, and Arch), and on immutable distros straight from the tarball. With wireview-hwmon installed, use 1.8.1 or later: its wireviewd takes the LAN port back from WireView Mini when it starts while WireView Mini holds it.

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

Or grab the standalone `.rpm` from the [Releases](https://github.com/emaspa/wireview-linux/releases) page. A single RPM works on all current Fedora releases (tested on 43 and 44):

```bash
sudo dnf install ./wireview-linux-*.x86_64.rpm
```

To also install the hwmon kernel module and daemon for system-wide sensor integration (same COPR repo):

```bash
sudo dnf install wireview-hwmon wireview-hwmon-dkms
```

The package is a self-contained binary: no .NET runtime is required. For immutable, atomic distros (Bazzite, Silverblue, Kinoite), use the [Flatpak](#flatpak), or the [tarball](#pre-built-binary-no-net-required) for WireView Mini.

## Flatpak

For Bazzite, Silverblue and other immutable distros. A `.flatpak` bundle is available on the [Releases](https://github.com/emaspa/wireview-linux/releases) page:

```bash
flatpak install ./wireview-linux-*.flatpak
flatpak run io.github.emaspa.WireViewLinux
```

The Flatpak supports direct USB serial mode and carries the app only, without [WireView Mini](wireview-mini.md): the sandbox has no network access, so the browser could not reach WireView Mini's control panel. For the same reason the Flatpak app cannot connect through a WireView Mini started from the tarball on the same PC; the two then compete for the serial port, so run one or the other. A sandbox cannot install udev rules, so install the rule on the host once. Shutdown on fault does not work inside the sandbox (it cannot power the host off), and the Flatpak keeps its settings in `~/.var/app/io.github.emaspa.WireViewLinux/config/PowerMonitor/` instead of `~/.config/PowerMonitor/`:

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

The flake builds the app only; WireView Mini is not in it yet. `nix run github:emaspa/wireview-linux` starts the app without installing it, but direct serial access still needs the udev rule from the module. The package is marked unfree because it contains decompiled upstream code and Thermal Grizzly's firmware. The flake allows it on its own, so no `allowUnfree` setting is needed.

## Pre-built binary (no .NET required)

The tarball is self-contained: the app binary, WireView Mini (`wireview-mini` and the three files it reads from its own directory), the udev rule, the desktop entries, and a one-time `install.sh` that sets up USB serial access and adds **WireView Pro II** and **WireView Mini** to the application launcher for the current user (`~/.local/share/applications/`).

```bash
# Download and extract the latest release
curl -sL $(curl -s https://api.github.com/repos/emaspa/wireview-linux/releases/latest | grep -o 'https://.*linux-x64.tar.gz') | tar xz
cd wireview-linux-*-linux-x64

# One-time setup: the udev rule for USB serial access, and the launcher entries
./install.sh

# Run
./WireView2

# WireView Mini: start it at login, and now
./wireview-mini --install
./wireview-mini
```

Keep `wireview-mini`, `libSystem.IO.Ports.Native.so`, `TG-WV-PRO2-FW.hex` and `ext_flash.bin` in one directory. The login entry and the launcher entries point at it, so after moving the directory run `--install` and `./install.sh` again.

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
