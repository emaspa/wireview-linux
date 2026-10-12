#!/usr/bin/env bash
#
# One-time setup for the precompiled WireView Pro II tarball: installs the udev
# rule that grants access to the device's USB serial port and reloads udev.
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
