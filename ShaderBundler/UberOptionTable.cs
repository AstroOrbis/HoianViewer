using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// uberspec's <c>--options-table</c>, derived from an ubershader archive: for every
    /// option, its choice list and the dword slot of the option constant buffer the
    /// specialised code reads it from.
    ///
    /// This is the specialiser's input, which maps an option to an opt uniform slot and
    /// the value stored there is the choice name as an integer.
    ///
    /// The slot is the option's positional index among the members of the
    /// <c>gsys_shader_option</c> uniform block. Options absent from that block get no slot
    /// and uberspec ignores them.
    /// </summary>
    public sealed class UberOptionTable
    {
        public const string OptionBlockName = "gsys_shader_option";

        public sealed class Row
        {
            public string Name;
            public string[] ChoiceNames;
            public int DefaultChoiceIdx;

            /// <summary>Slot in the option constant buffer, or -1 when the option has no
            /// member in the block and therefore no slot.</summary>
            public int C6OptionIndex = -1;

            public bool HasSlot => C6OptionIndex >= 0;
        }

        public readonly List<Row> Rows = new();
        readonly Dictionary<string, Row> _byName = new(StringComparer.Ordinal);

        /// <summary>
        /// Number of <c>gsys_shader_option</c> members
        /// </summary>
        public int MemberCount { get; private set; }

        public string ModelName { get; private set; }

        public Row Find(string name) => _byName.TryGetValue(name, out var r) ? r : null;

        public static UberOptionTable Build(ShaderModel sm)
        {
            if (sm == null)
                throw new ArgumentNullException(nameof(sm));

            var t = new UberOptionTable { ModelName = sm.Name };
            for (int i = 0; i < sm.StaticOptions.Count; i++)
                t.Add(sm.StaticOptions[i]);
            for (int i = 0; i < sm.DynamicOptions.Count; i++)
                t.Add(sm.DynamicOptions[i]);

            if (sm.UniformBlocks.ContainsKey(OptionBlockName))
            {
                var block = sm.UniformBlocks[OptionBlockName];
                t.MemberCount = block.Uniforms.Count;
                for (int i = 0; i < block.Uniforms.Count; i++)
                {
                    var row = t.Find(block.Uniforms.GetKey(i));
                    if (row != null)
                        row.C6OptionIndex = i;
                }
            }
            return t;
        }

        void Add(ShaderOption o)
        {
            var names = new string[o.Choices.Count];
            for (int i = 0; i < o.Choices.Count; i++)
                names[i] = o.Choices.GetKey(i);

            var row = new Row
            {
                Name = o.Name,
                ChoiceNames = names,
                DefaultChoiceIdx = o.DefaultChoiceIdx,
            };
            Rows.Add(row);
            _byName[row.Name] = row;
        }

        /// <summary>
        /// Refuses an option table and an ubershader that are not the same archive
        /// generation.
        /// </summary>
        public void RequireSameGeneration(ShaderModel uber)
        {
            var other = Build(uber);
            if (other.MemberCount != MemberCount)
                throw new InvalidOperationException(
                    $"option table and ubershader are different archive generations: the table has "
                        + $"{MemberCount} {OptionBlockName} members, '{uber.Name}' has {other.MemberCount}. "
                        + "Every specialised constant read would land on the wrong option."
                );

            for (int i = 0; i < Rows.Count && i < other.Rows.Count; i++)
                if (Rows[i].Name != other.Rows[i].Name)
                    throw new InvalidOperationException(
                        $"option table and ubershader disagree at option {i}: "
                            + $"'{Rows[i].Name}' vs '{other.Rows[i].Name}'."
                    );

            if (Rows.Count != other.Rows.Count)
                throw new InvalidOperationException(
                    $"option table and ubershader declare different option counts: "
                        + $"{Rows.Count} vs {other.Rows.Count}."
                );
        }

        public string ToJson()
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("options");
                foreach (var r in Rows)
                {
                    w.WriteStartObject();
                    w.WriteString("name", r.Name);
                    w.WriteStartArray("choice_names");
                    foreach (var c in r.ChoiceNames)
                        w.WriteStringValue(c);
                    w.WriteEndArray();
                    w.WriteNumber("default_choice_idx", r.DefaultChoiceIdx);
                    if (r.HasSlot)
                        w.WriteNumber("c6_option_index", r.C6OptionIndex);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
