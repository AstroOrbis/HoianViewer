using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using Gsys;

namespace PlayerViewer.Materials
{
    /// <summary>
    /// Every render info a Splatoon 3 material carries, with the choices worth offering for
    /// the string ones and, where the viewer derives something from an absent key, what it
    /// falls back to.
    /// </summary>
    public static class RenderInfoCatalog
    {
        public sealed class Entry
        {
            public string Name;

            /// <summary>Null when the value is free text.</summary>
            public string[] Choices;

            /// <summary>What the viewer derives when the material does not carry the key.</summary>
            public string AbsentMeans;

            /// <summary>Type and element count to create the entry with, both measured.</summary>
            public RenderInfoType Type = RenderInfoType.String;
            public int Length = 1;
        }

        //Engine tables. These are closed sets: a name outside them reads as the first entry.
        static readonly string[] RenderStateModes = Ordered(GsysShaderOptions.RenderStateModes);
        static readonly string[] DisplayFaces = Ordered(GsysShaderOptions.DisplayFaceTypes);
        static readonly string[] CompareFuncs = Ordered(GsysShaderOptions.AlphaTestFuncs);
        static readonly string[] Passes = Ordered(GsysShaderOptions.PassTypes);
        static readonly string[] BlendFactors =
        {
            "zero",
            "one",
            "src_color",
            "one_minus_src_color",
            "dst_color",
            "one_minus_dst_color",
            "src_alpha",
            "one_minus_src_alpha",
            "dst_alpha",
            "one_minus_dst_alpha",
            "const_color",
            "one_minus_const_color",
            "const_alpha",
            "one_minus_const_alpha",
            "src_alpha_saturate",
            "src1_color",
            "one_minus_src1_color",
            "src1_alpha",
            "one_minus_src1_alpha",
        };
        static readonly string[] BlendOps =
        {
            "add",
            "src_minus_dst",
            "dst_minus_src",
            "min",
            "max",
        };

        //The names of a name to choice index table, in choice order.
        static string[] Ordered(IReadOnlyDictionary<string, string> table) =>
            table.OrderBy(x => int.Parse(x.Value)).Select(x => x.Key).ToArray();

        static readonly string[] Bool = { "false", "true" };
        static readonly string[] OnOff = { "off", "on" };
        static readonly string[] Digit = { "0", "1" };

