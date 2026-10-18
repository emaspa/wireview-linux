#!/usr/bin/env bash
#
# wireview-linux release: rebuild and check every Linux release artifact from
# the tag v<version>, then print the publish steps (none of which this script
# runs). The version bump (csproj files, changelogs, metainfo, aur/PKGBUILD
# pkgver, the Flatpak manifest's tarball URL) must already be committed and
# tagged; the tarball checksum pins are written by this script.
#
#   packaging/release.sh <version>            build + check into dist/
#   packaging/release.sh <version> --no-pin   same, without touching aur/ and flatpak/
#   REF=HEAD OUT=/tmp/x packaging/release.sh <version> --no-pin   dry run of another commit
#   CHECKS_ONLY=1 packaging/release.sh <version>   only the install checks (step 6) of dist/
#
# What it does, in order:
#   1. a detached worktree of v<version> (clean, so the app shows "<version>")
#   2. dotnet build WireView2Linux.sln -c Release: 0 warnings, 0 errors
#   3. WireView Mini, NativeAOT in ubuntu:22.04 (packaging/build-mini.sh):
#      0 IL warnings, GLIBC floor at most 2.35
#   4. tarball, .deb (ubuntu:24.04), .src.rpm + dist-less .rpm (fedora:44),
#      Flatpak bundle (local tarball as the source, same sha256)
#   5. pins the new tarball's sha256 in aur/PKGBUILD, aur/.SRCINFO and the
#      Flatpak manifest of the main checkout (left uncommitted; --no-pin skips)
#   6. installs and runs each package in a container: deb in ubuntu:24.04,
#      rpm in fedora:44 and fedora:43, the AUR package (makepkg + pacman -U)
#      in archlinux, the tarball in ubuntu:22.04/24.04/26.04, fedora:43 and
#      archlinux; checks the Flatpak bundle's file list
#
# The containers get no devices, an empty /sys/class/hwmon and a fake
# /sys/class/tty that lists one WireView (ttyACM9, 0483:5740) with no device
# node: WireView Mini then tries to open /dev/ttyACM9 through
# libSystem.IO.Ports.Native.so, which proves the library loads, and fails with
# "no such file". Nothing in them can see or open the real WireView, and they
# have their own network namespace, so they never reach a WireView Mini on
# this host.
#
# A rebuild gives new checksums (the app's build stamp differs), so publish
# the files of one run and commit that run's pins.
#
# Requires: git, dotnet (8.0+ SDK), docker (or sudo docker), rpmbuild,
#           flatpak-builder with org.freedesktop.Sdk//24.08, ostree, python3.
#
set -euo pipefail

VERSION="${1:?usage: $0 <version> [--no-pin]}"
TAG="v$VERSION"
APP_ID=io.github.emaspa.WireViewLinux
PIN=1
[[ "${2:-}" == "--no-pin" ]] && PIN=0

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/dist}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

DOCKER=(docker)
docker info >/dev/null 2>&1 || DOCKER=(sudo docker)
STAGE="$(mktemp -d)"
# Mounted into every check container; created here so they stay ours, not root's.
mkdir -p "$STAGE/aur" "$STAGE/aur-out"
# No devices, no hwmon, a fake tty class: the real WireView stays invisible.
mkdir -p "$STAGE/faketty/ttyACM9"
echo 0483 > "$STAGE/faketty/ttyACM9/idVendor"
echo 5740 > "$STAGE/faketty/ttyACM9/idProduct"
SANDBOX=(--rm --tmpfs /sys/class/hwmon:ro -v "$STAGE/faketty:/sys/class/tty:ro")
WT="$STAGE/wireview-linux-$VERSION"
cleanup() {
    git -C "$REPO" worktree remove --force "$WT" 2>/dev/null || true
    # Files the containers created as root.
    "${DOCKER[@]}" run --rm -v "$STAGE:/s" ubuntu:24.04 chown -R "$(id -u):$(id -g)" /s 2>/dev/null || true
    rm -rf "$STAGE"
}
trap cleanup EXIT

