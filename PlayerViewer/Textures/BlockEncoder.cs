using System;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// Turns one mip level into the bytes its surface format stores.
    /// </summary>
    public static class BlockEncoder
    {
        public static byte[] Encode(Image<Rgba32> image, TextureFormat format)
        {
            if (format.Compression == null)
            {
                var raw = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(raw);
                return raw;
            }

            using var padded = Pad(image);

            var pixels = new ColorRgba32[padded.Width * padded.Height];
            padded.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        pixels[y * rows.Width + x] = new ColorRgba32(p.R, p.G, p.B, p.A);
                    }
                }
            });

            var encoder = new BcEncoder
            {
                OutputOptions =
                {
                    GenerateMipMaps = false,
                    Quality = CompressionQuality.BestQuality,
                    Format = Compression(format, padded),
                },
            };
            byte[] blocks = encoder.EncodeToRawBytes(
                new ReadOnlyMemory2D<ColorRgba32>(pixels, padded.Height, padded.Width)
            )[0];

            if (format.Signed)
                ToSigned(blocks, format.Compression == CompressionFormat.Bc5);
            return blocks;
        }

        static CompressionFormat Compression(TextureFormat format, Image<Rgba32> image)
        {
            if (format.Compression == CompressionFormat.Bc1 && HasAlpha(image))
                return CompressionFormat.Bc1WithAlpha;

            return format.Compression.Value;
        }

        static bool HasAlpha(Image<Rgba32> image)
        {
            bool alpha = false;
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height && !alpha; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        if (row[x].A != 255)
                        {
                            alpha = true;
                            break;
                        }
                }
            });
            return alpha;
        }

        static void ToSigned(byte[] blocks, bool twoChannel)
        {
            int stride = twoChannel ? 16 : 8;
            for (int i = 0; i + stride <= blocks.Length; i += stride)
            {
                blocks[i] ^= 0x80;
                blocks[i + 1] ^= 0x80;
                if (twoChannel)
                {
                    blocks[i + 8] ^= 0x80;
                    blocks[i + 9] ^= 0x80;
                }
            }
        }

        static Image<Rgba32> Pad(Image<Rgba32> image)
        {
            int w = (image.Width + 3) / 4 * 4;
            int h = (image.Height + 3) / 4 * 4;
            if (w == image.Width && h == image.Height)
                return image.Clone();

            var padded = new Image<Rgba32>(w, h);
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(y, image.Height - 1);
                for (int x = 0; x < w; x++)
                    padded[x, y] = image[Math.Min(x, image.Width - 1), sy];
            }
            return padded;
        }
    }
}
