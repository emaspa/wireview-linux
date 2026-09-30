WireView Pro II - Linux (Unofficial)
====================================

Self-contained build, no .NET runtime required.

Quick start
-----------
  1. One-time USB serial setup (installs the udev rule):

         ./install.sh

  2. Run the app:

         ./WireView2

Notes
-----
- install.sh uses sudo for the privileged step; run it as your normal user.
- The udev rule grants serial access to the user logged in at the local seat
  (no group membership or logout needed). Over SSH, join the dialout group:
  sudo usermod -aG dialout $USER, then log in again.
- The hwmon kernel module (wireview-hwmon) is optional. Without it the app talks
  to the device directly over USB serial and everything works.

Project: https://github.com/emaspa/wireview-linux
