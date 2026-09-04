using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Mappings written by whoever runs the server, which answer before either downloaded source
/// and are the last word on whatever they place.
/// </summary>
/// <remarks>
/// Two shapes of library cannot be described by any source that describes AniDB as it is, and
/// this is what states them:
/// <list type="bullet">
/// <item>
/// A season or a show whose episodes AniDB files as another entry's other episodes. Berserk's
/// Golden Age Arc Memorial Edition and Hellsing Ultimate Abridged are held that way, and a
/// library that keeps either as a show of its own has nothing to be identified as.
/// </item>
/// <item>
/// A specials season holding a special AniDB does not list at all - something shown at an event
/// and never released. It leaves the season one longer than AniDB's own list, and the specials
/// after it one out of step, which is enough to cost the whole season its numbering.
/// </item>
/// </list>
/// <para>
/// Written in the AniBridge schema, so that the reader for it does for both and there is no
/// second format to learn: a file naming an entry's scope maps ranges of it onto ranges of a
/// season, and the scope says which of the entry's numberings the range is read against.
/// </para>
/// </remarks>
internal static class AniDbMappingOverrides
{
    /// <summary>
    /// What the file is called. It sits beside the plugin's own configuration rather than in
    /// the data folder, because it is written by hand and nothing here can fetch it again: the
    /// data folder is a cache, and a cache is a thing people are told to empty.
    /// </summary>
    public const string FileName = "anidb-mapping-overrides.json";

    private const string Description = "the mapping overrides";

    private static readonly MappingSourceCache<AniBridgeIndex> _cache = new(
        FileName,
        null,
        Description,
        0,
        (path, logger, writtenAtUtc) => AniBridgeIndex.Parse(path, logger, writtenAtUtc, Description),
        appPaths => appPaths.PluginConfigurationsPath);

    /// <summary>
    /// The AniDB entries the given season is filled from, in the order its episodes run
    /// through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> where the file does not place that season.</returns>
    public static async Task<IReadOnlyList<AniDbSeasonSegment>?> ResolveSeason(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 1)
        {
            return null;
        }

        var (index, siblings) = await FindShow(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false);

        if (index == null || siblings == null)
        {
            return null;
        }

        var key = FormattableString.Invariant($"{seriesId}/{seasonNumber}");

        if (index.Placements.TryGetValue(key, out var known))
        {
            return known.Count == 0 ? null : known;
        }

        var segments = AniBridgeIndex.Place(siblings, seasonNumber);

        // At information rather than debug, and once per season: a placement written by hand is
        // acted on ahead of everything else, so whoever wrote it should be able to see from an
        // ordinary log that it was read and what it was taken to say.
        if (index.Placements.TryAdd(key, segments) && segments.Count > 0)
        {
            logger.LogInformation(
                "{Source} fill season {SeasonNumber} of AniDB series {SeriesId} with {Placement}",
                Description,
                seasonNumber,
                seriesId,
                string.Join(", ", segments.Select(SeasonSegments.Describe)));
        }

