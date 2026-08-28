{
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    {  self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f (pkgsFor system));
      pkgsFor =
        system:
        import nixpkgs {
          inherit system;
          config.allowUnfree = true; # prebuilt .net is unfree
        };

      sdkFor = pkgs: pkgs.dotnetCorePackages.sdk_10_0;

      runtimeLibsFor =
        pkgs: with pkgs; [
          libglvnd
          libGL
          libx11
          libxi
          libxrandr
          libxcursor
          libxext
          libxinerama
          libxkbcommon
          icu
          fontconfig
          freetype
          zlib
          openssl
          stdenv.cc.cc.lib
        ];

      packageFor =
        pkgs:
        let
          dotnet = sdkFor pkgs;
        in
        pkgs.buildDotnetModule {
          pname = "hoianviewer";
          version = "0.1.0";

          src = ./.;

          projectFile = "PlayerViewer/PlayerViewer.csproj";
          nugetDeps = ./deps.json;

          dotnet-sdk = dotnet;
          dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;

          executables = [ "PlayerViewer" ];

          runtimeDeps = runtimeLibsFor pkgs;

          makeWrapperArgs = [
            # ffmpeg for export, zenity for tinyfiledialogs' file pickers
            "--prefix"
            "PATH"
            ":"
            (pkgs.lib.makeBinPath [
              pkgs.ffmpeg
              pkgs.zenity
            ])
          ]
          ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [
            "--prefix"
            "LD_LIBRARY_PATH"
            ":"
            "/run/opengl-driver/lib"
          ];

          meta = {
            description = "Standalone Splatoon 3 player/model viewer";
            homepage = "https://github.com/nvnprogram/HoianViewer";
            mainProgram = "PlayerViewer";
            platforms = dotnet.meta.platforms;
          };
        };
    in
    {
      packages = forAllSystems (pkgs: {
        default = packageFor pkgs;
        hoianviewer = packageFor pkgs;
      });

      apps = forAllSystems (
        pkgs:
        let
          pkg = packageFor pkgs;
        in
        {
          default = {
            type = "app";
            program = "${pkg}/bin/PlayerViewer";
          };

          # nix run .#fetch-deps -- ./deps.json
          fetch-deps = {
            type = "app";
            program = "${pkg.fetch-deps}";
          };
        }
      );

      devShells = forAllSystems (
        pkgs:
        let
          dotnet = sdkFor pkgs;
          runtimeLibs = runtimeLibsFor pkgs;
        in
        {
          default = pkgs.mkShell {
            name = "hoianviewer-dotnet10";

            # ffmpeg for export, zenity for tinyfiledialogs' file pickers
            packages = [
              dotnet
              pkgs.ffmpeg
              pkgs.zenity
            ];

            env = {
              DOTNET_ROOT = "${dotnet}";
              DOTNET_CLI_TELEMETRY_OPTOUT = "1";
              DOTNET_NOLOGO = "1";
            };

            shellHook = ''
              export NUGET_PACKAGES="$PWD/.nuget/packages"
              export LD_LIBRARY_PATH="/run/opengl-driver/lib:${pkgs.lib.makeLibraryPath runtimeLibs}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
              launch() {
                dotnet run --project PlayerViewer -c "''${CONFIG:-Debug}" "$@"
              }
              launch_tracy() {
                DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 \
                  dotnet run --project PlayerViewer -c "''${CONFIG:-Release}" --property:Tracy=true "$@"
              }
            '';
          };
        }
      );

      formatter = forAllSystems (pkgs: pkgs.nixpkgs-fmt);
    };
}
