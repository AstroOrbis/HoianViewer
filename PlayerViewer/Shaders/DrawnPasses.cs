using System;
using ShaderBundler;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// The passes the viewer draws a material with. gbuffer has no column in the ubershader
    /// and falls back to zonly, so these two are what a visible material is drawn with.
    /// </summary>
    public static class DrawnPasses
    {
        public static readonly string[] All = { PassPolicy.Material, PassPolicy.Zonly };

        public static bool IsDrawn(string pass) =>
            pass == PassPolicy.Material || pass == PassPolicy.Zonly;

        /// <summary>The pass name without its gsys_assign_ prefix.</summary>
        public static string Short(string pass) =>
            pass != null && pass.StartsWith("gsys_assign_", StringComparison.Ordinal)
                ? pass.Substring(12)
                : pass;
    }
}
