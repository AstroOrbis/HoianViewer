using System;
using System.Diagnostics;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// Timestamps the path from a material edit to the splice reaching the screen. Off unless
    /// PV_SPLICE_DEBUG=1, because it prints per stage and per splice.
    ///
    /// Every line is measured from the edit that started the round, so the numbers read as a
    /// latency budget rather than as durations to be added up.
    /// </summary>
    public static class SpliceTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("PV_SPLICE_DEBUG") == "1";

        static long _edit;

        public static void Edit(double settleMs)
        {
            if (!Enabled)
                return;
            _edit = Stopwatch.GetTimestamp();
            Console.WriteLine($"[SpliceT] 0.0ms edit (settle {settleMs:0}ms)");
        }

        public static void Log(string what)
        {
            if (!Enabled || _edit == 0)
                return;
            Console.WriteLine(
                $"[SpliceT] {Stopwatch.GetElapsedTime(_edit).TotalMilliseconds:0.0}ms {what}"
            );
        }

        /// <summary>A line that is not part of an edit's timeline, printed only under the
        /// same switch.</summary>
        public static void Note(string what)
        {
            if (Enabled)
                Console.WriteLine("[Splice] " + what);
        }
    }
}
