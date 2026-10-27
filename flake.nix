{
  description = "Reproduzierbare Entwicklungs- und Linux-Release-Builds für DMRoute-ng";

  inputs.nixpkgs.url = "nixpkgs/nixpkgs-unstable";

  outputs =
    { self, nixpkgs }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
    in
    {
      packages = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
          lib = pkgs.lib;
          dotnetSdk = pkgs.dotnetCorePackages.sdk_9_0;
          dotnetRuntime = pkgs.dotnetCorePackages.runtime_9_0;
          # Releasepakete zielen bewusst auf den bestehenden Linux-x64-Vertrag.
          # Die Entwicklungs-Shell bleibt zusätzlich auf Apple Silicon nutzbar.
          runtimeId = "linux-x64";

          server = pkgs.buildDotnetModule {
            pname = "dmroute-ng";
            version = "0.1.0";
            src = self;
            projectFile = "src/DMRoute_ng/DMRoute-ng.csproj";
            nugetDeps = ./nix/server-deps.json;
            inherit runtimeId;
            selfContainedBuild = true;
            dotnet-sdk = dotnetSdk;
            dotnet-runtime = dotnetRuntime;
            dotnetFlags = [
              "-p:PublishAot=true"
              "-p:OptimizationPreference=Speed"
            ];
            nativeBuildInputs = [ pkgs.clang ];
            buildInputs = [
              pkgs.zlib
              pkgs.zlib.static
            ];
            executables = [ ];
            doCheck = false;
          };

          playground = pkgs.buildDotnetModule {
            pname = "dmroute-ng-playground";
            version = "0.1.0";
            src = self;
            projectFile = "tools/DMRoute_ng.Playground/DMRoute_ng.Playground.csproj";
            nugetDeps = ./nix/playground-deps.json;
            inherit runtimeId;
            selfContainedBuild = true;
            dotnet-sdk = dotnetSdk;
            dotnet-runtime = dotnetRuntime;
            dotnetInstallFlags = [
              "-p:PublishSingleFile=true"
              "-p:DebugType=None"
            ];
            executables = [ ];
            doCheck = false;
          };

          openDmr = pkgs.stdenv.mkDerivation {
            pname = "opendmr-dmroute";
            version = "1.0.0+dmroute.2";
            src = ./third_party/OpenDMR;
            nativeBuildInputs = [ pkgs.gnumake ];
            buildPhase = ''
              runHook preBuild
              make -j$NIX_BUILD_CORES CXX=c++ CC=cc dmr_codec codec_test
              ./codec_test
              runHook postBuild
            '';
            installPhase = ''
              runHook preInstall
              install -Dm755 dmr_codec "$out/bin/dmr_codec"
              runHook postInstall
            '';
            meta.license = lib.licenses.gpl2Only;
          };

          blip25Encoder = pkgs.rustPlatform.buildRustPackage {
            pname = "dmroute-blip25-encoder";
            version = "0.1.0";
            src = ./tools/dmroute-blip25-encoder;
            cargoLock.lockFile = ./tools/dmroute-blip25-encoder/Cargo.lock;
            doCheck = true;
            meta.license = lib.licenses.mit;
          };

          documentation = pkgs.stdenvNoCC.mkDerivation {
            pname = "dmroute-ng-documentation";
            version = "0.1.0";
            src = self;
            nativeBuildInputs = [ pkgs.asciidoctor-with-extensions ];
            buildPhase = ''
              runHook preBuild
              for file in $(find docs -type f -name '*.adoc' ! -name handbook.adoc ! -name hardware-tests.adoc); do
                asciidoctor --failure-level WARN --out-file /dev/null "$file"
              done
              asciidoctor --failure-level WARN -o DMRoute-ng-Handbuch.html docs/handbook.adoc
              asciidoctor --failure-level WARN -o DMRoute-ng-Hardwaretests.html docs/hardware-tests.adoc
              asciidoctor-pdf --failure-level WARN -o DMRoute-ng-Handbuch.pdf docs/handbook.adoc
              asciidoctor-pdf --failure-level WARN -o DMRoute-ng-Hardwaretests.pdf docs/hardware-tests.adoc
              runHook postBuild
            '';
            installPhase = ''
              runHook preInstall
              install -Dm644 DMRoute-ng-Handbuch.html "$out/DMRoute-ng-Handbuch.html"
              install -Dm644 DMRoute-ng-Hardwaretests.html "$out/DMRoute-ng-Hardwaretests.html"
              install -Dm644 DMRoute-ng-Handbuch.pdf "$out/DMRoute-ng-Handbuch.pdf"
              install -Dm644 DMRoute-ng-Hardwaretests.pdf "$out/DMRoute-ng-Hardwaretests.pdf"
              runHook postInstall
            '';
          };

          releaseBundle =
            pkgs.runCommand "dmroute-ng-linux-release"
              {
                nativeBuildInputs = [
                  pkgs.gnutar
                  pkgs.gzip
                ];
              }
              ''
                mkdir -p "$out/DMRoute-ng-linux-x64"
                cp -a ${server}/lib/dmroute-ng/. "$out/DMRoute-ng-linux-x64/"

                mkdir -p "$out/DMRoute-ng-playground-linux-x64"
                cp -a ${playground}/lib/dmroute-ng-playground/. "$out/DMRoute-ng-playground-linux-x64/"
                cp ${openDmr}/bin/dmr_codec "$out/DMRoute-ng-playground-linux-x64/"
                cp ${./THIRD-PARTY-NOTICES.md} "$out/DMRoute-ng-playground-linux-x64/"
                cp ${./third_party/OpenDMR/LICENSE} "$out/DMRoute-ng-playground-linux-x64/OpenDMR-LICENSE"
                cp ${./third_party/OpenDMR/decoder/CREDITS} "$out/DMRoute-ng-playground-linux-x64/OpenDMR-decoder-CREDITS"
                cp ${./third_party/OpenDMR/encoder/CREDITS} "$out/DMRoute-ng-playground-linux-x64/OpenDMR-encoder-CREDITS"
                tar -czf "$out/DMRoute-ng-playground-linux-x64/OpenDMR-1.0.0+dmroute.2-source.tar.gz" \
                  -C ${./third_party} OpenDMR
                tar -czf "$out/DMRoute-ng-playground-linux-x64.tar.gz" \
                  -C "$out" DMRoute-ng-playground-linux-x64

                mkdir -p "$out/docs"
                cp ${documentation}/DMRoute-ng-Handbuch.pdf "$out/docs/"
                cp ${documentation}/DMRoute-ng-Hardwaretests.pdf "$out/docs/"
              '';
        in
        {
          inherit openDmr documentation;
          blip25-encoder = blip25Encoder;
          default = blip25Encoder;
        }
        // lib.optionalAttrs (system == "x86_64-linux") {
          inherit server playground releaseBundle;
          default = releaseBundle;
        }
      );

      checks = forAllSystems (
        system:
        let
          packages = self.packages.${system};
        in
        {
          inherit (packages) openDmr documentation;
          blip25-encoder = packages.blip25-encoder;
        }
        // nixpkgs.lib.optionalAttrs (system == "x86_64-linux") {
          inherit (packages) releaseBundle;
        }
      );

      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              asciidoctor-with-extensions
              cargo
              clang
              dotnetCorePackages.sdk_9_0
              gcc
              git
              gnumake
              nixfmt
              pkg-config
              rustc
            ];
            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1
            '';
          };
        }
      );

      formatter = forAllSystems (system: (import nixpkgs { inherit system; }).nixfmt);
    };
}
