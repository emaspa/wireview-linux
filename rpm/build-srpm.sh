#!/usr/bin/env bash
#
# Build a source RPM (.src.rpm) for wireview-linux.
#
# Like the Debian/PPA flow, this ships a *pre-built* self-contained .NET binary
# inside the source tarball — the RPM itself performs no compilation, so it builds
# in a COPR/mock chroot that has neither the .NET SDK nor network access.
#
# Requirements: dotnet (8.0 SDK) and rpmbuild on PATH.
# Usage:        rpm/build-srpm.sh [version]
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

# Static packaging assets from the repo.
cp "$REPO/packaging/wireview-linux.desktop" "$SRC/wireview-linux.desktop"
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
echo "  copr-cli build sparvoli/wireview-linux $TOP/SRPMS/$NAME-$VERSION-1.*.src.rpm"
