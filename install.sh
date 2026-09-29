#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Run as your user; the script uses sudo where it needs root.
TARGET_USER="${SUDO_USER:-$USER}"

echo "=== WireView Pro II Linux Installer ==="
echo

# Install udev rules: the serial port and the DFU bootloader become 0660
# root:dialout plus an ACL for the user logged in at the local seat (uaccess).
echo "[1/3] Installing udev rules..."
sudo cp "$SCRIPT_DIR/udev/99-wireview.rules" /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
echo "  Done."

# A local desktop session needs no group. SSH and other remote sessions get
# no seat ACL, so they need the dialout group.
NEED_RELOGIN=0
if [ -n "${SSH_CONNECTION:-}" ] || [ -z "${XDG_SEAT:-}" ]; then
    echo "[2/3] No local seat session: adding '$TARGET_USER' to the dialout group..."
    sudo usermod -aG dialout "$TARGET_USER"
    NEED_RELOGIN=1
else
    echo "[2/3] Local seat session: the udev rule grants access, no group needed."
fi
echo "  Done."

# Build
echo "[3/3] Building WireView Pro II..."
dotnet build "$SCRIPT_DIR/WireView2Linux.sln" -c Release
echo "  Done."

echo
echo "=== Installation complete ==="
if [ "$NEED_RELOGIN" = 1 ]; then
    echo "NOTE: log in again for the dialout membership to take effect."
fi
echo "Run with: dotnet run --project $SCRIPT_DIR/WireView2/ -c Release"
