#!/usr/bin/env bash
#
# One-time setup for the precompiled WireView Pro II tarball: installs the udev
# rule that grants access to the device's USB serial port and reloads udev.
#
# The rule (MODE=0666 + uaccess) grants access without any group membership, so
# no logout is needed.
#
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Installing udev rule (requires sudo)..."
sudo install -Dm0644 "$DIR/99-wireview.rules" /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules
sudo udevadm trigger

echo
echo "Done. Run the app with:"
echo "    $DIR/WireView2"
