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
/// The AniBridge mappings, which record which episodes of which AniDB entry fill which episodes
/// of which season of a show. They state both sides of every placement outright, where the
/// anime list leaves one to be worked out from an offset, and they carry TMDB show ids, which
/// the anime list does not.
/// </summary>
internal static class AniBridgeMappings
{
    /// <summary>
    /// The account and repository the mappings are released from.
    /// </summary>
    private const string Owner = "anibridge";
    private const string Repository = "anibridge-mappings";

    /// <summary>
    /// The release the mappings are downloaded from. The tag names the schema the reader
    /// understands, and it is a rolling release rebuilt daily, so it always carries the newest
    /// build of that schema and a later schema never arrives unannounced. Raising this is how a
    /// later one is taken up, alongside whatever the reader needs to read it.
    /// </summary>
    private const string ReleaseTag = "v3";

    /// <summary>
    /// The asset of that release which holds the mappings.
    /// </summary>
    private const string AssetName = "mappings.min.json";

    /// <summary>
    /// Where the asset is downloaded from where the release cannot be asked which build it is
    /// holding. The tag resolves to whatever is attached to it, so this is the same file, only
    /// fetched without knowing whether it is the one already cached.
    /// </summary>
    private const string MappingsUrl = "https://github.com/" + Owner + "/" + Repository + "/releases/download/" + ReleaseTag + "/" + AssetName;

    /// <summary>
    /// How long a downloaded copy is used before it is fetched again. The mappings are rebuilt
    /// daily, but they gain shows rather than change the ones already placed, and a show already
    /// in a library is placed the same way a week later.
    /// </summary>
    private const int MaxAgeDays = 7;

    private const string Description = "the AniBridge mappings";

    private static readonly MappingSourceCache<AniBridgeIndex> _cache = new(
        "anibridge-mappings.json",
        MappingsUrl,
        Description,
        MaxAgeDays,
        (path, logger, cachedAtUtc) => AniBridgeIndex.Parse(path, logger, cachedAtUtc, Description),
        resolve: (httpClient, cancellationToken) => GitHubRelease.ResolveAsset(
            httpClient,
            Owner,
            Repository,
            ReleaseTag,
            AssetName,
            cancellationToken));

    /// <summary>
    /// The AniDB entries the given season of the given series is filled from, in the order the
    /// season's episodes run through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when the mappings do not place that season.</returns>
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

        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

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
        var segments = siblings == null ? [] : AniBridgeIndex.Place(siblings, seasonNumber);

        // One line per season, and only at debug: the season resolver reports the placement it
        // settles on, so reporting each source's own account again would say it twice. Every
        // episode of the season asks the same question and gets the same answer, so the memo
        // above is what keeps this from being logged per episode.
        if (index.Placements.TryAdd(key, segments) && segments.Count > 0)
        {
            logger.LogDebug(
                "The AniBridge mappings fill season {SeasonNumber} of AniDB series {SeriesId} with {Placement}",
                seasonNumber,
                seriesId,
                string.Join(", ", segments.Select(SeasonSegments.Describe)));
        }

        return segments.Count == 0 ? null : segments;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TVDB id another provider has already
    /// settled on.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the mappings place nothing against that TVDB id.</returns>
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

        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FirstSeasonByTvdb(tvdbId);
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TMDB id another provider has already
    /// settled on. Jellyfin takes TMDB as the provider for shows by default, so for a great
    /// many libraries this is the only id the show carries.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tmdbId">The TMDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the mappings place nothing against that TMDB id.</returns>
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

        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FirstSeasonByTmdb(tmdbId);
    }

    /// <summary>
    /// The AniDB entry a movie is, and which of its episodes holds it.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> when the mappings identify no movie under that id.</returns>
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

        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.ResolveMovie(key);
    }

    /// <summary>
    /// The show an entry is filed under, as the TVDB id its seasons are numbered against.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TVDB id, or <c>null</c> where the mappings do not place the entry.</returns>
    public static async Task<string?> ResolveSeriesKey(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.SeriesKeyOf(animeId);
    }

    /// <summary>
    /// The entry a show begins in, given an entry of it that the mappings file as a later
    /// season.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the mappings do not place the entry or already place it at the show's first season.</returns>
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

        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

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
    /// <returns>The episode, or <c>null</c> when the mappings do not place it.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveSpecial(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);
        var siblings = index?.Siblings(seriesId);

        if (siblings == null)
        {
            return null;
        }

        var placed = AniBridgeIndex.PlaceSpecial(siblings, episodeNumber);

        if (placed != null)
        {
            logger.LogDebug(
                "The AniBridge mappings place special {EpisodeNumber} of AniDB series {SeriesId} in anime {AnimeId}",
                episodeNumber,
                seriesId,
                placed.AnimeId);
        }

        return placed;
    }

    /// <summary>
    /// Whether the mappings file the given entry in an ordinary season of a show.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> where the mappings place the entry in a season of its own.</returns>
    public static async Task<bool> FilesInOrdinarySeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.FilesInOrdinarySeason(animeId) == true;
    }

    /// <summary>
    /// Whether the mappings place the given show at all, which says whether an answer another
    /// source gave is one these disagree with or one they simply do not have.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> where the mappings place the show.</returns>
    public static async Task<bool> Places(
        IApplicationPaths appPaths,
        string seriesId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var index = await GetIndex(appPaths, logger, cancellationToken).ConfigureAwait(false);

        return index?.Siblings(seriesId) != null;
    }

    /// <summary>
    /// Asks the release what build it carries now, whatever the age of the cached copy, and
    /// downloads it where it has been rebuilt since. For the button on the configuration page.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the check came to.</returns>
    internal static Task<MappingSourceCheck> CheckNow(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
        => Plugin.Instance.Configuration.UseAniBridgeMappings
            ? _cache.CheckNow(appPaths, logger, cancellationToken)
            : Task.FromResult(MappingSourceCheck.NotUsed);

    /// <summary>
    /// What is known of the mappings, for the status the configuration page shows.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>When the cached copy was downloaded, when the release was last asked whether it holds a newer build, how many entries have been read from the copy, and how many days one is used for.</returns>
    internal static (DateTime? CachedAtUtc, DateTime? CheckedAtUtc, int EntryCount, int MaxAgeInDays) GetStatus(IApplicationPaths appPaths)
    {
        var (cachedAtUtc, checkedAtUtc, index, maxAgeInDays, _) = _cache.GetStatus(appPaths);

        return (cachedAtUtc, checkedAtUtc, index?.EntryCount ?? 0, maxAgeInDays);
    }

    /// <summary>
    /// The mappings, or <c>null</c> where they are switched off or could not be read.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mappings.</returns>
    private static Task<AniBridgeIndex?> GetIndex(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.UseAniBridgeMappings)
        {
            return Task.FromResult<AniBridgeIndex?>(null);
        }

        return _cache.GetIndex(appPaths, logger, cancellationToken);
    }
}
