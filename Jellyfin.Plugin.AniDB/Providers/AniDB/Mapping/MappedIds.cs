namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The ids other providers know an item by, as the mapping sources file it. What each means
/// depends on the item: a show's TVDB id numbers a series, a movie's numbers a film, and TVDB
/// numbers the two separately.
/// </summary>
/// <param name="Tvdb">The TVDB id, or <c>null</c> where no source names one.</param>
/// <param name="Tmdb">The TMDB id, or <c>null</c> where no source names one.</param>
/// <param name="Imdb">The IMDb id, or <c>null</c> where no source names one.</param>
internal sealed record MappedIds(string? Tvdb, string? Tmdb, string? Imdb)
{
    /// <summary>
    /// Nothing identified.
    /// </summary>
    public static readonly MappedIds None = new(null, null, null);

    /// <summary>
    /// Gets a value indicating whether any id was found.
    /// </summary>
    public bool Any => Tvdb != null || Tmdb != null || Imdb != null;
}
