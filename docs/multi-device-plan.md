# Multi-device WireView support: implementation plan for wireview-hwmon and wireview-linux

Status (2026-11-23): implemented in wireview-hwmon 1.9.0 and the wireview-linux commits after 1.4.2.0. Deviations from this plan: hwmon instances after the first are named `wireviewN` (so tools that take the first `wireview` keep the primary device), the LIST_DEVICES record is 160 bytes, nicknames are per user in `settings.json`, `/dev/wireview-pro2` is kept beside the new per-unit `/dev/wireview-<serial>`, and Mini's device selection is held by WireView Mini rather than passed with each request.

Nothing was edited, and no serial port was opened or probed. Everything below comes from reading code, sysfs, udev properties, the process list and logs.

## 0. Where the second device is dropped today

Live state:

| Port | USB path | USB serial | State |
|---|---|---|---|
| `ttyACM0` | 3-4 | `20A036A64E46` | Owned by `wireviewd` 1.8.1 (journal: "using /dev/ttyACM0 ... Noctua Edition (EF06)") |
| `ttyACM1` | 3-6 | `205B38A53245` | Untouched by every process |

The second device is dropped at three independent points, and each must be fixed:

1. **Daemon discovery returns the first match.** `find_device()` returns on the first ttyACM with VID/PID 0483:5740 (`wireview-hwmon/wireviewd.c:402-471`, return at `:452-455`). `main()` holds one `serial_fd`/`hwmon_fd` pair (`:2724-2725`), and all device state is file-scope singletons.
2. **The kernel module has exactly one instance.** It registers one platform device with id -1 (`wireview_hwmon.c:569-570`), one hwmon device named `"wireview"` (`:509-513`) and one misc node `"wireview-hwmon"` (`:517-521`). The frame carries no device identity (`:41-59`).
3. **The app stops looking once a daemon-backed hwmon device exists.** `HwmonDevice.FindHwmonPath()` returns the first hwmon dir named `wireview` (`WireViewDeviceLib/WireViewDeviceLib/Device/HwmonDevice.cs:761-780`). `DeviceManager.DefaultProbe` adds it under the fixed source `"hwmon"` and then `yield break`s before the serial scan (`DeviceManager.cs:107-116`). The Mini log confirms it: `local devices: WireView Pro II Noctua Edition (hwmon + daemon) [hwmon]`.

Two related defects surfaced on the way:

- **Latent daemon bug.** If the first enumerated port holds a product the daemon does not drive, `device_connect()` clears `dev_path` and `find_device()` returns the same port again (`wireviewd.c:2619-2628`). A supported device behind it is never reached.
- **Colliding udev symlink.** The app's rule gives every matching tty `SYMLINK+="wireview-pro2"` (`udev/99-wireview.rules:21`). Right now `/dev/wireview-pro2 -> ttyACM1`, which is not the daemon's device. Nothing in either repo reads the symlink.

## 1. Single-device assumptions

### wireview-hwmon