        static readonly Entry[] Entries =
        {
            //Render state. The first five are the ones the program key is derived from, so
            //their absent value is exactly what GsysShaderOptions writes.
            E("gsys_render_state_mode", RenderStateModes, "opaque"),
            E("gsys_render_state_display_face", DisplayFaces, "both"),
            E("gsys_render_state_blend_mode", new[] { "none", "color" }, "none, so no blending"),
            E("gsys_alpha_test_enable", Bool, "false"),
            E("gsys_alpha_test_func", CompareFuncs, "never"),
            N("gsys_alpha_test_value", RenderInfoType.Single, 1),
            E("gsys_pass", Passes, "no_setting"),
            E("gsys_depth_test_enable", Bool, "false"),
            E("gsys_depth_test_func", CompareFuncs, null),
            E("gsys_depth_test_write", Bool, "false"),
            E("gsys_color_blend_rgb_op", BlendOps, "add"),
            E("gsys_color_blend_rgb_src_func", BlendFactors, "zero"),
            E("gsys_color_blend_rgb_dst_func", BlendFactors, "zero"),
            E("gsys_color_blend_alpha_op", BlendOps, "add"),
            E("gsys_color_blend_alpha_src_func", BlendFactors, "zero"),
            E("gsys_color_blend_alpha_dst_func", BlendFactors, "zero"),
            N("gsys_color_blend_const_color", RenderInfoType.Single, 4),
            //Shadow and bake.
            E("gsys_static_depth_shadow", new[] { "0", "1", "2" }, null),
            E("gsys_static_depth_shadow_only", Digit, "0, so the mesh draws normally"),
            E("gsys_dynamic_depth_shadow", new[] { "0", "1", "2" }, null),
            E("gsys_dynamic_depth_shadow_only", Digit, null),
            E("gsys_bake_group", new[] { "none", "group1", "group2", "group3" }, null),
            N("gsys_bake_texel_param", RenderInfoType.Single, 1),
            E("gsys_bake_uv_unite", new[] { "none" }, null),
            E("gsys_bake_option", new[] { "none", "option1" }, null),
            E("gsys_bake_normal_map", new[] { "default" }, null),
            E("gsys_bake_emission_map", new[] { "default" }, null),
            E("bake_cast_shadow", Bool, null),
            E("bake_only_ao", Bool, null),
            //Environment and reflection.
            E("gsys_cube_map", Digit, null),
            E("gsys_cube_map_only", Digit, "0, so the mesh is not cube map only"),
            E("gsys_env_obj_set", null, null),
            E("gsys_multi_filter", Digit, null),
            E("gsys_dynamic_reflection", Digit, null),
            //Sorting and misc engine flags.
            N("gsys_priority", RenderInfoType.Int32, 1),
            E(
                "gsys_priority_hint",
                new[]
                {
                    "none",
                    "field_ground",
                    "field_wall",
                    "object",
                    "npc",
                    "player",
                    "effect",
                    "vr",
                },
                null
            ),
            E("gsys_override_shader", Digit, null),
            E(
                "dynamic_alpha_fadeout",
                new[]
                {
                    "off",
                    "append_xluzprepass",
                    "append_dither",
                    "overlook",
                    "overlook_xluzprepass",
                },
                null
            ),
            E("force_disable_view_frustum_culling", OnOff, null),
            E("spl_model_type", new[] { "0", "1", "3", "5", "6", "7", "8", "9" }, null),
            E("blitz_silhouette_obj", Digit, null),
            //Paint.
            E(
                "paint_prior_face",
                new[]
                {
                    "none",
                    "YPlus",
                    "OnlyFloor",
                    "OnlyWall",
                    "WithoutWall",
                    "TriangleDir",
                    "ThinTriangleFloor",
                },
                null
            ),
            E(
                "paint_build_option",
                new[]
                {
                    "none",
                    "Independent",
                    "DisableConnectPanel",
                    "EnableReplaceUVWarpTrash",
                    "WallDebris",
                    "PriorInside",
                    "DisableBigThinTriangle",
                    "DisableReplaceUVWarp",
                    "DisableDrawMask",
                    "Fit",
                },
                null
            ),
            //Team colour offsets.
            N("my_team_color_hue_offset", RenderInfoType.Single, 1),
            N("my_team_color_bright_offset", RenderInfoType.Single, 1),
            N("substitute_color_hue_offset", RenderInfoType.Single, 1),
            N("substitute_color_bright_offset", RenderInfoType.Single, 1),
            N("substitute_color_saturate_offset", RenderInfoType.Single, 1),
            //Miiverse / memo.
            E("enable_miiverse_filter", OnOff, null),
            E("enable_miiverse_auto_replace", OnOff, null),
            E("miiverse_filter_type", new[] { "0", "4" }, null),
            N("miiverse_priority", RenderInfoType.Int32, 1),
            N("memo_texture_num", RenderInfoType.Int32, 1),
        };

        static Entry E(string name, string[] choices, string absent) =>
            new Entry
            {
                Name = name,
                Choices = choices,
                AbsentMeans = absent,
            };

        static Entry N(string name, RenderInfoType type, int length) =>
            new Entry
            {
                Name = name,
                Type = type,
                Length = length,
            };

        static readonly Dictionary<string, Entry> ByName = Build();

        static Dictionary<string, Entry> Build()
        {
            var map = new Dictionary<string, Entry>();
            foreach (var e in Entries)
                map[e.Name] = e;
            return map;
        }

        /// <summary>Every known key, in the order the panel lists them.</summary>
        public static IReadOnlyList<Entry> All => Entries;

        public static Entry Find(string name) =>
            name != null && ByName.TryGetValue(name, out var e) ? e : null;
    }
}
