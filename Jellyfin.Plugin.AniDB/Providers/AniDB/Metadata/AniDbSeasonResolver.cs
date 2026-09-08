using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// Maps a Jellyfin season to the AniDB anime that holds it.
/// </summary>
internal static partial class AniDbSeasonResolver
{
    /// <summary>
    /// How many candidates one route may read. An uncached candidate costs an AniDB request,
    /// so a long relation list cannot spend the whole allowance. The relation route and the
    /// title route get this many each.
    /// </summary>
    private const int MaxCandidatesPerRoute = 4;

    /// <summary>
    /// How many AniDB entries one series is assumed to span. Bounds the relation walk done
    /// for specials, which has no season number to stop at.
    /// </summary>
    private const int MaxSeasonsInChain = 12;

    /// <summary>
    /// How far back along prequel relations a name match is followed.
    /// </summary>
    private const int MaxPrequelHops = 4;

    /// <summary>
    /// How many episode numbers a season must still be short of before a second AniDB entry is
    /// pulled into it. A season split across two entries is short by a whole cour, while a
    /// season that merely counts an extra file is short by one, and must not swallow the entry
    /// that belongs to the next season.
    /// </summary>
    private const int MinimumSplitEpisodes = 3;

    /// <summary>
    /// How many movies or OVAs in a row the walk will step over to reach the next season. Also
    /// what stops a relation graph that leads back on itself from being followed forever.
    /// </summary>
    private const int MaxInterludeHops = 2;

    /// <summary>
    /// How many AniDB entries one season may be filled from. A season released in cours is two
    /// entries, occasionally three; a higher count means the episode numbers the season spans
    /// are not what they seem, and the later seasons must not be consumed on the strength of it.
    /// </summary>
    private const int MaxSegmentsPerSeason = 3;

    /// <summary>
    /// The AniDB formats a Jellyfin season may be. Sequel relations also point at OVAs,
    /// movies and specials, which are separate items in Jellyfin.
    /// </summary>
    private static readonly string[] _seasonAnimeTypes = ["TV Series", "Web"];

    /// <summary>
    /// How far into a season the one that follows it may start. AniDB's end date is the last
    /// episode's air date, which a delayed finale or a recap can push past the sequel's start.
    /// </summary>
    private static readonly TimeSpan _airingOverlapAllowance = TimeSpan.FromDays(31);

    private static readonly string[] _romanNumerals = ["II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"];

    /// <summary>
    /// The AniDB entries each season of a series was mapped to, keyed by the series id and the
    /// layout it was built from. Building one walks the chain of entries from the series, and
    /// the season and episode providers ask for the same mapping. The layout is part of the key
    /// so that adding episodes to the library rebuilds it rather than reusing an answer that
    /// was right for fewer of them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<AniDbSeasonSegment>>> _mappings = new(StringComparer.Ordinal);

    /// <summary>
    /// One gate per mapping. A scan asks for the same series once per season and once per
    /// episode, all at the same time; without this each of them would walk the chain, and every
    /// walk costs AniDB requests that the first one is about to make anyway.
    /// </summary>
    /// <summary>
    /// How many episodes AniDB records for an entry, against the timestamp of the document it
    /// was read from. Checking a placement needs this for each of its segments, and each of a
    /// season's episodes has the placement checked, so parsing that document every time would
    /// cost more than the check is worth. Keyed by timestamp so that a document downloaded
    /// again is read again: an entry still airing gains episodes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime WrittenAtUtc, int EpisodeCount)> _episodeCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// The seasons whose placement has already been reported, so that it is said once rather
    /// than once per episode.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> _reportedPlacements = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _mappingGates = new(StringComparer.Ordinal);

    /// <summary>
    /// The seasons already reported as unmappable. Every episode of such a season asks about it
    /// too, and one line per season is what says something the log does not already have.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> _reportedUnmapped = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds the AniDB id of the anime holding the given season of the given series.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface, used to read how the series is laid out.</param>
    /// <param name="seriesId">The AniDB id of the series, which is also its first season.</param>
    /// <param name="seasonNumber">The Jellyfin season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entry the season starts in, or <c>null</c> when it cannot be identified.</returns>
    public static async Task<AniDbSeasonSegment?> ResolveSeason(
        IApplicationPaths appPaths,
        ILibraryManager? libraryManager,
        string seriesId,
        int? seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The first season is the series entry itself, and so are specials: AniDB keeps
        // them inside the entry they belong to.
        if (Plugin.Instance.Configuration.IgnoreSeason || seasonNumber is null or <= 1)
        {
            return new AniDbSeasonSegment(seriesId, 1, 0);
        }

        var segments = await ResolveSeasonSegments(appPaths, libraryManager, seriesId, seasonNumber.Value, logger, cancellationToken).ConfigureAwait(false);

        // The season is filled from the entry it starts in. A season split across two entries
        // takes its name and its dates from the first of them, which is where it began.
        return segments?[0];
    }

    /// <summary>
    /// Picks the entry holding the given episode of a season. The episode number and the number
    /// within the entry differ when Jellyfin numbers as one season what AniDB splits across
    /// several entries: episode 13 of such a season is episode 1 of the second entry.
    /// </summary>
    /// <param name="segments">The entries the season is filled from.</param>
    /// <param name="episodeNumber">The Jellyfin episode number.</param>
    /// <returns>The segment holding the episode.</returns>
    public static AniDbSeasonSegment PickSegment(IReadOnlyList<AniDbSeasonSegment> segments, int episodeNumber)
    {
        foreach (var segment in segments)
        {
            if (segment.EpisodeCount <= 0 || episodeNumber < segment.FirstEpisodeNumber + segment.EpisodeCount)
            {
                return segment;
            }
        }

        // Past the last episode AniDB accounts for. The last entry is still the only one the
        // episode can belong to; if it holds no such episode the lookup simply finds nothing.
        return segments[^1];
    }

