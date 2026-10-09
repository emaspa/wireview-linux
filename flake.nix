{
  description = "Unofficial Linux GUI for the Thermal Grizzly WireView Pro II";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      lib = nixpkgs.lib;
      systems = [ "x86_64-linux" "aarch64-linux" ];
      # The package is unfree (decompiled upstream code, Thermal Grizzly's
      # firmware); allow exactly this one.
      pkgsFor = system: import nixpkgs {
        inherit system;
        config.allowUnfreePredicate = pkg: lib.getName pkg == "wireview-linux";
      };
      forAllSystems = f: lib.genAttrs systems (system: f (pkgsFor system));
      version = builtins.head (builtins.match ".*<Version>([^<]+)</Version>.*"
        (builtins.readFile ./WireView2/WireView2.csproj));
    in
    {
      packages = forAllSystems (pkgs: rec {
        wireview-linux = pkgs.callPackage ./nix/package.nix { src = self; inherit version; };
        default = wireview-linux;
      });

      nixosModules.default = import ./nix/module.nix { inherit (self) packages; };

      checks = forAllSystems (pkgs: {
        inherit (self.packages.${pkgs.stdenv.hostPlatform.system}) wireview-linux;
      });
    };
}
