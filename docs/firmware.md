# Firmware updates

The app's **Device** page and WireView Mini's control panel flash the bundled firmware over USB DFU. Back to the [README](../README.md).

## Warning

> **Warning:** firmware flashing restarts the device into its STM32 bootloader and rewrites its flash. It follows the same DFU procedure as the official Windows client (and refuses to start if `dfu-util` or the firmware image is missing), but a power loss or unplug mid-flash can leave the device unbootable until reflashed manually. This is unofficial software, not affiliated with or endorsed by Thermal Grizzly: flash at your own risk.

The previous experimental `dfu-enabled` branch has been removed in favor of this built-in implementation.

## The bundled firmware

The bundled firmware is v05, build 20260902_0741 (`TG-WV-PRO2-FW.hex`). The app and WireView Mini ship the same image; WireView Mini keeps it next to `wireview-mini`.

The bundled image is byte for byte the firmware a Noctua Edition ships with, so flashing it again restores the factory firmware on either edition.

## Checks before a flash

The app and WireView Mini run the same checks:

- they compare the bundled and device firmware by version and build date,
- they refuse an image made for another product,
- they refuse an image older than Noctua Edition support on a Noctua Edition,
- they warn before downgrades: an older or identical build is flashed only after you confirm (in WireView Mini, by ticking *Flash the older (or same) firmware anyway*).

## In the app

Flash the bundled firmware from the **Device** page. Requires the `dfu-util` package on Linux.

## In WireView Mini

The panel's **Firmware** card shows the device's and the bundled firmware (version and build), what a flash would do, and **Update firmware**. **Update firmware** shows the same warning as the app first; **Flash now** restarts the device into its bootloader and flashes it, with a progress bar, and the device reconnects on its own.

While a flash runs the other device controls are off, the app cannot borrow the serial port, and the tray's Quit waits for the end. The tray shows *Firmware update available*, which opens the panel, when the bundled firmware is newer than the device's. See [WireView Mini](wireview-mini.md#firmware).

## Requirements

### Linux: dfu-util

Flashing needs the `dfu-util` package (WireView Mini's card says when it is missing). The Flatpak bundles it; deb/rpm/AUR packages list it as a recommended/optional dependency. `WIREVIEW_DFU_UTIL` overrides which `dfu-util` WireView Mini runs.

### Linux: udev rule for the bootloader

During a flash the device re-enumerates as the STM32 DFU bootloader, `0483:df11` (see [USB device IDs](usage.md#usb-device-ids)). The udev rule `udev/99-wireview.rules` makes it `0660 root:dialout`, plus the `uaccess` ACL for the user at the local seat; the AUR package uses `uucp` instead of `dialout`. See [Serial access](install.md#serial-access).

### Linux: wireviewd

With wireviewd the bootloader command goes through the daemon and needs the `wireview` group (`sudo usermod -aG wireview $USER`). See [connection modes](usage.md#connection-modes).

### Windows: WinUSB

On Windows the app and WireView Mini use the native WinUSB flasher, so `dfu-util` is not needed.
