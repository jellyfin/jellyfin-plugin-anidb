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
/// The mapping sources, asked in order.
/// </summary>
/// <remarks>
/// The AniBridge mappings are asked first. They place half again as many AniDB entries as the
/// anime list, state each placement's episode ranges outright rather than leaving them to be
/// worked out from an offset, and are built partly from the anime list itself, so where both
/// place a show they are the later word on it. They also carry TMDB show ids, which the anime
/// list has for movies only.
/// <para>
/// The anime list answers what AniBridge does not, which at the time of writing is 152 shows
/// AniBridge maps to no TVDB id and a couple of hundred entries' specials. Each source's answer
/// is self-consistent, and the primary answers whenever it can, so the two are only ever mixed
/// for a show AniBridge places in part. That case is logged, because one of the two is wrong
/// about it.
/// </para>
/// <para>
/// Ahead of both sit <see cref="AniDbMappingOverrides"/>, written by whoever runs the server.
/// Neither downloaded source can be corrected from here and neither describes a library that
/// holds something AniDB does not list, so a file that states such a thing outright is the last
/// word on whatever it names, and nothing on anything else.
/// </para>
/// </remarks>
internal static class AniDbMappings
{
    private const string AniBridge = "the AniBridge mappings";
    private const string AnimeList = "the anime list";
    private const string Overrides = "the mapping overrides";

