using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using ShaderLibrary;
using ResString = BfresLibrary.ResString;

namespace PlayerViewer.Materials
{
    /// <summary>What a normalisation changed on one material.</summary>
    public sealed class MigrationReport
    {
        public string FromModel;
        public int OptionsDropped;
        public int OptionsDefaulted;
        public int SamplersDropped;
        public int AttributesDropped;
        public int ParamsDropped;
        public readonly List<string> Notes = new();

        public bool Any =>
            FromModel != null
            || OptionsDropped > 0
            || OptionsDefaulted > 0
            || SamplersDropped > 0
            || AttributesDropped > 0
            || ParamsDropped > 0;

        public override string ToString()
        {
            var parts = new List<string>();
            if (FromModel != null)
                parts.Add($"moved from {FromModel}");
            if (OptionsDropped > 0)
                parts.Add($"{OptionsDropped} option(s) dropped");
            if (OptionsDefaulted > 0)
                parts.Add($"{OptionsDefaulted} option value(s) defaulted");
            if (SamplersDropped > 0)
                parts.Add($"{SamplersDropped} sampler(s) dropped");
            if (AttributesDropped > 0)
                parts.Add($"{AttributesDropped} attribute assign(s) dropped");
            if (ParamsDropped > 0)
                parts.Add($"{ParamsDropped} param(s) dropped");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Brings a material onto a shading model so its option set forms a key the archive can
    /// look up: an option the model does not declare is dropped, a value it has no choice for
    /// goes back to the default, and samplers, attribute assigns and params it does not know
    /// go with them. A material already in that shape is left alone.
    /// </summary>
    public static class MaterialMigration
    {
        public const string ArchiveName = "Hoian_UBER";

        public static MigrationReport Normalise(
            Material mat,
            ShaderModel target,
            string archiveName
        )
        {
            var assign = mat.ShaderAssign;
            if (assign == null || target == null)
                return null;
            var report = new MigrationReport();

            if (assign.ShadingModelName != target.Name)
            {
                report.FromModel = string.IsNullOrEmpty(assign.ShadingModelName)
                    ? "(none)"
                    : assign.ShadingModelName;
                assign.ShadingModelName = target.Name;
            }
            if (assign.ShaderArchiveName != archiveName)
                assign.ShaderArchiveName = archiveName;

            foreach (var entry in assign.ShaderOptions.ToArray())
            {
                if (!target.StaticOptions.ContainsKey(entry.Key))
                {
                    assign.ShaderOptions.RemoveKey(entry.Key);
                    report.OptionsDropped++;
                    report.Notes.Add($"option {entry.Key} is not declared");
                    continue;
                }
                string value = Normalised(entry.Value?.String);
                if (value == MaterialSamplers.Unset)
                {
                    if (entry.Value?.String != value)
                        assign.ShaderOptions.Set(entry.Key, new ResString { String = value });
                    continue;
                }
                if (target.StaticOptions[entry.Key].Choices.ContainsKey(value))
                {
                    if (entry.Value?.String != value)
                        assign.ShaderOptions.Set(entry.Key, new ResString { String = value });
                    continue;
                }
                assign.ShaderOptions.Set(
                    entry.Key,
                    new ResString { String = MaterialSamplers.Unset }
                );
                report.OptionsDefaulted++;
                report.Notes.Add($"option {entry.Key} has no choice {value}");
            }

            foreach (var entry in assign.SamplerAssigns.ToArray())
            {
                if (target.Samplers.ContainsKey(entry.Key))
                    continue;
                MaterialSamplers.Unbind(mat, entry.Key, removeKey: true);
                report.SamplersDropped++;
                report.Notes.Add($"sampler {entry.Key} is not declared");
            }

            foreach (var entry in assign.AttribAssigns.ToArray())
            {
                if (target.Attributes.ContainsKey(entry.Key))
                    continue;
                assign.AttribAssigns.RemoveKey(entry.Key);
                report.AttributesDropped++;
                report.Notes.Add($"attribute {entry.Key} is not declared");
            }

            var block = target.UniformBlocks.Values.FirstOrDefault(x =>
                x.Type == BfshaUniformBlock.BlockType.Material
            );
            if (block != null)
                foreach (var entry in mat.ShaderParams.ToArray())
                {
                    if (block.Uniforms.ContainsKey(entry.Key))
                        continue;
                    mat.ShaderParams.RemoveKey(entry.Key);
                    report.ParamsDropped++;
                    report.Notes.Add($"param {entry.Key} is not in the material block");
                }

            return report.Any ? report : null;
        }

        //A bool stored as text is the choice named 1 or 0; empty reads as unset.
        static string Normalised(string value)
        {
            if (string.IsNullOrEmpty(value))
                return MaterialSamplers.Unset;
            return value == "True" ? "1"
                : value == "False" ? "0"
                : value;
        }
    }
}
