using System;
using System.Linq;
using BfresLibrary;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// A render info or user data value held as its own arrays, apart from the live object
    /// the editor mutates in place. Exactly one array is set and it decides the type written
    /// back.
    /// </summary>
    public sealed class EntryValue
    {
        public string[] Strings;
        public float[] Floats;
        public int[] Ints;
        public byte[] Bytes;
        public bool Unicode;

        public static EntryValue From(RenderInfo info) =>
            info.Type switch
            {
                RenderInfoType.Single => new EntryValue { Floats = info.GetValueSingles() },
                RenderInfoType.Int32 => new EntryValue { Ints = info.GetValueInt32s() },
                _ => new EntryValue { Strings = info.GetValueStrings() },
            };

        public static EntryValue From(UserData data) =>
            data.Type switch
            {
                UserDataType.Single => new EntryValue { Floats = data.GetValueSingleArray() },
                UserDataType.Int32 => new EntryValue { Ints = data.GetValueInt32Array() },
                UserDataType.Byte => new EntryValue { Bytes = data.GetValueByteArray() },
                UserDataType.WString => new EntryValue
                {
                    Strings = data.GetValueStringArray(),
                    Unicode = true,
                },
                _ => new EntryValue { Strings = data.GetValueStringArray() },
            };

        /// <summary>The type name the transfer file uses.</summary>
        public string TypeName =>
            Floats != null ? "Single"
            : Ints != null ? "Int32"
            : Bytes != null ? "Byte"
            : Unicode ? "WString"
            : "String";

        /// <summary>A comparable spelling of the value.</summary>
        public string Key =>
            Strings != null ? "s:" + string.Join("\u0001", Strings.Select(x => x ?? ""))
            : Floats != null ? "f:" + string.Join(",", Floats)
            : Ints != null ? "i:" + string.Join(",", Ints)
            : Bytes != null ? "b:" + Convert.ToBase64String(Bytes)
            : "empty";

        //Render info has no byte type, so a byte value writes nothing.
        public void WriteTo(RenderInfo info)
        {
            if (Strings != null)
                info.SetValue(Strings);
            else if (Floats != null)
                info.SetValue(Floats);
            else if (Ints != null)
                info.SetValue(Ints);
        }

        public void WriteTo(UserData data)
        {
            if (Strings != null)
                data.SetValue(Strings, Unicode);
            else if (Floats != null)
                data.SetValue(Floats);
            else if (Ints != null)
                data.SetValue(Ints);
            else if (Bytes != null)
                data.SetValue(Bytes);
        }
    }
}
