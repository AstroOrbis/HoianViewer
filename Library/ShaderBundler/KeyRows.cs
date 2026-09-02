using System.Collections.Generic;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// Reads a program's key row back into option choices, which is what a program carried
    /// from one archive into another is re-keyed from.
    /// </summary>
    public static class KeyRows
    {
        /// <summary>The choice name of every option for a program, or null when a row holds a
        /// choice index the option does not declare.</summary>
        public static Dictionary<string, string> ReadOptions(ShaderModel sm, int program)
        {
            int stride = sm.StaticKeyLength + sm.DynamicKeyLength;
            int at = program * stride;
            if (at < 0 || at + stride > sm.KeyTable.Length)
                return null;

            var options = new Dictionary<string, string>();
            for (int i = 0; i < sm.StaticOptions.Count; i++)
            {
                var option = sm.StaticOptions[i];
                int choice = option.GetChoiceIndex(sm.KeyTable[at + option.Bit32Index]);
                if (choice < 0 || choice >= option.Choices.Count)
                    return null;
                options[option.Name] = option.Choices.GetKey(choice);
            }
            for (int i = 0; i < sm.DynamicOptions.Count; i++)
            {
                var option = sm.DynamicOptions[i];
                int word = sm.StaticKeyLength + (option.Bit32Index - option.KeyOffset);
                int choice = option.GetChoiceIndex(sm.KeyTable[at + word]);
                if (choice < 0 || choice >= option.Choices.Count)
                    return null;
                options[option.Name] = option.Choices.GetKey(choice);
            }
            return options;
        }

        /// <summary>The static half of a program's row, as a comparable string.</summary>
        public static string StaticSignature(ShaderModel sm, int program)
        {
            int stride = sm.StaticKeyLength + sm.DynamicKeyLength;
            return Signature(sm.KeyTable, program * stride, sm.StaticKeyLength);
        }

        /// <summary>The static half of a key the way <see cref="StaticSignature(ShaderModel, int)"/>
        /// spells it.</summary>
        public static string StaticSignature(ShaderModel sm, int[] key) =>
            key == null ? null : Signature(key, 0, sm.StaticKeyLength);

        public static string Signature(int[] key) =>
            key == null ? null : Signature(key, 0, key.Length);

        static string Signature(int[] words, int offset, int count)
        {
            var sb = new System.Text.StringBuilder(count * 9);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(words[offset + i]);
            }
            return sb.ToString();
        }
    }
}
