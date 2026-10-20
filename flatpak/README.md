# Flatpak packaging

Flatpak build for `wireview-linux`, primarily for immutable / atomic distros
(Bazzite, Silverblue, Kinoite) where layering RPMs is discouraged. It also works
on any distro with Flatpak.

Like the other packages, the manifest installs the **pre-built self-contained
binary** from the GitHub release tarball - no compilation in the Flatpak builder.

## Files

| File | Purpose |
|------|---------|
| `io.github.emaspa.WireViewLinux.yaml` | Flatpak manifest (pins the release tarball by sha256). |
| `io.github.emaspa.WireViewLinux.desktop` | Desktop entry. |
| `io.github.emaspa.WireViewLinux.metainfo.xml` | AppStream metadata. |

Icons are pulled from `../packaging/icons`.

## Build & install locally

Requires `flatpak` and `flatpak-builder`, plus the freedesktop runtime/SDK:

```bash
flatpak install -y flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08

flatpak-builder --user --install --force-clean \
  build-dir flatpak/io.github.emaspa.WireViewLinux.yaml

flatpak run io.github.emaspa.WireViewLinux
```

Produce a single distributable bundle (attach to a GitHub release):

```bash
flatpak-builder --repo=repo --force-clean build-dir \
  flatpak/io.github.emaspa.WireViewLinux.yaml
flatpak build-bundle repo wireview-linux-<version>-x86_64.flatpak io.github.emaspa.WireViewLinux
# users then: flatpak install ./wireview-linux-<version>-x86_64.flatpak
```

## Important: USB serial access

A Flatpak **cannot** install the udev rule (the sandbox can't write
`/etc/udev/rules.d`). Install it on the host once so the device node is
accessible:

```bash
sudo curl -fsSL \
  https://raw.githubusercontent.com/emaspa/wireview-linux/main/udev/99-wireview.rules \
  -o /etc/udev/rules.d/99-wireview.rules
# Arch-based distros have no dialout group, their serial group is uucp
getent group dialout >/dev/null || sudo sed -i 's/GROUP="dialout"/GROUP="uucp"/g' /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

The rule makes the device node `0660` in the distro's serial group (`dialout`,
or `uucp` on Arch-based distros) and gives the user at the
local seat a logind `uaccess` ACL, so no group membership is needed for a
desktop session. Over SSH, join the serial group instead and log in again:

```bash
# Debian, Ubuntu, Fedora and most other distros
sudo usermod -aG dialout "$USER"

# Arch-based distros
sudo usermod -aG uucp "$USER"
```

The Flatpak requests `--device=all` because Flatpak has no finer-grained tty
filter.

## Notes

- The Flatpak carries the app only. **WireView Mini** is left out: the manifest
  gives the sandbox no network access (no `--share=network`), so the browser
  could not reach its control panel on `127.0.0.1`, and `wireview-mini --install`
  would write its login entry inside the sandbox's own config directory. The
  tarball's `wireview-mini` runs on the host instead.
- Only **direct USB serial** mode is supported under Flatpak. The hwmon path
  needs the `wireview-hwmon` kernel module on the host, which is out of scope on
  immutable distros.
- The manifest's tarball URL is bumped in the version commit.
  `packaging/release.sh <version>` builds the bundle from a copy of the
  manifest whose tarball source is the local tarball (same `sha256`) and pins
  that `sha256` in the manifest; commit the pin once the release is live.
  Both give the same app files.
- Publishing to **Flathub** additionally requires submitting this manifest to
  the flathub repo; expect review of the `--device=all` permission.