        return segments.Count == 0 ? null : segments;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TVDB id another provider settled on.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> where the file places nothing against that id.</returns>
    public static async Task<string?> ResolveSeriesId(
        IApplicationPaths appPaths,
        string? tvdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tvdbId) || !tvdbId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FirstSeasonByTvdb(tvdbId);
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TMDB id another provider settled on.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tmdbId">The TMDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> where the file places nothing against that id.</returns>
    public static async Task<string?> ResolveSeriesIdByTmdb(
        IApplicationPaths appPaths,
        string? tmdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tmdbId) || !tmdbId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FirstSeasonByTmdb(tmdbId);
    }

    /// <summary>
    /// The AniDB entry a movie is, and which of its episodes holds it.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> where the file identifies no movie under that id.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveMovie(
        IApplicationPaths appPaths,
        string? key,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.ResolveMovie(key);
    }

    /// <summary>
    /// The show an entry is filed under, as the TVDB id its seasons are numbered against.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TVDB id, or <c>null</c> where the file does not place the entry.</returns>
    public static async Task<string?> ResolveSeriesKey(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.SeriesKeyOf(animeId);
    }

    /// <summary>
    /// The TMDB show an entry is placed against, where the file places it against one.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB show id, or <c>null</c> where the file names none.</returns>
    public static async Task<string?> ResolveTmdbShow(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.TmdbShowOf(animeId);
    }

    /// <summary>
    /// Every key the file identifies a movie under, given the AniDB entry and episode it is.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry holding the movie.</param>
    /// <param name="episode">Which of its episodes the movie is, where that is known.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The keys, which is empty where the file identifies no such movie.</returns>
    public static async Task<IReadOnlyList<string>> ResolveMovieKeys(
        IApplicationPaths appPaths,
        string animeId,
        AniDbAnimeListEpisode? episode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.MovieKeysOf(animeId, episode) ?? [];
    }

    /// <summary>
    /// The entry a show begins in, given an entry of it the file places as a later season.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the file does not place the entry or already places it first.</returns>
    public static async Task<string?> ResolveFirstSeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(animeId))
        {
            return null;
        }

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.WalkBackToFirstSeason(animeId);
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> where the file does not place it.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveSpecial(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (_, siblings) = await FindShow(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false);

        if (siblings == null)
        {
            return null;
        }

        var placed = AniBridgeIndex.PlaceSpecial(siblings, episodeNumber);

        if (placed != null)
        {
            logger.LogInformation(
                "{Source} place special {EpisodeNumber} of AniDB series {SeriesId} at {EpisodeNumberInEntry} of anime {AnimeId}",
                Description,
                episodeNumber,
                seriesId,
                placed.Number,
                placed.AnimeId);
        }

        return placed;
    }

    /// <summary>
    /// Whether the file places the given entry in an ordinary season of a show, which makes its
    /// silence about walking that entry back a statement rather than a gap.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> where the file places the entry in a season of its own.</returns>
    public static async Task<bool> FilesInOrdinarySeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FilesInOrdinarySeason(animeId) == true;
    }

    /// <summary>
    /// What is known of the file, for the status the configuration page shows.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Where the file belongs, when it was last written, how many entries, shows and movies have been read from it, and why it could not be read where it could not.</returns>
    internal static async Task<(string Path, DateTime? WrittenAtUtc, int EntryCount, int ShowCount, int MovieCount, string? Error)> GetStatus(
        IApplicationPaths appPaths,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await _cache.GetIndex(appPaths, logger, cancellationToken, reread: true).ConfigureAwait(false);

        var (writtenAtUtc, _, index, _, error) = _cache.GetStatus(appPaths);

        // Movies counted apart because a file that names only movies places no season at all, and
        // one reported as holding no entries would read as a file that had not been understood.
        // Shows apart from entries because a show is what the file is written for, and the two
        // seldom match: one show's season, its specials and its movie are three entries.
        return (
            _cache.GetPath(appPaths),
            writtenAtUtc,
            index?.EntryCount ?? 0,
            index?.ShowCount ?? 0,
            index?.MovieCount ?? 0,
            error);
    }

    /// <summary>
    /// The overrides, and the entries they file under the show the given series belongs to.
    /// </summary>
    /// <remarks>
    /// Looked up by the entry first and by the show it belongs to second. A file written to
    /// place a show nothing else places names the entry a season is read from, and that entry
    /// is what the show is then identified as, so the entry alone is enough. A file written to
    /// correct one season of a show the downloaded sources already place names only that
    /// season's entry, which is not the entry the show was identified as, and there the show is
    /// what the two have in common: whichever downloaded source placed the show says which TVDB
    /// id its seasons are numbered against, and that is the id the overrides are keyed by.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The overrides and the show's entries, either of which may be <c>null</c>.</returns>
    private static async Task<(AniBridgeIndex? Index, IReadOnlyList<AniBridgeEntry>? Siblings)> FindShow(
        IApplicationPaths appPaths,
        string seriesId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        if (index == null || string.IsNullOrEmpty(seriesId))
        {
            return (index, null);
        }

        var siblings = index.Siblings(seriesId);

        if (siblings != null)
        {
            return (index, siblings);
        }

        var seriesKey = await AniDbMappings.ResolveSeriesKey(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false);

        return (index, string.IsNullOrEmpty(seriesKey) ? null : index.EntriesFor(seriesKey));
    }
}
