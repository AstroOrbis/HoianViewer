# HoianViewer

Standalone(as in, a separate exe, not part of the game) Splatoon 3 player viewer (and other models viewer), aiming to replicate Splatoon 3's PlayerCustomPart/Mgr stuff and rendering. You can see (almost exactly - there may be bugs, and hair collision is not 100% exact) how your player mods look like in game, or in general make promo-like player renders.

## Building

Requires .NET 10 SDK.

```
cd PlayerViewer
dotnet build -c Release
```

Which builds to  `PlayerViewer/bin/Release/net10.0/PlayerViewer.exe`.


For a self contained single file build:

```
cd PlayerViewer
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Move the whole `publish` folder, not just the exe: `Shaders/`, `Resources/`, `Plugins/` and
`Lib/` are read from disk beside it at runtime.


`dotnet tool restore` is needed if you want to run csharpier for formatting.

NuGet dependencies are pinned in `deps.json` for nix, so after changing a `PackageReference`:

```
nix run .#fetch-deps -- ./deps.json
```

## Setup

On first launch, the viewer asks for a romfs path. Point it at a Splatoon 3 romfs dump of the version you are using (you can dump it with Ryujinx -> Right Click Splatoon 3 -> Dump -> Romfs). The path is saved to `config.json` next to the exe, you can change it there or in File dropdown in the app if you want to change the version.

Shader archives (`.bfsha`) are read from `romfs/ShaderData/` at runtime. The first launch compiles and caches all referenced shader programs, which may lag a little, subsequent launches reuse the cache.

## Layered filesystem

The romfs loader supports romfs mods. If you have a mod that overrides files (atmosphere-style romfs directory), put the override files in the same directory structure alongside the base romfs. The loader checks the layered path first, then falls back to the base dump.

## Drag and drop

You can drag `.bfres` or `.bfres.zs` files onto the viewer window to open them as standalone models. Animations embedded in the file show up in a dropdown.

## What it does

**Player mode**: Recreation of the PlayerViewer functionality. Note that the Hair Physics, while directly using the havok cloth data, are not necessarily 100% accurately simulated, since I didn't make a proper decomp of havok clothes (but they are directly using the havok cloth data, and are mostly accurate)

**Standalone mode**: you can also load any BFRES model. Skeletal animations will be listed, and playing them will play their corresponding material, texture pattern, and visibility animations. Individual meshes can be toggled on/off.

**Recording**: captures animation loops to mp4/webm(transparent)/webp(transparent) via ffmpeg (must be on PATH or in same folder as the app). 

**Environment**: switch between the Viewer lighting and the AutoWalk stage lighting. You can also toggle Shadow Prepass (the models casting shadows)

**Material Editor**: Edit models' materials and textures with a live preview, and for materials, you are able to generate brand new shader variations, embedding the bfsha directly within the model file.

## Project layout

```
PlayerViewer/          the viewer app
Cafe-Shader-Studio/    rendering engine
ShaderLibrary/         shader binary parser
ShaderBundler/         ubersplicer interface/custom bfsha packer
Gsys/                  shared shaderopt derivation
```

## Credits

The viewer itself, Splatoon 3 Renderer & various fixes by [nvnprogram](https://github.com/nvnprogram).

Various features (supersampler, anim loop export, etc) & CI by [AstroOrbis](https://github.com/AstroOrbis).

Original versions of Cafe Shader Studio and ShaderLibrary by [KillzXGaming](https://github.com/killzxgaming).

Base reference for loading the bphcl file (+ some bugfixes) by [RAMDRAGONS](https://github.com/RAMDRAGONS)

General assistance with models and stuff [OctoSquiddy](https://github.com/OctoSquiddy)
