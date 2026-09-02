using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary.Helpers
{
    public class ShaderOptionSearcher
    {
        //Program key table indexed by hashed key vector, built once per shader model.
        //Stands in for the binary search the engine does over its sorted key table.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderModel, Dictionary<KeyVector, int>> _programLookups = new();

        readonly struct KeyVector : IEquatable<KeyVector>
        {
            readonly int[] _keys;
            readonly int _offset;
            readonly int _length;
            readonly int _hash;

            public KeyVector(int[] keys, int offset, int length)
            {
                _keys = keys;
                _offset = offset;
                _length = length;

                int hash = 17;
                for (int i = 0; i < length; i++)
                    hash = hash * 31 + keys[offset + i];
                _hash = hash;
            }

            public bool Equals(KeyVector other)
            {
                if (_length != other._length || _hash != other._hash)
                    return false;
                for (int i = 0; i < _length; i++)
                {
                    if (_keys[_offset + i] != other._keys[other._offset + i])
                        return false;
                }
                return true;
            }

            public override bool Equals(object obj) => obj is KeyVector other && Equals(other);
            public override int GetHashCode() => _hash;
        }

        static Dictionary<KeyVector, int> GetProgramLookup(ShaderModel shader)
        {
            return _programLookups.GetValue(shader, s =>
            {
                int stride = s.StaticKeyLength + s.DynamicKeyLength;
                var lookup = new Dictionary<KeyVector, int>(s.Programs.Count);
                for (int i = 0; i < s.Programs.Count; i++)
                {
                    var key = new KeyVector(s.KeyTable, stride * i, stride);
                    if (!lookup.ContainsKey(key))
                        lookup.Add(key, i);
                }
                return lookup;
            });
        }

        //Profiling: how many program lookups were made and how many found nothing.
        public static readonly Stopwatch SearchTime = new Stopwatch();
        public static int Searches, Misses;

        /// <summary>
        /// Finds the program whose key is exactly the one the given options produce, or -1.
        /// </summary>
        public static int GetProgramIndex(ShaderModel shader, Dictionary<string, string> options)
        {
            SearchTime.Start();
            try
            {
                Searches++;

                int[] key_lookup = WriteOptionKeys(shader, options);
                if (key_lookup != null &&
                    GetProgramLookup(shader).TryGetValue(new KeyVector(key_lookup, 0, key_lookup.Length), out int index))
                    return index;

                Misses++;
                return -1;
            }
            finally { SearchTime.Stop(); }
        }

        /// <summary>
        /// The key vector for a set of option choices, or null if a choice does not exist in
        /// this shader model.
        /// </summary>
        public static int[] WriteOptionKeys(ShaderModel shader, Dictionary<string, string> options)
        {
            //Setup default keys
            int[] key_lookup = WriteDefaultKey(shader);

            //Setup static and dynamic keys
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the static option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    return null;

                option.SetKey(ref key_lookup[option.Bit32Index], choiceIndex);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the dynamic option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    return null;

                int ind = option.Bit32Index - option.KeyOffset;
                option.SetKey(ref key_lookup[shader.StaticKeyLength + ind], choiceIndex);
            }
            return key_lookup;
        }

        /// <summary>
        /// The key every lookup starts from. A shader model can name a program whose key is
        /// the default, in which case the per option default choices are not used at all.
        /// </summary>
        static int[] WriteDefaultKey(ShaderModel shader)
        {
            int num_keys = shader.StaticKeyLength + shader.DynamicKeyLength;

            int[] keys = new int[num_keys];

            if (shader.DefaultProgramIndex != -1 &&
                shader.DefaultProgramIndex < shader.Programs.Count)
            {
                Array.Copy(shader.KeyTable, num_keys * shader.DefaultProgramIndex, keys, 0, num_keys);
                return keys;
            }

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                //Set the default static option choice
                option.SetKey(ref keys[option.Bit32Index], option.DefaultChoiceIdx);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];

                //Set the default dynamic option choice.
                //Dynamic keys live after the static keys and are relative to KeyOffset;
                //writing to Bit32Index directly would corrupt the static key area.
                int ind = option.Bit32Index - option.KeyOffset;
                option.SetKey(ref keys[shader.StaticKeyLength + ind], option.DefaultChoiceIdx);
            }

            return keys;
        }

        public static bool IsValidProgram(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            //The amount of keys used per program
            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            //Static key (total * program index)
            int baseIndex = numKeysPerProgram * programIndex;

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                //The options must be the same between bfres and bfsha
                if (!options.ContainsKey(option.Name))
                    continue;

                //Get key in table
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table! Option {option.Name} choice {options[option.Name]}");

                //If the choice is not in the program, then skip the current program
                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table!");

                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }
            return true;
        }

        //Checks if the shader option list is missing any shader option choices required for a full key search
        public static void CheckMissingShaderOptions(ShaderModel shader, Dictionary<string, string> options)
        {
            int num_keys_per_program = shader.StaticKeyLength + shader.DynamicKeyLength;
            for (int i = 0; i < shader.Programs.Count; i++)
            {
                if (IsValidProgram(shader, i, options))
                    CheckChoices(shader, i, options);
            }
        }

        static void CheckChoices(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            Debug.WriteLine($"checking program {programIndex}");

            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            var maxBit = shader.StaticOptions.Values.Max(x => x.Bit32Index);
            int baseIndex = numKeysPerProgram * programIndex;
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");

                string choice = option.Choices.GetKey(choiceIndex);

                //A shader option choice not set in the lookup and not a default choice
                //This must be set for a valid lookup
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");


                string choice = option.Choices.GetKey(choiceIndex);
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }
        }
    }
}
