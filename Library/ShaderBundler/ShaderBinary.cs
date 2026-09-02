using System;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// The stages a bnsh variation can carry that this pipeline produces.
    /// </summary>
    public enum ShaderStage
    {
        Vertex,
        Fragment,
    }

    /// <summary>
    /// One compiled stage: the NVN bytecode blob and its control blob.
    /// </summary>
    public sealed class ShaderBinary
    {
        public readonly byte[] ByteCode;
        public readonly byte[] ControlCode;

        public ShaderBinary(byte[] byteCode, byte[] controlCode)
        {
            ByteCode = byteCode ?? throw new ArgumentNullException(nameof(byteCode));
            ControlCode = controlCode ?? throw new ArgumentNullException(nameof(controlCode));
        }

        /// <summary>A bnsh stage as a binary, or null when either blob is missing.</summary>
        public static ShaderBinary From(BnshFile.ShaderCode code) =>
            code?.ByteCode == null || code.ControlCode == null
                ? null
                : new ShaderBinary(code.ByteCode, code.ControlCode);
    }
}
