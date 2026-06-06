#!/usr/bin/env bash
#
# One-time setup for the precompiled WireView Pro II tarball:
#   - installs the udev rule that grants access to the device's USB serial port
#   - adds the current user to the 'dialout' and 'plugdev' groups
#
# Run it as your normal user (it uses sudo for the privileged steps); do NOT run
# the whole script with sudo, or the groups would be added to root instead of you.
#
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Installing udev rule (requires sudo)..."
sudo install -Dm0644 "$DIR/99-wireview.rules" /etc/udev/rules.d/99-wireview.rules
sudo udevadm control --reload-rules
sudo udevadm trigger

echo "Adding '$USER' to the 'dialout' and 'plugdev' groups..."
sudo usermod -aG dialout "$USER" 2>/dev/null || true
sudo usermod -aG plugdev "$USER" 2>/dev/null || true

echo
echo "Done. Log out and back in for the group changes to take effect, then run:"
echo "    $DIR/WireView2"
