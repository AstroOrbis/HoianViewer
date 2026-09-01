using System;
using System.Collections.Generic;
using BfresLibrary;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// One material's sampler bindings as the editor drives them: the texture each shader
    /// sampler was last seen on, the samplers a map option created, and the binds that follow
    /// from the options. The memory is needed because the listed samplers follow the options,
    /// so switching a map option back on has to bring its texture back with it.
    /// </summary>
    public sealed class SamplerBindings
    {
        readonly string _name;
        readonly Material _mat;
        readonly MaterialBaseline _baseline;
        readonly Func<string, bool> _modelHas;
        readonly Action _edited;
        readonly Action<string> _log;

        readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);

        //Samplers the automatic bind created, so switching the option off takes exactly those
        //away and leaves a binding made by hand alone.
        readonly HashSet<string> _autoBound = new(StringComparer.Ordinal);

        //Samplers an automatic bind has failed on, so the failure is reported once.
        readonly HashSet<string> _autoBindFailed = new(StringComparer.Ordinal);

        /// <param name="name">The material's name, for the log.</param>
        /// <param name="modelHas">Whether the model carries a texture of that name.</param>
        /// <param name="edited">Called after every change to the material.</param>
        /// <param name="log">Takes one line per event worth a log entry.</param>
        public SamplerBindings(
            string name,
            Material mat,
            MaterialBaseline baseline,
            Func<string, bool> modelHas,
            Action edited,
            Action<string> log
        )
        {
            _name = name;
            _mat = mat;
            _baseline = baseline;
            _modelHas = modelHas;
            _edited = edited;
            _log = log;
        }

        /// <summary>
        /// Binds a texture to a shader sampler, or clears the binding when the name is null.
        /// The memory follows the choice, and a binding that cannot be made is forgotten too,
        /// or the restore would retry it every frame.
        /// </summary>
        public bool SetTexture(string shaderSampler, string textureName)
        {
            try
            {
                if (string.IsNullOrEmpty(textureName))
                {
                    Unbind(shaderSampler);
                    _memory.Remove(shaderSampler);
                }
                else
                {
                    MaterialSamplers.SetTexture(_mat, shaderSampler, textureName);
                    _memory[shaderSampler] = textureName;
                }
                _edited();
                return true;
            }
            catch (Exception ex)
            {
                _memory.Remove(shaderSampler);
                _log($"{shaderSampler} on {_name}: {ex.Message}");
                return false;
            }
        }

        //The key stays when the file had it and goes when the editor added it.
        public void Unbind(string shaderSampler) =>
            MaterialSamplers.Unbind(
                _mat,
                shaderSampler,
                removeKey: !_baseline.HadSamplerKey(shaderSampler)
            );

        /// <summary>A reset through the baseline, with the memory following it the way a
        /// pick does. The caller reports the edit.</summary>
        public void Reset(string shaderSampler)
        {
            _baseline.ResetSampler(_mat, shaderSampler);
            _autoBound.Remove(shaderSampler);
            string now = MaterialSamplers.BoundTexture(_mat, shaderSampler);
            if (string.IsNullOrEmpty(now))
                _memory.Remove(shaderSampler);
            else
                _memory[shaderSampler] = now;
        }

        /// <summary>
        /// Records what every declared sampler is bound to, and puts back the one the shader
        /// has started reading again while the material has nothing on it.
        /// </summary>
        public void RestoreRemembered(IEnumerable<string> declared, HashSet<string> read)
        {
            foreach (string sampler in declared)
            {
                if (MaterialSamplers.EngineProvided(sampler))
                    continue;
                string current = MaterialSamplers.BoundTexture(_mat, sampler);
                if (!string.IsNullOrEmpty(current))
                {
                    _memory[sampler] = current;
                    continue;
                }
                if (read == null || !read.Contains(sampler))
                    continue;
                //A map sampler whose option is off is the automatic bind's to take away.
                string option = MaterialSamplers.OptionFor(sampler);
                if (option != null && !MaterialSamplers.MapOptionOn(_mat, option))
                    continue;
                if (!_memory.TryGetValue(sampler, out string remembered))
                    continue;
                _log($"{_name}: {sampler} is read again, restoring {remembered}");
                SetTexture(sampler, remembered);
            }
        }

        /// <summary>
        /// Gives a map option that has been switched on the sampler it reads, bound to what
        /// this material last had on it or to a placeholder. Switching the option off again
        /// removes only the samplers this created.
        /// </summary>
        public void AutoBindMapSamplers()
        {
            foreach (var (option, samplers) in MaterialSamplers.MapOptions)
            {
                if (MaterialSamplers.MapOptionOn(_mat, option))
                    continue;
                foreach (string sampler in samplers)
                {
                    if (!_autoBound.Remove(sampler))
                        continue;
                    _log($"{_name}: {option} is off, removing {sampler}");
                    Unbind(sampler);
                    _edited();
                }
            }

            foreach (var (option, sampler) in MaterialSamplers.MissingMapSamplers(_mat))
            {
                if (!_autoBindFailed.Add(sampler))
                    continue;
                string texture = _memory.TryGetValue(sampler, out string remembered)
                    ? remembered
                    : MaterialSamplers.DefaultTexture(sampler, _modelHas);
                _log($"{_name}: {option} is on, binding {sampler} to {texture}");
                if (SetTexture(sampler, texture))
                {
                    _autoBindFailed.Remove(sampler);
                    _autoBound.Add(sampler);
                }
            }
        }
    }
}
