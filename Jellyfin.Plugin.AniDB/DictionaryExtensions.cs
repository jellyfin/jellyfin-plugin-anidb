using System.Collections.Generic;

namespace Jellyfin.Plugin.AniDB
{
    /// <summary>
    /// Extension methods for <see cref="IDictionary{TKey, TValue}"/>.
    /// </summary>
    public static class DictionaryExtensions
    {
        /// <summary>
        /// Gets the value associated with the specified key, or the default value of <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="T">The type of the value.</typeparam>
        /// <param name="dict">The dictionary to look in.</param>
        /// <param name="key">The key to look up.</param>
        /// <returns>The value, or <c>default</c> when the key is not present.</returns>
        public static T? GetOrDefault<TKey, T>(this IDictionary<TKey, T> dict, TKey key)
        {
            if (dict.TryGetValue(key, out var value))
            {
                return value;
            }

            return default;
        }
    }
}