| Area | Location | Assumption |
|---|---|---|
| Kernel instance | `wireview_hwmon.c:563-581` | One static `wireview_pdev`, id -1 |
| Kernel hwmon/misc | `wireview_hwmon.c:509-521` | One hwmon `"wireview"`, one misc node, state in one `wireview_priv` (`:67-73`) |
| Kernel lifetime | `wireview_hwmon.c:551-557` | `suppress_bind_attrs`: instance lives for the module's lifetime |
| Daemon state | `wireviewd.c:253, 256-257, 261, 267-270, 274, 278-280` | `dev_info`, `g_last`, `g_dev_uid`, energy counters, `g_hwmon_v2`, `g_serial_fd`, suspend state: all singletons |
| Daemon main loop | `wireviewd.c:2724-2733, 2942, 3031-3049` | One fd pair, one `prev_status/prev_log/next_poll`, a function-static `was_suspended`, one disconnect path |
| Unsupported-device memo | `wireviewd.c:2539-2540` | One path remembered |
| Socket | `wireviewd.c:61-63, 763-790` | One `/run/wireviewd.sock`; no device address in any request (`:969-1176`); `MAX_CLIENTS 8` (`:87`) |
| Handover | `wireviewd.c:1000-1027` | Global suspend deadline and owner |
| HTTP `/sensors` | `wireviewd.c:1343-1348` | Comment "This daemon manages a single device"; 2048-byte body (`:2429`) |
| HTTP `/metrics` | `wireviewd.c:1501-1522` | One `device=` label |
| HTTP `/config` | `wireviewd.c:2471` | Exact-match path, so no `?id=` |
| HTTP `POST /command` | `wireviewd.c:2319-2347` | `deviceId` in the body is never read |
| Daemon CLI | `wireviewd.c:2637-2644, 2657-2675` | `-d` takes one path |
| `wireviewctl` socket | `wireviewctl.c:37-38, 82-106` | Fixed socket, no device selector |
| `wireviewctl` hwmon | `wireviewctl.c:1338-1385` | First hwmon named `wireview` |
| `wireviewctl flash` | `wireviewctl.c:1197-1207, 1264, 1302-1304` | Any `[0483:df11]` counts as the target; the bootloader command is skipped if any DFU device is already present; `dfu-util -d 0483:df11` with no path or serial |
| Service unit | `wireviewd.service`, `debian/wireviewd.service:53-54`, `nix/module.nix:74` | Already class-wide (`char-ttyACM`, `char-misc`); nothing to change |
| udev | `99-wireview-hwmon.rules` | Already matches every device; no symlink |

### wireview-linux

| Area | Location | Assumption |
|---|---|---|
| hwmon discovery | `HwmonDevice.cs:761-780`; `DeviceManager.cs:107-109` | First match, fixed source `"hwmon"` |
| Serial scan suppressed | `DeviceManager.cs:115-116`, prune at `:392-394` | Daemon is assumed to own all ports |
| Daemon socket | `HwmonDevice.cs:14, 38` | Fixed path, no device address |
| Collection key | `DeviceManager.cs:309` | Keyed by source (`serial:/dev/ttyACM0`), not UID, despite the doc at `:22-23`; unstable across replug |
| Initial selection | `DeviceManager.cs:325`, `:200`, `:371` | First to connect wins; nothing persisted |
| Handover port | `DirectSerialSession.cs:49` | `FindMatchingComPorts().FirstOrDefault()`, could open the wrong device |
| GUI bootloader via Mini | `WireView2/ViewModels/DeviceViewModel.cs:769` | First port |
| Mini bootloader | `WireViewMini/MiniService.cs:424` | First port |
| COM logger | `WireView2/Services/ComPortLogger.cs:16-19` | `ports[0]` |
| DFU flash | `DfuUtilFlasher.cs:57-59, 81` | Any 0483:df11; `-d` only |
| GUI view models | `DeviceAutoConnector.cs:30, 38-43` | All view models see only the selected device |
| GUI fault alerts | `ConnectionStatusViewModel.cs:22, 201-231` | One `FaultMonitor`, fed by the selected device only; a fault on an unselected device raises no alert and no shutdown |
| GUI tray | `WireView2/App.axaml.cs:261-264, 290-300` | Selected device only |
| GUI picker | `MainWindowViewModel.cs:34-45, 101-121`; `MainWindow.axaml:169-171` | Already exists; label is "name - transport · last 6 of UID" |
| Mini device choice | `MiniService.cs:362-368` | `PreferredLocal()` picks one device for the panel, flash, theme, log and history (`:376-395, 472, 504`) |
| Mini tray status | `MiniService.cs:341-356` | First connected device |
| Mini history | `MiniService.cs:102, 500-517` | One buffer; cleared when "another device" appears |
| Mini daemon mode | `MiniService.cs:564-572` | One bool |
| Mini panel | `WireViewMini/ControlPanel.cs:271, 288, 493` | No device parameter on any `/api/*` route (`:117-142`) |
| Settings | `WireViewNet/Services/AppSettings.cs:76-166` | Flat global `settings.json`; no per-device section |
| Profiles | `DeviceProfileService.cs:69-122` | Named files, device-agnostic; fine as is |
| Single-instance locks | `SingleInstanceService.cs:11-32`; `MiniInstance.cs:24-66` | One process per user; correct for multi-device, no change |
| CLI | `App.axaml.cs:590-596`; `WireViewMini/Program.cs:73-99` | GUI has only `--page`; Mini takes one option at a time, no device selector |
| udev | `udev/99-wireview.rules:21` | Colliding `wireview-pro2` symlink |

