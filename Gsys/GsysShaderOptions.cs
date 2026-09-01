using System;
using System.Collections.Generic;
using BfresLibrary;

namespace Gsys
{
    /// <summary>
    /// The shader option set the game builds for a material, and the assign type rules that
    /// go with it.
    ///
    /// The engine starts from the shading model's default key, writes the options the material
    /// actually stores, then overwrites the handful it derives from the material's render state.
    /// The dynamic half is written per draw from the shape and the requested assign type. Every
    /// value is a hard constraint: the program lookup is an exact key match, so a value guessed
    /// differently from whoever built the archive means no program rather than a near miss.
    /// </summary>
    public static class GsysShaderOptions
    {
        /// <summary>
        /// The value the bfres reader hands back for an option the material does not store.
        /// Those options are absent from the file and keep the archive's default choice.
        /// </summary>
        public const string Unset = "<Default Value>";

        /// <summary>
        /// Shader assign types in engine enum order. The index doubles as the program pass
        /// index, so pass 0 is the visible draw.
        /// </summary>
        public static readonly string[] AssignTypes =
        {
            "gsys_assign_material",
            "gsys_assign_zonly",
            "gsys_assign_zprepass",
            "gsys_assign_gbuffer",
            "gsys_assign_depth_silhouette",
            "gsys_assign_dilate",
            "gsys_assign_reflection",
            "gsys_assign_cubemap",
            "gsys_assign_depthshadow",
            "gsys_assign_xlu_zprepass",
            "gsys_assign_visualize",
            "gsys_assign_user0",
            "gsys_assign_user1",
            "gsys_assign_user2",
            "gsys_assign_user3",
            "gsys_assign_dynamic0",
            "gsys_assign_dynamic1",
            "gsys_assign_dynamic2",
            "gsys_assign_dynamic3",
            "gsys_assign_dynamic4",
            "gsys_assign_dynamic5",
            "gsys_assign_dynamic6",
            "gsys_assign_dynamic7",
        };

        /// <summary>
        /// The assign type to fall back to when nothing can serve the one asked for. This is
        /// the Splatoon 3's own table, indexed by assign type: the depth shaped passes fall back
        /// through zonly, everything else goes straight to gsys_assign_material, and
        /// gsys_assign_material itself terminates the walk.
        /// </summary>
        static readonly Dictionary<string, string> AssignFallback = new Dictionary<string, string>()
        {
            { "gsys_assign_zprepass", "gsys_assign_zonly" },
            { "gsys_assign_gbuffer", "gsys_assign_zonly" },
            { "gsys_assign_depth_silhouette", "gsys_assign_zonly" },
            { "gsys_assign_depthshadow", "gsys_assign_zonly" },
        };

        /// <summary>
        /// The assign type the archive can actually serve, starting from the one requested.
        /// </summary>
        public static string GetValidAssignType(string assignType, ICollection<string> available)
        {
            for (int i = 0; i < AssignTypes.Length; i++)
            {
                if (available.Contains(assignType))
                    return assignType;
                if (!AssignFallback.TryGetValue(assignType, out assignType))
                    break;
            }
            return AssignTypes[0];
        }

        /// <summary>
        /// The static half of the key: the options the material stores, then the ones the
        /// engine derives from its render state. The derived ones are written last because
        /// they override anything the material happens to carry under the same name.
        /// </summary>
        public static Dictionary<string, string> BuildStaticOptions(Material mat)
        {
            var options = new Dictionary<string, string>();

            foreach (var op in mat.ShaderAssign.ShaderOptions)
            {
                if (op.Value == Unset)
                    continue;
                //A bool stored as text is the choice named 1 or 0.
                options[op.Key] =
                    op.Value == "True" ? "1"
                    : op.Value == "False" ? "0"
                    : op.Value;
            }

            options["gsys_renderstate"] = Lookup(
                RenderStateModes,
                GetRenderInfo(mat, "gsys_render_state_mode")
            );
            options["gsys_alpha_test_func"] = Lookup(
                AlphaTestFuncs,
                GetRenderInfo(mat, "gsys_alpha_test_func")
            );
            options["gsys_alpha_test_enable"] =
                GetRenderInfo(mat, "gsys_alpha_test_enable") == "true" ? "1" : "0";
            options["gsys_pass"] = Lookup(PassTypes, GetRenderInfo(mat, "gsys_pass"));
            options["gsys_display_face_type"] = Lookup(
                DisplayFaceTypes,
                GetRenderInfo(mat, "gsys_render_state_display_face")
            );

            return options;
        }

        /// <summary>
        /// Adds the dynamic half: the skin weight count off the shape and the assign type
        /// for this pass.
        /// </summary>
        public static void AddDynamicOptions(
            Dictionary<string, string> options,
            uint vertexSkinCount,
            string assignType
        )
        {
            options["gsys_weight"] = vertexSkinCount.ToString();
            options["gsys_assign_type"] = assignType;
        }

        static string GetRenderInfo(Material mat, string name)
        {
            if (!mat.RenderInfos.ContainsKey(name))
                return null;

            var info = mat.RenderInfos[name];
            if (info.Data == null || info.Type != RenderInfoType.String)
                return null;

            var values = info.GetValueStrings();
            return values.Length > 0 ? values[0] : null;
        }

        //An unset or unrecognised render state reads as the first choice, same as the engine.
        static string Lookup(IReadOnlyDictionary<string, string> table, string value)
        {
            if (value != null && table.TryGetValue(value, out string choice))
                return choice;
            return "0";
        }

        //The engine's render info name to choice index tables, in the engine's order.
        public static readonly IReadOnlyDictionary<string, string> RenderStateModes =
            new Dictionary<string, string>()
            {
                { "opaque", "0" },
                { "mask", "1" },
                { "translucent", "2" },
                { "custom", "3" },
            };

        public static readonly IReadOnlyDictionary<string, string> AlphaTestFuncs = new Dictionary<
            string,
            string
        >()
        {
            { "never", "0" },
            { "less", "1" },
            { "equal", "2" },
            { "lequal", "3" },
            { "greater", "4" },
            { "nequal", "5" },
            { "gequal", "6" },
            { "always", "7" },
        };

        public static readonly IReadOnlyDictionary<string, string> PassTypes = new Dictionary<
            string,
            string
        >()
        {
            { "no_setting", "0" },
            { "seal", "1" },
            { "xlu_water", "2" },
            { "reduced_buffer", "3" },
        };

        public static readonly IReadOnlyDictionary<string, string> DisplayFaceTypes =
            new Dictionary<string, string>()
            {
                { "both", "0" },
                { "front", "1" },
                { "back", "2" },
                { "none", "3" },
            };
    }
}