step() { echo; echo "=== $*"; }
fail() { echo "!! $*" >&2; exit 1; }

TARBALL="wireview-linux-$VERSION-linux-x64.tar.gz"
DEB="wireview-linux_${VERSION}_amd64.deb"
RPM="wireview-linux-$VERSION-1.x86_64.rpm"
SRPM="wireview-linux-$VERSION-1.src.rpm"
FLATPAK="wireview-linux-$VERSION-x86_64.flatpak"

# ---------------------------------------------------------------- 1. checkout
# REF=<commit> builds another commit as a dry run (its version shows the
# commit suffix); the release is built from the tag.
REF="${REF:-$TAG}"
step "1. Worktree of $REF"
if [[ "$REF" == "$TAG" ]]; then
    git -C "$REPO" rev-parse -q --verify "refs/tags/$TAG" >/dev/null || fail "tag $TAG does not exist"
fi
git -C "$REPO" worktree add -q --detach "$WT" "$REF"
cd "$WT"
[[ -z "$(git status --porcelain)" ]] || fail "worktree is not clean"
desc="$(git describe --tags --always --dirty)"
if [[ "$REF" == "$TAG" ]]; then
    [[ "$desc" == "$TAG" ]] || fail "git describe says $desc, not $TAG"
else
    echo "!! dry run of $REF: the app will show $desc"
fi
echo "git describe: $desc"

if [[ -n "${CHECKS_ONLY:-}" ]]; then
    # Only step 6, on the artifacts already in $OUT.
    PIN=0
    TAR_SHA="$(sha256sum "$OUT/$TARBALL" | cut -d' ' -f1)"
    tar -xzOf "$OUT/$TARBALL" "wireview-linux-$VERSION-linux-x64/wireview-mini" > "$STAGE/wireview-mini"
    GLIBC_FLOOR="$(objdump -T "$STAGE/wireview-mini" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
else
# ---------------------------------------------------------------- 2. build
step "2. dotnet build WireView2Linux.sln -c Release"
dotnet build WireView2Linux.sln -c Release --no-incremental > "$STAGE/build.log" 2>&1 \
    || { tail -30 "$STAGE/build.log"; fail "solution build failed"; }
grep -E '^ +[0-9]+ (Warning|Error)\(s\)' "$STAGE/build.log"
grep -qE '^ +0 Warning\(s\)' "$STAGE/build.log" || fail "the solution build has warnings"
grep -qE '^ +0 Error\(s\)' "$STAGE/build.log" || fail "the solution build has errors"

# ---------------------------------------------------------------- 3. Mini
step "3. WireView Mini (NativeAOT, ubuntu:22.04)"
MINI="$STAGE/mini"
packaging/build-mini.sh "$MINI"
export MINI_DIR="$MINI"
GLIBC_FLOOR="$(objdump -T "$MINI/wireview-mini" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
echo "wireview-mini GLIBC floor: $GLIBC_FLOOR"

# ---------------------------------------------------------------- 4. artifacts
step "4a. Tarball"
packaging/build-tarball.sh "$VERSION" "$OUT" >/dev/null
tar -tzvf "$OUT/$TARBALL"

step "4b. .deb (dpkg-buildpackage in ubuntu:24.04)"
packaging/build-deb.sh "$VERSION" "$OUT" >/dev/null
dpkg-deb -f "$OUT/$DEB" Package Version Recommends
dpkg-deb -c "$OUT/$DEB" | grep -E 'bin/|wireview-mini|Ports.Native|\.hex|ext_flash'
# The app's displayed version (InformationalVersion, shown without "+build...").
APP_VER="$(dpkg-deb --fsys-tarfile "$OUT/$DEB" | tar -xO ./usr/lib/wireview-linux/WireView2.dll \
    | grep -ao '[0-9]\.[0-9]\.[0-9]\.[0-9][-.0-9a-z]*+build[0-9]*' | head -1)"
echo "app InformationalVersion: $APP_VER"
if [[ "$REF" == "$TAG" && "$APP_VER" != "$VERSION+build"* ]]; then
    fail "the app would show ${APP_VER%%+*}, not $VERSION"