Already multi-device and reusable:

- `DeviceManager` holds N serial devices when no daemon is present (`DeviceManager.cs:129-141`).
- Mini keeps fault monitors per device id (`MiniService.cs:73, 81, 134`).
- The LAN schema has a `devices[]` array and a per-command `deviceId` (`WireViewSensorDto.cs:104-108`, `WireViewCommand.cs:17`).
- The app's own publisher honours `deviceId` and `/config?id=` (`LocalPublishHost.cs:165-170, 197-199`, `SensorPublisher.cs:164-168`).
- `wireview-mini --status` iterates all devices (`StatusCommand.cs:46`).
- `wireviewctl` parses a `devices` array (`wireviewctl.c:1998-2004`).

## 2. Stable device identity

- **Canonical key: the 12-byte chip UID as 24 uppercase hex characters.** The firmware returns it for `CMD_READ_UID` (`wireviewd.c:720-724`). The app already uses it as `IWireViewDevice.UniqueId`, `WireViewSensorDto.Id`, the `/config` `deviceId` and the `/metrics` `device` label.
- **The USB serial is derived from the UID, so a tty can be matched to a UID without opening it.** With the UID as three little-endian 32-bit words w0, w1, w2: `serial = hex8(w0 + w2) + hex4(w1 >> 16)`. I checked this on one device only: the Noctua UID `610069000B50464E45363720` gives `20A036A64E46`, which is exactly ttyACM0's sysfs `serial`. Confirm it on the second unit once its UID is known.
- **This sidesteps the probe-interference trap.** "Which tty is device X" becomes a sysfs lookup instead of a welcome-string probe.
- **Short id for humans: the 12-character USB serial**, shown as its last 4 or 6 characters. It is what `/dev/serial/by-id` and `ID_SERIAL_SHORT` already show.
- **DFU mode: target by USB topology path, not serial.** The path is the basename of the USB device's sysfs dir (`3-4`, `3-6`) and survives re-enumeration into the bootloader. Record it before sending the bootloader command and pass it as `dfu-util -p <path>`. I did not verify whether the ROM bootloader reports the same serial string, which is why the path is the key.

How identity flows:

- **hwmon:** every instance keeps `name` = `wireview` (same-type chips share a name, as the two `nvme` entries here do). Each instance gains `serial`, `uid` and `label` attributes. libsensors chip names become `wireview-isa-0000`, `wireview-isa-0001`, and so on, from the platform device id. The first device keeps `wireview-isa-0000` and the sysfs path `/sys/devices/platform/wireview_hwmon`.
- **Index stability:** the daemon persists the serial-to-index map, so a device keeps its chip address across boots and replugs. This matters here because `coolercontrold` is installed.
- **Settings:** a per-device section keyed by UID.
- **UI:** nickname if set, else "edition · last 4 of serial".

## 3. Design and ordered steps

### 3.1 wireview-hwmon (target 1.9.0)

One daemon process serves every device through one socket, one LAN listener and one config. Templated per-device units would fight over port 9876 and the socket.

**H0. Discovery bugfix (shippable alone, e.g. 1.8.2).**
- `find_device()` becomes `scan_devices()`, returning every matching tty with its USB serial and port path, read from the same sysfs walk at `wireviewd.c:431-466`.
- The daemon is still single-device, but it tries the next candidate when one is unsupported or fails.
- This fixes the latent lock-on bug.

**H1. Kernel module: multiple instances.**
- Keep the permanent index-0 instance exactly as now (`wireview_hwmon.c:569`).
- Add `.open`, `.release` and `.unlocked_ioctl` to `wireview_misc_fops` (`:125-128`). Each open file carries its own binding.
- New ioctl `WIREVIEW_IOC_BIND { u32 index; char serial[16]; char uid[25]; char label[48]; }`:
  - index 0 binds the permanent instance;
  - index N >= 1 creates platform device `wireview_hwmon.N` plus its hwmon device, and removes both on `release`;
  - a second bind of an index in use returns `-EBUSY`.
