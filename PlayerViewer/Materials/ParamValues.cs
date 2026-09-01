using System;
using System.Globalization;
using System.Text;
using BfresLibrary;
using Syroot.Maths;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// The shapes a shader param value comes in, in one place: scalars and arrays of float,
    /// int, uint and bool, the three SRT structs, and raw bytes for the reserved types.
    /// </summary>
    public static class ParamValues
    {
        //An array is mutated in place by the editor; everything else is a value type that
        //boxing already copied.
        public static object Clone(object value) => value is Array array ? array.Clone() : value;

        /// <summary>
        /// A comparable spelling of a value. The SRT structs get their fields spelled out
        /// because a struct's default ToString is its type name.
        /// </summary>
        public static string Key(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case Array array:
                {
                    var text = new StringBuilder("[");
                    foreach (object element in array)
                        text.Append(Convert.ToString(element, CultureInfo.InvariantCulture))
                            .Append(',');
                    return text.Append(']').ToString();
                }
                case TexSrt srt:
                    return $"texsrt:{srt.Mode}:{srt.Scaling.X},{srt.Scaling.Y}:{srt.Rotation}:"
                        + $"{srt.Translation.X},{srt.Translation.Y}";
                case Srt2D srt:
                    return $"srt2d:{srt.Scaling.X},{srt.Scaling.Y}:{srt.Rotation}:"
                        + $"{srt.Translation.X},{srt.Translation.Y}";
                case Srt3D srt:
                    return $"srt3d:{srt.Scaling.X},{srt.Scaling.Y},{srt.Scaling.Z}:"
                        + $"{srt.Rotation.X},{srt.Rotation.Y},{srt.Rotation.Z}:"
                        + $"{srt.Translation.X},{srt.Translation.Y},{srt.Translation.Z}";
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?";
            }
        }

        /// <summary>The value as a transfer file entry. A shape this does not know is
        /// written as Unsupported and skipped on the way back.</summary>
        public static MaterialTransfer.ValueEntry ToEntry(string name, object value)
        {
            var entry = new MaterialTransfer.ValueEntry { Name = name, Type = "Unsupported" };
            switch (value)
            {
                case float f:
                    entry.Type = "Float";
                    entry.Floats = new[] { f };
                    break;
                case float[] floats:
                    entry.Type = "FloatArray";
                    entry.Floats = floats;
                    break;
                case int n:
                    entry.Type = "Int";
                    entry.Ints = new[] { n };
                    break;
                case int[] ints:
                    entry.Type = "IntArray";
                    entry.Ints = ints;
                    break;
                case uint u:
                    entry.Type = "UInt";
                    entry.Uints = new[] { u };
                    break;
                case uint[] uints:
                    entry.Type = "UIntArray";
                    entry.Uints = uints;
                    break;
                case bool b:
                    entry.Type = "Bool";
                    entry.Bools = new[] { b };
                    break;
                case bool[] bools:
                    entry.Type = "BoolArray";
                    entry.Bools = bools;
                    break;
                case byte[] bytes:
                    entry.Type = "Bytes";
                    entry.Bytes = Convert.ToBase64String(bytes);
                    break;
                case TexSrt srt:
                    entry.Type = "TexSrt";
                    entry.Ints = new[] { (int)srt.Mode };
                    entry.Floats = new[]
                    {
                        srt.Scaling.X,
                        srt.Scaling.Y,
                        srt.Rotation,
                        srt.Translation.X,
                        srt.Translation.Y,
                    };
                    break;
                case Srt2D srt:
                    entry.Type = "Srt2D";
                    entry.Floats = new[]
                    {
                        srt.Scaling.X,
                        srt.Scaling.Y,
                        srt.Rotation,
                        srt.Translation.X,
                        srt.Translation.Y,
                    };
                    break;
                case Srt3D srt:
                    entry.Type = "Srt3D";
                    entry.Floats = new[]
                    {
                        srt.Scaling.X,
                        srt.Scaling.Y,
                        srt.Scaling.Z,
                        srt.Rotation.X,
                        srt.Rotation.Y,
                        srt.Rotation.Z,
                        srt.Translation.X,
                        srt.Translation.Y,
                        srt.Translation.Z,
                    };
                    break;
            }
            return entry;
        }

        /// <summary>
        /// The entry as a value of the same shape as the one it replaces, or null when the
        /// shapes disagree. The target's shape wins because the shading model decides it.
        /// </summary>
        public static object FromEntry(MaterialTransfer.ValueEntry entry, object current)
        {
            switch (current)
            {
                case float when entry.Floats?.Length >= 1:
                    return entry.Floats[0];
                case float[] existing when entry.Floats?.Length == existing.Length:
                    return (float[])entry.Floats.Clone();
                case int when entry.Ints?.Length >= 1:
                    return entry.Ints[0];
                case int[] existing when entry.Ints?.Length == existing.Length:
                    return (int[])entry.Ints.Clone();
                case uint when entry.Uints?.Length >= 1:
                    return entry.Uints[0];
                case uint[] existing when entry.Uints?.Length == existing.Length:
                    return (uint[])entry.Uints.Clone();
                case bool when entry.Bools?.Length >= 1:
                    return entry.Bools[0];
                case bool[] existing when entry.Bools?.Length == existing.Length:
                    return (bool[])entry.Bools.Clone();
                case byte[] existing when entry.Bytes != null:
                {
                    var bytes = Convert.FromBase64String(entry.Bytes);
                    return bytes.Length == existing.Length ? bytes : null;
                }
                case TexSrt when entry.Floats?.Length == 5:
                    return new TexSrt
                    {
                        Mode = (TexSrtMode)(entry.Ints?.Length > 0 ? entry.Ints[0] : 0),
                        Scaling = new Vector2F(entry.Floats[0], entry.Floats[1]),
                        Rotation = entry.Floats[2],
                        Translation = new Vector2F(entry.Floats[3], entry.Floats[4]),
                    };
                case Srt2D when entry.Floats?.Length == 5:
                    return new Srt2D
                    {
                        Scaling = new Vector2F(entry.Floats[0], entry.Floats[1]),
                        Rotation = entry.Floats[2],
                        Translation = new Vector2F(entry.Floats[3], entry.Floats[4]),
                    };
                case Srt3D when entry.Floats?.Length == 9:
                    return new Srt3D
                    {
                        Scaling = new Vector3F(entry.Floats[0], entry.Floats[1], entry.Floats[2]),
                        Rotation = new Vector3F(entry.Floats[3], entry.Floats[4], entry.Floats[5]),
                        Translation = new Vector3F(
                            entry.Floats[6],
                            entry.Floats[7],
                            entry.Floats[8]
                        ),
                    };
                default:
                    return null;
            }
        }
    }
}
