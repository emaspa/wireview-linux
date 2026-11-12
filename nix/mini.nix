# WireView Mini, the headless client, compiled ahead of time (NativeAOT): one
# native executable plus libSystem.IO.Ports.Native.so, with the bundled
# firmware image and factory artwork next to it, where it looks for them.
# package.nix installs it with a wrapper and the launcher entry.
# NuGet packages (the ILCompiler among them) are pinned in mini-deps.json;
# regenerate it after a package change with
#   nix build .#default.mini.fetch-deps && ./result nix/mini-deps.json
{
  lib,
  stdenv,
  buildDotnetModule,
  dotnetCorePackages,
  clang,
  zlib,
  src,
  version,
}:

buildDotnetModule {
  pname = "wireview-mini";
  inherit version src;

  projectFile = "WireViewMini/WireViewMini.csproj";
  nugetDeps = ./mini-deps.json;

  # The project targets net8.0; NativeAOT compiles it with the ILCompiler pack
  # the SDK pins for net8.0, so the binary needs no .NET runtime.
  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  selfContainedBuild = true;

  # The native link: clang drives it, zlib is linked in.
  nativeBuildInputs = [ clang ];
  buildInputs = [ zlib ];

  # Restore for the one target this RID builds. The hook's `--runtime` sets
  # RuntimeIdentifiers, which the project does not key on: it would restore
  # both targets (Windows included), and the restore's ILCompiler import would
  # then wait for a TargetFramework the project only sets later, so the
  # publish would quietly ship the JIT build.
  dotnetRestoreFlags = [ "-p:RuntimeIdentifier=${dotnetCorePackages.systemToDotnetRid stdenv.hostPlatform.system}" ];

  # package.nix writes the wrapper.
  executables = [ ];

  # StripSymbols leaves the symbols (.dbg) next to the binary and the libraries
  # leave .pdb files; the other packages ship neither. A wireview-mini.dll means
  # the publish fell back to the JIT build: fail rather than ship that.
  postInstall = ''
    rm -f $out/lib/wireview-mini/*.dbg $out/lib/wireview-mini/*.pdb
    if [ -e $out/lib/wireview-mini/wireview-mini.dll ]; then
      echo "wireview-mini was not compiled ahead of time" >&2
      exit 1
    fi
  '';

  meta = {
    description = "WireView Mini, the headless WireView Pro II client";
    homepage = "https://github.com/emaspa/wireview-linux";
    license = lib.licenses.unfreeRedistributable;
    platforms = [ "x86_64-linux" "aarch64-linux" ];
    mainProgram = "wireview-mini";
  };
}