    /// <summary>
    /// How each source that places the given season says it is filled, best account first.
    /// </summary>
    /// <remarks>
    /// Every source that places the season is offered, rather than only the first, because a
    /// placement is a claim about entries that may not hold the episodes it claims. The caller
    /// checks each against what AniDB records and takes the first that holds up, so that a
    /// season one source is wrong about is still filled by the other. A placement from the
    /// overrides is marked as the last word: the caller takes it wherever AniDB holds what it
    /// names, and falls back to the sources below it only where AniDB does not.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The placements, in the order they are worth trying, or empty when no source places the season.</returns>
    public static async Task<IReadOnlyList<SeasonPlacement>> ResolveSeasons(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var placements = new List<SeasonPlacement>(3);

        void Offer(IReadOnlyList<AniDbSeasonSegment>? segments, string source, bool authoritative = false)
        {
            // Two sources agreeing is one placement, not two: the caller checks each against
            // what AniDB records, and checking the same claim twice would only log it twice.
            if (segments != null && !placements.Any(placement => placement.Segments.SequenceEqual(segments)))
            {
                placements.Add(new SeasonPlacement(segments, source, authoritative));
            }
        }

        Offer(
            await AniDbMappingOverrides.ResolveSeason(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false),
            Overrides,
            true);

        var bridged = await AniBridgeMappings.ResolveSeason(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);

        Offer(bridged, AniBridge);

        var listed = await AniDbAnimeList.ResolveSeason(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);

        Offer(listed, AnimeList);

        if (bridged == null && listed != null)
        {
            await ReportGap(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);
        }

        return placements;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from whichever ids another provider has already
    /// settled on. The TVDB id is tried first because both sources carry it, and a show placed
    /// by both is placed against TVDB's numbering by both.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series, where it has one.</param>
    /// <param name="tmdbId">The TMDB id of the series, where it has one.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The show, or <c>null</c> when no source files anything under those ids.</returns>
    public static async Task<MappedSeries?> ResolveSeriesId(
        IApplicationPaths appPaths,
        string? tvdbId,
        string? tmdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var overridden = await AniDbMappingOverrides.ResolveSeriesId(appPaths, tvdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(overridden))
        {
            return new MappedSeries(overridden, Overrides, "TVDB", tvdbId!);
        }

        var bridged = await AniBridgeMappings.ResolveSeriesId(appPaths, tvdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(bridged))
        {
            return new MappedSeries(bridged, AniBridge, "TVDB", tvdbId!);
        }

        var listed = await AniDbAnimeList.ResolveSeriesId(appPaths, tvdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(listed))
        {
            return new MappedSeries(listed, AnimeList, "TVDB", tvdbId!);
        }

        // Only the overrides and AniBridge answer for TMDB, and both are asked after every
        // source has been asked for TVDB: a show carrying both ids is better placed by the id
        // its season numbering will be read against.
        var overriddenByTmdb = await AniDbMappingOverrides.ResolveSeriesIdByTmdb(appPaths, tmdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(overriddenByTmdb))
        {
            return new MappedSeries(overriddenByTmdb, Overrides, "TMDB", tmdbId!);
        }

        var byTmdb = await AniBridgeMappings.ResolveSeriesIdByTmdb(appPaths, tmdbId, logger, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(byTmdb) ? null : new MappedSeries(byTmdb, AniBridge, "TMDB", tmdbId!);
    }

    /// <summary>
    /// The AniDB entry a movie is, found from whichever ids another provider has already settled
    /// on, and which of that entry's episodes holds it.
    /// </summary>
    /// <remarks>
    /// A movie has no seasons to be laid over, so an id is all there is to go on and every
    /// source is asked for every id it might carry. The sources are asked in their own order
    /// rather than the ids in theirs: which source answers decides what the movie is taken to
    /// be, where which of its ids answered decides nothing.
    /// <para>
    /// Both downloaded sources identify a movie chiefly as an AniDB entry of its own. What the
    /// overrides add is the other case: a movie AniDB holds inside an entry registered for
    /// something else, as one of its other episodes or one of its specials.
    /// </para>
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tmdbId">The TMDB movie id, where the item has one.</param>
    /// <param name="imdbId">The IMDb id, where the item has one.</param>
    /// <param name="tvdbId">The TVDB movie id, where the item has one.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movie, or <c>null</c> when no source identifies one under those ids.</returns>
    public static async Task<MappedMovie?> ResolveMovieId(
        IApplicationPaths appPaths,
        string? tmdbId,
        string? imdbId,
        string? tvdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        (string Provider, string? Id, string? Key)[] candidates =
        [
            ("TMDB", tmdbId, MovieKey.Tmdb(tmdbId)),
            ("IMDb", imdbId, MovieKey.Imdb(imdbId)),
            ("TVDB", tvdbId, MovieKey.Tvdb(tvdbId)),
        ];

        (string Name, Func<string?, Task<AniDbAnimeListEpisode?>> Resolve)[] sources =
        [
            (Overrides, key => AniDbMappingOverrides.ResolveMovie(appPaths, key, logger, cancellationToken)),
            (AniBridge, key => AniBridgeMappings.ResolveMovie(appPaths, key, logger, cancellationToken)),
            (AnimeList, key => AniDbAnimeList.ResolveMovie(appPaths, key, logger, cancellationToken)),
        ];

        foreach (var source in sources)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Key == null)
                {
                    continue;
                }

                var found = await source.Resolve(candidate.Key).ConfigureAwait(false);

                if (found != null)
                {
                    return new MappedMovie(found, source.Name, candidate.Provider, candidate.Id!);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The entry a show begins in, given an entry of it that a source files as a later season.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where no source walks it back.</returns>
    public static async Task<string?> ResolveFirstSeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var overridden = await AniDbMappingOverrides.ResolveFirstSeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        var bridged = await AniBridgeMappings.ResolveFirstSeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(bridged))
        {
            return bridged;
        }

        // Not walked back by the anime list where AniBridge files the entry in a season of its
        // own and left it there: AniBridge has already said the entry is a show's first season,
        // and the two disagree about which show an entry belongs to often enough that overruling
        // it here would hand the show to the wrong one. An entry AniBridge knows only as another
        // show's specials is not such a statement, and is left to the anime list.
        if (await AniDbMappingOverrides.FilesInOrdinarySeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false)
            || await AniBridgeMappings.FilesInOrdinarySeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await AniDbAnimeList.ResolveFirstSeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the given episode of the specials season may be read from, best guess first.
    /// </summary>
    /// <remarks>
    /// Every source that places the episode is offered, rather than only the first, because a
    /// placement is a claim about an entry that may not hold such an episode at all: the two
    /// sources disagree about several hundred specials, and AniBridge alone claims some 2,300
    /// that the anime list leaves to be matched against the show's own specials. A caller that
    /// stopped at the first claim would lose a special the second source places correctly.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The placements, in the order they are worth trying, or empty when no source places the episode.</returns>
    public static async Task<IReadOnlyList<AniDbAnimeListEpisode>> ResolveSpecials(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var placements = new List<AniDbAnimeListEpisode>(3);

        void Offer(AniDbAnimeListEpisode? episode)
        {
            if (episode != null && !placements.Contains(episode))
            {
                placements.Add(episode);
            }
        }

        Offer(await AniDbMappingOverrides.ResolveSpecial(appPaths, seriesId, episodeNumber, logger, cancellationToken).ConfigureAwait(false));
        Offer(await AniBridgeMappings.ResolveSpecial(appPaths, seriesId, episodeNumber, logger, cancellationToken).ConfigureAwait(false));
        Offer(await AniDbAnimeList.ResolveSpecial(appPaths, seriesId, episodeNumber, logger, cancellationToken).ConfigureAwait(false));

        return placements;
    }

    /// <summary>
    /// The show an entry belongs to, as the TVDB id whichever downloaded source places it
    /// numbers its seasons against.
    /// </summary>
    /// <remarks>
    /// What a sparsely written override file is reached through: it names the one entry a
    /// season is to be read from, which is rarely the entry the show was identified as, so the
    /// show the two have in common is the only thing that connects them.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TVDB id, or <c>null</c> where no source places the entry against one.</returns>
    public static async Task<string?> ResolveSeriesKey(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bridged = await AniBridgeMappings.ResolveSeriesKey(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(bridged)
            ? await AniDbAnimeList.ResolveSeriesKey(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false)
            : bridged;
    }

    /// <summary>
    /// Notes a season the anime list places and AniBridge does not, for a show AniBridge places
    /// otherwise. One of the two is wrong about that show, and this is the only point at which
    /// a season is filled from a source other than the one that identified the show.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task ReportGap(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await AniBridgeMappings.Places(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug(
                "The AniBridge mappings place AniDB series {SeriesId} but not its season {SeasonNumber}, which {Source} filled instead",
                seriesId,
                seasonNumber,
                AnimeList);
        }
    }
}