fi

step "4c. .src.rpm (rpmbuild -bs) and .rpm (rpmbuild -bb in fedora:44, no dist tag)"
rpm/build-srpm.sh "$VERSION" >/dev/null
cp "rpm/build/SRPMS/$SRPM" "$OUT/$SRPM"
cat > "$STAGE/rpmbuild.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
dnf -y -q install rpm-build >/dev/null 2>&1
rpmbuild -bb --define "_topdir /work" --define "dist %{nil}" /work/SPECS/wireview-linux.spec >/work/rpmbuild.log 2>&1 \
    || { tail -30 /work/rpmbuild.log; exit 1; }
chown -R "$HOST_UID:$HOST_GID" /work
EOF
"${DOCKER[@]}" run --rm -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
    -v "$WT/rpm/build:/work" -v "$STAGE/rpmbuild.sh:/rpmbuild.sh:ro" fedora:44 bash /rpmbuild.sh
cp "rpm/build/RPMS/x86_64/$RPM" "$OUT/$RPM"
RPMQ=(rpm --dbpath "$STAGE/rpmdb" -qp)
"${RPMQ[@]}" --qf '%{NAME} %{VERSION}-%{RELEASE}\n' "$OUT/$RPM"
"${RPMQ[@]}" --recommends "$OUT/$RPM"
"${RPMQ[@]}" -l "$OUT/$RPM" | grep -E 'bin/|wireview-mini|Ports.Native|\.hex|ext_flash'

step "4d. Flatpak bundle (manifest copy with the local tarball)"
TAR_SHA="$(sha256sum "$OUT/$TARBALL" | cut -d' ' -f1)"
FP="$STAGE/flatpak"
mkdir -p "$FP"
python3 - "$WT/flatpak/$APP_ID.yaml" "$FP/$APP_ID.yaml" "$OUT/$TARBALL" "$TAR_SHA" "$WT" "$VERSION" <<'EOF'
import sys
src, dst, tarball, sha, wt, version = sys.argv[1:]
s = open(src).read()
url = (f"url: https://github.com/emaspa/wireview-linux/releases/download/v{version}/"
       f"wireview-linux-{version}-linux-x64.tar.gz")
assert url in s, url
start = s.index(url)
end = s.index("\n", s.index("sha256:", start))
s = s[:start] + f"path: {tarball}\n        sha256: {sha}" + s[end:]
for f in ("io.github.emaspa.WireViewLinux.desktop", "io.github.emaspa.WireViewLinux.metainfo.xml"):
    assert f"path: {f}" in s, f
    s = s.replace(f"path: {f}", f"path: {wt}/flatpak/{f}")
assert "path: ../packaging/icons" in s
s = s.replace("path: ../packaging/icons", f"path: {wt}/packaging/icons")
open(dst, "w").write(s)
EOF
grep -nE 'path:|sha256:' "$FP/$APP_ID.yaml"
flatpak-builder --state-dir="$FP/state" --repo="$FP/repo" --force-clean \
    "$FP/build-dir" "$FP/$APP_ID.yaml" > "$FP/builder.log" 2>&1 \
    || { tail -30 "$FP/builder.log"; fail "flatpak-builder failed"; }
flatpak build-bundle "$FP/repo" "$OUT/$FLATPAK" "$APP_ID"
# The bundle itself: import it into an empty repo and list the app's files.
ostree --repo="$FP/check" init --mode=archive-z2
flatpak build-import-bundle "$FP/check" "$OUT/$FLATPAK" >/dev/null
ostree --repo="$FP/check" ls -R "app/$APP_ID/x86_64/master" /files \
    | grep -E ' /files/(bin|wireview-linux|share/(applications|metainfo))/' | sed -E 's/^[^/]*//' 

# ---------------------------------------------------------------- 5. pins
if (( PIN )); then
    step "5. Pin the tarball sha256 ($TAR_SHA) in aur/ and flatpak/ of $REPO"
    python3 - "$REPO" "$TAR_SHA" <<'EOF'