- An fd that writes without binding feeds index 0, so an old daemon on the new module behaves as today.
- Add `serial`, `uid` and `label` to `wireview_extra_attrs` (`:246-252`); they return `-ENODATA` while unbound.
- The frame format stays v3 (`:41-59`).
- Keep `suppress_bind_attrs`.
- Dynamic instances need a refcounted priv, so `release` cannot free it under a sysfs reader.

**H2. Daemon refactor, no behaviour change.**
- Move the singletons listed in section 1 into `struct wv_device`, including the function-static `was_suspended` (`:2942`).
- Pass the device to `read_sensors`, `write_hwmon`, `energy_*`, `query_device_info`, `relay_*` (`:2157-2204`), `build_sensors_json`, `build_metrics` and `handle_client_request`.
- With a table of size 1 the existing unit and e2e tests must pass untouched.

**H3. Daemon multi-device.**
- **Table:** `MAX_DEVICES` 8.
- **Scanning:** rescan every 2 to 5 s for arrivals; per-device reconnect back-off replaces `next_reconnect` (`:2733`).
- **Index assignment:**
  - persist `serial index` lines in `/var/lib/wireview/devices`;
  - add `StateDirectory=wireview` to all three unit files and `nix/module.nix`;
  - the first device ever seen gets 0, later ones the lowest free index;
  - on first run with several devices present and no state file, assign by ascending ttyACM number.
- **hwmon binding:** one `open(HWMON_DEV)` plus `BIND` per device. On `ENOTTY` (old module) only index 0 feeds hwmon; log one warning. The other devices still work over the socket and HTTP.
- **Socket protocol** (0x0C and 0x0D are free after `WCMD_RELOAD_CONFIG` 0x0B, `:135`):
  - `WCMD_LIST_DEVICES 0x0C` replies with a count and one record per device: uid[12], vendor, product, fw, config version, hwmon index, state, usb serial, tty path, usb port path.
  - `WCMD_SELECT_DEVICE 0x0D` (payload uid[12]) binds this connection to a device, stored in `struct client` (`:804-813`).
  - A connection that never selects targets the lowest-index device, so GUI <= 1.4.x and older `wireviewctl` keep working.
  - Old daemons answer unknown commands with `RESP_ERROR` (`:1172-1174`), or `RESP_NOT_CONNECTED` when no device is attached (`:995-998`). Clients treat any non-OK reply to LIST as "legacy daemon".
- **Handover:** suspend deadline and owner become per device (`:1000-1027`, `:1232`). One client may hold several.
- **Client limit:** raise `MAX_CLIENTS` to 32 (`:87`, `pfds` at `:2788`). Each app holds one connection per device.
- **HTTP:**
  - `/sensors` emits N entries and needs a larger body buffer (`:2429`);
  - `/metrics` emits every series per device and needs a larger buffer (`:2450`);
  - `/config?id=<UID>` requires prefix matching at `:2471`;
  - `POST /command` honours `deviceId`; empty means primary; an unknown id returns 404. The app always sends the UID (`NetworkDevice.cs:182`).
- **CLI:** `-d` becomes repeatable and means "only these ports".
- **Logs:** audit lines get a short-id prefix.

**H4. wireviewctl.**
- Global `--device <uid|serial|suffix|index>`.
- New `devices` command listing index, serial, UID, edition, firmware, tty, USB path and hwmon dir.
- `sensors` and `sensors --json` cover all devices. `find_hwmon_path` becomes a list (`wireviewctl.c:1338-1385`), matched by the `serial` attribute.
- `info`, `screen`, `nvm`, `write-config` and friends send `SELECT_DEVICE` first.
- With more than one device and no `--device`, read commands use the primary and write commands refuse with a list of candidates.
- `flash`:
  - require `--device` when several are attached;
  - take the USB path from LIST;
  - run `dfu-util -p <path>`;
  - replace `dfu_device_present()` (`:1197-1207`) with a path-specific check;
  - never treat an unrelated DFU device as the target (`:1264`).

**H5. Docs and packaging.**
- README: multi-device section, sensors/fan-control naming, `--device`.
- Changelogs for deb, rpm, AUR and nix.
- No udev change.

### 3.2 wireview-linux (target 1.5.0.0)