    /// <summary>
    /// The AniDB entries the series spans, in season order, starting with the series entry.
    /// Costs no AniDB request: it reuses a mapping already built, then follows sequel relations
    /// only through documents already cached on disk. A library that has not been scanned yet
    /// gets the part of the chain that is known.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series, which is also its first season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB ids of the entries the series spans.</returns>
    public static async Task<IReadOnlyList<string>> GetCachedSeasonChain(IApplicationPaths appPaths, string seriesId, CancellationToken cancellationToken)
    {
        var chain = new List<string> { seriesId };

        if (Plugin.Instance.Configuration.IgnoreSeason)
        {
            return chain;
        }

        // A mapping already built went through the full checks, and reusing it keeps a
        // season's specials with the entries that season was filled from.
        foreach (var mapped in GetMappedChain(seriesId))
        {
            if (!chain.Contains(mapped, StringComparer.Ordinal))
            {
                chain.Add(mapped);
            }
        }

        while (chain.Count < MaxSeasonsInChain)
        {
            var previous = await LoadCachedSummary(appPaths, chain[^1]).ConfigureAwait(false);
            if (previous == null)
            {
                break;
            }

            var next = await FindCachedNextSeason(appPaths, previous, previous.SequelIds, chain, MaxInterludeHops, cancellationToken).ConfigureAwait(false);

            if (next == null)
            {
                break;
            }

            chain.Add(next.Id);
        }

        return chain;
    }

    /// <summary>
    /// Finds the entry that follows the given one, reading only documents already cached. Steps
    /// over a movie or an OVA the same way the full walk does, so that a season reached only
    /// through one still has its specials searched.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="previous">The entry to follow.</param>
    /// <param name="candidateIds">The entries to consider.</param>
    /// <param name="chain">The chain built so far, which nothing may repeat.</param>
    /// <param name="hopsLeft">How many more movies or OVAs may be stepped over.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The next entry, or <c>null</c> when the cached documents do not reach one.</returns>
    private static async Task<AniDbAnimeSummary?> FindCachedNextSeason(
        IApplicationPaths appPaths,
        AniDbAnimeSummary previous,
        IReadOnlyList<string> candidateIds,
        IReadOnlyList<string> chain,
        int hopsLeft,
        CancellationToken cancellationToken)
    {
        AniDbAnimeSummary? next = null;
        var interludes = new List<AniDbAnimeSummary>();

        foreach (var candidateId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chain.Contains(candidateId, StringComparer.Ordinal))
            {
                continue;
            }

            var candidate = await LoadCachedSummary(appPaths, candidateId).ConfigureAwait(false);

            if (candidate == null)
            {
                continue;
            }

            if (!CanFollow(previous, candidate))
            {
                if (!_seasonAnimeTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase)
                    && StartsAfter(previous, candidate))
                {
                    interludes.Add(candidate);
                }

                continue;
            }

