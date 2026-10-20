#!/usr/bin/env bash
#
# Build a source RPM (.src.rpm) for wireview-linux.
#
# Like the Debian/PPA flow, this ships a *pre-built* self-contained .NET binary
# inside the source tarball: the RPM itself performs no compilation, so it builds
# in a COPR/mock chroot that has neither the .NET SDK nor network access.
# WireView Mini's NativeAOT wireview-mini (packaging/build-mini.sh) goes in the
# same directory as the app; it reads libSystem.IO.Ports.Native.so,
# TG-WV-PRO2-FW.hex and ext_flash.bin from there, the same files the app's
# publish has.
#
# Requirements: dotnet (8.0 SDK) and rpmbuild on PATH, docker for
#               packaging/build-mini.sh.
# Usage:        rpm/build-srpm.sh [version]
# Env:          MINI_DIR=<dir>   reuse a packaging/build-mini.sh output
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

VERSION="${1:-$(grep -oP '<Version>\K[^<]+' "$REPO/WireView2/WireView2.csproj")}"
NAME="wireview-linux"
echo ">> Building SRPM for $NAME $VERSION"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
SRC="$WORK/$NAME-$VERSION"
mkdir -p "$SRC/linux-x64" "$SRC/icons" "$SRC/udev"

echo ">> dotnet publish (loose + single-file)"
dotnet publish "$REPO/WireView2/WireView2.csproj" -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=false -o "$WORK/loose"  >/dev/null
dotnet publish "$REPO/WireView2/WireView2.csproj" -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true        -o "$WORK/single" >/dev/null

# Loose payload (DLLs, native libs, runtimeconfig) with the bundled launcher overlaid.
cp -r "$WORK/loose/." "$SRC/linux-x64/"
find "$SRC/linux-x64" -name '*.pdb' -delete
cp "$WORK/single/WireView2" "$SRC/linux-x64/WireView2"
chmod +x "$SRC/linux-x64/WireView2"

echo ">> WireView Mini"
MINI="${MINI_DIR:-$WORK/mini}"
[[ -n "${MINI_DIR:-}" ]] || "$REPO/packaging/build-mini.sh" "$MINI" >/dev/null
install -m0755 "$MINI/wireview-mini" "$SRC/linux-x64/wireview-mini"
for f in libSystem.IO.Ports.Native.so TG-WV-PRO2-FW.hex ext_flash.bin; do
    cmp "$MINI/$f" "$SRC/linux-x64/$f" || { echo "!! $f differs between the app and WireView Mini" >&2; exit 1; }
done

# Static packaging assets from the repo.
cp "$REPO/packaging/wireview-linux.desktop" "$SRC/wireview-linux.desktop"
cp "$REPO/packaging/wireview-mini.desktop"  "$SRC/wireview-mini.desktop"
cp -r "$REPO/packaging/icons/hicolor"       "$SRC/icons/hicolor"
cp "$REPO/udev/99-wireview.rules"           "$SRC/udev/99-wireview.rules"

# rpmbuild tree
TOP="$REPO/rpm/build"
rm -rf "$TOP"
mkdir -p "$TOP"/{SOURCES,SPECS,SRPMS,RPMS,BUILD,BUILDROOT}

echo ">> Creating source tarball"
tar -C "$WORK" -czf "$TOP/SOURCES/$NAME-$VERSION-linux-x64.tar.gz" "$NAME-$VERSION"

# Spec with the version pinned to this build.
sed "s/^Version:.*/Version:        $VERSION/" "$REPO/rpm/wireview-linux.spec" \
    > "$TOP/SPECS/wireview-linux.spec"

echo ">> rpmbuild -bs"
rpmbuild -bs --define "_topdir $TOP" "$TOP/SPECS/wireview-linux.spec"

echo
echo ">> SRPM ready:"
ls -1 "$TOP"/SRPMS/*.src.rpm
echo
echo "Upload to COPR with:"
echo "  copr-cli build emaspa/wireview-linux $TOP/SRPMS/$NAME-$VERSION-1.*.src.rpm"
