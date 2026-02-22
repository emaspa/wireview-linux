#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== WireView Pro II Linux Installer ==="
echo

# Install udev rules
echo "[1/3] Installing udev rules..."
sudo cp "$SCRIPT_DIR/udev/99-wireview.rules" /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
echo "  Done."

# Add user to dialout and plugdev groups
echo "[2/3] Adding user '$USER' to dialout and plugdev groups..."
sudo usermod -aG dialout "$USER" 2>/dev/null || true
sudo usermod -aG plugdev "$USER" 2>/dev/null || true
echo "  Done."

# Build
echo "[3/3] Building WireView Pro II..."
dotnet build "$SCRIPT_DIR/WireView2Linux.sln" -c Release
echo "  Done."

echo
echo "=== Installation complete ==="
echo "NOTE: You may need to log out and back in for group changes to take effect."
echo "Run with: dotnet run --project $SCRIPT_DIR/WireView2/ -c Release"
