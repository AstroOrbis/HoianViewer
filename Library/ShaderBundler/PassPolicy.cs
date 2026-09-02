using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// Which gsys_assign_type passes to generate for one material
    /// (based on empirical data)
    /// </summary>
    public static class PassPolicy
    {
        public const string Material = "gsys_assign_material";
        public const string Zonly = "gsys_assign_zonly";
        public const string Visualize = "gsys_assign_visualize";
        public const string Cubemap = "gsys_assign_cubemap";
        public const string DepthSil = "gsys_assign_depth_silhouette";
        public const string Dilate = "gsys_assign_dilate";
        public const string XluZpp = "gsys_assign_xlu_zprepass";
        public const string User1 = "gsys_assign_user1";
        public const string User2 = "gsys_assign_user2";
        public const string User3 = "gsys_assign_user3";
        public const string Dynamic0 = "gsys_assign_dynamic0";
        public const string Dynamic1 = "gsys_assign_dynamic1";
        public const string Dynamic2 = "gsys_assign_dynamic2";
        public const string Dynamic3 = "gsys_assign_dynamic3";
        public const string Dynamic4 = "gsys_assign_dynamic4";

        /// <summary>gsys_weight is the shape's VertexSkinCount and only w0 to w4 are ever
        /// compiled; no shape in the game asks for more, and neither product archive has a
        /// program for -1 or w5 to w8.</summary>
        public const int MaxShippedWeight = 4;

        public enum CubemapMode
        {
            /// <summary>See <see cref="CubemapPredicate"/>.</summary>
            Predictor,
            Always,
            Never,
        }

        public sealed class Opts
        {
            /// <summary>blitz_paint_type in {1,5} ships 0 of 4364 depth_silhouette programs
            /// and blitz_paint_type 5 ships 0 of 239 xlu_zprepass programs. Both are exact
            /// over the whole game.</summary>
            public bool PaintRefine = true;

            public CubemapMode Cubemap = CubemapMode.Predictor;

            /// <summary>Re-admit the four pipelines with zero compiled programs in either
            /// product archive.</summary>
            public bool AllowDead = false;
        }

        /// <summary>
        /// The material level facts the policy may read: the stated, non default shader
        /// options. Nothing here touches an archive.
        /// </summary>
        public sealed class Facts
        {
            public Dictionary<string, string> Opts = new(StringComparer.Ordinal);

            public string Opt(string name, string ifUnstated = "0") =>
                Opts.TryGetValue(name, out var v) ? v : ifUnstated;

            public bool Is(string name, string value) => Opt(name, null) == value;

            public static Facts From(Material mat)
            {
                var f = new Facts();
                var sa = mat.ShaderAssign;
                if (sa?.ShaderOptions != null)
                    for (int i = 0; i < sa.ShaderOptions.Count; i++)
                    {
                        string v = sa.ShaderOptions[i]?.String ?? "";
                        if (v.Length == 0 || v == OptionVector.Unset)
                            continue;
                        f.Opts[sa.ShaderOptions.GetKey(i)] = OptionVector.Normalise(v);
                    }
                return f;
            }
        }

        public sealed class Result
        {
            /// <summary>The passes to generate, in the archive's assign type order.</summary>
            public List<string> Passes = new();

            /// <summary>Pass to the measurement that put it in.</summary>
            public Dictionary<string, string> Why = new(StringComparer.Ordinal);

            /// <summary>Pass to the measurement that kept it out.</summary>
            public Dictionary<string, string> WhyNot = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// Fitted to romfs sample data.
        /// </summary>
        public static bool CubemapPredicate(Facts f) =>
            f.Is("enable_envmap_emission", "1")
            || f.Is("enable_correction_in_envmap", "1")
            || f.Is("blitz_enable_interior_map", "1")
            || f.Is("blitz_rendering_mode", "3")
            || f.Is("blitz_rendering_mode", "4")
            || f.Is("gsys_enable_color_buffer", "1");

        /// <param name="assignTypes">The gsys_assign_type choices the archive declares; a
        /// pass outside them cannot be generated and is left out of the result.</param>
        public static Result Decide(Facts f, Opts o, IReadOnlyList<string> assignTypes)
        {
            if (assignTypes == null)
                throw new ArgumentNullException(nameof(assignTypes));
            o ??= new Opts();
            var r = new Result();
            var set = new HashSet<string>(StringComparer.Ordinal);
            void Add(string p, string why)
            {
                set.Add(p);
                r.Why[p] = why;
            }
            void Not(string p, string why)
            {
                r.WhyNot[p] = why;
            }

            string bpt = f.Opt("blitz_paint_type", "0");
            bool painted = bpt != "0";

            Add(Material, "universal");
            Add(Zonly, "universal");

            if (bpt == "1")
                Add(Dynamic0, "blitz_paint_type == 1");
            else
                Not(Dynamic0, "blitz_paint_type != 1");

            if (painted)
                Add(Dynamic1, $"blitz_paint_type = {bpt} != 0");
            else
                Not(Dynamic1, "blitz_paint_type == 0");

            if (f.Is("is_enable_box_reflection", "1"))
                Add(Dynamic3, "is_enable_box_reflection == 1");
            else
                Not(Dynamic3, "is_enable_box_reflection != 1");

            if (f.Is("is_mantaking_child", "1"))
                Add(User1, "is_mantaking_child == 1");
            else
                Not(User1, "is_mantaking_child != 1");

            Not(Dynamic4, "nothing in the game draws dynamic4");

            switch (o.Cubemap)
            {
                case CubemapMode.Always:
                    Add(Cubemap, "cubemap policy: always");
                    break;
                case CubemapMode.Never:
                    Not(Cubemap, "cubemap policy: never");
                    break;
                default:
                    if (CubemapPredicate(f))
                        Add(Cubemap, "envmap/interior/rendering-mode/color-buffer predictor union");
                    else
                        Not(
                            Cubemap,
                            "no envmap/interior/rendering-mode/color-buffer option stated"
                        );
                    break;
            }

            //depth_silhouette and xlu_zprepass idk, bias towards true
            bool ds = true,
                xl = true;
            string dsWhy = "the engine switches depth_silhouette on per model at runtime";
            string xlWhy = "the engine switches xlu_zprepass on with depth_silhouette at runtime";

            if (o.PaintRefine)
            {
                if (bpt == "1" || bpt == "5")
                {
                    ds = false;
                    dsWhy = $"blitz_paint_type = {bpt}";
                }
                if (bpt == "5")
                {
                    xl = false;
                    xlWhy = "blitz_paint_type = 5";
                }
            }

            if (ds)
                Add(DepthSil, dsWhy);
            else
                Not(DepthSil, dsWhy);
            if (xl)
                Add(XluZpp, xlWhy);
            else
                Not(XluZpp, xlWhy);

            Add(Dynamic2, "every hoian_uber material draws dynamic2");

            foreach (var dead in new[] { Visualize, Dilate, User2, User3 })
            {
                if (o.AllowDead)
                    Add(dead, "dead passes are admitted: no shipped program exists for this pass");
                else
                    Not(
                        dead,
                        "0 programs in hoian_uber in either product archive; "
                            + (
                                dead == Visualize
                                    ? "and even also excluded from the exhaustive fur model compile"
                                : dead == Dilate
                                    ? "and its ubershader column has no fragment colour output at all"
                                : "a byte for byte clone of the material column"
                            )
                    );
            }

            if (set.Contains(DepthSil) && !set.Contains(XluZpp))
                Add(XluZpp, "depth_silhouette subset of xlu_zprepass");
            if (set.Contains(Dynamic0) && !set.Contains(Dynamic1))
                Add(Dynamic1, "dynamic0 subset of dynamic1");

            r.Passes = assignTypes.Where(set.Contains).ToList();
            return r;
        }
    }
}