            // Of everything that could follow this entry, the next season starts first.
            if (next == null || (candidate.StartDate ?? DateTime.MaxValue) < (next.StartDate ?? DateTime.MaxValue))
            {
                next = candidate;
            }
        }

        if (next != null || hopsLeft <= 0)
        {
            return next;
        }

        foreach (var interlude in interludes)
        {
            next = await FindCachedNextSeason(appPaths, previous, interlude.SequelIds, chain, hopsLeft - 1, cancellationToken).ConfigureAwait(false);

            if (next != null)
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>
    /// The entries of every mapping already built for the series, in season order.
    /// </summary>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <returns>The AniDB ids the series' seasons were mapped to.</returns>
    private static IEnumerable<string> GetMappedChain(string seriesId)
    {
        var prefix = seriesId + "|";

        foreach (var mapping in _mappings)
        {
            if (!mapping.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var season in mapping.Value.OrderBy(entry => entry.Key))
            {
                foreach (var segment in season.Value)
                {
                    yield return segment.AnimeId;
                }
            }
        }
    }

    /// <summary>
    /// The AniDB entries the given season is filled from, in the order its episodes run
    /// through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The Jellyfin season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when the season cannot be identified.</returns>
    public static async Task<IReadOnlyList<AniDbSeasonSegment>?> ResolveSeasonSegments(
        IApplicationPaths appPaths,
        ILibraryManager? libraryManager,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var layout = AniDbSeasonLayout.Read(libraryManager, seriesId);
        var placed = await PickPlacement(appPaths, seriesId, seasonNumber, layout, logger, cancellationToken).ConfigureAwait(false);

        if (placed.Fitted.Count > 0)
        {
            return placed.Fitted;
        }

        var key = seriesId + "|" + (layout?.Signature ?? "-");

        if (!_mappings.TryGetValue(key, out var mapping))
        {
            var gate = _mappingGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!_mappings.TryGetValue(key, out mapping))
                {
                    mapping = await BuildMapping(appPaths, seriesId, layout, logger, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        if (mapping.TryGetValue(seasonNumber, out var segments))
        {
            return segments;
        }

        // A placement that does not account for the season is still better than nothing, and
        // this is where nothing is what the chain came to.
        if (placed.Partial.Count > 0)
        {
            return placed.Partial;
        }

        if (_reportedUnmapped.TryAdd(FormattableString.Invariant($"{key}/{seasonNumber}"), 0))
        {
            logger.LogWarning(
                "Season {SeasonNumber} of AniDB series {SeriesId} could not be mapped to an AniDB entry. Nothing AniDB relates to the entries before it, or shares a title with them, can be that season. It and its episodes stay without metadata; set its AniDB id by hand to fill it in",
                seasonNumber,
                seriesId);
        }

        return null;
    }

    /// <summary>
    /// The placement of a season that holds up against what AniDB records, out of those the
    /// mapping sources offer.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="layout">How the series is laid out in the library, or <c>null</c> when it cannot be seen.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The placement that accounts for the season, and the fullest that does not.</returns>
    private static async Task<(IReadOnlyList<AniDbSeasonSegment> Fitted, IReadOnlyList<AniDbSeasonSegment> Partial)> PickPlacement(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        AniDbSeasonLayout? layout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var placements = await AniDbMappings.ResolveSeasons(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);

        if (placements.Count == 0)
        {
            return ([], []);
        }

        // Said once per season rather than once per episode. The placement is worked out afresh
        // each time, cheaply, so that a mapping file downloaded since is acted on without
        // waiting for a restart.
        var reported = _reportedPlacements.TryAdd(FormattableString.Invariant($"{seriesId}/{seasonNumber}"), 0);
        var wanted = layout?.Seasons.FirstOrDefault(season => season.Number == seasonNumber)?.EpisodeCount ?? 0;

        SeasonPlacement? best = null;
        var covered = 0;

        foreach (var placement in placements)
        {
            var unheld = await FirstUnheldSegment(appPaths, placement.Segments).ConfigureAwait(false);

            if (unheld != null)
            {
                if (reported)
                {
                    logger.LogWarning(
                        "{Source} fill season {SeasonNumber} of AniDB series {SeriesId} from episode {EpisodeNumberInEntry} onwards of anime {AnimeId}, which AniDB records only {EpisodeCount} episodes for, so that placement is not used",
                        placement.Source,
                        seasonNumber,
                        seriesId,
                        unheld.Segment.FirstEpisodeInEntry,
                        unheld.Segment.AnimeId,
                        unheld.EpisodeCount);
                }

                continue;
            }

            // A placement written by hand is not weighed against anything: not against how far
            // the other sources reach, and not against how long the library's season is. It
            // says which episodes of which entry fill a season, AniDB holds them, and that is
            // the whole of the question.
            if (placement.Authoritative)
            {
                if (reported)
                {
                    logger.LogInformation(
                        "Season {SeasonNumber} of AniDB series {SeriesId} is filled with {Placement}, where {Source} place it",
                        seasonNumber,
                        seriesId,
                        string.Join(", ", placement.Segments.Select(SeasonSegments.Describe)),
                        placement.Source);
                }

                return (placement.Segments, []);
            }

            var reach = Reach(placement.Segments);

            if (best == null || reach > covered)
            {
                best = placement;
                covered = reach;
            }

            // Nothing beats accounting for the whole season, and where the library cannot say
            // how long the season is there is nothing to compare by, so the first source to
            // answer keeps its precedence.
            if (wanted <= 0 || covered >= wanted)
            {
                break;
            }
        }

        if (best == null)
        {
            return ([], []);
        }

        // A placement that leaves episodes of the season unaccounted for is not describing this
        // library's season. Inuyasha is the case that shows why: TVDB has since merged what the
        // sources still call its sixth and seventh seasons, so from the sixth on their numbering
        // runs one ahead of the library's, and the season holding the Final Act is given the
        // last eight episodes of the original series instead. Laying the chain of entries over
        // the seasons the library actually has gets that right, so it is given the chance to,
        // and this is kept only for where the chain comes to nothing.
        var fits = wanted <= 0 || covered >= wanted;

        if (reported)
        {
            if (fits)
            {
                logger.LogInformation(
                    "Season {SeasonNumber} of AniDB series {SeriesId} is filled with {Placement}, where {Source} place it",
                    seasonNumber,
                    seriesId,
                    string.Join(", ", best.Segments.Select(SeasonSegments.Describe)),
                    best.Source);
            }
            else
            {
                logger.LogWarning(
                    "{Source} account for {Covered} of the {Wanted} episodes the library holds under season {SeasonNumber} of AniDB series {SeriesId}, so that placement is not used and the season is worked out from AniDB's own relations instead. The mapping file is describing a different season layout from the one the library has",
                    best.Source,
                    covered,
                    wanted,
                    seasonNumber,
                    seriesId);
            }
        }

        return fits ? (best.Segments, []) : ([], best.Segments);
    }

    /// <summary>
    /// How many of a season's episodes a placement accounts for.
    /// </summary>
    /// <remarks>
    /// A source that describes only part of a season - AniBridge maps one episode of Ginga
    /// Eiyuu Densetsu: Die Neue These's later seasons and leaves the other eleven to its own
    /// scope for AniDB's other episode types, which nothing here reads - would otherwise be
    /// preferred over one that describes all of it, its answer being neither empty nor wrong
    /// about the episode it does name.
    /// </remarks>
    /// <param name="segments">The segments the placement is made of.</param>
    /// <returns>The episode count, or <see cref="int.MaxValue"/> where a segment runs to the end of the season.</returns>
    private static int Reach(IReadOnlyList<AniDbSeasonSegment> segments)
    {
        var reach = 0;

        foreach (var segment in segments)
        {
            if (segment.EpisodeCount <= 0)
            {
                return int.MaxValue;
            }

            reach += segment.EpisodeCount;
        }

        return reach;
    }

    /// <summary>
    /// The first segment of a placement that names episodes the entry it names does not have.
    /// </summary>
    /// <remarks>
    /// The mapping sources report tens of thousands of range inconsistencies among themselves,
    /// and a segment reaching past the end of its entry is the one kind that can be checked
    /// here for nothing: the entry's episode count is in a document already on disk. An entry
    /// that is not cached cannot be checked, and is taken at the source's word rather than
    /// spending a request to doubt it.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="segments">The segments the placement is made of.</param>
    /// <returns>The offending segment and the entry's episode count, or <c>null</c> where every segment holds up.</returns>
    private static async Task<UnheldSegment?> FirstUnheldSegment(IApplicationPaths appPaths, IReadOnlyList<AniDbSeasonSegment> segments)
    {
        foreach (var segment in segments)
        {
            // AniDB counts an entry's ordinary episodes and nothing else, so a segment reading
            // from another of its numberings has nothing here to be checked against.
            if (segment.Kind != AniDbEpisodeKind.Regular)
            {
                continue;
            }

            var episodeCount = await GetCachedEpisodeCount(appPaths, segment.AnimeId).ConfigureAwait(false);

            // Nothing on disk to check against. AniDB also counts an anime still airing as the
            // episodes it will have, so a segment reaching into a season part way through
            // airing is not an inconsistency.
            if (episodeCount <= 0)
            {
                continue;
            }

            // A segment with no count runs to the end of the season, so only where it starts
            // can be checked.
            var last = segment.EpisodeCount > 0
                ? segment.FirstEpisodeInEntry + segment.EpisodeCount - 1
                : segment.FirstEpisodeInEntry;

            if (last > episodeCount)
            {
                return new UnheldSegment(segment, episodeCount);
            }
        }

        return null;
    }

    /// <summary>
    /// How many episodes AniDB records for an entry, read from the document already on disk.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <returns>The episode count, or 0 where the entry is not cached or records none.</returns>
    private static async Task<int> GetCachedEpisodeCount(IApplicationPaths appPaths, string animeId)
    {
        var path = Path.Combine(AniDbSeriesProvider.GetSeriesDataPath(appPaths, animeId), "series.xml");
        var file = new FileInfo(path);

        if (!file.Exists || file.Length == 0)
        {
            return 0;
        }

        if (_episodeCounts.TryGetValue(animeId, out var known) && known.WrittenAtUtc == file.LastWriteTimeUtc)
        {
            return known.EpisodeCount;
        }

        var summary = await ParseSummary(animeId, path).ConfigureAwait(false);

        _episodeCounts[animeId] = (file.LastWriteTimeUtc, summary.EpisodeCount);

        return summary.EpisodeCount;
    }

    /// <summary>
    /// Lays the chain of AniDB entries over the seasons the library has, giving each season the
    /// entries whose episodes it covers.
    /// </summary>
    /// <remarks>
    /// AniDB and the provider that numbers the seasons do not agree on where a season ends: a
    /// season released in two cours is two AniDB entries and one season everywhere else.
    /// Counting one entry per season therefore slips a place at the first such split and gets
    /// every later season wrong, which is why the episode numbers a season spans are what the
    /// chain is laid against. Without a layout to lay it against there is nothing to correct
    /// with, and one entry per season is all that can be assumed.
    /// </remarks>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="layout">How the series is laid out in the library, or <c>null</c> when it cannot be seen.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries each season is filled from.</returns>
    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<AniDbSeasonSegment>>> BuildMapping(
        IApplicationPaths appPaths,
        string seriesId,
        AniDbSeasonLayout? layout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var mapping = new Dictionary<int, IReadOnlyList<AniDbSeasonSegment>>();

        // Left outside the ban handling below: a ban on the series' own entry is what the
        // providers report, and there is nothing to map without it.
        var first = await LoadSummary(appPaths, seriesId, cancellationToken).ConfigureAwait(false);

        if (first == null)
        {
            return mapping;
        }

        var chain = new List<AniDbAnimeSummary> { first };
        var chainIndex = 0;

        // How many episodes of the entry at that position earlier seasons have already taken.
        var consumedInEntry = 0;
        var complete = true;

        foreach (var season in layout?.Seasons ?? GetAssumedSeasons())
        {
            if (season.Number <= 0)
            {
                continue;
            }

            var segments = new List<AniDbSeasonSegment>();
            var covered = 0;

            while (true)
            {
                var (entry, banned) = await GetChainEntry(appPaths, seriesId, chain, chainIndex, logger, cancellationToken).ConfigureAwait(false);

                complete &= !banned;

                if (entry == null)
                {
                    break;
                }

                var (count, outlastsSeason) = Allocate(entry.EpisodeCount, consumedInEntry, season.EpisodeCount, covered);

                segments.Add(new AniDbSeasonSegment(entry.Id, season.FirstEpisodeNumber + covered, count, consumedInEntry + 1));
                covered += count;

                if (outlastsSeason)
                {
                    consumedInEntry += count;
                }
                else
                {
                    chainIndex++;
                    consumedInEntry = 0;
                }

                if (season.EpisodeCount - covered < MinimumSplitEpisodes || segments.Count >= MaxSegmentsPerSeason)
                {
                    break;
                }
            }

            if (segments.Count == 0)
            {
                break;
            }

            mapping[season.Number] = segments;

            if (segments.Count > 1)
            {
                logger.LogInformation(
                    "Season {SeasonNumber} of AniDB series {SeriesId} spans {EntryCount} AniDB entries, {AnimeIds}, because it covers {EpisodeCount} episodes and no single entry does",
                    season.Number,
                    seriesId,
                    segments.Count,
                    string.Join(", ", segments.Select(segment => segment.AnimeId)),
                    season.EpisodeCount);
            }
            else
            {
                logger.LogInformation(
                    "Season {SeasonNumber} of AniDB series {SeriesId} is anime {SeasonId}",
                    season.Number,
                    seriesId,
                    segments[0].AnimeId);
            }
        }

        if (layout == null)
        {
            logger.LogInformation(
                "AniDB series {SeriesId} was mapped one entry per season, because its seasons could not be read from the library. A season AniDB splits in two will be off by one entry from there on; refreshing it once the episodes are in the library corrects that",
                seriesId);
        }

        // A mapping cut short by a ban would otherwise be reused for as long as the server runs.
        if (complete)
        {
            _mappings[seriesId + "|" + (layout?.Signature ?? "-")] = mapping;
        }

        return mapping;
    }

    /// <summary>
    /// How much of an entry one season takes, and whether the entry runs on past it.
    /// </summary>
    /// <remarks>
    /// An entry with more episodes left than the season has room for is one that the season
    /// numbering breaks into several seasons, as a long-running show kept as a single AniDB
    /// entry is: Dragon Ball Z is one entry of 291 episodes that TVDB splits into nine. Such an
    /// entry gives this season what fits and keeps the rest for the next one. Taking a fresh
    /// entry per season instead left every season past the first with nothing, the chain having
    /// run out after one.
    /// </remarks>
    /// <param name="entryEpisodeCount">How many episodes AniDB records for the entry, or 0 where it records none.</param>
    /// <param name="consumedInEntry">How many of the entry's episodes earlier seasons have taken.</param>
    /// <param name="seasonEpisodeCount">How many episode numbers the season spans, or 0 where the library cannot be read.</param>
    /// <param name="covered">How many of the season's episodes the segments so far account for.</param>
    /// <returns>How many episodes this season takes from the entry, and whether the entry has episodes left over.</returns>
    private static (int Count, bool OutlastsSeason) Allocate(int entryEpisodeCount, int consumedInEntry, int seasonEpisodeCount, int covered)
    {
        var room = Math.Max(seasonEpisodeCount - covered, 0);

        // An entry AniDB gives no episode count for takes whatever the season has left, so that
        // it is never the reason a second entry is pulled in. Without a season length to fit it
        // to there is nothing to split against either, and the entry answers for the season
        // whole, which is what a library that cannot be read has always been given.
        if (entryEpisodeCount <= 0 || seasonEpisodeCount <= 0)
        {
            return (entryEpisodeCount > 0 ? entryEpisodeCount - consumedInEntry : room, false);
        }

        var left = entryEpisodeCount - consumedInEntry;

        return left > room ? (room, true) : (left, false);
    }

    /// <summary>
    /// Reads the entry at the given position of the chain, extending the chain to reach it.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="chain">The chain built so far.</param>
    /// <param name="index">The position wanted.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entry, or <c>null</c> when the chain ends before it, and whether a ban stopped the walk.</returns>
    private static async Task<(AniDbAnimeSummary? Entry, bool Banned)> GetChainEntry(
        IApplicationPaths appPaths,
        string seriesId,
        List<AniDbAnimeSummary> chain,
        int index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        while (chain.Count <= index && chain.Count < MaxSeasonsInChain)
        {
            string? nextId;

            try
            {
                nextId = await ResolveNextSeason(appPaths, seriesId, chain[^1], chain.Count + 1, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (AniDbBannedException)
            {
                logger.LogDebug(
                    "The chain of AniDB series {SeriesId} could not be walked past anime {AnimeId} because AniDB has banned this client",
                    seriesId,
                    chain[^1].Id);

                return (null, true);
            }

            if (string.IsNullOrEmpty(nextId) || chain.Exists(entry => string.Equals(entry.Id, nextId, StringComparison.Ordinal)))
            {
                return (null, false);
            }

            var next = await LoadSummary(appPaths, nextId, cancellationToken).ConfigureAwait(false);

            if (next == null)
            {
                return (null, false);
            }

            chain.Add(next);
        }

        return (index < chain.Count ? chain[index] : null, false);
    }

    /// <summary>
    /// The seasons assumed when the library cannot be read: as many as the chain of AniDB
    /// entries turns out to have, one entry each.
    /// </summary>
    /// <returns>The assumed seasons.</returns>
    private static IEnumerable<AniDbLibrarySeason> GetAssumedSeasons()
    {
        for (var season = 1; season <= MaxSeasonsInChain; season++)
        {
            yield return new AniDbLibrarySeason(season, 1, 0);
        }
    }

    /// <summary>
    /// Whether a name is asking for one season of a show rather than for the show itself, as a
    /// folder named "Show Season 2" is.
    /// </summary>
    /// <remarks>
    /// A season is also named by the word for it in another language, and by the name a final
    /// season is given instead of a number: AniDB files fifteen entries as "Kanketsuhen" and
    /// titles them "The Final Act" in English, InuYasha's eighth season among them. Such a name
    /// read as the show's own took the show's first entry and left the season it asked for
    /// unidentified.
    /// </remarks>
    /// <param name="name">The name the series was searched under.</param>
    /// <returns><c>true</c> when the name ends in a season marker.</returns>
    public static bool NamesASeason(string? name)
        => SeasonMarkerRegex().IsMatch(name ?? string.Empty);

    /// <summary>
    /// Walks a name match back to the first season of the show it belongs to.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name search produced.</param>
    /// <param name="seriesName">The name the series was searched under.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id of the first season, or the given id when it already is one.</returns>
    public static async Task<string> ResolveFirstSeasonId(
        IApplicationPaths appPaths,
        string animeId,
        string? seriesName,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        // A folder that names a season is asking for that season, not for the show.
        if (string.IsNullOrEmpty(animeId) || NamesASeason(seriesName))
        {
            return animeId;
        }

        var currentId = animeId;

        try
        {
            currentId = await WalkToFirstSeason(appPaths, animeId, seriesName, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (AniDbBannedException)
        {
            // The matched entry may be cached while its prequel is not. Keep the match
            // rather than lose metadata already on disk.
            logger?.LogDebug("Anime {AnimeId} could not be checked for an earlier season because AniDB has banned this client", animeId);
        }

        return currentId;
    }

    private static async Task<string> WalkToFirstSeason(
        IApplicationPaths appPaths,
        string animeId,
        string? seriesName,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var currentId = animeId;

        for (var hop = 0; hop < MaxPrequelHops; hop++)
        {
            var current = await LoadSummary(appPaths, currentId, cancellationToken).ConfigureAwait(false);

            // Only an entry with a prequel can be a later season.
            if (current == null || current.PrequelIds.Count == 0)
            {
                break;
            }

            string? earlierId = null;

            foreach (var prequelId in current.PrequelIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidate = await LoadSummary(appPaths, prequelId, cancellationToken).ConfigureAwait(false);

                // AniDB records a prequel relation between any two shows that follow each
                // other, including ones Jellyfin keeps apart. Only a title continued by
                // number is the same show.
                if (candidate != null
                    && _seasonAnimeTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase)
                    && IsNumberedContinuation(candidate.Titles, current.Titles))
                {
                    earlierId = candidate.Id;

                    break;
                }
            }

            if (earlierId == null)
            {
                break;
            }

            logger?.LogInformation(
                "The name {SeriesName} matched anime {MatchedId}, which is a later season of anime {FirstId}. Using the earlier entry, so that the seasons below it resolve from the start of the show",
                seriesName,
                currentId,
                earlierId);

            currentId = earlierId;
        }

        return currentId;
    }

    private static async Task<string?> ResolveNextSeason(
        IApplicationPaths appPaths,
        string seriesId,
        AniDbAnimeSummary previous,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var previousId = previous.Id;

        // Sequel relations catch the seasons that were renamed rather than numbered, and
        // never turn up an anime that merely shares a title. Read from the previous season's
        // cached document, so the route costs nothing.
        var related = previous.SequelIds
            .Where(sequelId => !string.Equals(sequelId, previousId, StringComparison.Ordinal))
            .ToList();

        var fromRelations = await ChooseNextSeason(appPaths, previous, related, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(fromRelations))
        {
            return fromRelations;
        }

        // A movie or an OVA released between two seasons carries the chain on: AniDB relates
        // the next season to that release rather than to the season before it, so a season
        // whose only sequel is one would otherwise be the end of the show. It is stepped over
        // rather than mapped, Jellyfin having it as an item of its own.
        var beyond = await ChooseNextSeasonBeyond(appPaths, previous, related, [previousId], MaxInterludeHops, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(beyond))
        {
            return beyond;
        }

        // No usable relation, which is also how an entry with none recorded looks. The titles
        // file is already in memory, so a sequel titled with its season number costs no
        // request; a hit there still has to pass the same checks.
        var series = string.Equals(previousId, seriesId, StringComparison.Ordinal)
            ? previous
            : await LoadSummary(appPaths, seriesId, cancellationToken).ConfigureAwait(false);

        if (series == null)
        {
            return null;
        }

        var titled = new List<string>();

        foreach (var title in GetSeasonTitles(series.Titles, seasonNumber))
        {
            var id = await LookupTitle(title, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(id)
                && !string.Equals(id, previousId, StringComparison.Ordinal)
                && !related.Contains(id, StringComparer.Ordinal)
                && !titled.Contains(id, StringComparer.Ordinal))
            {
                titled.Add(id);
            }
        }

        return await ChooseNextSeason(appPaths, previous, titled, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ChooseNextSeason(
        IApplicationPaths appPaths,
        AniDbAnimeSummary previous,
        IReadOnlyList<string> candidateIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var survivors = new List<AniDbAnimeSummary>();
        var read = 0;

        foreach (var candidateId in candidateIds)
        {
            if (read++ >= MaxCandidatesPerRoute)
            {
                break;
            }

            var candidate = await LoadSummary(appPaths, candidateId, cancellationToken).ConfigureAwait(false);

            if (candidate == null)
            {
                logger.LogDebug("Candidate {CandidateId} to follow anime {PreviousId} could not be read", candidateId, previous.Id);

                continue;
            }

            var usable = CanFollow(previous, candidate);

            logger.LogDebug(
                "Candidate {CandidateId} to follow anime {PreviousId} is a {Type} that aired {StartDate} to {EndDate}, related {Related}, usable {Usable}",
                candidateId,
                previous.Id,
                candidate.Type,
                candidate.StartDate,
                candidate.EndDate,
                IsRelated(previous, candidate),
                usable);

            if (usable)
            {
                survivors.Add(candidate);
            }
        }

        // Of everything that could follow this season, the next one starts first. A related
        // anime outranks one that only matched by title.
        return survivors
            .OrderBy(candidate => IsRelated(previous, candidate) ? 0 : 1)
            .ThenBy(candidate => candidate.StartDate ?? DateTime.MaxValue)
            .FirstOrDefault()?.Id;
    }

    private static async Task<string?> ChooseNextSeasonBeyond(
        IApplicationPaths appPaths,
        AniDbAnimeSummary previous,
        IReadOnlyList<string> candidateIds,
        IReadOnlyCollection<string> seen,
        int hopsLeft,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (hopsLeft <= 0)
        {
            return null;
        }

        var visited = new HashSet<string>(seen, StringComparer.Ordinal);
        visited.UnionWith(candidateIds);

        var onwards = new List<string>();
        var read = 0;

        foreach (var candidateId in candidateIds)
        {
            if (read++ >= MaxCandidatesPerRoute)
            {
                break;
            }

            // Already read on the route above, so this comes off the disk rather than AniDB.
            var interlude = await LoadSummary(appPaths, candidateId, cancellationToken).ConfigureAwait(false);

            // Only something released after this season can stand between it and the next one,
            // and only something that is not a season itself needs stepping over.
            if (interlude == null
                || _seasonAnimeTypes.Contains(interlude.Type, StringComparer.OrdinalIgnoreCase)
                || !StartsAfter(previous, interlude))
            {
                continue;
            }

            foreach (var sequelId in interlude.SequelIds)
            {
                if (visited.Add(sequelId))
                {
                    onwards.Add(sequelId);
                }
            }
        }

        if (onwards.Count == 0)
        {
            return null;
        }

        logger.LogDebug(
            "Nothing AniDB relates to anime {PreviousId} is a season, so the {Count} anime beyond the movies, specials and OVAs that follow it are tried instead",
            previous.Id,
            onwards.Count);

        var found = await ChooseNextSeason(appPaths, previous, onwards, logger, cancellationToken).ConfigureAwait(false);

        // A season can sit behind more than one release, as a movie followed by a recap special.
        return string.IsNullOrEmpty(found)
            ? await ChooseNextSeasonBeyond(appPaths, previous, onwards, visited, hopsLeft - 1, logger, cancellationToken).ConfigureAwait(false)
            : found;
    }

    /// <summary>
    /// Whether the candidate was released after the given anime began. Looser than
    /// <see cref="CanFollow"/>, which asks whether one season follows another: a movie released
    /// between two seasons routinely comes out while the first is still on the air.
    /// </summary>
    /// <param name="previous">The anime the candidate would come after.</param>
    /// <param name="candidate">The candidate.</param>
    /// <returns>Whether the candidate came later.</returns>
    private static bool StartsAfter(AniDbAnimeSummary previous, AniDbAnimeSummary candidate)
        => !candidate.StartDate.HasValue
            || !previous.StartDate.HasValue
            || candidate.StartDate > previous.StartDate;

    private static bool CanFollow(AniDbAnimeSummary previous, AniDbAnimeSummary candidate)
    {
        if (!_seasonAnimeTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!candidate.StartDate.HasValue)
        {
            // AniDB leaves the date off anime that have only been announced. Nothing here
            // can rule this one out.
            return true;
        }

        // A season starts once the one before it has finished airing. The end date is what
        // separates the next season from an entry that ran alongside this one; the start date
        // alone does not.
        if (previous.EndDate.HasValue)
        {
            return candidate.StartDate >= previous.EndDate.Value - _airingOverlapAllowance;
        }

        // Still airing, or no end date recorded: whatever follows cannot have started first.
        return !previous.StartDate.HasValue || candidate.StartDate > previous.StartDate;
    }

    /// <summary>
    /// Whether any title of the later entry is a title of the earlier one followed by
    /// nothing but a season number.
    /// </summary>
    /// <param name="earlierTitles">The titles of the earlier entry.</param>
    /// <param name="laterTitles">The titles of the later entry.</param>
    /// <returns>Whether the later entry continues the earlier one by number.</returns>
    private static bool IsNumberedContinuation(IReadOnlyList<string> earlierTitles, IReadOnlyList<string> laterTitles)
    {
        foreach (var earlier in earlierTitles)
        {
            var prefix = NormalizeTitle(earlier);

            // Short titles match far too much once punctuation is gone.
            if (prefix.Length < 4)
            {
                continue;
            }

            foreach (var later in laterTitles)
            {
                var full = NormalizeTitle(later);

                if (full.Length > prefix.Length
                    && full.StartsWith(prefix, StringComparison.Ordinal)
                    && SeasonSuffixRegex().IsMatch(full[prefix.Length..]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a title to its letters and digits, upper cased, so that punctuation and
    /// spacing cannot keep two spellings of the same name apart.
    /// </summary>
    /// <param name="value">The title to reduce.</param>
    /// <returns>The reduced title.</returns>
    private static string NormalizeTitle(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static bool IsRelated(AniDbAnimeSummary previous, AniDbAnimeSummary candidate)
        => previous.SequelIds.Contains(candidate.Id, StringComparer.Ordinal)
            || candidate.PrequelIds.Contains(previous.Id, StringComparer.Ordinal);

    /// <summary>
    /// Builds the titles the given season would have if AniDB numbered it.
    /// </summary>
    /// <param name="seriesTitles">The titles of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The candidate titles.</returns>
    private static IEnumerable<string> GetSeasonTitles(IReadOnlyList<string> seriesTitles, int seasonNumber)
    {
        var number = seasonNumber.ToString(CultureInfo.InvariantCulture);

        var suffixes = new List<string>
        {
            "Season " + number,
            number,
            GetOrdinal(seasonNumber) + " Season"
        };

        if (seasonNumber >= 2 && seasonNumber - 2 < _romanNumerals.Length)
        {
            suffixes.Add(_romanNumerals[seasonNumber - 2]);
        }

        foreach (var title in seriesTitles)
        {
            foreach (var suffix in suffixes)
            {
                yield return title + " " + suffix;
            }
        }
    }

    private static string GetOrdinal(int value)
    {
        var suffix = (value % 100) is >= 11 and <= 13
            ? "th"
            : (value % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };

        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static async Task<string?> LookupTitle(string title, CancellationToken cancellationToken)
    {
        var matcher = AniDbTitleMatcher.DefaultInstance;

        if (matcher == null)
        {
            return null;
        }

        return await matcher.FindSeries(title, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an entry's summary only if it is already cached, so that a miss costs nothing.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <returns>The summary, or <c>null</c> when the entry is not cached.</returns>
    private static async Task<AniDbAnimeSummary?> LoadCachedSummary(IApplicationPaths appPaths, string animeId)
    {
        var seriesDataPath = Path.Combine(AniDbSeriesProvider.GetSeriesDataPath(appPaths, animeId), "series.xml");
        var fileInfo = new FileInfo(seriesDataPath);

        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return null;
        }

        return await ParseSummary(animeId, seriesDataPath).ConfigureAwait(false);
    }

    private static async Task<AniDbAnimeSummary?> LoadSummary(IApplicationPaths appPaths, string animeId, CancellationToken cancellationToken)
    {
        var seriesDataPath = await AniDbSeriesProvider.GetSeriesData(appPaths, animeId, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(seriesDataPath))
        {
            return null;
        }

        return await ParseSummary(animeId, seriesDataPath).ConfigureAwait(false);
    }

    private static async Task<AniDbAnimeSummary> ParseSummary(string animeId, string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        string? type = null;
        var episodeCount = 0;
        DateTime? startDate = null;
        DateTime? endDate = null;
        var titles = new List<string>();
        var sequelIds = new List<string>();
        var prequelIds = new List<string>();

        using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
        using (var reader = XmlReader.Create(streamReader, settings))
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);

            var done = false;

            while (!done && await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "type":
                        type = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "episodecount":
                        _ = int.TryParse(
                            await reader.ReadElementContentAsStringAsync().ConfigureAwait(false),
                            CultureInfo.InvariantCulture,
                            out episodeCount);
                        break;

                    case "startdate":
                        startDate = ParseDate(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
                        break;

                    case "enddate":
                        endDate = ParseDate(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
                        break;

                    case "titles":
                        await ReadTitles(reader, titles).ConfigureAwait(false);
                        break;

                    case "relatedanime":
                        await ReadRelations(reader, sequelIds, prequelIds).ConfigureAwait(false);
                        break;

                    case "characters":
                    case "episodes":
                        // Everything read here comes before these two, which hold the bulk
                        // of the document.
                        done = true;
                        break;
                }
            }
        }

        return new AniDbAnimeSummary
        {
            Id = animeId,
            Type = type,
            EpisodeCount = episodeCount,
            StartDate = startDate,
            EndDate = endDate,
            Titles = titles,
            SequelIds = sequelIds,
            PrequelIds = prequelIds
        };
    }

    private static async Task ReadTitles(XmlReader reader, List<string> titles)
    {
        using var subtree = reader.ReadSubtree();

        while (await subtree.ReadAsync().ConfigureAwait(false))
        {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "title")
            {
                var title = await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(title) && !titles.Contains(title, StringComparer.Ordinal))
                {
                    titles.Add(title);
                }
            }
        }
    }

    private static async Task ReadRelations(XmlReader reader, List<string> sequelIds, List<string> prequelIds)
    {
        using var subtree = reader.ReadSubtree();

        while (await subtree.ReadAsync().ConfigureAwait(false))
        {
            if (subtree.NodeType != XmlNodeType.Element || subtree.Name != "anime")
            {
                continue;
            }

            var relatedId = subtree.GetAttribute("id");

            if (string.IsNullOrEmpty(relatedId))
            {
                continue;
            }

            // AniDB relates anime in many ways. Only the two that order a series matter.
            var relation = subtree.GetAttribute("type");
            List<string>? relatedIds = null;

            if (string.Equals(relation, "Sequel", StringComparison.OrdinalIgnoreCase))
            {
                relatedIds = sequelIds;
            }
            else if (string.Equals(relation, "Prequel", StringComparison.OrdinalIgnoreCase))
            {
                relatedIds = prequelIds;
            }

            if (relatedIds != null && !relatedIds.Contains(relatedId, StringComparer.Ordinal))
            {
                relatedIds.Add(relatedId);
            }
        }
    }

    private static DateTime? ParseDate(string? value)
    {
        // AniDB reports a calendar date with no time and no zone. AssumeUniversal keeps it as
        // written rather than shifting it by the server's offset.
        if (!string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            return date;
        }

        return null;
    }

    /// <summary>
    /// A season number, as it is written at the end of a folder name.
    /// </summary>
    /// <remarks>
    /// The Roman numeral is the one part matched as written, case and all, because a romanized
    /// Japanese title ends in those same letters as words of its own: "Raise wa Tanin ga Ii"
    /// ends in "Ii", which read case-insensitively is season two. A numeral is written in
    /// capitals wherever it means a number, so requiring them costs nothing.
    /// <para>
    /// A lone V or X is left out altogether. Either can end a title as a letter - "Nazo no
    /// Kanojo X", "After War Gundam X" - where no title reaches a fifth or tenth season
    /// written as a numeral rather than a number.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"(\b(season|staffel|part|series|stage|cour)\s*\d+|\b\d+(st|nd|rd|th)\s+(season|staffel|part|series|stage|cour)|\b\d+\.\s*(season|staffel|part|series|stage|cour)|\b(final\s+(season|act|chapter))|\bkanketsuhen|(?-i:\s(II|III|IV|VI|VII|VIII|IX)))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonMarkerRegex();

    /// <summary>
    /// What may follow a title for it still to be the same show, one season on. Matched
    /// against a title reduced by <see cref="NormalizeTitle"/>.
    /// </summary>
    [GeneratedRegex(@"^(?:(?:SEASON|PART|SERIES|STAGE|COUR)?(?:[2-9]|1[0-9])(?:ST|ND|RD|TH)?(?:SEASON|PART|SERIES|STAGE|COUR)?|II|III|IV|V|VI|VII|VIII|IX|X|(?:SECOND|THIRD|FOURTH|FIFTH|SIXTH|FINAL)(?:SEASON|PART|SERIES|STAGE|COUR)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonSuffixRegex();

    /// <summary>
    /// A segment naming episodes its entry does not have.
    /// </summary>
    /// <param name="Segment">The segment.</param>
    /// <param name="EpisodeCount">How many episodes AniDB records for the entry it names.</param>
    private sealed record UnheldSegment(AniDbSeasonSegment Segment, int EpisodeCount);
}
