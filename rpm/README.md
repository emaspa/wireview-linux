# RPM / COPR packaging

Fedora (and Fedora-based, non-immutable) packaging for `wireview-linux`, served
through a [COPR](https://copr.fedorainfracloud.org/) repository.

Like the Debian/PPA packaging, the source tarball ships a **pre-built,
self-contained .NET 8 binary** and the RPM performs no compilation. This is
because COPR/mock build chroots have neither the .NET SDK nor network access for
a NuGet restore. The trade-off: the SRPM is large (~90 MB) and is x86_64-only.

## Files

| File | Purpose |
|------|---------|
| `wireview-linux.spec` | The RPM spec (installs the pre-built app and WireView Mini, both desktop entries, icons and the udev rule). |
| `build-srpm.sh` | Publishes the app, builds WireView Mini, assembles the source tarball, and builds the `.src.rpm`. |
| `../packaging/` | Shared static assets (desktop entries, hicolor icons) used by this and other packaging. |

## Build the SRPM locally

Requires `dotnet` (8.0 SDK) and `rpmbuild` (`sudo dnf install rpm-build`, or
`sudo apt install rpm` on Debian/Ubuntu) on PATH, and docker (WireView Mini is
built in a container by `packaging/build-mini.sh`):

```bash
rpm/build-srpm.sh            # version is read from WireView2.csproj
# or: rpm/build-srpm.sh 1.4.1.0
```

The resulting `.src.rpm` lands in `rpm/build/SRPMS/` (this directory is a build
artifact and is git-ignored).

### Build in Docker (no Fedora host needed)

Build the SRPM on the host, then the binary RPM inside a Fedora container.
`packaging/release.sh` does the same for a release:

```bash
rpm/build-srpm.sh    # leaves rpm/build/ as an rpmbuild tree (SOURCES, SPECS)
docker run --rm -v "$PWD/rpm/build:/work" fedora:44 bash -c "
  dnf -y install rpm-build &&
  rpmbuild -ba --define '_topdir /work' /work/SPECS/wireview-linux.spec &&
  chown -R $(id -u):$(id -g) /work"
```

The `chown` gives the files back to you, so the next `build-srpm.sh` can clear
`rpm/build/`.

A clean-room install check in a fresh container:

```bash
docker run --rm -v "$PWD/rpm/build:/work" fedora:44 \
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
copr-cli build emaspa/wireview-linux dist/wireview-linux-<version>-1.src.rpm   # from packaging/release.sh <version>
```

Upload the SRPM `packaging/release.sh` built and tested rather than a fresh
`rpm/build-srpm.sh` one: a rebuild gives different checksums.

COPR rebuilds the binary RPM for every enabled Fedora release.

## What users run

```bash
sudo dnf copr enable emaspa/wireview-linux
sudo dnf install wireview-linux
```

The package installs the app as `wireview-linux` and WireView Mini as
`wireview-mini` (both in `/usr/lib/wireview-linux/`, linked from `/usr/bin`);
`wireview-mini --install` starts WireView Mini at login.

After install, the udev rule is in place: the serial port and the DFU bootloader
are `0660 root:dialout` plus a `uaccess` ACL for the user logged in at the local
seat, so a desktop session needs no group. SSH and other remote sessions get no
seat ACL; join `dialout` and log in again:

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
