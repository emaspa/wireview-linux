#!/usr/bin/env bash
#
# Build the Launchpad PPA source packages (ppa:sparvoli/wireview-hwmon) for a
# wireview-linux release that is already published on GitHub.
#
# The PPA package is a repack of the released .deb: the self-contained .NET
# publish, desktop file, icons and udev rule are taken from
# wireview-linux_<version>_amd64.deb, so the PPA installs the exact bits of the
# GitHub release. Launchpad builders have no .NET SDK and no network, so the
# "source" package carries the binaries (format 3.0 (native), amd64 only, no
# build step; see debian/rules).
#
# Before running: the top entry of packaging/ppa/debian/changelog must be
# "wireview-linux (<version>~resolute1) resolute; ...". For every other series
# the script rewrites only that top header to "<version>~<series>1) <series>;".
#
# Requires: gh (to fetch the release .deb), dpkg-dev, debhelper.
# Usage:    packaging/ppa/build-ppa-source.sh <version> [path/to/released.deb]
# Output:   packaging/ppa/build/<series>/wireview-linux_<version>~<series>1_source.changes
#           (unsigned; sign with debsign, upload with dput, see packaging/ppa/README.md)
#
set -euo pipefail

SERIES_LIST=(resolute noble)   # 26.04, 24.04: the series the PPA carries

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:?usage: $0 <version> [released.deb]}"
DEB="${2:-}"
OUT="$HERE/build"

top="$(dpkg-parsechangelog -l "$HERE/debian/changelog" -S Version)"
if [[ "$top" != "$VERSION~resolute1" ]]; then
    echo "!! top entry of packaging/ppa/debian/changelog is $top, expected $VERSION~resolute1" >&2
    echo "!! add a '$VERSION~resolute1) resolute;' entry first" >&2
    exit 1
fi

rm -rf "$OUT"
mkdir -p "$OUT"

if [[ -z "$DEB" ]]; then
    echo ">> Fetching wireview-linux_${VERSION}_amd64.deb from GitHub release v$VERSION"
    gh release download "v$VERSION" -R emaspa/wireview-linux \
        -p "wireview-linux_${VERSION}_amd64.deb" -D "$OUT"
    DEB="$OUT/wireview-linux_${VERSION}_amd64.deb"
fi
debver="$(dpkg-deb -f "$DEB" Version)"
[[ "$debver" == "$VERSION" ]] || { echo "!! $DEB is version $debver, not $VERSION" >&2; exit 1; }

echo ">> Unpacking $DEB"
dpkg-deb -x "$DEB" "$OUT/deb"

for series in "${SERIES_LIST[@]}"; do
    ver="$VERSION~${series}1"
    src="$OUT/$series/wireview-linux-$VERSION"
    echo ">> Assembling $ver"
    mkdir -p "$src/udev" "$src/icons"
    cp -a "$OUT/deb/usr/lib/wireview-linux"                  "$src/linux-x64"
    cp -a "$OUT/deb/usr/share/icons/hicolor"                 "$src/icons/hicolor"
    cp -a "$OUT/deb/usr/share/applications/wireview-linux.desktop" "$src/"
    cp -a "$OUT/deb/lib/udev/rules.d/99-wireview.rules"      "$src/udev/"
    cp -a "$HERE/debian"                                     "$src/debian"
    # Only the top entry decides the target series; older entries stay as uploaded.
    sed -i "1s/^wireview-linux ([^)]*) [a-z]*;/wireview-linux ($ver) $series;/" \
        "$src/debian/changelog"
    [[ "$(dpkg-parsechangelog -l "$src/debian/changelog" -S Distribution)" == "$series" ]]

    (cd "$src" && dpkg-buildpackage --build=source -us -uc -d >/dev/null)
    rm -rf "$src"
done

rm -rf "$OUT/deb"
echo
echo ">> Source packages ready:"
ls -1 "$OUT"/*/*_source.changes
echo
echo "Next: debsign -k0E12EEBBC7B9A54D $OUT/*/*_source.changes"
echo "      dput ppa:sparvoli/wireview-hwmon $OUT/<series>/*_source.changes   (each)"
