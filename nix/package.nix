# wireview-linux built from source with the .NET SDK. NuGet packages are
# pinned in deps.json; regenerate it after a package change with
#   nix build .#default.fetch-deps && ./result nix/deps.json
{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  dfu-util,
  fontconfig,
  freetype,
  icu,
  libGL,
  libICE,
  libSM,
  libX11,
  libXcursor,
  libXext,
  libXi,
  libXrandr,
  src,
  version,
}:

buildDotnetModule {
  pname = "wireview-linux";
  inherit version src;

  projectFile = "WireView2/WireView2.csproj";
  nugetDeps = ./deps.json;

  # The app targets net8.0; it runs on the current runtime.
  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;
  dotnetFlags = [ "-p:RollForward=Major" ];

  executables = [ "WireView2" ];

  # Loaded at run time by Avalonia (X11, GL) and SkiaSharp/HarfBuzz (fonts).
  runtimeDeps = [
    fontconfig
    freetype
    icu
    libGL
    libICE
    libSM
    libX11
    libXcursor
    libXext
    libXi
    libXrandr
  ];

  # In-app firmware flashing runs dfu-util.
  makeWrapperArgs = [ "--prefix" "PATH" ":" (lib.makeBinPath [ dfu-util ]) ];

  postInstall = ''
    install -Dm644 packaging/wireview-linux.desktop $out/share/applications/wireview-linux.desktop
    substituteInPlace $out/share/applications/wireview-linux.desktop \
      --replace-fail "Exec=/usr/lib/wireview-linux/WireView2" "Exec=wireview-linux"
    mkdir -p $out/share/icons
    cp -r packaging/icons/hicolor $out/share/icons/

    # Ahead of 73-seat-late.rules, which grants the uaccess tag, so the
    # explicit uaccess builtin the 99- file needs elsewhere is dropped
    # (NixOS verifies rules with a udevadm that lacks it).
    mkdir -p $out/lib/udev/rules.d
    sed 's/, RUN{builtin}+="uaccess"//' udev/99-wireview.rules > $out/lib/udev/rules.d/70-wireview.rules
    if grep -q 'RUN{builtin}' $out/lib/udev/rules.d/70-wireview.rules; then
      echo "udev rule still calls a builtin" >&2; exit 1
    fi
  '';

  postFixup = ''
    ln -s $out/bin/WireView2 $out/bin/wireview-linux
  '';

  meta = {
    description = "Unofficial Linux GUI for the Thermal Grizzly WireView Pro II";
    homepage = "https://github.com/emaspa/wireview-linux";
    # Contains code decompiled from the upstream Windows client and Thermal
    # Grizzly's firmware image; see the README's License section.
    license = lib.licenses.unfreeRedistributable;
    platforms = [ "x86_64-linux" "aarch64-linux" ];
    mainProgram = "wireview-linux";
  };
}
