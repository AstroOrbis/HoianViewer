using System;
using System.Collections.Generic;
using System.Text;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// The key table ordering the engine's range search depends on, and the checks over it.
    /// </summary>
    public static class KeyOrder
    {
        public delegate int KeyCmp(int[] a, int ao, int[] b, int bo, int len);

        public static readonly KeyCmp CanonicalCmp = CmpWordMajorU;

        static int CmpWordMajorU(int[] a, int ao, int[] b, int bo, int len)
        {
            for (int i = 0; i < len; i++)
            {
                uint x = (uint)a[ao + i],
                    y = (uint)b[bo + i];
                if (x != y)
                    return x < y ? -1 : 1;
            }
            return 0;
        }

        /// <summary>Indices of programs whose static key a lower bound range search cannot
        /// reach. Empty on a sorted table, so this only ever names rows in a broken one.</summary>
        public static List<int> UnreachableByRangeSearch(
            int[] table,
            int n,
            int stride,
            int staticLen
        )
        {
            var bad = new List<int>();
            for (int p = 0; p < n; p++)
            {
                int first = LowerBound(table, n, stride, staticLen, table, p * stride);
                if (
                    first >= n
                    || CanonicalCmp(table, first * stride, table, p * stride, staticLen) != 0
                )
                    bad.Add(p);
            }
            return bad;
        }

        public enum LookupStep
        {
            Found = 0,

            /// <summary>The static key is not in the table, so there is no span to search.</summary>
            NoRange,

            /// <summary>The span exists but no row in it carries the requested dynamic key,
            /// so the archive has the material but not for this assign type and weight.</summary>
            NoDynamic,
        }

        /// <summary>
        /// Lower bound over the static words for the contiguous run,
        /// then an exact match on every dynamic word inside it.
        /// </summary>
        public static int EngineFindProgram(
            int[] table,
            int n,
            int stride,
            int staticLen,
            int[] key,
            out LookupStep step
        )
        {
            int first = LowerBound(table, n, stride, staticLen, key, 0);
            if (first >= n || CanonicalCmp(table, first * stride, key, 0, staticLen) != 0)
            {
                step = LookupStep.NoRange;
                return -1;
            }

            int end = first;
            while (end < n && CanonicalCmp(table, end * stride, key, 0, staticLen) == 0)
                end++;

            int dynLen = stride - staticLen;
            for (int p = first; p < end; p++)
            {
                bool ok = true;
                for (int i = 0; i < dynLen; i++)
                    if (table[p * stride + staticLen + i] != key[staticLen + i])
                    {
                        ok = false;
                        break;
                    }
                if (ok)
                {
                    step = LookupStep.Found;
                    return p;
                }
            }
            step = LookupStep.NoDynamic;
            return -1;
        }

        public static int EngineFindProgram(ShaderModel sm, int[] key, out LookupStep step) =>
            EngineFindProgram(
                sm.KeyTable,
                sm.Programs.Count,
                sm.StaticKeyLength + sm.DynamicKeyLength,
                sm.StaticKeyLength,
                key,
                out step
            );

        static int LowerBound(
            int[] table,
            int n,
            int stride,
            int staticLen,
            int[] key,
            int keyOffset
        )
        {
            int first = 0,
                len = n;
            while (len > 0)
            {
                int half = len / 2,
                    mid = first + half;
                if (CanonicalCmp(table, mid * stride, key, keyOffset, staticLen) < 0)
                {
                    first = mid + 1;
                    len = len - half - 1;
                }
                else
                    len = half;
            }
            return first;
        }

        /// <summary>
        /// The ordering problems a key table has. Meant for an archive read back off disk,
        /// where it checks the writer and the loader rather than the builder.
        /// </summary>
        public static List<string> Check(ShaderModel sm, string what)
        {
            var problems = new List<string>();
            int n = sm.Programs.Count;
            if (n == 0)
                return problems;

            int stat = sm.StaticKeyLength,
                stride = stat + sm.DynamicKeyLength;
            var t = sm.KeyTable;

            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var runStart = new List<int>();
            int unsorted = 0,
                duplicateRows = 0;
            var fullRows = new HashSet<string>(StringComparer.Ordinal);

            for (int p = 0; p < n; p++)
            {
                distinct.Add(Row(t, p, stride, 0, stat));
                if (!fullRows.Add(Row(t, p, stride, 0, stride)))
                    duplicateRows++;
                if (p == 0 || CanonicalCmp(t, p * stride, t, (p - 1) * stride, stat) != 0)
                    runStart.Add(p);
                if (p > 0 && CanonicalCmp(t, (p - 1) * stride, t, p * stride, stride) > 0)
                    unsorted++;
            }

            if (runStart.Count != distinct.Count)
                problems.Add(
                    $"{what}: key table is not grouped by static key, "
                        + $"{runStart.Count} contiguous runs for {distinct.Count} distinct static keys. "
                        + "The engine's range is a contiguous span, so the programs behind the later "
                        + "occurrences are unreachable."
                );

            if (unsorted > 0)
                problems.Add(
                    $"{what}: key table is not sorted under word-major-unsigned "
                        + $"({unsorted} descending step(s)), which is the order both shipped product "
                        + "archives satisfy. A static key search that assumes sorted order will miss rows."
                );

            if (duplicateRows > 0)
                problems.Add(
                    $"{what}: {duplicateRows} duplicate full key row(s); the shipped archives have "
                        + "none, and only one program per row is reachable."
                );

            var unreachable = UnreachableByRangeSearch(t, n, stride, stat);
            if (unreachable.Count > 0)
                problems.Add(
                    $"{what}: {unreachable.Count} of {n} program(s) sit at a static key a lower "
                        + $"bound range search cannot reach: [{string.Join(", ", unreachable.GetRange(0, Math.Min(24, unreachable.Count)))}"
                        + $"{(unreachable.Count > 24 ? ", ..." : "")}]. Each falls back to the global archive."
                );

            return problems;
        }

        /// <summary>
        /// The write path indexes the dynamic half with a bare Bit32Index while the lookup
        /// path uses StaticKeyLength + (Bit32Index - KeyOffset).
        /// </summary>
        public static void AssertDynamicKeyOffsets(ShaderModel sm, string what)
        {
            for (int i = 0; i < sm.DynamicOptions.Count; i++)
            {
                var o = sm.DynamicOptions[i];
                if (o.KeyOffset != sm.StaticKeyLength)
                    throw new InvalidOperationException(
                        $"{what}: dynamic option '{o.Name}' has KeyOffset {o.KeyOffset} but the static key "
                            + $"is {sm.StaticKeyLength} words. The write path and the lookup path would address "
                            + "different words and every program would miss with no error."
                    );
            }
        }

        static string Row(int[] t, int p, int stride, int offset, int len)
        {
            var sb = new StringBuilder(len * 9);
            for (int i = 0; i < len; i++)
                sb.Append(((uint)t[p * stride + offset + i]).ToString("X8")).Append(',');
            return sb.ToString();
        }
    }
}
