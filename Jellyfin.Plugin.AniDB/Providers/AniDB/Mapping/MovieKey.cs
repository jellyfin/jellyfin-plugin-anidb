using System;
using System.Linq;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// How a movie is keyed across the mapping sources, so that all of them file one under the same
/// string whatever each calls it: AniBridge writes "tmdb_movie:128", the anime list writes a
/// tmdbid attribute, and Jellyfin holds the same id under its own provider name.
/// </summary>
internal static class MovieKey
{
    /// <summary>
    /// The key for a TMDB movie id.
    /// </summary>
    /// <param name="id">The id, as the provider wrote it.</param>
    /// <returns>The key, or <c>null</c> where that is not an id.</returns>
    public static string? Tmdb(string? id) => Numeric("tmdb", id);

    /// <summary>
    /// The key for a TVDB movie id, which TVDB numbers separately from its series.
    /// </summary>
    /// <param name="id">The id, as the provider wrote it.</param>
    /// <returns>The key, or <c>null</c> where that is not an id.</returns>
    public static string? Tvdb(string? id) => Numeric("tvdb", id);

    /// <summary>
    /// The key for an IMDb title id.
    /// </summary>
    /// <param name="id">The id, as the provider wrote it.</param>
    /// <returns>The key, or <c>null</c> where that is not an id.</returns>
    public static string? Imdb(string? id)
    {
        var trimmed = id?.Trim();

        // "tt" and at least one digit. The anime list carries a few ids written some other way,
        // and a key made from one of those would never be asked for.
        if (trimmed == null || trimmed.Length <= 2 || !trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var character in trimmed.AsSpan(2))
        {
            if (!char.IsAsciiDigit(character))
            {
                return null;
            }
        }

        // Lowercased, because a provider writing the prefix in capitals is writing the same id.
        return "imdb:" + trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The provider and id a key is made of, which is what a key found by looking a movie up
    /// has to be taken apart into before it can be written back onto one.
    /// </summary>
    /// <param name="key">The key, as the other methods here write one.</param>
    /// <returns>The two parts, or <c>null</c> where the key is not one of these.</returns>
    public static (string Provider, string Id)? Split(string key)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);

        return separator <= 0 || separator == key.Length - 1
            ? null
            : (key[..separator], key[(separator + 1)..]);
    }

    /// <summary>
    /// The key an AniBridge movie descriptor names, which is "&lt;provider&gt;_movie:&lt;id&gt;".
    /// </summary>
    /// <param name="descriptor">The descriptor as written.</param>
    /// <returns>The key, or <c>null</c> where the descriptor does not name a movie this reads.</returns>
    public static string? FromDescriptor(string descriptor)
    {
        var parts = descriptor.Split(':');

        if (parts.Length != 2)
        {
            return null;
        }

        return parts[0] switch
        {
            "tmdb_movie" => Tmdb(parts[1]),
            "imdb_movie" => Imdb(parts[1]),
            "tvdb_movie" => Tvdb(parts[1]),
            _ => null,
        };
    }

    private static string? Numeric(string provider, string? id)
    {
        var trimmed = id?.Trim();

        return !string.IsNullOrEmpty(trimmed) && trimmed.All(char.IsAsciiDigit) ? provider + ":" + trimmed : null;
    }
}
