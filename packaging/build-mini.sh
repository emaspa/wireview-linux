#!/usr/bin/env bash
#
# Build the Linux WireView Mini for the release packages: the NativeAOT
# wireview-mini executable, libSystem.IO.Ports.Native.so (the serial port) and
# the two files it reads next to itself, TG-WV-PRO2-FW.hex (firmware flashing)
# and ext_flash.bin (Restore factory artwork). The tarball, .deb and .rpm
# builds call this script, or reuse its output through MINI_DIR.
#
# A NativeAOT executable links against the glibc of the machine that builds
# it, so the build runs in an Ubuntu 22.04 container (packaging/mini/Dockerfile,
# .NET 8 SDK, clang, zlib): glibc 2.35, so the binary runs on Ubuntu 24.04+,
# Debian 13, Fedora 43+, Arch and the other targets. The script fails when the
# binary needs a newer glibc than MINI_GLIBC_MAX or when the publish reports a
# trim or AOT (IL) warning.
#
# The source is the working tree's tracked and untracked, not ignored, files.
# WireView Mini shows its <Version>, so its build identity does not depend on
# git; build releases from a clean checkout of the tag anyway, like the app.
#
# Requires: docker (or sudo docker).
# Usage:    packaging/build-mini.sh <output-dir>
# Env:      MINI_IMAGE=wireview-mini-build:ubuntu22.04   builder image tag
#           MINI_GLIBC_MAX=2.35                          highest GLIBC_ symbol allowed
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
OUTDIR="${1:?usage: $0 <output-dir>}"
MINI_IMAGE="${MINI_IMAGE:-wireview-mini-build:ubuntu22.04}"
MINI_GLIBC_MAX="${MINI_GLIBC_MAX:-2.35}"

DOCKER=(docker)
docker info >/dev/null 2>&1 || DOCKER=(sudo docker)

mkdir -p "$OUTDIR"
OUTDIR="$(cd "$OUTDIR" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/src" "$WORK/out"

echo ">> Builder image $MINI_IMAGE (packaging/mini/Dockerfile)"
"${DOCKER[@]}" build -q -t "$MINI_IMAGE" "$HERE/mini" >/dev/null

echo ">> Copying the source tree"
git -C "$REPO" ls-files -co --exclude-standard -z \
    | (cd "$REPO" && tar --null --ignore-failed-read -T - -cf - 2>/dev/null) | tar -C "$WORK/src" -xf -

cat > "$WORK/build.sh" <<'INNER'
#!/usr/bin/env bash
set -euo pipefail
dotnet publish /work/src/WireViewMini/WireViewMini.csproj -c Release -f net8.0 \
    -r linux-x64 -o /work/out > /work/publish.log 2>&1 || { tail -40 /work/publish.log; exit 1; }
glibc="$(objdump -T /work/out/wireview-mini | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
echo "$glibc" > /work/glibc
chown -R "$HOST_UID:$HOST_GID" /work
INNER

echo ">> dotnet publish WireViewMini (NativeAOT, linux-x64) in $MINI_IMAGE"
"${DOCKER[@]}" run --rm -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
    -v "$WORK:/work" -v wireview-mini-nuget:/root/.nuget/packages \
    "$MINI_IMAGE" bash /work/build.sh

# Trim and AOT analysis warnings (ILxxxx) mean a reflection path NativeAOT
# may have cut; the release build has none.
if grep -q 'warning IL[0-9]' "$WORK/publish.log"; then
    grep 'warning IL[0-9]' "$WORK/publish.log" | sort -u >&2
    echo "!! the WireView Mini publish reported IL warnings" >&2
    exit 1
fi
glibc="$(cat "$WORK/glibc")"
if [[ "$(printf '%s\n%s\n' "${glibc#GLIBC_}" "$MINI_GLIBC_MAX" | sort -V | tail -1)" != "$MINI_GLIBC_MAX" ]]; then
    echo "!! wireview-mini needs $glibc, newer than GLIBC_$MINI_GLIBC_MAX" >&2
    exit 1
fi

install -m0755 "$WORK/out/wireview-mini"                "$OUTDIR/wireview-mini"
install -m0644 "$WORK/out/libSystem.IO.Ports.Native.so" "$OUTDIR/libSystem.IO.Ports.Native.so"
install -m0644 "$WORK/out/TG-WV-PRO2-FW.hex"            "$OUTDIR/TG-WV-PRO2-FW.hex"
install -m0644 "$WORK/out/ext_flash.bin"                "$OUTDIR/ext_flash.bin"
echo ">> Wrote $OUTDIR (wireview-mini needs up to $glibc, 0 IL warnings)"
ls -l "$OUTDIR"
