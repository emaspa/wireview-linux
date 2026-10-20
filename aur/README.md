# AUR packaging (`wireview-linux-bin`)

A binary AUR package that installs the pre-built release tarball - no `dotnet-sdk`
build dependency and no compile step, so it installs in seconds.

It installs the app as `wireview-linux` and WireView Mini as `wireview-mini`
(both in `/usr/lib/wireview-linux/`, linked from `/usr/bin`).

It `provides`/`conflicts` `wireview-linux`, so it coexists with the community
source-built [`wireview-linux`](https://aur.archlinux.org/packages/wireview-linux)
package (maintained separately); users pick whichever they prefer.

## Files

| File | Purpose |
|------|---------|
| `PKGBUILD` | Package recipe (pulls the GitHub release tarball, sha256-pinned). |
| `.SRCINFO` | Generated metadata (`makepkg --printsrcinfo`). Must be regenerated on every change. |
| `wireview-linux.desktop`, `wireview-linux.png` | Desktop entry and icon, shipped in the AUR repo. |

## Publish (first time, done for 1.0.6.0; kept for reference)

Requires an [AUR account](https://aur.archlinux.org/) with your SSH public key
registered.

```bash
git clone ssh://aur@aur.archlinux.org/wireview-linux-bin.git
cp PKGBUILD .SRCINFO wireview-linux.desktop wireview-linux.png wireview-linux-bin/
cd wireview-linux-bin
git add PKGBUILD .SRCINFO wireview-linux.desktop wireview-linux.png
git commit -m "Initial import: wireview-linux-bin 1.0.6.0"
git push
```

## Update for a new release

`pkgver` (with `pkgrel=1`) is bumped in the version commit, and
`packaging/release.sh <version>` pins the tarball's sha256 in `PKGBUILD`,
regenerates `.SRCINFO` and checks `makepkg` in an Arch container. Once the
release is live, from the repository root:

```bash
git clone ssh://aur@aur.archlinux.org/wireview-linux-bin.git /tmp/aur-wv
cp aur/{PKGBUILD,.SRCINFO,wireview-linux.desktop,wireview-linux.png} /tmp/aur-wv/
cd /tmp/aur-wv
makepkg -f    # downloads the release tarball and checks the pinned sha256
git add -A && git commit -m "Update to <version>" && git push origin HEAD:master
```

## Build / test locally

```bash
makepkg -si          # build and install
namcap PKGBUILD      # optional lint
namcap *.pkg.tar.zst
```

Tested with `makepkg` + `pacman -U` on Arch (also applies to Arch-based distros
such as CachyOS and EndeavourOS).
