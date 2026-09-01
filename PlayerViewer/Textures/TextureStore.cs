using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using GLFrameworkEngine;
using PlayerViewer.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Toolbox.Core;
using BntxFile = Syroot.NintenTools.NSW.Bntx.BntxFile;
using BntxTextureData = Syroot.NintenTools.NSW.Bntx.Texture;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// The model's texture container
    /// </summary>
    public sealed class TextureStore
    {
        readonly BFRES _bfres;
        readonly BfresRender _render;

        public TextureStore(BFRES bfres, BfresRender render)
        {
            _bfres = bfres;
            _render = render;
        }

        /// <summary>The container the model's textures live in, or null for a model that
        /// carries none.</summary>
        public BntxFile Bntx
        {
            get
            {
                var res = _bfres?.ResFile;
                if (res == null)
                    return null;
                foreach (var file in res.ExternalFiles)
                    if (file.Value.LoadedFileData is BntxFile bntx)
                        return bntx;
                return null;
            }
        }

        public IEnumerable<KeyValuePair<string, STGenericTexture>> Textures =>
            _render?.Textures ?? new Dictionary<string, STGenericTexture>();

        //Null is a name an unbound sampler hands over, so it answers no rather than throwing
        //out of whichever row asked.
        public bool Has(string name) =>
            _render != null && name != null && _render.Textures.ContainsKey(name);

        public STGenericTexture Find(string name) =>
            _render != null && name != null && _render.Textures.TryGetValue(name, out var tex)
                ? tex
                : null;

        /// <summary>Materials that name this texture, so a delete can say what it breaks.</summary>
        public List<string> UsedBy(string name)
        {
            var users = new List<string>();
            var res = _bfres?.ResFile;
            if (res == null)
                return users;
            foreach (var model in res.Models.Values)
            foreach (var mat in model.Materials.Values)
                if (mat.TextureRefs.Any(x => x.Name == name))
                    users.Add($"{model.Name}/{mat.Name}");
            return users;
        }

        /// <summary>
        /// Puts a built texture into the container and into the three name keyed registries,
        /// replacing whatever held the name before.
        /// </summary>
        public void Install(BntxTextureData texture)
        {
            var bntx = Bntx;
            if (bntx == null)
                throw new InvalidOperationException("this model has no texture container");

            TextureImport.Install(bntx, texture);
            Forget(texture.Name);

            var wrapper = new BntxTexture(bntx, texture);
            _render.Textures[texture.Name] = wrapper;
            _bfres.Textures.Add(wrapper);

            var res = _bfres.ResFile;
            var shared = new BfresLibrary.Switch.SwitchTexture(bntx, texture);
            res.Textures.Set(texture.Name, shared);
        }

        /// <summary>
        /// Removes a texture everywhere. Materials that named it are left naming it: the
        /// viewer then binds a type correct default and says so, which is more useful than
        /// silently rewriting a material the user did not ask to edit.
        /// </summary>
        public void Delete(string name)
        {
            var bntx = Bntx;
            if (bntx == null)
                return;

            for (int i = bntx.Textures.Count - 1; i >= 0; i--)
                if (bntx.Textures[i].Name == name)
                    bntx.Textures.RemoveAt(i);
            RebuildDict(bntx);

            Forget(name);
            _bfres.ResFile.Textures.RemoveKey(name);
        }

        /// <summary>
        /// Renames a texture and repoints every material texture ref that named it, which is
        /// the whole reason a rename is not a delete plus an install.
        /// </summary>
        public int Rename(string oldName, string newName)
        {
            var bntx = Bntx;
            if (bntx == null)
                throw new InvalidOperationException("this model has no texture container");
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("a texture needs a name", nameof(newName));
            if (newName == oldName)
                return 0;
            if (bntx.Textures.Any(x => x.Name == newName))
                throw new InvalidOperationException($"'{newName}' is already taken");

            var texture = bntx.Textures.FirstOrDefault(x => x.Name == oldName);
            if (texture == null)
                throw new InvalidOperationException($"no texture called '{oldName}'");

            texture.Name = newName;
            RebuildDict(bntx);

            Forget(oldName);
            _bfres.ResFile.Textures.RemoveKey(oldName);

            var wrapper = new BntxTexture(bntx, texture);
            _render.Textures[newName] = wrapper;
            _bfres.Textures.Add(wrapper);
            var shared = new BfresLibrary.Switch.SwitchTexture(bntx, texture);
            _bfres.ResFile.Textures.Set(newName, shared);

            int repointed = 0;
            foreach (var model in _bfres.ResFile.Models.Values)
            foreach (var mat in model.Materials.Values)
            foreach (var reference in mat.TextureRefs)
                if (reference.Name == oldName)
                {
                    reference.Name = newName;
                    repointed++;
                }
            return repointed;
        }

        //Drops the GL side and the two managed lists for a name. The BNTX entry is the
        //caller's to move.
        void Forget(string name)
        {
            if (_render == null || !_render.Textures.TryGetValue(name, out var old))
                return;
            TextureDataPrefetch.Remove(old);
            (old.RenderableTex as GLTexture)?.Dispose();
            old.RenderableTex = null;
            _render.Textures.Remove(name);
            _bfres.Textures.Remove(old);
        }

        static void RebuildDict(BntxFile bntx)
        {
            bntx.TextureDict.Clear();
            foreach (var tex in bntx.Textures)
                bntx.TextureDict.Add(tex.Name);
        }

        /// <summary>Why an image cannot be read over this texture, or null when it can. The
        /// import writes one flat 2D layer.</summary>
        public static string ReplaceRefusal(STGenericTexture texture)
        {
            if (texture == null)
                return null;
            if (texture.SurfaceType != STSurfaceType.Texture2D)
                return $"a {texture.SurfaceType} texture cannot be replaced by a flat image";
            if (texture.ArrayCount > 1)
                return $"a {texture.ArrayCount} layer array cannot be replaced by a flat image";
            if (texture.Depth > 1)
                return $"a {texture.Depth} deep volume cannot be replaced by a flat image";
            return null;
        }

        /// <summary>Whether the texture reads as sRGB, which an export has to record so a
        /// re-import does not change what the shader sees.</summary>
        public static bool IsSrgb(STGenericTexture texture) =>
            texture.Platform.OutputFormat.ToString().EndsWith("SRGB", StringComparison.Ordinal);

        /// <summary>
        /// Writes mip 0 as a PNG.
        /// </summary>
        public static void ExportPng(STGenericTexture texture, string path)
        {
            using var image = Decode(texture);
            image.SaveAsPng(path);
        }

        public static Image<Rgba32> Decode(STGenericTexture texture)
        {
            var wide = BlockDecoder.Decode(texture);
            if (wide != null)
                return wide;

            byte[] bgra = texture.GetDecodedSurface();
            int width = (int)texture.Width;
            int height = (int)texture.Height;
            int decoded = bgra.Length / 4;
            int alignedWidth = width,
                alignedHeight = height;
            if (decoded != width * height)
            {
                alignedWidth = (width + 3) / 4 * 4;
                alignedHeight = (height + 3) / 4 * 4;
            }
            if (bgra.Length < alignedWidth * alignedHeight * 4)
                throw new InvalidOperationException(
                    $"the decoder returned {bgra.Length} bytes for a "
                        + $"{alignedWidth}x{alignedHeight} surface"
                );

            var image = Image
                .LoadPixelData<Bgra32>(bgra, alignedWidth, alignedHeight)
                .CloneAs<Rgba32>();
            if (alignedWidth != width || alignedHeight != height)
                image.Mutate(x => x.Crop(new Rectangle(0, 0, width, height)));
            return image;
        }

        /// <summary>The format a re-import should ask for, as a plain word for the file.</summary>
        public static string FormatName(STGenericTexture texture) =>
            texture.Platform.OutputFormat.ToString();
    }
}