**A1. Identity helpers, no behaviour change.**
- `Stm32PortFinder` returns records of port, USB serial and USB path; the walk at `Stm32PortFinder.cs:113-133` already reaches the right sysfs dir.
- Add `UsbSerialFromUid(uid)` and `FindPortForUid(uid)`.

**A2. Remove every "first port" (shippable before any hwmon change).**
- Resolve the port by the target device's UID in `DirectSerialSession.cs:49`, `DeviceViewModel.cs:769`, `MiniService.cs:424` and `ComPortLogger.cs:16-19`.
- `DfuUtilFlasher.WaitForDfuDeviceAsync` and `FlashAsync` take the USB path and pass `-p` (`DfuUtilFlasher.cs:57-59, 81`).
- Refuse to flash when the target path is unknown and more than one 0483:5740 or 0483:df11 device is attached.
- The Windows flasher is out of scope; refuse there when more than one device is attached.

**A3. hwmon and daemon multi-instance (needs hwmon 1.9.0 to do anything).**
- `FindHwmonPaths()` returns all instances.
- One `HwmonDevice` per instance. The source is `hwmon:<uid>` when the `uid` attribute exists, else the legacy `"hwmon"`.
- Each `HwmonDevice` sends `SELECT_DEVICE` after connecting (`HwmonDevice.cs:126-177`) and again after every socket renewal (`:201-213, 260-266`).
- With a legacy daemon, behaviour is unchanged.
- `DeviceManager.cs:115-116` stays: a 1.9.0 daemon owns every supported port.

**A4. Stable keys and remembered selection.**
- `ManagedDevice` gains `Uid`.
- New `AppSettings.SelectedDeviceUid`.
- `DeviceManager.cs:325` prefers the saved UID over first-come.
- `RestoreDeviceSelectionAsync` restores by UID (`DeviceViewModel.cs:790-813`).

**A5. Per-device settings.**
- Add `Devices: { "<UID>": { Nickname, ScreenAfterConnection? } }` to `settings.json`, registered in both source-generated contexts (`AppSettings.cs:309-336`).
- No migration: an absent map means global defaults.
- Check that Mini's settings writer round-trips the new section.
- The CSV log gets a per-device file suffix when more than one local device is logged.

**A6. GUI multi-device behaviour.**
- Per-device `FaultMonitor`s driven from `DeviceManager.DataUpdated`, replacing the single one at `ConnectionStatusViewModel.cs:22`. The shutdown-on-fault and Mini-defers logic stays (`:221-231`).
- Tray menu and picker labels as in section 4.
- `--device <uid|serial|nickname>` next to `--page` (`App.axaml.cs:590-596`).

**A7. Mini multi-device.**
- `PreferredLocal(uid?)`.
- Every `/api/*` route accepts `?device=<UID>` (`ControlPanel.cs:117-142`).
- `/api/status` returns a `devices[]` list.
- One history buffer per device, allocated on first reading (about 348 KB per device at 60 min, per the log).
- Flash, theme and log services take a target UID and stay mutually exclusive globally.
- `_historyDevice` clearing (`MiniService.cs:509-513`) goes away.
- `GetStatus()` aggregates (`:341-356`).
- Verify with the AOT binary from `packaging/build-mini.sh`.

**A8. udev and docs.**
- Resolve the `wireview-pro2` symlink (open question 4).
- Update `docs/usage.md`, `docs/wireview-mini.md` and `docs/firmware.md`.

### 3.3 Order and compatibility

Release order: H0, then H1 to H5 together as 1.9.0, then app 1.5.0.0. A1, A2, A4 and A5 have no hwmon dependency and can ship first.

| Combination | Result |
|---|---|
| Old daemon + new module | Unbound writes feed index 0; identical to today |
| New daemon + old module | Index 0 in hwmon only; all devices over socket and HTTP; one warning. Covers "package upgraded, module not reloaded" |
| Old app + new daemon | Sees the primary device via unselected socket commands and the first `wireview` hwmon dir, so it may pair an extra hwmon dir with primary identity. Mitigate with a `Breaks:`/`Conflicts:` on wireview-linux < 1.5 in the hwmon packages, or accept and document |
| New app + old daemon | Legacy single-device path |
| Existing single-device users | Same hwmon path, name, chip address, socket path, `/sensors` shape, `/metrics` labels and `settings.json` |
| Remote consumers | `/sensors` may now list several devices. The app's `RemoteDeviceProbe` and `wireviewctl top` already iterate; third-party scrapers reading `devices[0]` see the primary first (sort by index) |

