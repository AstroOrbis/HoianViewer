using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Syroot.NintenTools.NSW.Bntx;
using Syroot.NintenTools.NSW.Bntx.GFX;
using Toolbox.Core.Imaging;
using BntxTextureData = Syroot.NintenTools.NSW.Bntx.Texture;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// Builds BNTX textures from image files, or from a flat colour, in any of the formats
    /// <see cref="TextureFormats"/> offers. The block layout is the console packager's, so a
    /// built surface is byte for byte what the game would have shipped for those pixels.
    /// </summary>
    public static class TextureImport
    {
        /// <summary>
        /// Texture names a sampler added by the editor points at until the user picks a real
        /// one. The save generates one texture per name still referenced and removes one no
        /// longer referenced. White exists because the game's bake shadow and opacity dummies
        /// are white, and black there would shadow or cut the whole material.
        /// </summary>
        public const string BlackName = "PV_AutoBlack";
        public const string WhiteName = "PV_AutoWhite";

        public static readonly (string Name, Rgba32 Colour)[] Generated =
        {
            (BlackName, new Rgba32(0, 0, 0, 255)),
            (WhiteName, new Rgba32(255, 255, 255, 255)),
        };

        public static bool IsGenerated(string name) => name == BlackName || name == WhiteName;

        /// <summary>The one texture that serves every slot naming a generated name.</summary>
        public static BntxTextureData Generate(string name, BntxTextureData donor)
        {
            foreach (var (candidate, colour) in Generated)
                if (candidate == name)
                    return Solid(name, 4, colour, donor);
            throw new ArgumentException($"'{name}' is not a generated texture name", nameof(name));
        }

        /// <summary>Decodes an image file and builds a block linear texture from it.</summary>
        public static BntxTextureData FromFile(
            string path,
            string name,
            TextureFormat format,
            BntxTextureData donor
        )
        {
            using var image = Image.Load<Rgba32>(path);
            return FromImage(image, name, format, donor);
        }

        /// <summary>Decodes an image file and picks the format the shipped textures use for
        /// the sampler it is being bound to.</summary>
        public static BntxTextureData FromFile(
            string path,
            string name,
            string assignKey,
            BntxTextureData donor,
            out TextureFormat chosen
        )
        {
            using var image = Image.Load<Rgba32>(path);
            chosen = TextureFormats.Suggest(assignKey, image);
            return FromImage(image, name, chosen, donor);
        }

        /// <summary>A one mip square of a single colour, for the generated default.</summary>
        public static BntxTextureData Solid(
            string name,
            int size,
            Rgba32 colour,
            BntxTextureData donor
        )
        {
            using var image = new Image<Rgba32>(size, size, colour);
            return FromImage(
                image,
                name,
                TextureFormats.Find(SurfaceFormat.R8_G8_B8_A8_UNORM),
                donor,
                mips: false
            );
        }

        public static BntxTextureData FromImage(
            Image<Rgba32> image,
            string name,
            TextureFormat format,
            BntxTextureData donor,
            bool mips = true
        )
        {
            uint width = (uint)image.Width;
            uint height = (uint)image.Height;
            if (width == 0 || height == 0)
                throw new InvalidOperationException("the image has no pixels");

            uint mipCount = mips ? MipCount(width, height) : 1;
            var linear = LinearMipChain(image, mipCount, format);

            var swizzle = new SwitchSwizzle(format.Tex) { Target = 1 };
            uint imageOffset = 0;
            var swizzled = swizzle.SwizzleSurfaceMipMaps(
                linear,
                width,
                height,
                1,
                mipCount,
                ref imageOffset,
                1
            );

            var surface = new List<byte[]>();
            for (int mip = 0; mip < swizzled.Count; mip++)
                surface.Add(Concat(swizzled.Skip(mip)));

            var texture = new BntxTextureData
            {
                Name = name,
                Path = "",
                Width = width,
                Height = height,
                Depth = 1,
                ArrayLength = 1,
                MipCount = mipCount,
                SampleCount = 1,
                Format = format.Surface,
                Dim = Dim.Dim2D,
                SurfaceDim = SurfaceDim.Dim2D,
                TileMode = TileMode.Default,
                Swizzle = 0,
                Alignment = 512,
                Pitch = 0,
                AccessFlags = AccessFlags.Texture,
                ImageSize = swizzle.ImageSize,
                MipOffsets = swizzle.MipOffsets.Select(x => (long)x).ToArray(),
                TextureData = new List<List<byte[]>> { surface },
                ChannelRed = ChannelType.Red,
                ChannelGreen = ChannelType.Green,
                ChannelBlue = ChannelType.Blue,
                ChannelAlpha = ChannelType.Alpha,
                ReadTextureLayout = 1,
                sparseBinding = 0,
                sparseResidency = 0,
                IsResTexture = true,
                BlockHeightLog2 = swizzle.BlockHeightLog2,
                BlockDepthLog2 = 0,
                TileWidthLog2 = 0,
                PackagerVersion = 0,
                UserData = new List<UserData>(),
                UserDataDict = new ResDict(),
            };

            if (donor != null)
            {
                texture.AccessFlags = donor.AccessFlags;
                texture.Alignment = donor.Alignment;
                texture.PackagerVersion = donor.PackagerVersion;
                texture.TileWidthLog2 = donor.TileWidthLog2;
                texture.IsResTexture = donor.IsResTexture;
            }
            return texture;
        }

        /// <summary>Full chain down to 1x1, which is what a stock texture carries.</summary>
        static uint MipCount(uint width, uint height)
        {
            uint count = 1;
            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
                count++;
            }
            return count;
        }

        static byte[] LinearMipChain(Image<Rgba32> image, uint mipCount, TextureFormat format)
        {
            var buffers = new List<byte[]>();
            for (int mip = 0; mip < mipCount; mip++)
            {
                int w = Math.Max(1, image.Width >> mip);
                int h = Math.Max(1, image.Height >> mip);
                if (mip == 0)
                    buffers.Add(BlockEncoder.Encode(image, format));
                else
                {
                    using var level = image.Clone(x => x.Resize(w, h, KnownResamplers.Box));
                    buffers.Add(BlockEncoder.Encode(level, format));
                }
            }
            return Concat(buffers);
        }

        static byte[] Concat(IEnumerable<byte[]> parts)
        {
            var list = parts as IList<byte[]> ?? parts.ToList();
            var output = new byte[list.Sum(x => x.Length)];
            int offset = 0;
            foreach (var part in list)
            {
                Buffer.BlockCopy(part, 0, output, offset, part.Length);
                offset += part.Length;
            }
            return output;
        }

        /// <summary>A name not already taken in the file, derived from the source file name.</summary>
        public static string UniqueName(BntxFile bntx, string wanted)
        {
            string clean = new string(
                (wanted ?? "").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()
            );
            if (clean.Length == 0)
                clean = "Imported";
            if (!bntx.Textures.Any(x => x.Name == clean))
                return clean;
            for (int i = 1; ; i++)
            {
                string candidate = clean + "_" + i;
                if (!bntx.Textures.Any(x => x.Name == candidate))
                    return candidate;
            }
        }

        /// <summary>Puts a built texture into the container, replacing one of the same name.</summary>
        public static void Install(BntxFile bntx, BntxTextureData texture)
        {
            int existing = -1;
            for (int i = 0; i < bntx.Textures.Count; i++)
                if (bntx.Textures[i].Name == texture.Name)
                    existing = i;

            if (existing >= 0)
                bntx.Textures[existing] = texture;
            else
                bntx.Textures.Add(texture);

            bntx.TextureDict.Clear();
            foreach (var tex in bntx.Textures)
                bntx.TextureDict.Add(tex.Name);
        }
    }
}
