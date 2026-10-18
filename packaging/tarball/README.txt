WireView Pro II - Linux (Unofficial)
====================================

Self-contained build, no .NET runtime required.

Quick start
-----------
  1. One-time setup (installs the udev rule for USB serial access and adds
     WireView Pro II and WireView Mini to your application launcher):

         ./install.sh

  2. Run the app:

         ./WireView2

  3. Optional, WireView Mini: a program with no window that keeps fault
     alerts, shutdown on fault and LAN publishing going with the app closed,
     with a tray icon and a control panel in the browser:

         ./wireview-mini --install    # start it at login
         ./wireview-mini              # or run it now, in the foreground
         ./wireview-mini --help

     Its launcher entry starts it, or opens its control panel when it runs
     (wireview-mini --launch).

     Keep wireview-mini, libSystem.IO.Ports.Native.so, TG-WV-PRO2-FW.hex and
     ext_flash.bin together: it reads them from its own directory. The login
     entry points at this directory, so run --install again after moving it.

Notes
-----
- install.sh uses sudo for the privileged step; run it as your normal user.
- The launcher entries (~/.local/share/applications/wireview-linux.desktop and
  wireview-mini.desktop) point at this directory, so run install.sh again
  after moving it.
- The udev rule grants serial access to the user logged in at the local seat
  (no group membership or logout needed). Over SSH, join the dialout group
  (uucp on Arch-based distros): sudo usermod -aG dialout $USER, then log in
  again.
- The hwmon kernel module (wireview-hwmon) is optional. Without it the app talks
  to the device directly over USB serial and everything works.

Project: https://github.com/emaspa/wireview-linux
