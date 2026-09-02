namespace ShaderBundler
{
    /// <summary>
    /// Identity of one splice: the option vector, the pass and the weight. The cache key per
    /// stage and the scheduler's job key are both spelled from it.
    /// </summary>
    public readonly record struct SpliceKey(string VectorHash, string Pass, uint Weight)
    {
        /// <summary>The cache entry name for one stage.</summary>
        public string Cache(ShaderStage stage) =>
            UberSliceCache.MakeKey(VectorHash, stage, Pass, Weight.ToString());

        /// <summary>The scheduler's job identity; the quick lane is its own job.</summary>
        public string Job(bool preview) => preview ? ToString() + "|quick" : ToString();

        public override string ToString() => $"{VectorHash}|{Pass}|w{Weight}";
    }
}
