# Flatpak packaging

Flatpak build for `wireview-linux`, primarily for immutable / atomic distros
(Bazzite, Silverblue, Kinoite) where layering RPMs is discouraged. It also works
on any distro with Flatpak.

Like the other packages, the manifest installs the **pre-built self-contained
binary** from the GitHub release tarball — no compilation in the Flatpak builder.

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
flatpak build-bundle repo wireview-linux.flatpak io.github.emaspa.WireViewLinux
# users then: flatpak install ./wireview-linux.flatpak
```

## Important: USB serial access

A Flatpak **cannot** install the udev rule (the sandbox can't write
`/etc/udev/rules.d`). Install it on the host once so the device node is
accessible:

```bash
sudo curl -fsSL \
  https://raw.githubusercontent.com/emaspa/wireview-linux/main/udev/99-wireview.rules \
  -o /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

The rule grants access via `MODE=0666` + a logind `uaccess` ACL, so no group
membership is needed. The Flatpak requests `--device=all` because Flatpak has no
finer-grained tty filter.

## Notes

- Only **direct USB serial** mode is supported under Flatpak. The hwmon path
  needs the `wireview-hwmon` kernel module on the host, which is out of scope on
  immutable distros.
- The version and `sha256` in the manifest must be bumped to match each new
  release tarball.
- Publishing to **Flathub** additionally requires submitting this manifest to
  the flathub repo; expect review of the `--device=all` permission.
