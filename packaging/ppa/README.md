# Ubuntu PPA packaging

wireview-linux is published to the Launchpad PPA
[`ppa:sparvoli/wireview-hwmon`](https://launchpad.net/~sparvoli/+archive/ubuntu/wireview-hwmon),
which also carries wireview-hwmon. Users install it with:

```bash
sudo add-apt-repository ppa:sparvoli/wireview-hwmon
sudo apt install wireview-linux
```

## How the package is built

Launchpad builders have no .NET SDK and no network, so the source package ships
the pre-built self-contained publish (`linux-x64/`) and `debian/rules` has no
build step (format `3.0 (native)`, `Architecture: amd64`, no strip, no
shlibdeps). `build-ppa-source.sh` takes the payload straight out of the GitHub
release's `wireview-linux_<version>_amd64.deb`, so the PPA installs exactly the
bits of the release.

| File | Purpose |
|------|---------|
| `debian/` | Packaging template. `debian/changelog` holds the PPA history; its top entry is the release being uploaded. |
| `build-ppa-source.sh` | Fetches the release .deb and builds one source package per series into `build/` (git-ignored). |
| `lp-builds.py` | Shows the Launchpad builds of a version; `--wait` polls until they finish, `--retry` retries a failed build that has no log. |

## Series and version scheme

One source upload per Ubuntu series, with the series in the version:

| Series | Ubuntu | Version |
|--------|--------|---------|
| `resolute` | 26.04 | `<version>~resolute1` |
| `noble` | 24.04 | `<version>~noble1` |

The top `debian/changelog` entry picks the series. The committed changelog is
written for resolute; the script rewrites only the top header for noble. If a
series needs a re-upload of the same version, bump the suffix (`~resolute2`).
Add a new series to `SERIES_LIST` in `build-ppa-source.sh` and `SERIES` in
`lp-builds.py`.

## The GitHub release .deb

`packaging/build-deb.sh` builds `wireview-linux_<version>_amd64.deb` for the
GitHub release from this `debian/` template, so the release .deb and the PPA
packages share their metadata:

```bash
packaging/build-deb.sh <version> dist/     # needs dotnet and docker
```

Run it, like the other release builds, from a clean checkout of the release
tag: the app shows `git describe` as its version, so a build from any other
state shows a commit suffix instead of the plain version.

It publishes the app like `rpm/build-srpm.sh` (the loose self-contained
linux-x64 publish with the single-file `WireView2` overlaid as the launcher, no
.pdb), assembles the same tree as the PPA source package (desktop entry, icons
and udev rule from the repo), rewrites the top changelog header to
`wireview-linux (<version>) unstable;` in its build copy, and runs
`dpkg-buildpackage -b` in an `ubuntu:24.04` container (`DEB_BUILDER=host` builds
on the host instead). The members are xz-compressed: every dpkg that may get
the .deb (Ubuntu 24.04 and later, Debian) reads xz, while zstd needs dpkg
1.21.18 or later. Up to 1.2.5.0 the release .deb was a hand repack of the
previous one; the scripted build has the same file list, with 0755
directories and 0644 libraries.

## Release steps

1. Add the changelog entry at the top of `packaging/ppa/debian/changelog`
   (`dch` or by hand), with the current UTC date (`date -uR`):

   ```
   wireview-linux (<version>~resolute1) resolute; urgency=medium

     * ...

    -- Emanuele Sparvoli <sparvoli@gmail.com>  <date -uR>
   ```

   Keep `debian/control` in line with what the release ships (Recommends).

   Build the release .deb (`packaging/build-deb.sh <version> dist/`) and attach
   it to the GitHub release. The rest of these steps run once that release is
   live.

2. Build both source packages:

   ```bash
   packaging/ppa/build-ppa-source.sh <version>
   ```

   Optional check, the Launchpad build in a container (should give the same
   files as the release .deb):

   ```bash
   dpkg-source -x packaging/ppa/build/noble/*.dsc /tmp/wv-src
   docker run --rm -v /tmp/wv-src:/src ubuntu:24.04 bash -c \
     'apt-get update -qq && apt-get install -y -qq debhelper && cd /src && dpkg-buildpackage -b -us -uc'
   ```

3. Sign (key `0E12EEBBC7B9A54D`, Emanuele Sparvoli) and upload each series:

   ```bash
   cd packaging/ppa/build
   debsign -k0E12EEBBC7B9A54D resolute/*_source.changes noble/*_source.changes
   dput ppa:sparvoli/wireview-hwmon resolute/*_source.changes
   dput ppa:sparvoli/wireview-hwmon noble/*_source.changes
   ```

4. Wait for the builds (Launchpad takes a few minutes to accept the upload;
   a rejection arrives by email and the version never shows up):

   ```bash
   /usr/bin/python3 packaging/ppa/lp-builds.py <version> --wait --retry
   ```

   Needs launchpadlib credentials at `~/.config/launchpadlib-wireview.creds`.
   Use `/usr/bin/python3`: a Homebrew python3 has no launchpadlib.

5. Commit the changelog entry.
