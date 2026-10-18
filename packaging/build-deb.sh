#!/usr/bin/env bash
#
# Build the GitHub-release .deb (wireview-linux_<version>_amd64.deb).
#
# The package metadata is the PPA template in packaging/ppa/debian/ (control,
# copyright, rules, install/links, changelog), so the release .deb and the
# PPA builds come from the same files. The payload is built like the rpm's
# (rpm/build-srpm.sh): the loose self-contained linux-x64 publish (no .pdb)
# with the single-file, self-extracting WireView2 overlaid as the launcher,
# WireView Mini's NativeAOT wireview-mini beside it (packaging/build-mini.sh;
# its libSystem.IO.Ports.Native.so, TG-WV-PRO2-FW.hex and ext_flash.bin are the
# same files the app's publish already has there), plus the desktop entries,
# icons and udev rule from this repo, laid out like the PPA source tree (see
# packaging/ppa/build-ppa-source.sh).
#
# The binary package is built with dpkg-buildpackage in an ubuntu:24.04
# container, the oldest series the PPA carries, and compressed with xz: every
# dpkg that could meet this .deb (Ubuntu 24.04+, Debian) reads xz, while zstd
# members need dpkg >= 1.21.18.
#
# Before running: the top entry of packaging/ppa/debian/changelog must be the
# release ("wireview-linux (<version>~resolute1) resolute; ..."). The build
# copy rewrites only that header to "wireview-linux (<version>) unstable;".
#
# Requires: dotnet (8.0+ SDK), docker (packaging/build-mini.sh, and
#           dpkg-buildpackage unless DEB_BUILDER=host with debhelper).
# Usage:    packaging/build-deb.sh [version] [output-dir]
# Env:      DEB_BUILDER=docker|host   where dpkg-buildpackage runs (default docker)
#           DEB_IMAGE=ubuntu:24.04    container image for DEB_BUILDER=docker
#           MINI_DIR=<dir>            reuse a packaging/build-mini.sh output
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

VERSION="${1:-$(grep -oP '<Version>\K[^<]+' "$REPO/WireView2/WireView2.csproj")}"
OUTDIR="${2:-$REPO/dist}"
DEB_BUILDER="${DEB_BUILDER:-docker}"
DEB_IMAGE="${DEB_IMAGE:-ubuntu:24.04}"
DEBIAN_TEMPLATE="$HERE/ppa/debian"
NAME="wireview-linux_${VERSION}_amd64.deb"

top="$(dpkg-parsechangelog -l "$DEBIAN_TEMPLATE/changelog" -S Version)"
if [[ "$top" != "$VERSION~resolute1" ]]; then
    echo "!! top entry of packaging/ppa/debian/changelog is $top, expected $VERSION~resolute1" >&2
    exit 1
fi

mkdir -p "$OUTDIR"
OUTDIR="$(cd "$OUTDIR" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
SRC="$WORK/wireview-linux-$VERSION"
mkdir -p "$SRC/udev" "$SRC/icons"

echo ">> dotnet publish (loose + single-file, self-contained, linux-x64)"
dotnet publish "$REPO/WireView2/WireView2.csproj" -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=false -p:DebugType=none \
    -o "$SRC/linux-x64" >/dev/null
dotnet publish "$REPO/WireView2/WireView2.csproj" -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true -o "$WORK/single" >/dev/null
# The bundled launcher replaces the apphost, as in every released .deb/.rpm.
install -m0755 "$WORK/single/WireView2" "$SRC/linux-x64/WireView2"
rm -rf "$WORK/single"

echo ">> WireView Mini"
MINI="${MINI_DIR:-$WORK/mini}"
[[ -n "${MINI_DIR:-}" ]] || "$HERE/build-mini.sh" "$MINI" >/dev/null
install -m0755 "$MINI/wireview-mini" "$SRC/linux-x64/wireview-mini"
# It reads these from its own directory; the app's publish ships the same
# files there, so one copy serves both.
for f in libSystem.IO.Ports.Native.so TG-WV-PRO2-FW.hex ext_flash.bin; do
    cmp "$MINI/$f" "$SRC/linux-x64/$f" || { echo "!! $f differs between the app and WireView Mini" >&2; exit 1; }
done
# dh_fixperms leaves .dll modes alone; only the three executables keep +x.
find "$SRC/linux-x64" -type f ! -name WireView2 ! -name wireview-mini ! -name createdump -exec chmod 0644 {} +

echo ">> Assembling the package tree"
cp -a "$REPO/packaging/icons/hicolor"          "$SRC/icons/hicolor"
cp -a "$REPO/packaging/wireview-linux.desktop" "$SRC/"
cp -a "$REPO/packaging/wireview-mini.desktop"  "$SRC/"
cp -a "$REPO/udev/99-wireview.rules"           "$SRC/udev/"
cp -a "$DEBIAN_TEMPLATE"                       "$SRC/debian"
sed -i "1s/^wireview-linux ([^)]*) [a-z]*;/wireview-linux ($VERSION) unstable;/" "$SRC/debian/changelog"
[[ "$(dpkg-parsechangelog -l "$SRC/debian/changelog" -S Version)" == "$VERSION" ]]
cat >> "$SRC/debian/rules" <<'EOF'

# Release .deb only (appended by packaging/build-deb.sh): xz members.
override_dh_builddeb:
	dh_builddeb -- -Zxz
EOF

case "$DEB_BUILDER" in
docker)
    cat > "$WORK/build.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends debhelper >/dev/null
cd "/work/$SRC_NAME"
# -d: nothing is compiled, so build-essential (implied by dpkg-checkbuilddeps)
# is not needed; debhelper, the only Build-Depends, is installed above.
dpkg-buildpackage -b -us -uc -d
chown -R "$HOST_UID:$HOST_GID" /work
EOF
    DOCKER=(docker)
    docker info >/dev/null 2>&1 || DOCKER=(sudo docker)
    echo ">> dpkg-buildpackage in $DEB_IMAGE"
    "${DOCKER[@]}" run --rm -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
        -e SRC_NAME="$(basename "$SRC")" \
        -v "$WORK:/work" "$DEB_IMAGE" bash /work/build.sh
    ;;
host)
    echo ">> dpkg-buildpackage on the host"
    (cd "$SRC" && dpkg-buildpackage -b -us -uc)
    ;;
*)
    echo "!! DEB_BUILDER must be docker or host" >&2
    exit 1
    ;;
esac

cp "$WORK/$NAME" "$OUTDIR/$NAME"
echo
echo ">> Wrote $OUTDIR/$NAME"
dpkg-deb -I "$OUTDIR/$NAME" | sed -n '/^ Package:/,/^ Description:/p'
