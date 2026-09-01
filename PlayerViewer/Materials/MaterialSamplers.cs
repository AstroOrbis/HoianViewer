using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using BfresLibrary.GX2;
using PlayerViewer.Textures;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// Sampler edits on a material
    /// </summary>
    public static class MaterialSamplers
    {
        /// <summary>What the reader hands back for a declared but unassigned entry, and what
        /// writing it back means.</summary>
        public const string Unset = Gsys.GsysShaderOptions.Unset;

        /// <summary>
        /// Map options and the sampler the specialised program reads once they are on.
        /// </summary>
        public static readonly (string Option, string[] Samplers)[] MapOptions =
        {
            ("enable_roughness_map", new[] { "_r0" }),
            ("enable_metalness_map", new[] { "_m0" }),
            ("enable_emission_map", new[] { "_e0" }),
            ("enable_opacity_tex", new[] { "_op0" }),
            ("enable_transmission_map", new[] { "_t0" }),
            ("enable_ao", new[] { "_ao0" }),
            ("enable_sfxmask", new[] { "_fm0" }),
            ("enable_bake_shadow", new[] { "_b0", "_b1" }),
        };

        /// <summary>
        /// The dummy the game binds to a map sampler that has no real map, and its colour. A
        /// model that names one carries it in its own container. The other map samplers have
        /// no stock placeholder: a stock material with the option off carries no sampler.
        /// </summary>
        public static readonly (string Sampler, string GameDummy, bool White)[] Placeholders =
        {
            ("_b0", "BakeDummy00", true),
            ("_b1", "LightBakeDummy00", false),
            ("_op0", "AlphaDummy00", true),
        };

        /// <summary>What to bind to a map sampler that has nothing: the model's own dummy
        /// when it carries the one the game would use, else a generated placeholder of the
        /// colour that dummy would have been.</summary>
        public static string DefaultTexture(string shaderSampler, Func<string, bool> modelHas)
        {
            foreach (var (sampler, dummy, white) in Placeholders)
            {
                if (sampler != shaderSampler)
                    continue;
                if (modelHas != null && modelHas(dummy))
                    return dummy;
                return white ? TextureImport.WhiteName : TextureImport.BlackName;
            }
            return TextureImport.BlackName;
        }

        /// <summary>Whether an option value asks for the map. The choice names are numbers
        /// and zero is off; an unset option is at the archive default, which is off for all
        /// of these.</summary>
        public static bool AsksForMap(string value) =>
            !string.IsNullOrEmpty(value) && value != Unset && value != "0";

        /// <summary>The map option that makes the shader read this sampler, or null for a
        /// sampler that is not gated by one.</summary>
        public static string OptionFor(string shaderSampler)
        {
            foreach (var (option, samplers) in MapOptions)
                if (Array.IndexOf(samplers, shaderSampler) >= 0)
                    return option;
            return null;
        }

        /// <summary>Whether the material has this map option switched on.</summary>
        public static bool MapOptionOn(Material mat, string option)
        {
            var options = mat.ShaderAssign?.ShaderOptions;
            return options != null
                && options.ContainsKey(option)
                && AsksForMap(options[option]?.String);
        }

        /// <summary>Map options the material has switched on whose sampler it does not
        /// supply. Each one draws with a default texture and reports nothing.</summary>
        public static List<(string Option, string Sampler)> MissingMapSamplers(Material mat)
        {
            var missing = new List<(string, string)>();
            var options = mat.ShaderAssign?.ShaderOptions;
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (options == null || assigns == null)
                return missing;

            foreach (var (option, samplers) in MapOptions)
            {
                if (!options.ContainsKey(option) || !AsksForMap(options[option]?.String))
                    continue;
                foreach (string sampler in samplers)
                {
                    if (Bound(mat, sampler) || missing.Any(x => x.Item2 == sampler))
                        continue;
                    missing.Add((option, sampler));
                }
            }
            return missing;
        }

        /// <summary>
        /// Whether the engine feeds this sampler rather than the material: shadow maps, the
        /// depth and colour buffers, the BRDF tables, the projection and gbuffer inputs.
        /// </summary>
        public static bool EngineProvided(string shaderSampler) =>
            shaderSampler != null && shaderSampler.StartsWith("gsys_", StringComparison.Ordinal);

        /// <summary>The material sampler a shader sampler points at, or null when the entry is
        /// absent, unset, or names a sampler the material does not have.</summary>
        public static string Target(Material mat, string shaderSampler)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null || !assigns.ContainsKey(shaderSampler))
                return null;
            string target = assigns[shaderSampler]?.String;
            if (string.IsNullOrEmpty(target) || target == Unset)
                return null;
            return mat.Samplers.ContainsKey(target) ? target : null;
        }

        /// <summary>The texture a shader sampler resolves to, or null.</summary>
        public static string BoundTexture(Material mat, string shaderSampler)
        {
            string target = Target(mat, shaderSampler);
            if (target == null)
                return null;
            int slot = mat.Samplers.IndexOf(target);
            if (slot < 0 || slot >= mat.TextureRefs.Count)
                return null;
            string name = mat.TextureRefs[slot].Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>
        /// Points a shader sampler at a texture, adding whatever is missing on the way. An
        /// entry that already resolves only has its texture ref rewritten, which is the small
        /// edit; the rest goes through <see cref="Bind"/> and its costs.
        /// </summary>
        public static void SetTexture(Material mat, string shaderSampler, string textureName)
        {
            string target = Target(mat, shaderSampler);
            if (target != null)
            {
                int slot = mat.Samplers.IndexOf(target);
                if (slot >= 0 && slot < mat.TextureRefs.Count)
                {
                    mat.TextureRefs[slot].Name = textureName;
                    return;
                }
            }
            Bind(mat, shaderSampler, SuggestName(mat, shaderSampler), textureName);
        }

        /// <summary>
        /// Takes a shader sampler back to nothing, the way a stock material carries one whose
        /// option is off: no slot behind it, and the key left unset when the file had it or
        /// removed when the editor added it, so the key set stays the file's.
        /// </summary>
        public static void Unbind(Material mat, string shaderSampler, bool removeKey)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null || !assigns.ContainsKey(shaderSampler))
                return;

            string target = Target(mat, shaderSampler);
            if (removeKey)
                assigns.RemoveKey(shaderSampler);
            else
                assigns.Set(shaderSampler, new ResString { String = Unset });

            if (target != null && !Reached(mat, target))
                RemoveSlot(mat, target);
        }

        /// <summary>Whether any assign still points at this material sampler.</summary>
        public static bool Reached(Material mat, string materialSampler)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null)
                return false;
            foreach (var entry in assigns.ToArray())
                if (entry.Value?.String == materialSampler)
                    return true;
            return false;
        }

        /// <summary>Removes a material sampler slot and the texture ref paired with it, at
        /// the same index.</summary>
        public static void RemoveSlot(Material mat, string materialSampler)
        {
            int slot = mat.Samplers.IndexOf(materialSampler);
            if (slot < 0)
                return;
            mat.Samplers.RemoveAt(slot);
            if (slot < mat.TextureRefs.Count)
                mat.TextureRefs.RemoveAt(slot);
        }

        /// <summary>Whether the shader sampler resolves all the way to a texture slot.</summary>
        public static bool Bound(Material mat, string shaderSampler) =>
            Target(mat, shaderSampler) != null;

        /// <summary>Shader samplers the archive declares that the material has no assign
        /// entry for at all. Rebinding one of these needs a new key; the ones already present
        /// and set to <see cref="Unset"/> do not.</summary>
        public static List<string> Undeclared(Material mat, IEnumerable<string> archiveSamplers)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            return (archiveSamplers ?? Array.Empty<string>())
                .Where(x => assigns == null || !assigns.ContainsKey(x))
                .ToList();
        }

        /// <summary>
        /// Binds a shader sampler to a new material sampler and texture slot.
        /// </summary>
        public static void Bind(
            Material mat,
            string shaderSampler,
            string materialSampler,
            string textureName
        )
        {
            if (string.IsNullOrEmpty(shaderSampler))
                throw new ArgumentException("no shader sampler named", nameof(shaderSampler));
            if (mat.Samplers.ContainsKey(materialSampler))
                throw new InvalidOperationException(
                    $"this material already has a sampler called '{materialSampler}'"
                );
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null)
                throw new InvalidOperationException("this material has no shader assign block");

            mat.Samplers.Add(
                materialSampler,
                new Sampler { Name = materialSampler, TexSampler = new TexSampler() }
            );
            mat.TextureRefs.Add(new TextureRef { Name = textureName ?? "" });
            assigns.Set(shaderSampler, new ResString { String = materialSampler });
        }

        /// <summary>A material sampler name not already taken, derived from the shader
        /// sampler, which is what stock materials name theirs after.</summary>
        public static string SuggestName(Material mat, string shaderSampler)
        {
            string wanted = shaderSampler ?? "sampler";
            if (!mat.Samplers.ContainsKey(wanted))
                return wanted;
            for (int i = 1; ; i++)
                if (!mat.Samplers.ContainsKey(wanted + i))
                    return wanted + i;
        }
    }
}
