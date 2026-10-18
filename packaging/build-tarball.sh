#!/usr/bin/env bash
#
# Build the self-contained precompiled tarball for a GitHub release.
#
# The tarball extracts to a single directory containing the app binary
# (WireView2), WireView Mini (wireview-mini with libSystem.IO.Ports.Native.so,
# TG-WV-PRO2-FW.hex and ext_flash.bin next to it, where it looks for them), the
# udev rule, the desktop entries of both with the app's 128x128 icon, and a
# one-time install.sh, so the precompiled path needs no extra downloads to get
# USB serial access and launcher entries working. The AUR package installs from
# this tarball.
#
# Requires: dotnet (8.0 SDK) on PATH, docker for packaging/build-mini.sh.
# Usage:    packaging/build-tarball.sh [version] [output-dir]
# Env:      MINI_DIR=<dir>   reuse a packaging/build-mini.sh output instead of
#                            building WireView Mini again
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

VERSION="${1:-$(grep -oP '<Version>\K[^<]+' "$REPO/WireView2/WireView2.csproj")}"
OUTDIR="${2:-$REPO/dist}"
NAME="wireview-linux-$VERSION-linux-x64"

echo ">> Building tarball for $NAME"
mkdir -p "$OUTDIR"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/$NAME"
mkdir -p "$STAGE"

echo ">> dotnet publish (single-file, self-contained)"
dotnet publish "$REPO/WireView2/WireView2.csproj" -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true -o "$WORK/single" >/dev/null

MINI="${MINI_DIR:-$WORK/mini}"
[[ -n "${MINI_DIR:-}" ]] || "$HERE/build-mini.sh" "$MINI" >/dev/null

install -m0755 "$WORK/single/WireView2"          "$STAGE/WireView2"
install -m0755 "$MINI/wireview-mini"             "$STAGE/wireview-mini"
for f in libSystem.IO.Ports.Native.so TG-WV-PRO2-FW.hex ext_flash.bin; do
    install -m0644 "$MINI/$f"                    "$STAGE/$f"
done
install -m0644 "$REPO/udev/99-wireview.rules"    "$STAGE/99-wireview.rules"
# install.sh writes per-user launcher entries from these, pointing at this
# directory.
install -m0644 "$HERE/wireview-linux.desktop"    "$STAGE/wireview-linux.desktop"
install -m0644 "$HERE/wireview-mini.desktop"     "$STAGE/wireview-mini.desktop"
install -m0644 "$HERE/icons/hicolor/128x128/apps/wireview-linux.png" "$STAGE/wireview-linux.png"
install -m0755 "$HERE/tarball/install.sh"        "$STAGE/install.sh"
install -m0644 "$HERE/tarball/README.txt"        "$STAGE/README.txt"

tar -C "$WORK" -czf "$OUTDIR/$NAME.tar.gz" "$NAME"
echo ">> Wrote $OUTDIR/$NAME.tar.gz"
tar -tzf "$OUTDIR/$NAME.tar.gz"
