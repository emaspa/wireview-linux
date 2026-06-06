#!/usr/bin/env bash
#
# Build the self-contained precompiled tarball for a GitHub release.
#
# The tarball extracts to a single directory containing the app binary, the udev
# rule, and a one-time install.sh — so the precompiled path needs no extra
# downloads to get USB serial access working.
#
# Requires: dotnet (8.0 SDK) on PATH.
# Usage:    packaging/build-tarball.sh [version] [output-dir]
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

install -m0755 "$WORK/single/WireView2"          "$STAGE/WireView2"
install -m0644 "$REPO/udev/99-wireview.rules"    "$STAGE/99-wireview.rules"
install -m0755 "$HERE/tarball/install.sh"        "$STAGE/install.sh"
install -m0644 "$HERE/tarball/README.txt"        "$STAGE/README.txt"

tar -C "$WORK" -czf "$OUTDIR/$NAME.tar.gz" "$NAME"
echo ">> Wrote $OUTDIR/$NAME.tar.gz"
tar -tzf "$OUTDIR/$NAME.tar.gz"
