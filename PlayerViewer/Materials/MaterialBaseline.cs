using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// What a material's options, shader params, render info and user data held before the
    /// editor touched them, so an edit can be shown as an edit and undone one entry at a time.
    /// </summary>
    public sealed class MaterialBaseline
    {
        /// <summary>A shader param as it was: the value, copied, and a key to compare
        /// against, since the value is an object of a dozen shapes.</summary>
        sealed class ParamValue
        {
            public object Data;
            public string Key;
        }

        readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
        readonly Dictionary<string, ParamValue> _params = new(StringComparer.Ordinal);
        readonly Dictionary<string, EntryValue> _renderInfo = new(StringComparer.Ordinal);
        readonly Dictionary<string, EntryValue> _userData = new(StringComparer.Ordinal);

        //Every sampler assign the file had, and the slot list in file order. The Sampler object
        //is kept so a removed slot comes back with its own settings.
        readonly Dictionary<string, string> _samplerAssigns = new(StringComparer.Ordinal);
        readonly List<(string Name, Sampler Sampler, string Texture)> _slots = new();

        public MaterialBaseline(Material mat)
        {
            foreach (
                var entry in mat.ShaderAssign?.ShaderOptions?.ToArray()
                    ?? Enumerable.Empty<KeyValuePair<string, ResString>>()
            )
                _options[entry.Key] = entry.Value?.String;

            foreach (
                var entry in mat.ShaderAssign?.SamplerAssigns?.ToArray()
                    ?? Enumerable.Empty<KeyValuePair<string, ResString>>()
            )
                _samplerAssigns[entry.Key] = entry.Value?.String;
            for (int i = 0; i < mat.Samplers.Count; i++)
                _slots.Add(
                    (
                        mat.Samplers.GetKey(i),
                        mat.Samplers[i],
                        i < mat.TextureRefs.Count ? mat.TextureRefs[i].Name : null
                    )
                );

            foreach (var entry in mat.ShaderParams.ToArray())
                _params[entry.Key] = new ParamValue
                {
                    Data = ParamValues.Clone(entry.Value?.DataValue),
                    Key = ParamValues.Key(entry.Value?.DataValue),
                };

            foreach (var info in mat.RenderInfos.Values)
                _renderInfo[info.Name] = EntryValue.From(info);

            foreach (var entry in mat.UserData.ToArray())
                _userData[entry.Key] = EntryValue.From(entry.Value);
        }

        //--- Shader params

        public bool ParamChanged(Material mat, string name)
        {
            bool now = mat.ShaderParams.ContainsKey(name);
            bool was = _params.ContainsKey(name);
            if (now != was)
                return true;
            return now && ParamValues.Key(mat.ShaderParams[name]?.DataValue) != _params[name].Key;
        }

        public IEnumerable<string> ChangedParams(Material mat) =>
            Names(_params.Keys, mat.ShaderParams.Keys)
                .Where(x => ParamChanged(mat, x))
                .OrderBy(x => x, StringComparer.Ordinal);

        /// <summary>
        /// Puts a param back. Nothing adds or removes a param, since the set belongs to the
        /// shading model rather than to the material, so this only ever writes a value.
        /// </summary>
        public void ResetParam(Material mat, string name)
        {
            if (!_params.TryGetValue(name, out var original) || !mat.ShaderParams.ContainsKey(name))
                return;
            //Its own copy of the array, or the material and the baseline would share it and
            //the next edit would move the baseline with it.
            mat.ShaderParams[name].DataValue = ParamValues.Clone(original.Data);
        }

        //--- Options

        public bool OptionChanged(Material mat, string name)
        {
            var options = mat.ShaderAssign?.ShaderOptions;
            bool now = options != null && options.ContainsKey(name);
            bool was = _options.ContainsKey(name);
            if (now != was)
                return true;
            return now && options[name]?.String != _options[name];
        }

        public IEnumerable<string> ChangedOptions(Material mat)
        {
            var options = mat.ShaderAssign?.ShaderOptions;
            var names = new HashSet<string>(_options.Keys, StringComparer.Ordinal);
            if (options != null)
                foreach (var entry in options.ToArray())
                    names.Add(entry.Key);
            return names.Where(x => OptionChanged(mat, x)).OrderBy(x => x, StringComparer.Ordinal);
        }

        public void ResetOption(Material mat, string name)
        {
            var options = mat.ShaderAssign?.ShaderOptions;
            if (options == null)
                return;

            if (!_options.TryGetValue(name, out string original))
            {
                if (options.ContainsKey(name))
                    options.RemoveKey(name);
                return;
            }
            options.Set(name, new ResString { String = original });
        }

        //--- Render info

        public bool RenderInfoChanged(Material mat, string name)
        {
            bool now = mat.RenderInfos.ContainsKey(name);
            bool was = _renderInfo.ContainsKey(name);
            if (now != was)
                return true;
            return now && EntryValue.From(mat.RenderInfos[name]).Key != _renderInfo[name].Key;
        }

        public IEnumerable<string> ChangedRenderInfo(Material mat) =>
            Names(_renderInfo.Keys, mat.RenderInfos.Keys)
                .Where(x => RenderInfoChanged(mat, x))
                .OrderBy(x => x, StringComparer.Ordinal);

        public void ResetRenderInfo(Material mat, string name)
        {
            if (!_renderInfo.TryGetValue(name, out var original))
            {
                if (mat.RenderInfos.ContainsKey(name))
                    mat.RenderInfos.RemoveKey(name);
                return;
            }

            var info = mat.RenderInfos.ContainsKey(name) ? mat.RenderInfos[name] : null;
            if (info == null)
            {
                info = new RenderInfo { Name = name };
                mat.RenderInfos.Add(name, info);
            }
            //The type follows the value written, so restoring the array restores the type.
            original.WriteTo(info);
        }

        //--- User data

        public bool UserDataChanged(Material mat, string name)
        {
            bool now = mat.UserData.ContainsKey(name);
            bool was = _userData.ContainsKey(name);
            if (now != was)
                return true;
            return now && EntryValue.From(mat.UserData[name]).Key != _userData[name].Key;
        }

        public IEnumerable<string> ChangedUserData(Material mat) =>
            Names(_userData.Keys, mat.UserData.Keys)
                .Where(x => UserDataChanged(mat, x))
                .OrderBy(x => x, StringComparer.Ordinal);

        public void ResetUserData(Material mat, string name)
        {
            if (!_userData.TryGetValue(name, out var original))
            {
                if (mat.UserData.ContainsKey(name))
                    mat.UserData.RemoveKey(name);
                return;
            }
            if (!mat.UserData.ContainsKey(name))
                mat.UserData.Add(name, new UserData { Name = name });
            original.WriteTo(mat.UserData[name]);
        }

        //--- Samplers

        public bool HadSamplerKey(string shaderSampler) =>
            _samplerAssigns.ContainsKey(shaderSampler);

        public bool HadSlot(string materialSampler) => _slots.Any(s => s.Name == materialSampler);

        //The assign string and the texture reached through it.
        static (string Assign, string Texture) State(Material mat, string shaderSampler)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null || !assigns.ContainsKey(shaderSampler))
                return (null, null);
            return (
                assigns[shaderSampler]?.String,
                MaterialSamplers.BoundTexture(mat, shaderSampler)
            );
        }

        (string Assign, string Texture) Original(string shaderSampler)
        {
            if (!_samplerAssigns.TryGetValue(shaderSampler, out string assign))
                return (null, null);
            string texture = null;
            if (!string.IsNullOrEmpty(assign) && assign != MaterialSamplers.Unset)
                foreach (var slot in _slots)
                    if (slot.Name == assign)
                    {
                        texture = string.IsNullOrEmpty(slot.Texture) ? null : slot.Texture;
                        break;
                    }
            return (assign, texture);
        }

        public bool SamplerChanged(Material mat, string shaderSampler) =>
            State(mat, shaderSampler) != Original(shaderSampler);

        public IEnumerable<string> ChangedSamplers(Material mat)
        {
            var names = new HashSet<string>(_samplerAssigns.Keys, StringComparer.Ordinal);
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns != null)
                foreach (var entry in assigns.ToArray())
                    names.Add(entry.Key);
            return names.Where(x => SamplerChanged(mat, x)).OrderBy(x => x, StringComparer.Ordinal);
        }

        /// <summary>Puts a shader sampler back to the file's assign, slot and texture. A key
        /// the file did not have is removed, slot and all.</summary>
        public void ResetSampler(Material mat, string shaderSampler)
        {
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            if (assigns == null)
                return;
            var (assign, texture) = Original(shaderSampler);
            string current = MaterialSamplers.Target(mat, shaderSampler);

            if (assign == null)
            {
                MaterialSamplers.Unbind(mat, shaderSampler, removeKey: true);
                return;
            }

            assigns.Set(shaderSampler, new ResString { String = assign });

            if (
                current != null
                && current != assign
                && !HadSlot(current)
                && !MaterialSamplers.Reached(mat, current)
            )
                MaterialSamplers.RemoveSlot(mat, current);

            if (string.IsNullOrEmpty(assign) || assign == MaterialSamplers.Unset)
                return;

            if (!mat.Samplers.ContainsKey(assign))
            {
                InsertSlot(mat, assign, texture ?? "");
                return;
            }
            int index = mat.Samplers.IndexOf(assign);
            if (index >= 0 && index < mat.TextureRefs.Count)
                mat.TextureRefs[index].Name = texture ?? "";
        }

        //Puts a removed slot back where the file had it. The dict has no insert, so the slots
        //after it are taken off and re-added behind it.
        void InsertSlot(Material mat, string name, string texture)
        {
            int wanted = _slots.FindIndex(s => s.Name == name);
            var sampler = _slots[wanted].Sampler;

            var tail = new List<(string Name, Sampler Sampler, TextureRef Ref)>();
            for (int i = mat.Samplers.Count - 1; i >= 0; i--)
            {
                if (_slots.FindIndex(s => s.Name == mat.Samplers.GetKey(i)) < wanted)
                    break;
                tail.Add(
                    (
                        mat.Samplers.GetKey(i),
                        mat.Samplers[i],
                        i < mat.TextureRefs.Count ? mat.TextureRefs[i] : new TextureRef()
                    )
                );
                mat.Samplers.RemoveAt(i);
                if (i < mat.TextureRefs.Count)
                    mat.TextureRefs.RemoveAt(i);
            }

            mat.Samplers.Add(name, sampler);
            mat.TextureRefs.Add(new TextureRef { Name = texture });
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                mat.Samplers.Add(tail[i].Name, tail[i].Sampler);
                mat.TextureRefs.Add(tail[i].Ref);
            }
        }

        static IEnumerable<string> Names(IEnumerable<string> a, IEnumerable<string> b)
        {
            var names = new HashSet<string>(a, StringComparer.Ordinal);
            foreach (string x in b)
                names.Add(x);
            return names;
        }
    }
}