import re, sys
repo, sha = sys.argv[1:]
p = f"{repo}/aur/PKGBUILD"
s = open(p).read()
s = re.sub(r"sha256sums=\('[0-9a-f]{64}'", f"sha256sums=('{sha}'", s, count=1)
open(p, "w").write(s)
p = f"{repo}/flatpak/io.github.emaspa.WireViewLinux.yaml"
s = open(p).read()
s = re.sub(r"(linux-x64\.tar\.gz\n\s+sha256: )[0-9a-f]{64}", rf"\g<1>{sha}", s, count=1)
open(p, "w").write(s)
EOF
    grep -c "$TAR_SHA" "$REPO/aur/PKGBUILD" "$REPO/flatpak/$APP_ID.yaml"
fi

fi # CHECKS_ONLY

# ---------------------------------------------------------------- 6. install checks
# The commands every check runs, from the installed paths. --status before the
# start is "not running" (3); with WireView Mini running in the background and
# no device it is 4. Its foreground log shows the serial library loaded.
cat > "$STAGE/check-mini.sh" <<'EOF'
#!/usr/bin/env bash
# usage: check-mini.sh <wireview-mini path> [app path]
set -uo pipefail
mini="$1"; app="${2:-}"
cd /tmp
echo "--- $mini --version"; "$mini" --version; echo "exit $?"
echo "--- $mini --help (first and last line)"; "$mini" --help > /tmp/help.txt; rc=$?
head -1 /tmp/help.txt; tail -1 /tmp/help.txt; echo "exit $rc"
echo "--- $mini --status (not running)"; "$mini" --status; rc=$?; echo "exit $rc"
[[ $rc == 3 ]] || { echo "!! expected exit 3"; exit 1; }
LD_DEBUG=libs "$mini" --no-tray --no-shutdown > /tmp/fg.log 2>&1 &
pid=$!
for _ in $(seq 20); do sleep 0.5; grep -q "publishing on" /tmp/fg.log && break; done
echo "--- $mini --status (running, no device)"; "$mini" --status; rc=$?; echo "exit $rc"
kill -TERM $pid; wait $pid
grep -v '^ *[0-9]*:' /tmp/fg.log | sed 's/^[0-9T:-]* //' | head -5
grep -o 'calling init: [^ ]*Ports.Native.so' /tmp/fg.log | head -1
[[ $rc == 4 ]] || { echo "!! expected exit 4"; exit 1; }
grep -q 'calling init: [^ ]*libSystem.IO.Ports.Native.so' /tmp/fg.log || { echo "!! serial library not loaded"; exit 1; }
if [[ -n "$app" ]]; then
    # The app has no --version. Under Xvfb it must still be running after
    # 20 s (timeout's exit 124) with no unhandled exception: the bundle
    # extracted, the runtime and Avalonia started and the window opened.
    echo "--- $app under Xvfb for 20 s"
    timeout 20 xvfb-run -a "$app" > /tmp/app.log 2>&1; rc=$?
    echo "exit $rc"
    if [[ $rc != 124 ]] || grep -q 'Unhandled exception' /tmp/app.log; then
        head -20 /tmp/app.log; echo "!! the app did not keep running"; exit 1
    fi
fi
echo "--- OK"
EOF
chmod +x "$STAGE/check-mini.sh"
# The packages' WireView Mini launcher entry: installed and valid. Installs
# desktop-file-utils with the image's package manager.
cat > "$STAGE/check-desktop.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
entry=/usr/share/applications/wireview-mini.desktop
if command -v apt-get >/dev/null; then
    apt-get install -y -qq --no-install-recommends desktop-file-utils > /tmp/dfu.log 2>&1
elif command -v dnf >/dev/null; then
    dnf -y -q install desktop-file-utils > /tmp/dfu.log 2>&1
else
    pacman -S --noconfirm --needed desktop-file-utils > /tmp/dfu.log 2>&1