## 4. UI/UX

**Desktop app.**
- Keep one window with the existing picker; the view models are built around one selected device (`DeviceAutoConnector.cs`).
- Picker label: `<nickname or edition> · <last 4 of serial> (<transport>)`. The transport is shown only when the same UID appears through two transports.
- Nickname is editable on the Device page.
- Remember the selection by UID.
- Fault banner: when an unselected device faults, show "Fault on <label>" with a "Show" action that switches the picker.
- The app theme follows the selected device's edition, as it does today (`ConnectionStatusViewModel.cs:201`).

**Desktop tray.**
- One icon.
- Number on the icon: selected device's watts (unchanged), with a new setting "Tray shows: Selected device / Sum of all devices".
- Menu: one row per local device with watts and a check mark on the selected one; clicking selects.
- Tooltip: "WireView - 2 devices - 512 W".
- Rebuild the device rows only on `DevicesChanged`, using the existing change-only throttle, because of the Avalonia 12 menu re-publish flicker.

**Mini.**
- One tray icon with the same aggregate rule; a fault on any device turns it to the fault state and names the device.
- Control panel: a device selector at the top, hidden with one device; the choice is kept in the URL fragment.
- Charts, config, theme, firmware and device log follow the selector.
- The status strip always lists every device with watts and fault state.

**Flashing (both apps and wireviewctl).**
- The target is always the explicitly selected device, never "preferred".
- The confirmation names it: label, serial, firmware build and USB port, for example "WireView Pro II Noctua Edition · 4E46, USB port 3-4".
- The picker is locked for the duration.
- Refuse if the device at that USB path changed between confirmation and the bootloader command.
- One flash at a time system-wide.

**Per-device display theme.** Theme editor state already lives on the device, so it follows the picker with no extra storage.

No UI string above uses an em dash, and no unreleased product is named. Keep the "is not supported yet" log strings out of new public text.

## 5. Risks, open questions, testing

### Risks

- **Kernel lifetime bugs** in dynamic instance create/destroy against concurrent sysfs reads. Mitigate with a refcounted priv and a VM test under KASAN/lockdep.
- **Handover cross-talk.** A device-log read on device A must suspend only A and open only A's tty. This is the worst case for the interference trap. A2 and the per-device suspend in H3 must land before two devices are driven through the daemon.
- **Flashing the wrong unit.** Mitigated by path targeting and the refuse-if-ambiguous rule.
- **Sequential polling.** A stalled device costs the others up to 1 s (`read_exact` timeout at `wireviewd.c:540`) before it is dropped. Acceptable at the default 1000 ms interval, not at `-i 100`.
- **Eviction churn.** If `MAX_CLIENTS` is not raised, two apps times N devices exhaust 8 slots.
- **Unverified assumptions.** Check each before coding against it:
  - the `wireview_hwmon.N` to `wireview-isa-000N` chip naming;
  - the `dfu-util -p` path format matching sysfs names (read-only check: `dfu-util -l` prints `path=`);
  - the UID-to-serial formula on the second unit;
  - how CoolerControl keys hwmon devices.

### Open questions for the maintainer

1. **Index policy.** Persist the serial-to-index map in `/var/lib/wireview` (recommended), or sort by serial with no state, where adding a lower-serial device renumbers the others?
2. **Primary on this machine.** Which unit should be index 0 on first run: the Noctua on 3-4 that is index 0 today, or do you want a `wireviewctl devices set-index`?
3. **Nicknames.** Per-user in `settings.json` (as planned), or system-wide in the daemon so the hwmon `label` and `/sensors` carry them?
4. **`/dev/wireview-pro2`.** Drop it, or rename it per device? `/dev/serial/by-id` already does the job.
5. **Tray default.** Selected device (no behaviour change) or the sum?
6. **Old app with new daemon.** Add `Breaks:` to the packages, or accept the mismatch?
7. **Interim direct serial.** Before hwmon 1.9.0 ships, should app 1.5 open the ports a legacy daemon does not own? This is possible via the UID-to-serial mapping and the daemon's TIOCEXCL, but it muddies Mini's daemon mode (`MiniService.cs:564-572`). I recommend no.
8. **Tests.** wireview-linux has no test project in the tree, although `DeviceManager` has test hooks (`DeviceManager.cs:60-64, 165`). Add one for A1 to A5, or does the harness live elsewhere?
9. **Second unit.** Which edition is `205B38A53245`? It has never been probed, so I do not know.

