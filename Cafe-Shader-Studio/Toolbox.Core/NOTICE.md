# Toolbox.Core

Vendored source, by KillzXGaming. Upstream: https://github.com/KillzXGaming/Toolbox.Core

This is the copy that shipped with the original Cafe Shader Studio, not a checkout of either
public branch. It replaces the prebuilt `CafeShaderStudio/Lib/Toolbox.Core.dll` that used to be committed here.

## Changes made to the original source

### `src/Plugins/PluginManager.cs`, `LoadPlugins`

`LoadPlugins` opened `Runtime.ExecutableDir\Toolbox.Core.dll` from disk, read its `AssemblyName`,
and `Assembly.Load`ed it, to scan the built in formats. That assembly is already loaded, so the
file read bought nothing and cost a loose copy of the DLL beside the exe. 

Now the already loaded assembly is used directly:

    loadedLibs.Add("Toolbox.Core.dll");
    assemblies.Add(typeof(PluginManager).Assembly);
    
### Texture debug logging removed

4 `Console.WriteLine` calls on the Switch texture path printed per texture and per mip level,
Removed from `TegraX1Swizzle.GetImageData`, `SwitchSwizzle` and `RGBAPixelDecoder`.