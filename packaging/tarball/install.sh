#!/usr/bin/env bash
#
# One-time setup for the precompiled WireView Pro II tarball: adds launcher
# entries for the app and WireView Mini for the current user, installs the udev
# rule that grants access to the device's USB serial port and reloads udev.
#
# The launcher entries (~/.local/share/applications/wireview-linux.desktop and
# wireview-mini.desktop) are written from the ones shipped here, with absolute
# paths to this directory: run install.sh again after moving it.
#
# The rule (0660 in the serial group + a uaccess ACL for the local seat user)
# grants desktop access without any group membership, so no logout is needed.
# Over SSH, join the serial group instead.
#
# Arch-based distros have no dialout group, their serial group is uucp. The
# serial port gets it from the distro's default rules; the DFU bootloader's
# line names dialout and is rewritten here.
#
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

# An Exec program path as a quoted desktop entry argument: \ " ` $ escaped for
# the quoting, then \ doubled for the string value, % doubled for field codes.
exec_quote() {
    local s="$1"
    s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//\`/\\\`}"; s="${s//\$/\\\$}"
    s="${s//\\/\\\\}"; s="${s//%/%%}"
    printf '"%s"' "$s"
}

# write_launcher <shipped .desktop> <program> <output>: every Exec line runs
# <program> with its arguments, Icon is this directory's icon.
write_launcher() {
    local exec icon line rest args
    exec="$(exec_quote "$2")"
    icon="$DIR/wireview-linux.png"
    icon="${icon//\\/\\\\}"
    while IFS= read -r line || [[ -n "$line" ]]; do
        case "$line" in
            Exec=*)
                rest="${line#Exec=}"
                case "$rest" in *' '*) args=" ${rest#* }" ;; *) args="" ;; esac
                line="Exec=$exec$args"
                ;;
            Icon=*)
                line="Icon=$icon"
                ;;
        esac
        printf '%s\n' "$line"
    done < "$1" > "$3.tmp"
    mv -f "$3.tmp" "$3"
}

echo "Adding launcher entries for $USER..."
mkdir -p "$APPS"
write_launcher "$DIR/wireview-linux.desktop" "$DIR/WireView2"     "$APPS/wireview-linux.desktop"
write_launcher "$DIR/wireview-mini.desktop"  "$DIR/wireview-mini" "$APPS/wireview-mini.desktop"
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$APPS" >/dev/null 2>&1 || true
fi

echo "Installing udev rule (requires sudo)..."
sudo install -Dm0644 "$DIR/99-wireview.rules" /etc/udev/rules.d/99-wireview.rules
if ! getent group dialout >/dev/null && getent group uucp >/dev/null; then
    sudo sed -i 's/GROUP="dialout"/GROUP="uucp"/g' /etc/udev/rules.d/99-wireview.rules
fi
sudo udevadm control --reload-rules
sudo udevadm trigger

echo
echo "Done. Run the app with:"
echo "    $DIR/WireView2"
echo "WireView Mini (tray icon, alerts, control panel in the browser):"
echo "    $DIR/wireview-mini            # in the foreground"
echo "    $DIR/wireview-mini --install  # start it at login"
echo "The application launcher now has WireView Pro II and WireView Mini entries"
echo "(in $APPS). They point at this directory: run install.sh again after moving it."
