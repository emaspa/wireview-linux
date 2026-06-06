# AUR packaging (`wireview-linux-bin`)

A binary AUR package that installs the pre-built release tarball — no `dotnet-sdk`
build dependency and no compile step, so it installs in seconds.

It `provides`/`conflicts` `wireview-linux`, so it coexists with the community
source-built [`wireview-linux`](https://aur.archlinux.org/packages/wireview-linux)
package (maintained separately); users pick whichever they prefer.

## Files

| File | Purpose |
|------|---------|
| `PKGBUILD` | Package recipe (pulls the GitHub release tarball, sha256-pinned). |
| `.SRCINFO` | Generated metadata (`makepkg --printsrcinfo`). Must be regenerated on every change. |
| `wireview-linux.desktop`, `wireview-linux.png` | Desktop entry and icon, shipped in the AUR repo. |

## Publish (first time)

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

```bash
# 1. bump pkgver (and reset pkgrel=1) in PKGBUILD
# 2. refresh checksums from the new release tarball
updpkgsums
# 3. regenerate metadata
makepkg --printsrcinfo > .SRCINFO
# 4. verify it still builds
makepkg -f
# 5. commit + push to the AUR repo
```

## Build / test locally

```bash
makepkg -si          # build and install
namcap PKGBUILD      # optional lint
namcap *.pkg.tar.zst
```

Tested with `makepkg` + `pacman -U` on Arch (also applies to Arch-based distros
such as CachyOS and EndeavourOS).
