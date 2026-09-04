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
/// The community anime list, which records which AniDB entry fills which season of a show.
/// </summary>
internal static class AniDbAnimeList
{
    /// <summary>
    /// Where the list is downloaded from. It is a file in a branch rather than a release - the
    /// repository publishes none - so there is nothing to ask which build is current, and the
    /// server is asked instead: the entity tag of the last download is offered back, and the
    /// list is sent again only where it has changed since.
    /// </summary>
    private const string ListUrl = "https://raw.githubusercontent.com/Anime-Lists/anime-lists/master/anime-list-full.xml";

    /// <summary>
    /// How long a downloaded list is used before it is fetched again. The list gains entries as
    /// shows are announced, and an entry for a show already in a library rarely changes.
    /// </summary>
    private const int MaxAgeDays = 7;

    private static readonly MappingSourceCache<AniDbAnimeListIndex> _cache = new(
        "anime-list.xml",
        ListUrl,
        "the anime list",
        MaxAgeDays,
        AniDbAnimeListIndex.Parse);

    /// <summary>
    /// The AniDB entries the given season of the given series is filled from, in the order the
    /// season's episodes run through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when the list does not place that season.</returns>
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

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        if (index == null)
        {
            return null;
        }

        var key = FormattableString.Invariant($"{seriesId}/{seasonNumber}");

        if (index.Placements.TryGetValue(key, out var known))
        {
            return known.Count == 0 ? null : known;
        }

        var siblings = index.Siblings(seriesId);
        var segments = siblings == null ? [] : AniDbAnimeListIndex.Place(siblings, seasonNumber);

        // One line per season, and only at debug: the season resolver reports the placement it
        // settles on, so reporting each source's own account again would say it twice. Every
        // episode of the season asks the same question and gets the same answer, so the memo
        // above is what keeps this from being logged per episode.
        if (index.Placements.TryAdd(key, segments) && segments.Count > 0)
        {
            logger.LogDebug(
                "The anime list fills season {SeasonNumber} of AniDB series {SeriesId} with {Placement}",
                seasonNumber,
                seriesId,
                string.Join(", ", segments.Select(SeasonSegments.Describe)));
        }

        return segments.Count == 0 ? null : segments;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TVDB id another provider has already
    /// settled on. The list keys its entries by TVDB id, so this identifies a show outright
    /// where matching on the name cannot: where AniDB spells the name differently, and where
    /// two shows share one name and only the id tells them apart.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the list files nothing under that TVDB id.</returns>
    public static async Task<string?> ResolveSeriesId(
        IApplicationPaths appPaths,
        string? tvdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Anything else is a placeholder the list uses for a show TVDB does not carry, and
        // those are not keys of the index below.
        if (string.IsNullOrEmpty(tvdbId) || !tvdbId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FirstSeasonByTvdb(tvdbId);
    }

    /// <summary>
    /// The AniDB entry a movie is. The list carries a movie's ids on the entry itself, so what it
    /// answers is always that entry's own first episode.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> when the list identifies no movie under that id.</returns>
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
    /// <returns>The TVDB id, or <c>null</c> where the list does not place the entry against one.</returns>
    public static async Task<string?> ResolveSeriesKey(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);
        var seriesKey = index?.SeriesKeyOf(animeId);

        // The list keys a show TVDB does not carry under a placeholder word instead of an id.
        return seriesKey != null && seriesKey.All(char.IsAsciiDigit) ? seriesKey : null;
    }

    /// <summary>
    /// Every key the list files a movie under, given the AniDB entry and episode it is.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry holding the movie.</param>
    /// <param name="episode">Which of its episodes the movie is, where that is known.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The keys, which is empty where the list identifies no such movie.</returns>
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
    /// The entry a show begins in, given an entry of it that the list files as a later season.
    /// AniDB titles a second season "&lt;name&gt; (&lt;year&gt;)" as readily as it titles a
    /// remake that way, so a name match on a show whose seasons all aired in one year lands on
    /// the sequel rather than on the show. The list records which season each entry fills, so
    /// it can walk that back without asking AniDB anything.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the list does not place the entry or already places it at the show's first season.</returns>
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
    /// <returns>The episode, or <c>null</c> when the list does not place it.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveSpecial(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);
        var siblings = index?.Siblings(seriesId);

        if (siblings == null)
        {
            return null;
        }

        var placed = AniDbAnimeListIndex.PlaceSpecial(siblings, episodeNumber);

        if (placed != null)
        {
            logger.LogDebug(
                "The anime list places special {EpisodeNumber} of AniDB series {SeriesId} in anime {AnimeId}",
                episodeNumber,
                seriesId,
                placed.AnimeId);
        }

        return placed;
    }

    /// <summary>
    /// Asks the list what it holds now, whatever the age of the cached copy, and downloads it
    /// where it has changed since. For the button on the configuration page.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the check came to.</returns>
    internal static Task<MappingSourceCheck> CheckNow(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
        => _cache.CheckNow(appPaths, logger, cancellationToken);

    /// <summary>
    /// What is known of the list, for the status the configuration page shows. Reads nothing
    /// but the timestamp of the cached file, so it costs little to ask often.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>When the cached copy was downloaded, when the source was last asked whether it holds a newer one, how many entries have been read from the copy, and how many days one is used for.</returns>
    internal static (DateTime? CachedAtUtc, DateTime? CheckedAtUtc, int EntryCount, int MaxAgeInDays) GetStatus(IApplicationPaths appPaths)
    {
        var (cachedAtUtc, checkedAtUtc, index, maxAgeInDays, _) = _cache.GetStatus(appPaths);

        return (cachedAtUtc, checkedAtUtc, index?.EntryCount ?? 0, maxAgeInDays);
    }
}
