# NixOS module: programs.wireview-linux.enable = true; installs the app and
# the udev rule that gives the local seat access to the device.
{ packages }:
{ config, lib, pkgs, ... }:

let
  cfg = config.programs.wireview-linux;
in
{
  options.programs.wireview-linux = {
    enable = lib.mkEnableOption "the WireView Pro II Linux GUI";

    package = lib.mkOption {
      type = lib.types.package;
      default = packages.${pkgs.stdenv.hostPlatform.system}.wireview-linux;
      defaultText = lib.literalExpression "wireview-linux from this flake";
      description = "The wireview-linux package.";
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];
    # Serial port and DFU bootloader: dialout + uaccess for the local seat,
    # and ModemManager leaves the device alone.
    services.udev.packages = [ cfg.package ];
  };
}
