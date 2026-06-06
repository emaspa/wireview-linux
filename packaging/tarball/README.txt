WireView Pro II — Linux (Unofficial)
====================================

Self-contained build — no .NET runtime required.

Quick start
-----------
  1. One-time USB serial setup (installs the udev rule, adds you to the
     dialout/plugdev groups):

         ./install.sh

  2. Log out and back in for the group changes to take effect.

  3. Run the app:

         ./WireView2

Notes
-----
- install.sh uses sudo for the privileged steps; run it as your normal user
  (not with sudo), otherwise the groups get added to root instead of you.
- The hwmon kernel module (wireview-hwmon) is optional. Without it the app talks
  to the device directly over USB serial and everything works.

Project: https://github.com/emaspa/wireview-linux
