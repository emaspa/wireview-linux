#!/usr/bin/env bash
#
# One-time setup for the precompiled WireView Pro II tarball: installs the udev
# rule that grants access to the device's USB serial port and reloads udev.
#
# The rule (0660 root:dialout + a uaccess ACL for the local seat user) grants
# desktop access without any group membership, so no logout is needed. Over SSH,
# join the dialout group instead.
#
# Arch-based distros have no dialout group, their serial group is uucp. udev
# drops a rule line whose group it cannot resolve, so the rule is rewritten there.
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