### Testing

| Step | Headless / container | On this machine (two real units) |
|---|---|---|
| H0 | Unit test `scan_devices` against a fake sysfs tree (add a sysfs-root override like the existing `-D` test defines in `tests/Makefile`); e2e with two `fake_device.py` ptys, the first one answering as unsupported | `journalctl -u wireviewd` shows both candidates considered |
| H1 | Extend `nix/test.nix` (VM): load the module, bind index 0 and 1, read `name`/`serial`/`label`, close an fd and check the instance is gone, legacy unbound write, `rmmod` under load | Maintainer runs `systemctl stop wireviewd`, reloads the module, restarts. Then read-only: `grep . /sys/class/hwmon/*/name`, `ls /sys/devices/platform/ \| grep wireview`, `sensors` |
| H2 | Existing `make -C tests` unchanged and green under ASan/UBSan | None needed |
| H3 | e2e with two fake devices (FIFO as hwmon exercises the `ENOTTY` path): LIST/SELECT, per-device suspend, unplug of one, `/sensors`, `/metrics`, `/config?id=`, `POST /command` with and without `deviceId`, state-file persistence | `curl localhost:9876/sensors` shows two ids matching both by-id serials; unplug and replug one and check its index and hwmon address are kept; check that an installed 1.4.1.0 app still shows the primary |
| H4 | `test_wireviewctl.c` with `WIREVIEW_HWMON_PATH`-style fake trees and argument parsing; flash dry path with a fake `dfu-util` | `wireviewctl devices`, `wireviewctl --device 3245 info`, `wireviewctl sensors`; flash one unit and confirm via `dfu-util -l` that only path 3-6 entered DFU while the other kept reporting |
| A1, A2 | Helper tests on a fake sysfs tree; flasher against `WIREVIEW_DFU_UTIL` (`DfuUtilFlasher.cs:23`) checking `-p` | With the daemon stopped by the maintainer, the GUI lists both serial devices; a log read on each hits the right unit |
| A3 to A5 | Fake hwmon dirs plus a fake daemon socket (`HwmonDevice.cs:37-38`), settings round-trip, selection restore | The GUI picker shows both through the daemon; restart keeps the selection |
| A6 | View-model tests for per-device fault routing | Maintainer launches the GUI (not possible from the sandbox): tray rows, fault banner for the unselected unit, flash confirmation text |
| A7 | AOT `wireview-mini` in a container with fake daemon and hwmon: `--status`, `/api/status?device=`, history per device, single-instance lock | Panel at `http://127.0.0.1:9877` with the selector; tray aggregate |

Release gate: all three compatibility combinations in 3.3 run on this machine before tagging 1.9.0.

### Critical Files for Implementation

- wireview-hwmon/wireviewd.c
- wireview-hwmon/wireview_hwmon.c
- wireview-hwmon/wireviewctl.c
- WireViewDeviceLib/WireViewDeviceLib/Device/DeviceManager.cs
- WireViewDeviceLib/WireViewDeviceLib/Device/HwmonDevice.cs

Also touched heavily:

- WireViewMini/MiniService.cs
- WireViewMini/ControlPanel.cs
- WireViewDeviceLib/WireViewDeviceLib/Device/Stm32PortFinder.cs
- WireViewDeviceLib/WireViewDeviceLib/Device/DirectSerialSession.cs
- WireViewDeviceLib/WireViewDeviceLib/Device/DfuUtilFlasher.cs
- WireViewNet/Services/AppSettings.cs
- WireView2/ViewModels/ConnectionStatusViewModel.cs
- WireView2/App.axaml.cs
- udev/99-wireview.rules
