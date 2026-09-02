using System;
using System.Collections.Generic;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// Picks the ubershader program a specialisation starts from, and hands back its two
    /// stage binaries and the constant bank each one binds the option block to.
    ///
    /// The ubershader archive is a dense grid: every program shares one static key row and
    /// differs only in the dynamic word, so it is keyed on <c>gsys_assign_type</c> and
    /// <c>gsys_weight</c> alone. The grid is laid out
    /// <c>weightChoiceIdx * assignTypeChoiceCount + assignTypeChoiceIdx</c>, but that is
    /// checked against the archive rather than trusted: the two choices are decoded back out
    /// of the selected program's own key row, and a disagreement falls through to a scan.
    ///
    /// The fragment program depends only on the assign type; the vertex program depends on
    /// both.
    /// </summary>
    public static class UberSelect
    {
        public sealed class Selection
        {
            public int ProgramIndex;
            public string AssignType;
            public string Weight;

            public ShaderBinary Vertex;
            public ShaderBinary Fragment;

            /// <summary>Option constant bank per stage, off the program's own uniform block
            /// location table.</summary>
            public int VertexOptionBank = -1;
            public int FragmentOptionBank = -1;

            public ShaderBinary Stage(ShaderStage stage) =>
                stage == ShaderStage.Vertex ? Vertex : Fragment;

            public int OptionBank(ShaderStage stage) =>
                stage == ShaderStage.Vertex ? VertexOptionBank : FragmentOptionBank;
        }

        /// <summary>The assign type choices the archive declares, in archive order. Nothing
        /// outside this set can ever be generated.</summary>
        public static IReadOnlyList<string> AssignTypes(ShaderModel sm)
        {
            var o = Dynamic(sm, "gsys_assign_type");
            var names = new string[o.Choices.Count];
            for (int i = 0; i < o.Choices.Count; i++)
                names[i] = o.Choices.GetKey(i);
            return names;
        }

        //The uniform block location tables are relative to the first user bank, c[3].
        const int FirstUserBank = 3;

        public static Selection Resolve(ShaderModel sm, string assignType, string weight)
        {
            var assign = Dynamic(sm, "gsys_assign_type");
            var gweight = Dynamic(sm, "gsys_weight");

            if (assign.Choices.GetIndex(assignType) < 0)
                throw new ArgumentException(
                    $"'{assignType}' is not a gsys_assign_type choice of '{sm.Name}'; "
                        + $"it declares {assign.Choices.Count}."
                );
            if (gweight.Choices.GetIndex(weight) < 0)
                throw new ArgumentException(
                    $"'{weight}' is not a gsys_weight choice of '{sm.Name}'."
                );

            int program = FindProgram(sm, assignType, weight, exact: true);
            if (program < 0)
                throw new InvalidOperationException(
                    $"'{sm.Name}' has no program for {assignType} at weight {weight}."
                );

            var s = new Selection
            {
                ProgramIndex = program,
                AssignType = assignType,
                Weight = weight,
            };

            var prog = sm.Programs[program];
            int blockIndex = sm.UniformBlocks.GetIndex(UberOptionTable.OptionBlockName);
            if (blockIndex < 0 || blockIndex >= prog.UniformBlockIndices.Count)
                throw new InvalidOperationException(
                    $"'{sm.Name}' program {program} has no {UberOptionTable.OptionBlockName} "
                        + "uniform block, so the option bank cannot be derived from it."
                );
            var loc = prog.UniformBlockIndices[blockIndex];
            s.VertexOptionBank = loc.VertexLocation < 0 ? -1 : loc.VertexLocation + FirstUserBank;
            s.FragmentOptionBank =
                loc.FragmentLocation < 0 ? -1 : loc.FragmentLocation + FirstUserBank;

            var bp = sm.GetVariation(program)?.BinaryProgram;
            s.Vertex = ShaderBinary.From(bp?.VertexShader);
            s.Fragment = ShaderBinary.From(bp?.FragmentShader);
            return s;
        }

        /// <summary>
        /// The program with this assign type and weight. With <paramref name="exact"/> off it
        /// falls back to any program with the assign type, then to program 0, and returns -1
        /// only for a model with no programs; with it on, -1 means no match.
        /// </summary>
        public static int FindProgram(ShaderModel sm, string assignType, string weight, bool exact)
        {
            if (sm.Programs.Count == 0)
                return -1;
            var assign = sm.DynamicOptions.ContainsKey("gsys_assign_type")
                ? sm.DynamicOptions["gsys_assign_type"]
                : null;
            var gweight = sm.DynamicOptions.ContainsKey("gsys_weight")
                ? sm.DynamicOptions["gsys_weight"]
                : null;
            if (assign == null || gweight == null)
                return exact ? -1 : 0;

            int ai = assign.Choices.GetIndex(assignType);
            int wi = gweight.Choices.GetIndex(weight);
            if (ai < 0 || wi < 0)
                return exact ? -1 : 0;

            //The ubershader grid is dense, so the cell is where the layout puts it unless
            //the archive says otherwise.
            int guess = wi * assign.Choices.Count + ai;
            if (
                guess < sm.Programs.Count
                && DynamicChoice(sm, guess, assign) == ai
                && DynamicChoice(sm, guess, gweight) == wi
            )
                return guess;

            int sameAssign = -1;
            for (int p = 0; p < sm.Programs.Count; p++)
            {
                if (DynamicChoice(sm, p, assign) != ai)
                    continue;
                if (DynamicChoice(sm, p, gweight) == wi)
                    return p;
                if (sameAssign < 0)
                    sameAssign = p;
            }
            if (exact)
                return -1;
            return sameAssign >= 0 ? sameAssign : 0;
        }

        /// <summary>A dynamic option's choice index in a program's key row.</summary>
        public static int DynamicChoice(ShaderModel sm, int program, ShaderOption o)
        {
            int stride = sm.StaticKeyLength + sm.DynamicKeyLength;
            int word = sm.StaticKeyLength + o.Bit32Index - o.KeyOffset;
            return o.GetChoiceIndex(sm.KeyTable[program * stride + word]);
        }

        static ShaderOption Dynamic(ShaderModel sm, string name)
        {
            if (!sm.DynamicOptions.ContainsKey(name))
                throw new InvalidOperationException(
                    $"'{sm.Name}' does not declare the dynamic option '{name}', so it is not an "
                        + "ubershader archive this pipeline can select from."
                );
            return sm.DynamicOptions[name];
        }
    }
}