fi || { tail -20 /tmp/dfu.log; echo "!! cannot install desktop-file-utils"; exit 1; }
echo "--- desktop-file-validate $entry"
[[ -f "$entry" ]] || { echo "!! $entry is not installed"; exit 1; }
out="$(desktop-file-validate "$entry" 2>&1)" || { echo "$out"; echo "!! $entry is not valid"; exit 1; }
[[ -z "$out" ]] || { echo "$out"; echo "!! desktop-file-validate reports issues"; exit 1; }
grep -E '^(Exec|Icon)=' "$entry"
EOF
chmod +x "$STAGE/check-desktop.sh"
run_check() { # image, script; /out is dist/, /check-mini.sh and /check-desktop.sh the checks
    local image="$1" script="$2"
    step "6. $image: $3"
    "${DOCKER[@]}" run "${SANDBOX[@]}" -v "$OUT:/out:ro" -v "$STAGE/check-mini.sh:/check-mini.sh:ro" \
        -v "$STAGE/check-desktop.sh:/check-desktop.sh:ro" \
        -v "$STAGE/aur:/aur:ro" -v "$STAGE/aur-out:/aurout" "$image" bash -c "$script" 2>&1 | tee "$STAGE/check-${image//[:\/]/-}-$4.log"
    grep -q -- '--- OK' "$STAGE/check-${image//[:\/]/-}-$4.log" || fail "$image: $3 failed"
}
APP_IN_PKG=/usr/bin/wireview-linux

run_check ubuntu:24.04 "set -e; export DEBIAN_FRONTEND=noninteractive; apt-get update -qq;
    apt-get install -y -qq /out/$DEB xvfb xauth libfontconfig1 > /tmp/apt.log 2>&1 || { tail -20 /tmp/apt.log; exit 1; }
    dpkg -s wireview-linux | grep -E '^(Version|Recommends):'; ls -l /usr/bin/wireview-linux /usr/bin/wireview-mini
    /check-desktop.sh
    /check-mini.sh /usr/bin/wireview-mini $APP_IN_PKG" "the .deb" deb

for image in fedora:44 fedora:43; do
    run_check "$image" "set -e; dnf -y -q install /out/$RPM xorg-x11-server-Xvfb xorg-x11-xauth > /tmp/dnf.log 2>&1 || { tail -20 /tmp/dnf.log; exit 1; }
        rpm -q wireview-linux; ls -l /usr/bin/wireview-linux /usr/bin/wireview-mini
        /check-desktop.sh
        /check-mini.sh /usr/bin/wireview-mini $APP_IN_PKG" "the .rpm" rpm
done

# AUR: the PKGBUILD of the tag with this tarball under the file name makepkg
# would download (the release is not live yet), built as a non-root user.
cp "$WT/aur/PKGBUILD" "$WT/aur/wireview-linux.desktop" "$WT/aur/wireview-linux.png" "$STAGE/aur/"
sed -i -E "s/sha256sums=\('[0-9a-f]{64}'/sha256sums=('$TAR_SHA'/" "$STAGE/aur/PKGBUILD"
cp "$OUT/$TARBALL" "$STAGE/aur/wireview-linux-bin-$VERSION-1.tar.gz"
run_check archlinux:latest "set -e; pacman -Sy --noconfirm --needed base-devel glibc gcc-libs zlib fontconfig freetype2 libx11 xorg-server-xvfb xorg-xauth > /tmp/pacman.log 2>&1 || { tail -20 /tmp/pacman.log; exit 1; }
    useradd -m builder; cp -r /aur /home/builder/aur; chown -R builder /home/builder/aur
    su builder -c 'cd ~/aur && makepkg -f --noconfirm > /tmp/makepkg.log 2>&1 && makepkg --printsrcinfo > .SRCINFO' || { tail -20 /tmp/makepkg.log; exit 1; }
    cp /home/builder/aur/.SRCINFO /aurout/.SRCINFO
    pacman -U --noconfirm /home/builder/aur/wireview-linux-bin-$VERSION-1-x86_64.pkg.tar.zst > /dev/null
    pacman -Qi wireview-linux-bin | grep -E '^(Version|Optional Deps)'; pacman -Ql wireview-linux-bin | grep -E 'bin/|wireview-mini|Ports.Native|\.hex|ext_flash'
    /check-desktop.sh
    /check-mini.sh /usr/bin/wireview-mini $APP_IN_PKG" "the AUR package (makepkg)" aur

