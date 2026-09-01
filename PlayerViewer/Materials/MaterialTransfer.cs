using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using BfresLibrary.GX2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayerViewer.Textures;
using Toolbox.Core;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// A material and the textures it binds, written to a folder and read back. Textures go
    /// beside it as PNG and are optional on the way back in. Its own JSON rather than
    /// BfresLibrary's material export, because that one carries positional slot data the
    /// saver rebuilds anyway and drops the assign keys left unset.
    /// </summary>
    public static class MaterialTransfer
    {
        public const string FileName = "material.json";

        public sealed class OptionEntry
        {
            public string Name;
            public string Value;
        }

        /// <summary>One assign key exactly as the material holds it, target included, so an
        /// entry left at "Default Value" survives the trip. Dropping one would change
        /// the material's assign key set, which is a far bigger edit than it looks.</summary>
        public sealed class AssignEntry
        {
            public string ShaderSampler;
            public string Target;
        }

        public sealed class AttribAssignEntry
        {
            public string ShaderAttribute;
            public string Target;
        }

        public sealed class SamplerEntry
        {
            public string ShaderSampler;
            public string MaterialSampler;
            public string Texture;
            public string ClampX,
                ClampY,
                ClampZ;
            public string MagFilter,
                MinFilter,
                ZFilter,
                MipFilter;
            public string MaxAnisotropicRatio,
                BorderType,
                DepthCompareFunc;
            public float MinLod,
                MaxLod,
                LodBias;
            public bool DepthCompareEnabled;
        }

        public sealed class ValueEntry
        {
            public string Name;
            public string Type;
            public string[] Strings;
            public float[] Floats;
            public int[] Ints;
            public uint[] Uints;
            public bool[] Bools;
            public string Bytes;
        }

        public sealed class TextureEntry
        {
            public string Name;
            public string File;
            public bool Srgb;
            public string SourceFormat;
            public int Width,
                Height;
        }

        public sealed class MaterialFile
        {
            public int Version = 2;
            public string Name;
            public string ShaderArchive;
            public string ShadingModel;
            public bool Visible = true;

            [JsonConverter(typeof(OptionListConverter))]
            public List<OptionEntry> Options = new();
            public List<ValueEntry> RenderInfo = new();
            public List<ValueEntry> Params = new();
            public List<SamplerEntry> Samplers = new();
            public List<AssignEntry> Assigns = new();
            public List<AttribAssignEntry> AttribAssigns = new();
            public List<ValueEntry> UserData = new();
            public List<TextureEntry> Textures = new();
        }

        //Options are an ordered list, since their order is part of the material. A version 1
        //file holds them as an object and is read back as one.
        sealed class OptionListConverter : JsonConverter<List<OptionEntry>>
        {
            public override List<OptionEntry> ReadJson(
                JsonReader reader,
                Type objectType,
                List<OptionEntry> existingValue,
                bool hasExistingValue,
                JsonSerializer serializer
            )
            {
                var token = JToken.Load(reader);
                var list = new List<OptionEntry>();
                if (token is JObject obj)
                    foreach (var property in obj.Properties())
                        list.Add(
                            new OptionEntry
                            {
                                Name = property.Name,
                                Value =
                                    property.Value.Type == JTokenType.Null
                                        ? ""
                                        : property.Value.ToString(),
                            }
                        );
                else if (token is JArray array)
                    list.AddRange(array.ToObject<List<OptionEntry>>(serializer) ?? new());
                return list;
            }

            public override void WriteJson(
                JsonWriter writer,
                List<OptionEntry> value,
                JsonSerializer serializer
            ) => serializer.Serialize(writer, value);
        }

        //--- Export

        /// <summary>
        /// Writes the material and every texture it binds into a folder. Returns the number of
        /// textures written; a texture that will not decode is reported and skipped rather
        /// than failing the whole export.
        /// </summary>
        public static int Export(
            Material mat,
            Func<string, STGenericTexture> findTexture,
            string folder,
            List<string> problems
        )
        {
            Directory.CreateDirectory(folder);
            var file = new MaterialFile
            {
                Name = mat.Name,
                ShaderArchive = mat.ShaderAssign?.ShaderArchiveName,
                ShadingModel = mat.ShaderAssign?.ShadingModelName,
                Visible = mat.Visible,
            };

            var options = mat.ShaderAssign?.ShaderOptions;
            if (options != null)
                foreach (var entry in options)
                    file.Options.Add(
                        new OptionEntry { Name = entry.Key, Value = entry.Value?.String ?? "" }
                    );

            foreach (var info in mat.RenderInfos.Values)
                file.RenderInfo.Add(ToEntry(info.Name, EntryValue.From(info)));

            for (int i = 0; i < mat.ShaderParams.Count; i++)
                file.Params.Add(
                    ParamValues.ToEntry(mat.ShaderParams.GetKey(i), mat.ShaderParams[i].DataValue)
                );

            if (mat.UserData != null)
                foreach (var entry in mat.UserData)
                    file.UserData.Add(ToEntry(entry.Key, EntryValue.From(entry.Value)));

            var assigns = mat.ShaderAssign?.SamplerAssigns;
            var written = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int slot = 0; slot < mat.Samplers.Count; slot++)
            {
                string materialSampler = mat.Samplers.GetKey(slot);
                string shaderSampler = assigns
                    ?.ToArray()
                    .FirstOrDefault(x => x.Value?.String == materialSampler)
                    .Key;
                string texture = slot < mat.TextureRefs.Count ? mat.TextureRefs[slot].Name : null;

                file.Samplers.Add(
                    FromSampler(shaderSampler, materialSampler, texture, mat.Samplers[slot])
                );

                if (string.IsNullOrEmpty(texture) || !written.Add(texture))
                    continue;

                var found = findTexture?.Invoke(texture);
                if (found == null)
                {
                    problems.Add($"{texture} is named by this material but is not in the file");
                    continue;
                }
                try
                {
                    string name = UniqueFileName(files, SafeFileName(texture)) + ".png";
                    TextureStore.ExportPng(found, Path.Combine(folder, name));
                    file.Textures.Add(
                        new TextureEntry
                        {
                            Name = texture,
                            File = name,
                            Srgb = TextureStore.IsSrgb(found),
                            SourceFormat = TextureStore.FormatName(found),
                            Width = (int)found.Width,
                            Height = (int)found.Height,
                        }
                    );
                }
                catch (Exception ex)
                {
                    problems.Add($"{texture} did not decode: {ex.Message}");
                }
            }

            if (assigns != null)
                foreach (var entry in assigns)
                    file.Assigns.Add(
                        new AssignEntry { ShaderSampler = entry.Key, Target = entry.Value?.String }
                    );

            var attribs = mat.ShaderAssign?.AttribAssigns;
            if (attribs != null)
                foreach (var entry in attribs)
                    file.AttribAssigns.Add(
                        new AttribAssignEntry
                        {
                            ShaderAttribute = entry.Key,
                            Target = entry.Value?.String,
                        }
                    );

            File.WriteAllText(
                Path.Combine(folder, FileName),
                JsonConvert.SerializeObject(file, Formatting.Indented)
            );
            return file.Textures.Count;
        }

        static SamplerEntry FromSampler(
            string shaderSampler,
            string materialSampler,
            string texture,
            Sampler sampler
        )
        {
            var state = sampler?.TexSampler ?? new TexSampler();
            return new SamplerEntry
            {
                ShaderSampler = shaderSampler,
                MaterialSampler = materialSampler,
                Texture = texture,
                ClampX = state.ClampX.ToString(),
                ClampY = state.ClampY.ToString(),
                ClampZ = state.ClampZ.ToString(),
                MagFilter = state.MagFilter.ToString(),
                MinFilter = state.MinFilter.ToString(),
                ZFilter = state.ZFilter.ToString(),
                MipFilter = state.MipFilter.ToString(),
                MaxAnisotropicRatio = state.MaxAnisotropicRatio.ToString(),
                BorderType = state.BorderType.ToString(),
                DepthCompareFunc = state.DepthCompareFunc.ToString(),
                MinLod = state.MinLod,
                MaxLod = state.MaxLod,
                LodBias = state.LodBias,
                DepthCompareEnabled = state.DepthCompareEnabled,
            };
        }

        static ValueEntry ToEntry(string name, EntryValue value) =>
            new ValueEntry
            {
                Name = name,
                Type = value.TypeName,
                Strings = value.Strings,
                Floats = value.Floats,
                Ints = value.Ints,
                Bytes = value.Bytes == null ? null : Convert.ToBase64String(value.Bytes),
            };

        //Null for a byte entry with no payload, which is what a version 1 file wrote.
        static EntryValue FromEntry(ValueEntry entry) =>
            entry.Type switch
            {
                "Single" => new EntryValue { Floats = entry.Floats ?? Array.Empty<float>() },
                "Int32" => new EntryValue { Ints = entry.Ints ?? Array.Empty<int>() },
                "WString" => new EntryValue
                {
                    Strings = entry.Strings ?? Array.Empty<string>(),
                    Unicode = true,
                },
                "Byte" => entry.Bytes == null
                    ? null
                    : new EntryValue { Bytes = Convert.FromBase64String(entry.Bytes) },
                _ => new EntryValue { Strings = entry.Strings ?? Array.Empty<string>() },
            };

        //--- Import

        public static MaterialFile Read(string folder) =>
            JsonConvert.DeserializeObject<MaterialFile>(
                File.ReadAllText(Path.Combine(folder, FileName))
            );

        /// <summary>Which of the exported textures actually sit beside the file.</summary>
        public static List<TextureEntry> AvailableTextures(MaterialFile file, string folder) =>
            (file?.Textures ?? new List<TextureEntry>())
                .Where(x =>
                    !string.IsNullOrEmpty(x.File) && File.Exists(Path.Combine(folder, x.File))
                )
                .ToList();

        /// <summary>
        /// Writes the file's content onto an existing material, keeping the material's own
        /// name and its place in the model.
        /// </summary>
        public static void Apply(Material mat, MaterialFile file, List<string> problems)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));
            if (mat.ShaderAssign == null)
                mat.ShaderAssign = new ShaderAssign();

            if (!string.IsNullOrEmpty(file.ShaderArchive))
                mat.ShaderAssign.ShaderArchiveName = file.ShaderArchive;
            if (!string.IsNullOrEmpty(file.ShadingModel))
                mat.ShaderAssign.ShadingModelName = file.ShadingModel;
            mat.Visible = file.Visible;

            mat.ShaderAssign.ShaderOptions.Clear();
            foreach (var entry in file.Options)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                if (mat.ShaderAssign.ShaderOptions.ContainsKey(entry.Name))
                {
                    problems.Add(
                        $"option '{entry.Name}' is listed twice, so the second was skipped"
                    );
                    continue;
                }
                mat.ShaderAssign.ShaderOptions.Add(
                    entry.Name,
                    new ResString { String = entry.Value ?? "" }
                );
            }

            mat.RenderInfos.Clear();
            foreach (var entry in file.RenderInfo)
            {
                var value = FromEntry(entry);
                if (value == null || value.Bytes != null)
                {
                    problems.Add($"render info '{entry.Name}' has no usable value");
                    continue;
                }
                var info = new RenderInfo { Name = entry.Name };
                value.WriteTo(info);
                mat.RenderInfos.Add(entry.Name, info);
            }

            ApplyParams(mat, file, problems);
            ApplySamplers(mat, file, problems);
            ApplyAttribAssigns(mat, file);

            mat.UserData.Clear();
            foreach (var entry in file.UserData)
            {
                var value = FromEntry(entry);
                if (value == null)
                {
                    problems.Add($"user data '{entry.Name}' is raw bytes and was not carried");
                    continue;
                }
                var data = new UserData { Name = entry.Name };
                value.WriteTo(data);
                mat.UserData.Add(entry.Name, data);
            }
        }

        //A version 1 file has no attribute assigns, so the material keeps its own.
        static void ApplyAttribAssigns(Material mat, MaterialFile file)
        {
            if (file.Version < 2)
                return;
            var assigns = mat.ShaderAssign.AttribAssigns;
            assigns.Clear();
            foreach (var entry in file.AttribAssigns)
            {
                if (string.IsNullOrEmpty(entry.ShaderAttribute))
                    continue;
                if (assigns.ContainsKey(entry.ShaderAttribute))
                    continue;
                assigns.Add(
                    entry.ShaderAttribute,
                    new ResString { String = entry.Target ?? MaterialSamplers.Unset }
                );
            }
        }

        //Parameters keep the target's own entries and take the source's values, because the
        //parameter set is the shading model's rather than the material's: writing a name the
        //model does not declare would build a material uniform block the program cannot read.
        static void ApplyParams(Material mat, MaterialFile file, List<string> problems)
        {
            foreach (var entry in file.Params)
            {
                if (!mat.ShaderParams.ContainsKey(entry.Name))
                {
                    problems.Add($"param '{entry.Name}' is not on this material and was skipped");
                    continue;
                }
                var param = mat.ShaderParams[entry.Name];
                object value = ParamValues.FromEntry(entry, param.DataValue);
                if (value == null)
                {
                    problems.Add($"param '{entry.Name}' has a different type here and was skipped");
                    continue;
                }
                param.DataValue = value;
            }
        }

        //The three sampler structures are rebuilt together and in one order, because the
        //material sampler and its texture ref pair positionally and the assign points at the
        //sampler by name.
        static void ApplySamplers(Material mat, MaterialFile file, List<string> problems)
        {
            mat.Samplers.Clear();
            mat.TextureRefs.Clear();
            mat.ShaderAssign.SamplerAssigns.Clear();

            foreach (var entry in file.Samplers)
            {
                string name = entry.MaterialSampler;
                if (string.IsNullOrEmpty(name))
                {
                    problems.Add("a sampler entry has no material sampler name, so it was skipped");
                    continue;
                }
                if (mat.Samplers.ContainsKey(name))
                {
                    problems.Add($"sampler '{name}' is listed twice, so the second was skipped");
                    continue;
                }
                mat.Samplers.Add(
                    name,
                    new Sampler { Name = name, TexSampler = ToTexSampler(entry) }
                );
                mat.TextureRefs.Add(new TextureRef { Name = entry.Texture ?? "" });
            }

            //Every assign key, in file order, including the ones left unset. A pre-Assigns
            //file has none, so the material sampler list is what names them instead.
            var written =
                file.Assigns.Count > 0
                    ? file.Assigns
                    : file
                        .Samplers.Where(x => !string.IsNullOrEmpty(x.ShaderSampler))
                        .Select(x => new AssignEntry
                        {
                            ShaderSampler = x.ShaderSampler,
                            Target = x.MaterialSampler,
                        })
                        .ToList();

            foreach (var entry in written)
            {
                if (string.IsNullOrEmpty(entry.ShaderSampler))
                    continue;
                if (mat.ShaderAssign.SamplerAssigns.ContainsKey(entry.ShaderSampler))
                    continue;
                mat.ShaderAssign.SamplerAssigns.Add(
                    entry.ShaderSampler,
                    new ResString { String = entry.Target ?? MaterialSamplers.Unset }
                );
            }
        }

        static TexSampler ToTexSampler(SamplerEntry entry)
        {
            var state = new TexSampler();
            state.ClampX = Enum<GX2TexClamp>(entry.ClampX, state.ClampX);
            state.ClampY = Enum<GX2TexClamp>(entry.ClampY, state.ClampY);
            state.ClampZ = Enum<GX2TexClamp>(entry.ClampZ, state.ClampZ);
            state.MagFilter = Enum<GX2TexXYFilterType>(entry.MagFilter, state.MagFilter);
            state.MinFilter = Enum<GX2TexXYFilterType>(entry.MinFilter, state.MinFilter);
            state.ZFilter = Enum<GX2TexZFilterType>(entry.ZFilter, state.ZFilter);
            state.MipFilter = Enum<GX2TexMipFilterType>(entry.MipFilter, state.MipFilter);
            state.MaxAnisotropicRatio = Enum<GX2TexAnisoRatio>(
                entry.MaxAnisotropicRatio,
                state.MaxAnisotropicRatio
            );
            state.BorderType = Enum<GX2TexBorderType>(entry.BorderType, state.BorderType);
            state.DepthCompareFunc = Enum<GX2CompareFunction>(
                entry.DepthCompareFunc,
                state.DepthCompareFunc
            );
            state.MinLod = entry.MinLod;
            state.MaxLod = entry.MaxLod;
            state.LodBias = entry.LodBias;
            state.DepthCompareEnabled = entry.DepthCompareEnabled;
            return state;
        }

        static T Enum<T>(string name, T fallback)
            where T : struct => System.Enum.TryParse<T>(name, out var value) ? value : fallback;

        //The same character rule a texture import applies to its name.
        static string SafeFileName(string name)
        {
            string clean = new string(
                (name ?? "").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()
            );
            return clean.Length == 0 ? "texture" : clean;
        }

        static string UniqueFileName(HashSet<string> taken, string wanted)
        {
            string candidate = wanted;
            for (int i = 1; !taken.Add(candidate); i++)
                candidate = $"{wanted}_{i}";
            return candidate;
        }
    }
}
