using BfresLibrary;
using BfresLibrary.Core;

namespace PlayerViewer.Materials
{
    public static class ResDictExtensions
    {
        /// <summary>Writes a value under a key, adding the entry when the dict has no such
        /// key. The indexer alone throws on a missing key and Add throws on a present one.</summary>
        public static void Set<T>(this ResDict<T> dict, string key, T value)
            where T : IResData, new()
        {
            if (dict.ContainsKey(key))
                dict[key] = value;
            else
                dict.Add(key, value);
        }
    }
}
