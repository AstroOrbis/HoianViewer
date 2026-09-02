using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// One material draw's complete option vector, and the uberspec <c>--options-file</c>
    /// that splices a shader out of it.
    /// </summary>
    public sealed class OptionVector
    {
        /// <summary>Every option of the serving archive at its derived or default choice,
        /// ordinal sorted. This is what the key row is written from.</summary>
        public SortedDictionary<string, string> Full { get; } = new(StringComparer.Ordinal);

        /// <summary>The subset the options file carries: options whose value differs from
        /// the option table's default choice.</summary>
        public SortedDictionary<string, string> NonDefault { get; } = new(StringComparer.Ordinal);

        /// <summary>Options the material states that the serving archive does not know,
        /// either by name or by choice. They stay at that archive's default.</summary>
        public List<string> Dropped { get; } = new();

        /// <summary>Options the ubershader cannot express, by name, by choice or by having no
        /// code for the value. Non empty is a report, not a crash: the caller decides whether
        /// to refuse the variation or warn about it.</summary>
        public List<string> Unsupported { get; } = new();

        /// <summary>
        /// The value the bfres reader hands back for an option a material does not store.
        /// Same sentinel the renderer uses.
        /// </summary>
        public const string Unset = "<Default Value>";

        /// <summary>Stable identity of the whole vector, for cache keys.</summary>
        public string Hash { get; private set; }

        /// <param name="serving">The archive whose key table the material will be looked up
        /// in, which is also the archive whose defaults complete the vector.</param>
        /// <param name="table">The option table the specialiser will be given.</param>
        /// <param name="derived">The options the engine sets for this draw. Comes from
        /// Gsys.GsysShaderOptions, which is the renderer's own derivation; this
        /// library completes it against the archive rather than deriving it a second time.</param>
        public static OptionVector Build(
            ShaderModel serving,
            UberOptionTable table,
            IReadOnlyDictionary<string, string> derived,
            uint vertexSkinCount,
            string assignType
        )
        {
            if (serving == null)
                throw new ArgumentNullException(nameof(serving));
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            if (derived == null)
                throw new ArgumentNullException(nameof(derived));

            var v = new OptionVector();

            Complete(serving, derived, v.Full, v.Dropped);

            foreach (var kv in v.Full)
            {
                //uberspec skips an option name its table does not carry and still exits 0.
                var row = table.Find(kv.Key);
                if (row == null)
                {
                    v.Unsupported.Add(
                        $"{kv.Key}: not an option of the ubershader '{table.ModelName}'"
                    );
                    continue;
                }

                if (Array.IndexOf(row.ChoiceNames, kv.Value) < 0)
                {
                    v.Unsupported.Add(
                        $"{kv.Key}={kv.Value}: the ubershader declares only "
                            + $"{row.ChoiceNames.Length} choice(s) for it"
                    );
                    continue;
                }
                if (kv.Value != row.ChoiceNames[row.DefaultChoiceIdx])
                    v.NonDefault[kv.Key] = kv.Value;
            }

            foreach (var name in UberArchive.Inexpressible)
                if (v.NonDefault.ContainsKey(name))
                    v.Unsupported.Add(
                        $"{name}={v.NonDefault[name]}: the ubershader has no code for it"
                    );

            v.Hash = XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(v.Canonical())).ToString("x32");
            return v;
        }

        /// <summary>
        /// The complete option vector the engine looks a program up with: every option the
        /// archive declares at its own default, then the ones the material derives written
        /// over the top. This half needs nothing but the serving archive, which is why it is
        /// separate: the existence question can be answered without the ubershader.
        /// </summary>
        public static void Complete(
            ShaderModel serving,
            IReadOnlyDictionary<string, string> derived,
            IDictionary<string, string> full,
            List<string> dropped
        )
        {
            for (int i = 0; i < serving.StaticOptions.Count; i++)
                full[serving.StaticOptions[i].Name] = serving.StaticOptions[i].DefaultChoice;
            for (int i = 0; i < serving.DynamicOptions.Count; i++)
                full[serving.DynamicOptions[i].Name] = serving.DynamicOptions[i].DefaultChoice;

            foreach (var kv in derived)
            {
                string value = Normalise(kv.Value);
                var option = Find(serving, kv.Key);
                if (option == null)
                {
                    dropped?.Add($"{kv.Key}: not an option of '{serving.Name}'");
                    continue;
                }
                if (option.Choices.GetIndex(value) < 0)
                {
                    dropped?.Add($"{kv.Key}={value}: not a choice '{serving.Name}' declares");
                    continue;
                }
                full[kv.Key] = value;
            }
        }

        static ShaderOption Find(ShaderModel sm, string name) =>
            sm.StaticOptions.ContainsKey(name) ? sm.StaticOptions[name]
            : sm.DynamicOptions.ContainsKey(name) ? sm.DynamicOptions[name]
            : null;

        /// <summary>The bfres stores some option values as booleans while the archive names
        /// those choices "0" and "1". Done here so nothing downstream has to.</summary>
        public static string Normalise(string value) =>
            value == "True" ? "1"
            : value == "False" ? "0"
            : value;

        string Canonical()
        {
            var sb = new StringBuilder();
            foreach (var kv in Full)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        /// <summary>The <c>--options-file</c> body.</summary>
        public string ToOptionsFile()
        {
            var sb = new StringBuilder();
            foreach (var kv in NonDefault)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            return sb.ToString();
        }
    }
}