if (( PIN )); then
    "${DOCKER[@]}" run --rm -v "$STAGE:/s" ubuntu:24.04 chown -R "$(id -u):$(id -g)" /s/aur-out
    cp "$STAGE/aur-out/.SRCINFO" "$REPO/aur/.SRCINFO"
    echo "aur/.SRCINFO regenerated:"; grep -E 'pkgver|sha256sums = [0-9a-f]{64}$' "$REPO/aur/.SRCINFO" | head -2
fi

for image in ubuntu:22.04 ubuntu:24.04 ubuntu:26.04 fedora:43 archlinux:latest; do
    run_check "$image" "set -e; mkdir -p /opt && tar -C /opt -xzf /out/$TARBALL
        /check-mini.sh /opt/wireview-linux-$VERSION-linux-x64/wireview-mini" "the tarball's wireview-mini" tar
done

# ---------------------------------------------------------------- summary
step "Artifacts in $OUT"
cd "$OUT"
ls -l "$TARBALL" "$DEB" "$RPM" "$SRPM" "$FLATPAK"
sha256sum "$TARBALL" "$DEB" "$RPM" "$SRPM" "$FLATPAK" | tee "SHA256SUMS-$VERSION"
echo "wireview-mini: $GLIBC_FLOOR floor, built in ubuntu:22.04"

cat <<EOF

=== Publish steps (not run by this script), in order

0. Commit the pins this run wrote (aur/PKGBUILD, aur/.SRCINFO, flatpak manifest),
   unless they are already committed with these checksums:
     git commit -am "aur, flatpak: pin the v$VERSION tarball"
   and push main and the tag:
     git push origin main && git push origin $TAG

1. Windows zip, on a Windows machine with the .NET 8 SDK and the Visual Studio
   2022 Build Tools (C++ workload), at the pushed tag:
     git fetch --tags && git checkout $TAG
     dotnet publish WireViewMini -c Release -r win-x64
   then zip wireview-mini.exe, TG-WV-PRO2-FW.hex and ext_flash.bin from
   WireViewMini\\bin\\Release\\net8.0-windows10.0.17763.0\\win-x64\\publish\\
   as $OUT/wireview-mini-$VERSION-windows-x64.zip

2. GitHub release:
     gh release create $TAG -R emaspa/wireview-linux --title "wireview-linux $VERSION" \\
       --notes-file $OUT/release-notes-$VERSION.md \\
       $OUT/$TARBALL $OUT/$DEB $OUT/$RPM $OUT/$FLATPAK $OUT/wireview-mini-$VERSION-windows-x64.zip

3. PPA (CLAUDE.md, packaging/ppa/README.md):
     packaging/ppa/build-ppa-source.sh $VERSION
     cd packaging/ppa/build
     debsign -k0E12EEBBC7B9A54D resolute/*_source.changes noble/*_source.changes
     dput ppa:sparvoli/wireview-hwmon resolute/*_source.changes
     dput ppa:sparvoli/wireview-hwmon noble/*_source.changes
     cd - && /usr/bin/python3 packaging/ppa/lp-builds.py $VERSION --wait --retry

4. COPR:
     copr-cli build emaspa/wireview-linux $OUT/$SRPM

5. AUR (wireview-linux-bin), once the release tarball is live:
     git clone ssh://aur@aur.archlinux.org/wireview-linux-bin.git /tmp/aur-wv && cd /tmp/aur-wv
     cp $REPO/aur/PKGBUILD $REPO/aur/.SRCINFO $REPO/aur/wireview-linux.desktop $REPO/aur/wireview-linux.png .
     makepkg -f    # downloads the release tarball and checks the pinned sha256
     git add -A && git commit -m "Update to $VERSION" && git push origin HEAD:master

6. Flatpak: the manifest commit from step 0 carries the release URL and sha256.

The community source package (AUR wireview-linux) is updated separately.
EOF
