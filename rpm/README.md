# RPM / COPR packaging

Fedora (and Fedora-based, non-immutable) packaging for `wireview-linux`, served
through a [COPR](https://copr.fedorainfracloud.org/) repository.

Like the Debian/PPA packaging, the source tarball ships a **pre-built,
self-contained .NET 8 binary** and the RPM performs no compilation. This is
because COPR/mock build chroots have neither the .NET SDK nor network access for
a NuGet restore. The trade-off: the SRPM is large (~80 MB) and is x86_64-only.

## Files

| File | Purpose |
|------|---------|
| `wireview-linux.spec` | The RPM spec (installs the pre-built bundle + desktop file + icons + udev rule). |
| `build-srpm.sh` | Publishes the app, assembles the source tarball, and builds the `.src.rpm`. |
| `../packaging/` | Shared static assets (desktop entry, hicolor icons) used by this and other packaging. |

## Build the SRPM locally

Requires `dotnet` (8.0 SDK) and `rpmbuild` (`sudo dnf install rpm-build`, or
`sudo apt install rpm` on Debian/Ubuntu) on PATH:

```bash
rpm/build-srpm.sh            # version is read from WireView2.csproj
# or: rpm/build-srpm.sh 1.2.0.0
```

The resulting `.src.rpm` lands in `rpm/build/SRPMS/` (this directory is a build
artifact and is git-ignored).

### Build in Docker (no Fedora host needed)

Publish the app with the host `dotnet`, then build the RPM inside a Fedora
container. This is how the package is validated on non-Fedora machines:

```bash
# 1. publish + assemble the source tree (host has the .NET SDK), then
#    drop it under a staging dir laid out as an rpmbuild tree (see build-srpm.sh)
# 2. build inside Fedora:
docker run --rm -v "$PWD/rpm/build:/work" fedora:42 bash -c '
  dnf -y install rpm-build &&
  rpmbuild -ba --define "_topdir /work" /work/SPECS/wireview-linux.spec'
```

A clean-room install check in a fresh container:

```bash
docker run --rm -v "$PWD/rpm/build:/work" fedora:42 \
  dnf -y install /work/RPMS/x86_64/wireview-linux-*.rpm
```

## Publish to COPR

One-time setup:

1. Create the project at <https://copr.fedorainfracloud.org/> (e.g.
   `emaspa/wireview-linux`), enabling the `fedora-*-x86_64` chroots you want.
2. `sudo dnf install copr-cli` and drop your API token in `~/.config/copr`
   (from the COPR web UI, *API* page).

Each release:

```bash
rpm/build-srpm.sh
copr-cli build emaspa/wireview-linux rpm/build/SRPMS/wireview-linux-*.src.rpm
```

COPR rebuilds the binary RPM for every enabled Fedora release.

## What users run

```bash
sudo dnf copr enable emaspa/wireview-linux
sudo dnf install wireview-linux
```

After install, the udev rule is in place; add yourself to the serial group and
re-login:

```bash
sudo usermod -aG dialout "$USER"
```

## Notes

- **Bazzite / immutable Fedora** (rpm-ostree) is *not* the target here - layering
  RPMs is discouraged on those. A Flatpak is the idiomatic path for them; this
  COPR package targets Fedora Workstation/Server and Silverblue toolbox/distrobox.
- The hwmon kernel module (`wireview-hwmon`) is a separate concern and is only a
  weak dependency (`Recommends`). The GUI works fully over direct USB serial
  without it.
