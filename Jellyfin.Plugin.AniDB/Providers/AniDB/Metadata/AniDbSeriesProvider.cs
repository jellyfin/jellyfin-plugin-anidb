using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for series.
/// </summary>
public partial class AniDbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    /// <summary>
    /// The rating given to anime AniDB flags as adult, so that Jellyfin's parental controls
    /// have something to act on.
    /// </summary>
    private const string AdultOfficialRating = "XXX";

    /// <summary>
    /// Where AniDB serves the pictures it names in a picture attribute.
    /// </summary>
    private const string PersonImageBaseUrl = "https://cdn.anidb.net/images/main/";

    /// <summary>
    /// How many name matches an identify offers. Each one costs an AniDB request, paced
    /// seconds apart, and the fuzzy search behind them returns every show whose name merely
    /// begins alike - 78 of them for "Oshi no Ko". Without a cap one identify of a commonly
    /// named show spends dozens of requests and is enough on its own to earn a ban.
    /// </summary>
    private const int MaxSearchResults = 10;

    // AniDB bans a client that sends requests closer together than 2500ms, which is the
    // floor the configured interval is clamped to. A bucket limiter cannot express this: it
    // replenishes independently of when tokens are consumed, so an idle plugin holding a full
    // bucket can fire two requests back to back. Gate on the time since the previous request
    // instead, measured with the monotonic clock so a system time change cannot shorten it.
    private static readonly TimeSpan _minimumDelay = TimeSpan.FromMilliseconds(1);
    private static readonly SemaphoreSlim _requestGate = new(1, 1);

    /// <summary>
    /// How long an id AniDB has refused is left alone before it is asked for again. Long
    /// enough that a scan does not keep paying for the same refusal, short enough that an
    /// entry restored on AniDB's side is picked up the same day.
    /// </summary>
    private static readonly TimeSpan _retryAfterRefusal = TimeSpan.FromHours(6);

    /// <summary>
    /// One gate per anime, so that the episodes of a season refreshing together send one
    /// request for the entry they share rather than one apiece.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates = new(StringComparer.Ordinal);

    /// <summary>
    /// The ids AniDB has refused, and what it said. Kept because a refusal caches nothing, so
    /// without it every episode of the show asks again and pays for the same answer.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, string Message)> _refusedIds = new(StringComparer.Ordinal);

    // A ban is temporary, but its remaining time cannot be queried and runs from 15 minutes
    // to 24 hours. Probing a banned server extends the ban, so double the backoff after each
    // consecutive ban and reset it once a request succeeds.
    private static readonly TimeSpan _initialBanBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _maximumBanBackoff = TimeSpan.FromHours(24);
    private static readonly Lock _banLock = new();

    /// <summary>
    /// AniDB tags that are dropped before anything else looks at them, matched on the tag's own
    /// id and on its parent id, so listing a branch also removes the tags directly under it.
    /// </summary>
    private static readonly int[] IgnoredTagIds =
    [
        // Maintenance tags. AniDB housekeeping that says nothing about the show.
        3955,   // DESCRIPTION MISSING (1610)
        5939,   // CAST MISSING (2042)
        6850,   // STAFF MISSING (335)

        // Origin. On more than 14000 anime between them, so they distinguish nothing.
        6173,   // origin
        6152,   // Chinese production (1471)
        6166,   // South Korean production (288)
        7885,   // Japanese production (14413)
    ];

    /// <summary>
    /// The tags AniDB flags as 18+ content, dropped unless
    /// <see cref="PluginConfiguration.IncludeAdultTags"/> says otherwise. Matched on the tag's
    /// own id and on its parent id, so listing a branch also removes the tags under it. Kept
    /// apart from <see cref="IgnoredTagIds"/> because whether these belong in a library is the
    /// owner's call. Yuri and pornography are flagged by AniDB but left in, because
    /// <see cref="GenreHelper"/> reads both as genres. The counts are the anime carrying each
    /// tag when the list was taken.
    /// </summary>
    private static readonly int[] AdultTagIds =
    [
        301,    // lactation (169)
        724,    // shotacon (0)
        1172,   // sex slave (0)
        1521,   // anal tail (0)
        2016,   // inverted nipples (0)
        2142,   // pubic hair (0)
        2429,   // futanari (107)
        2693,   // handjob (405)
        2694,   // glory hole (6)
        2696,   // threesome (497)
        2698,   // pantyjob (101)
        2699,   // sixty-nine (358)
        2700,   // window fuck (94)
        2701,   // pussy sandwich (83)
        2702,   // boobjob (602)
        2703,   // wakamezake (6)
        2704,   // cum play (208)
        2705,   // creampie (967)
        2706,   // masturbation (715)
        2707,   // internal shots (551)
        2708,   // uncensored version available (594)
        2709,   // netorare (203)
        2710,   // squirting (253)
        2711,   // urination (458)
        2714,   // point of view (38)
        2715,   // rape (835)
        2717,   // anal (710)
        2719,   // gang bang (363)
        2720,   // bestiality (64)
        2721,   // public sex (578)
        2722,   // doggy style (840)
        2724,   // double penetration (412)
        2725,   // ahegao (242)
        2726,   // prostitution (233)
        2727,   // stomach stretch (100)
        2728,   // fisting (36)
        2730,   // exhibitionism (169)
        2731,   // sex while on the phone (61)
        2732,   // female rapes female (145)
        2733,   // enjoyable rape (337)
        2734,   // scissoring (67)
        2736,   // mother-daughter incest (34)
        2737,   // father-daughter incest (47)
        2740,   // mother-son incest (57)
        2742,   // futa x futa (16)
        2743,   // futa x male (22)
        2744,   // futa x female (88)
        2747,   // hidden vibrator (101)
        2828,   // borderline porn (188)
        2842,   // incest (441)
        2896,   // deflowering (697)
        2899,   // strap-on dildo (113)
        2900,   // dildos - vibrators (465)
        2902,   // facesitting (90)
        2903,   // scat (63)
        2911,   // footjob (122)
        2912,   // shimaidon (86)
        2913,   // cum swapping (18)
        2914,   // bukkake (128)
        2918,   // cunnilingus (808)
        2920,   // rimming (186)
        2921,   // throat fucking (362)
        2935,   // stomach bulge (97)
        2995,   // nipple penetration (21)
        3299,   // double fellatio (158)
        3443,   // orgy (150)
        3800,   // oyakodon (45)
        4020,   // pegging (13)
        4046,   // foursome (177)
        4049,   // outdoor sex (405)
        4207,   // doujin (22)
        4492,   // gang rape (243)
        4594,   // acidic breast milk (0)
        4741,   // anal pissing (10)
        5053,   // vagina dentata (0)
        5327,   // urophagia (32)
        5451,   // soapland (11)
        5715,   // eye penetration (4)
        5750,   // triple penetration (118)
        6164,   // onahole (11)
        6218,   // gokkun (252)
        6221,   // cybersex (13)
        6268,   // fellatio (1165)
        6355,   // water sex (120)
        6389,   // cervix penetration (60)
        6443,   // wooden horse (34)
        6455,   // double-sided dildo (59)
        6507,   // pillory (8)
        6519,   // autofellatio (6)
        6520,   // foreskin sex (2)
        6538,   // large areolae (0)
        6572,   // FFM threesome (342)
        6573,   // MMF threesome (120)
        6731,   // wax play (38)
        6829,   // sleeping sex (30)
        6843,   // MMM threesome (8)
        7004,   // prostate massage (56)
        7148,   // impregnation with larvae (32)
        7151,   // pregnant with larvae (0)
        7229,   // fingering (410)
        7247,   // sumata (83)
        7251,   // cockring (18)
        7399,   // orgasm denial (24)
        7403,   // anal fingering (107)
        7420,   // spitroast (77)
        7422,   // reverse spitroast (63)
        7560,   // suspension bondage (65)
        7600,   // macrophilia (27)
        7614,   // double assjob (3)
        7615,   // assjob (13)
        7633,   // petplay (17)
        7829,   // FFF threesome (36)
        7868,   // microphilia (4)
        7886,   // wormhole sex (3)
        7957,   // group sex (0)
        7958,   // stripper (0)
        8013,   // vaginal pissing (2)
        8028,   // pasties (0)
        8038,   // nipple stimulation (260)
        8163,   // internal breast shots (3)
        8266,   // double vaginal penetration (2)
        8267,   // double anal penetration (0)
        8268,   // biphallic (3)

        // Branches AniDB leaves unflagged that explicit tags are filed under, matched as
        // parent ids to clear those out.
        1773,   // tentacle (335)
        1894,   // shota (136)
        2566,   // loli (394)
        2608,   // fetishes (0)
        2697,   // voyeurism (285)
        2712,   // sex toys (276)
        2729,   // BDSM (652)
        2816,   // sexual fantasies (336)
        2891,   // breasts (0)
        2901,   // bondage (524)
    ];

    private static readonly CompositeFormat _seriesQueryUrlFormat = CompositeFormat.Parse("http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver=1&protover=1&aid={1}");

    /// <summary>
    /// The monotonic timestamp before which no further AniDB request may be issued.
    /// Guarded by <see cref="_requestGate"/>.
    /// </summary>
    private static long _nextRequestTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// The UTC time at which the current ban is assumed to have lapsed. Wall clock rather than
    /// the monotonic clock used for request spacing, because a ban outlives the process.
    /// Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static DateTime _banUntilUtc = DateTime.MinValue;

    /// <summary>
    /// How long the next detected ban will be waited out for. Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static TimeSpan _currentBanBackoff = _initialBanBackoff;

    /// <summary>
    /// Whether a ban has been reported and no request has succeeded since. Guarded by
    /// <see cref="_banLock"/>, and read outside it only as a logging fast path.
    /// </summary>
    private static bool _banActive;

    /// <summary>
    /// Whether the "resuming requests" message has already been logged for the current ban.
    /// Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static bool _resumeLogged;

    /// <summary>
    /// How many callers are waiting for a request slot, how many requests have gone out since
    /// the server started, and when the last one did. Reported by <see cref="GetRequestStatus"/>
    /// and touched only through <see cref="Interlocked"/>.
    /// </summary>
    private static int _queuedRequests;
    private static long _requestsSent;
    private static long _lastRequestTicks;

    private readonly IApplicationPaths _appPaths;

    /// <summary>
    /// What AniDB's creator types mean to Jellyfin. The role is what the cast list shows
    /// under the name, so it keeps AniDB's own wording where Jellyfin has no kind for it.
    /// </summary>
    private static readonly Dictionary<string, (PersonKind Kind, string Role)> _creatorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Direction", (PersonKind.Director, "Director") },
        { "Chief Direction", (PersonKind.Director, "Chief Director") },
        { "Series Direction", (PersonKind.Director, "Series Director") },
        { "Animation Direction", (PersonKind.Director, "Animation Director") },
        { "Chief Animation Direction", (PersonKind.Director, "Chief Animation Director") },
        { "Music", (PersonKind.Composer, "Music") },
        { "Original Work", (PersonKind.Writer, "Original Creator") },
        { "Story Composition", (PersonKind.Writer, "Story Composition") },
        { "Series Composition", (PersonKind.Writer, "Series Composition") },
        { "Screenplay", (PersonKind.Writer, "Screenplay") },
        { "Original Plan", (PersonKind.Writer, "Original Plan") },
        { "Character Design", (PersonKind.Artist, "Character Design") },
        { "Main Character Design", (PersonKind.Artist, "Main Character Design") },
        { "Animation Character Design", (PersonKind.Artist, "Animation Character Design") }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbSeriesProvider"/> class.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public AniDbSeriesProvider(IApplicationPaths appPaths)
    {
        _appPaths = appPaths;
        TitleMatcher = AniDbTitleMatcher.DefaultInstance;
    }

    /// <inheritdoc />
    public int Order => -1;

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <summary>
    /// Gets or sets the logger used to report the plugin-wide AniDB ban state.
    /// </summary>
    internal static ILogger<AniDbSeriesProvider>? Logger { get; set; }

    private IAniDbTitleMatcher TitleMatcher { get; set; }

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var animeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        if (string.IsNullOrEmpty(animeId))
        {
            animeId = await Identify(info, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(animeId))
        {
            return await GetMetadataForId(animeId, info, cancellationToken).ConfigureAwait(false);
        }

        return new MetadataResult<Series>();
    }

    /// <summary>
    /// Works out which AniDB entry a show is, from whatever the library already knows of it.
    /// </summary>
    /// <param name="info">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the show cannot be identified.</returns>
    private async Task<string?> Identify(SeriesInfo info, CancellationToken cancellationToken)
    {
        // A TVDB or TMDB id a provider ahead of this one has already settled on names the show
        // outright, and the mapping sources record which AniDB entry that id is. It is tried
        // first because a name is the weaker evidence of the two: AniDB spells a great many
        // names differently from TVDB, and where two shows do share a name the id is the only
        // thing that tells them apart. A folder naming a season is left to the name match
        // below, because the id names the whole show and would answer with its first season.
        if (!AniDbSeasonResolver.NamesASeason(info.Name))
        {
            var mapped = await AniDbMappings.ResolveSeriesId(
                _appPaths,
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb)),
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb)),
                Logger ?? (ILogger)NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);

            if (mapped != null)
            {
                Logger?.LogInformation(
                    "{SeriesName} is AniDB anime {AnimeId}, which {Source} files under {Provider} series {ProviderId}",
                    info.Name,
                    mapped.AnimeId,
                    mapped.Source,
                    mapped.Provider,
                    mapped.ProviderId);

                return mapped.AnimeId;
            }
        }

        // The folder is what the user named, and it still says which show this is where the name
        // on the item no longer does.
        var folderName = Path.GetFileName(info.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var truncated = IsTruncation(info.Name, folderName);

        if (truncated)
        {
            Logger?.LogInformation(
                "{SeriesName} is what is left of the folder name {FolderName} once it was cut short, so the folder is searched instead. A name with no letters in it matches almost any anime, this search being a fuzzy one, and matching the wrong one would settle the show for good",
                info.Name,
                folderName);
        }

        var matched = string.IsNullOrEmpty(info.Name) || truncated
            ? string.Empty
            : await Equals_check.XmlFindId(info.Name, GetLookupYear(info), cancellationToken).ConfigureAwait(false);

        // The name searched above is the item's, which is whatever provider reached it first,
        // and a provider that matched the wrong show has already renamed it to that show. The
        // folder gets an attempt of its own, under the year written into it rather than the
        // wrong show's.
        if (string.IsNullOrEmpty(matched)
            && !string.IsNullOrEmpty(folderName)
            && !string.Equals(folderName, info.Name, StringComparison.OrdinalIgnoreCase))
        {
            matched = await Equals_check.XmlFindId(folderName, YearInName(folderName) ?? GetLookupYear(info), cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(matched))
            {
                Logger?.LogInformation(
                    "{SeriesName} could not be matched under that name, but its folder {FolderName} is AniDB anime {AnimeId}. The name on the item is another provider's, and that provider matched a different show",
                    info.Name,
                    folderName,
                    matched);
            }
        }

        if (string.IsNullOrEmpty(matched))
        {
            Logger?.LogInformation(
                "No AniDB entry could be identified for {SeriesName} (folder {FolderName}). Where two shows share a name, the year in the folder name is what tells them apart, and a TVDB id on the show lets the anime list answer instead",
                info.Name,
                folderName);

            return null;
        }

        // Only a name match is walked back. An id set by hand names the entry to use, the
        // TVDB route already answers with the show's first entry, and the season provider asks
        // for a season's own entry by id.
        var searchedName = info.Name ?? folderName;

        // The list is asked before AniDB's own relations are walked. A name match lands on a
        // later season more often than it looks: AniDB disambiguates a second season by
        // appending its year, exactly as it does a remake, so a show whose seasons all aired in
        // one year matches its own sequel. The list records which season every entry fills, so
        // it settles this for nothing, where each hop of the relation walk costs a request.
        if (!AniDbSeasonResolver.NamesASeason(searchedName))
        {
            var listedFirst = await AniDbMappings.ResolveFirstSeason(
                _appPaths,
                matched,
                Logger ?? (ILogger)NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(listedFirst))
            {
                Logger?.LogInformation(
                    "{SeriesName} matched AniDB anime {MatchedId}, which the mapping sources file as a later season. The show begins at anime {AnimeId}, which is used instead",
                    searchedName,
                    matched,
                    listedFirst);

                return listedFirst;
            }
        }

        return await AniDbSeasonResolver.ResolveFirstSeasonId(_appPaths, matched, searchedName, Logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the metadata for the given AniDB id.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="info">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="publishShowIds">Whether the ids the mapping sources file the show under may be written onto the result. Off for a movie read out of a show's entry, which is not that show and must not carry the ids of its seasons.</param>
    /// <returns>The metadata result.</returns>
    public async Task<MetadataResult<Series>> GetMetadataForId(
        string animeId,
        SeriesInfo info,
        CancellationToken cancellationToken,
        bool publishShowIds = true)
    {
        var result = new MetadataResult<Series>
        {
            Item = new Series(),
            HasMetadata = true
        };

        result.Item.ProviderIds.Add(ProviderNames.AniDb, animeId);

        if (publishShowIds)
        {
            await AddMappedShowIds(result.Item, info, animeId, cancellationToken).ConfigureAwait(false);
        }

        var seriesDataPath = await GetSeriesData(_appPaths, animeId, cancellationToken).ConfigureAwait(false);
        await FetchSeriesInfo(result, seriesDataPath, info.MetadataLanguage ?? "en").ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Writes the TVDB and TMDB ids the mapping sources file a show under onto it, where that is
    /// turned on. Those sites' image providers, and fanart, are keyed by them, so this is what
    /// lets them fetch artwork for a show AniDB identified.
    /// </summary>
    /// <param name="series">The show being filled in.</param>
    /// <param name="info">The lookup info, holding whatever ids the item already carries.</param>
    /// <param name="animeId">The AniDB id of the show.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task AddMappedShowIds(Series series, SeriesInfo info, string animeId, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.PublishMappedIds)
        {
            return;
        }

        var ids = await AniDbMappings.ResolveShowIds(
            _appPaths,
            animeId,
            Logger ?? (ILogger)NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        if (!ids.Any)
        {
            return;
        }

        AddMappedId(series, info.ProviderIds, nameof(MetadataProvider.Tvdb), ids.Tvdb);
        AddMappedId(series, info.ProviderIds, nameof(MetadataProvider.Tmdb), ids.Tmdb);

        Logger?.LogDebug(
            "AniDB anime {AnimeId} is filed under TVDB {TvdbId} and TMDB {TmdbId}, which are written onto the show so that whatever is keyed by them can fetch its artwork",
            animeId,
            ids.Tvdb,
            ids.Tmdb);
    }

    /// <summary>
    /// Writes one mapped id onto an item, leaving alone an id the item already carries: that one
    /// was either entered by hand or settled by the provider it belongs to, and either is better
    /// evidence about the item than a mapping is.
    /// </summary>
    /// <param name="item">The item being filled in.</param>
    /// <param name="known">The ids the item already carries.</param>
    /// <param name="provider">The provider whose id this is.</param>
    /// <param name="id">The id, where a source named one.</param>
    internal static void AddMappedId(IHasProviderIds item, IReadOnlyDictionary<string, string> known, string provider, string? id)
    {
        if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(known.GetValueOrDefault(provider)))
        {
            item.ProviderIds[provider] = id;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var offered = new HashSet<string>(StringComparer.Ordinal);
        var imageProvider = new AniDbImageProvider(_appPaths);

        async Task Offer(string? id)
        {
            if (string.IsNullOrEmpty(id) || !offered.Add(id))
            {
                return;
            }

            var metadata = await GetMetadataForId(id, searchInfo, cancellationToken).ConfigureAwait(false);

            if (metadata.HasMetadata)
            {
                // Read from the document the line above has just cached, so this costs no
                // request of its own.
                var images = await imageProvider.GetImages(id, cancellationToken).ConfigureAwait(false);

                results.Add(MetadataToRemoteSearchResult(metadata, images));
            }
        }

        await Offer(searchInfo.ProviderIds.GetValueOrDefault(ProviderNames.AniDb)).ConfigureAwait(false);

        // The mapping sources are the ones here that answer with a certainty rather than a
        // guess, so their answer goes first. They also settle the question the name cannot: they
        // hold anime only, so an id they do not carry belongs to something that is not an
        // anime - a live action adaptation sharing the show's name, most often - and an id they
        // do carry names the AniDB entry outright.
        var mapped = await AniDbMappings.ResolveSeriesId(
            _appPaths,
            searchInfo.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb)),
            searchInfo.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb)),
            Logger ?? (ILogger)NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        await Offer(mapped?.AnimeId).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(searchInfo.Name))
        {
            foreach (var id in await Equals_check.XmlSearch(searchInfo.Name, MaxSearchResults, cancellationToken).ConfigureAwait(false))
            {
                await Offer(id).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// Converts a metadata result into a remote search result.
    /// </summary>
    /// <param name="metadata">The metadata result.</param>
    /// <param name="images">The available images.</param>
    /// <returns>The remote search result.</returns>
    public static RemoteSearchResult MetadataToRemoteSearchResult(MetadataResult<Series> metadata, IEnumerable<RemoteImageInfo> images)
    {
        return new RemoteSearchResult
        {
            Name = metadata.Item.Name,
            ProductionYear = metadata.Item.PremiereDate?.Year,
            PremiereDate = metadata.Item.PremiereDate,
            ImageUrl = images.FirstOrDefault()?.Url,
            ProviderIds = metadata.Item.ProviderIds,
            SearchProviderName = ProviderNames.AniDb
        };
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        await WaitForImageSlot(cancellationToken).ConfigureAwait(false);
        var httpClient = Plugin.Instance.GetHttpClient();

        return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the path to the cached series data, downloading it when needed.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path to the series data file.</returns>
    public static async Task<string> GetSeriesData(IApplicationPaths appPaths, string seriesId, CancellationToken cancellationToken)
    {
        var seriesDataPath = Path.Combine(GetSeriesDataPath(appPaths, seriesId), "series.xml");

        if (!NeedsDownload(seriesDataPath))
        {
            return seriesDataPath;
        }

        // One download per anime at a time. Every episode of a season asks for the entry it is
        // read from, and a scan refreshes them together, so without this they all find nothing
        // cached and send the same request at once - one per episode, for one anime.
        var gate = _downloadGates.GetOrAdd(seriesId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Whoever held the gate has very likely just downloaded it.
            if (!NeedsDownload(seriesDataPath))
            {
                return seriesDataPath;
            }

            var fileInfo = new FileInfo(seriesDataPath);
            var hasCopy = fileInfo.Exists && fileInfo.Length > 0;

            // While banned, read the stale copy rather than spend a request that would only
            // be refused and lengthen the ban.
            if (hasCopy && GetRemainingBanTime() > TimeSpan.Zero)
            {
                return seriesDataPath;
            }

            // AniDB refuses some ids outright - one it has deleted or merged away, or one set
            // by hand that was never right - and answers with an error there is nothing to
            // cache. Nothing being cached is what makes the next episode ask again, so one bad
            // id costs a request for every file in the show, every scan. The refusal is
            // remembered instead, and raised again without asking.
            if (_refusedIds.TryGetValue(seriesId, out var refusal) && DateTime.UtcNow - refusal.At < _retryAfterRefusal)
            {
                throw new InvalidOperationException(refusal.Message);
            }

            try
            {
                await DownloadSeriesData(seriesId, seriesDataPath, appPaths.CachePath, cancellationToken).ConfigureAwait(false);

                _refusedIds.TryRemove(seriesId, out _);
            }
            catch (InvalidOperationException ex)
            {
                _refusedIds[seriesId] = (DateTime.UtcNow, ex.Message);

                Logger?.LogWarning(
                    "AniDB refused anime {AnimeId}: {Error}. Nothing more will be asked of that id for {RetryAfter}, so the rest of the show does not spend a request each on the same refusal",
                    seriesId,
                    ex.Message,
                    _retryAfterRefusal);

                throw;
            }
        }
        finally
        {
            gate.Release();
        }

        return seriesDataPath;
    }

    /// <summary>
    /// Whether the cached document of an anime has to be downloaded before it can be read.
    /// </summary>
    /// <param name="seriesDataPath">The path of the cached document.</param>
    /// <returns><c>true</c> when there is no usable copy on disk.</returns>
    private static bool NeedsDownload(string seriesDataPath)
    {
        var fileInfo = new FileInfo(seriesDataPath);

        return !fileInfo.Exists
            || fileInfo.Length == 0
            || DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(Plugin.Instance.Configuration.MaxCacheAge);
    }

    /// <summary>
    /// Waits until the AniDB rate limit allows another request to be made.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task WaitForRequestSlot(CancellationToken cancellationToken)
        => WaitForSlot(true, cancellationToken);

    /// <summary>
    /// Waits for the slot an image download takes. Images come from AniDB's image server rather
    /// than from its API, which counts and bans separately, so a download waits its turn like
    /// anything else but is not held back by a ban on the API. Otherwise a poster the API has
    /// already described is lost for as long as the ban lasts, which is what leaves a show
    /// identified during one without its artwork until someone picks it by hand.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task WaitForImageSlot(CancellationToken cancellationToken)
        => WaitForSlot(false, cancellationToken);

    private static async Task WaitForSlot(bool countsAgainstTheApi, CancellationToken cancellationToken)
    {
        if (countsAgainstTheApi)
        {
            ThrowIfBanned();
        }

        Interlocked.Increment(ref _queuedRequests);

        try
        {
            // The gate is held across the wait so that concurrent callers queue behind each
            // other rather than all sleeping in parallel and then firing at the same moment.
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Task.Delay may fire fractionally early, so re-check the interval.
                for (var remaining = GetRemainingInterval(); remaining > TimeSpan.Zero; remaining = GetRemainingInterval())
                {
                    await Task.Delay(remaining < _minimumDelay ? _minimumDelay : remaining, cancellationToken).ConfigureAwait(false);
                }

                // A caller ahead in the queue may have been banned while this one waited.
                if (countsAgainstTheApi)
                {
                    ThrowIfBanned();
                }

                _nextRequestTimestamp = Stopwatch.GetTimestamp() + GetRequestIntervalTicks();

                Interlocked.Increment(ref _requestsSent);
                Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedRequests);
        }
    }

    /// <summary>
    /// How the plugin currently stands with AniDB, for the status the configuration page
    /// shows. A scan that looks stalled is almost always one queued behind the rate limit or
    /// waiting out a ban, and neither is visible from the library screen.
    /// </summary>
    /// <returns>What is left of any ban, how many requests are waiting for a slot, how long until the next may be sent, how many have been sent since the server started, and when the last one went out.</returns>
    internal static (TimeSpan BanRemaining, int Queued, TimeSpan UntilNextRequest, long Sent, DateTime? LastSentUtc) GetRequestStatus()
    {
        var lastTicks = Interlocked.Read(ref _lastRequestTicks);
        var untilNext = GetRemainingInterval();

        return (
            GetRemainingBanTime(),
            Volatile.Read(ref _queuedRequests),
            untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero,
            Interlocked.Read(ref _requestsSent),
            lastTicks == 0 ? null : new DateTime(lastTicks, DateTimeKind.Utc));
    }

    /// <summary>
    /// Gets the time remaining on the current AniDB ban, or <see cref="TimeSpan.Zero"/>
    /// when the plugin is not currently banned.
    /// </summary>
    /// <returns>The remaining ban time.</returns>
    internal static TimeSpan GetRemainingBanTime()
    {
        lock (_banLock)
        {
            var remaining = _banUntilUtc - DateTime.UtcNow;

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Restores the ban recorded by a previous session, so that restarting the server does
    /// not hand a banned client a fresh allowance of requests.
    /// </summary>
    /// <param name="configuration">The plugin configuration holding the persisted state.</param>
    internal static void RestoreBanState(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_banLock)
        {
            _currentBanBackoff = configuration.AniDbBanBackoffMinutes > 0
                ? TimeSpan.FromMinutes(Math.Min(configuration.AniDbBanBackoffMinutes, _maximumBanBackoff.TotalMinutes))
                : _initialBanBackoff;

            var remaining = configuration.AniDbBannedUntilUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _banUntilUtc = DateTime.MinValue;
                _banActive = false;

                return;
            }

            // Guard against a persisted time that a clock change has pushed implausibly far
            // out; never wait longer than a ban could actually last.
            if (remaining > _maximumBanBackoff)
            {
                remaining = _maximumBanBackoff;
            }

            _banUntilUtc = DateTime.UtcNow + remaining;
            _banActive = true;
            _resumeLogged = false;

            Logger?.LogWarning(
                "An AniDB ban recorded before the last restart is still in force. AniDB requests stay paused for {RetryAfter}",
                remaining);
        }
    }

    /// <summary>
    /// Records that AniDB reported a ban and returns how long it will be waited out for.
    /// </summary>
    /// <returns>The backoff applied for this ban.</returns>
    private static TimeSpan RegisterBan()
    {
        lock (_banLock)
        {
            var backoff = _currentBanBackoff;
            _banUntilUtc = DateTime.UtcNow + backoff;

            var escalated = backoff + backoff;
            _currentBanBackoff = escalated > _maximumBanBackoff ? _maximumBanBackoff : escalated;

            if (_banActive)
            {
                Logger?.LogWarning(
                    "AniDB is still refusing requests. All AniDB requests remain paused, now for a further {RetryAfter}",
                    backoff);
            }
            else
            {
                Logger?.LogWarning(
                    "AniDB has banned this client. Pausing all AniDB requests for {RetryAfter}. Cached metadata will continue to be used in the meantime",
                    backoff);
            }

            _banActive = true;
            _resumeLogged = false;
            PersistBanState();

            return backoff;
        }
    }

    /// <summary>
    /// Clears the ban state after AniDB served a valid response.
    /// </summary>
    private static void RegisterSuccess()
    {
        lock (_banLock)
        {
            // Every successful download lands here, so do nothing unless a ban was in
            // effect. Otherwise the configuration would be rewritten once per series.
            if (!_banActive && _banUntilUtc == DateTime.MinValue && _currentBanBackoff == _initialBanBackoff)
            {
                return;
            }

            if (_banActive)
            {
                Logger?.LogInformation("AniDB accepted a request again, so the ban has lifted. Resuming normal metadata fetching");
            }

            _banUntilUtc = DateTime.MinValue;
            _currentBanBackoff = _initialBanBackoff;
            _banActive = false;
            _resumeLogged = false;
            PersistBanState();
        }
    }

    private static void ThrowIfBanned()
    {
        var remaining = GetRemainingBanTime();
        if (remaining <= TimeSpan.Zero)
        {
            // The backoff has elapsed. Whether the ban has actually lifted is only known
            // once a request succeeds, so announce the retry once rather than per request.
            if (Volatile.Read(ref _banActive))
            {
                lock (_banLock)
                {
                    if (_banActive && !_resumeLogged)
                    {
                        _resumeLogged = true;
                        Logger?.LogInformation("The AniDB ban backoff has elapsed. Retrying AniDB requests");
                    }
                }
            }

            return;
        }

        throw new AniDbBannedException(
            string.Format(CultureInfo.InvariantCulture, "AniDB has banned this client; no request will be sent for another {0}.", remaining))
        {
            RetryAfter = remaining
        };
    }

    /// <summary>
    /// Writes the current ban state to the plugin configuration. Must be called under
    /// <see cref="_banLock"/>.
    /// </summary>
    private static void PersistBanState()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.Configuration.AniDbBannedUntilUtc = _banUntilUtc;
            plugin.Configuration.AniDbBanBackoffMinutes = (int)_currentBanBackoff.TotalMinutes;
            plugin.SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Losing the record only costs the ban its ability to outlive a restart, which
            // must not fail the request that discovered it.
            Logger?.LogWarning(ex, "Could not persist the AniDB ban state, so it will not survive a restart");
        }
    }

    private static TimeSpan GetRemainingInterval()
        => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _nextRequestTimestamp);

    /// <summary>
    /// Gets the configured gap between two AniDB requests, in monotonic clock ticks. Read per
    /// request so that changing the setting takes effect without a restart.
    /// </summary>
    /// <returns>The request interval in <see cref="Stopwatch"/> ticks.</returns>
    private static long GetRequestIntervalTicks()
    {
        var interval = Plugin.Instance?.Configuration.RequestIntervalMs
            ?? PluginConfiguration.MinimumRequestIntervalMs;

        return (long)(Stopwatch.Frequency * (interval / 1000d));
    }

    private async Task FetchSeriesInfo(MetadataResult<Series> result, string seriesDataPath, string preferredMetadataLangauge)
    {
        var series = result.Item;
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using (var streamReader = File.Open(seriesDataPath, FileMode.Open, FileAccess.Read))
        using (var reader = XmlReader.Create(streamReader, settings))
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);

            if (string.Equals(reader.GetAttribute("restricted"), "true", StringComparison.OrdinalIgnoreCase))
            {
                series.OfficialRating = AdultOfficialRating;
            }

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "startdate":
                            var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                // AniDB reports a calendar date with no time and no zone.
                                // AssumeUniversal keeps it as written rather than reading it
                                // as the server's local time and shifting it by that offset.
                                if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    series.PremiereDate = date;
                                }
                            }

                            break;

                        case "enddate":
                            var endDate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(endDate))
                            {
                                // AniDB reports a calendar date with no time and no zone.
                                // AssumeUniversal keeps it as written rather than reading it
                                // as the server's local time and shifting it by that offset.
                                if (DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    series.EndDate = date;
                                }
                            }

                            break;

                        case "titles":
                            using (var subtree = reader.ReadSubtree())
                            {
                                var (title, originalTitle) = await ParseTitle(subtree, preferredMetadataLangauge).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(title))
                                {
                                    series.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                                        ? title.Replace('`', '\'')
                                        : title;
                                }

                                if (!string.IsNullOrEmpty(originalTitle))
                                {
                                    series.OriginalTitle = Plugin.Instance.Configuration.AniDbReplaceGraves
                                        ? originalTitle.Replace('`', '\'')
                                        : originalTitle;
                                }
                            }

                            break;

                        case "creators":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseCreators(result, subtree).ConfigureAwait(false);
                            }

                            break;

                        case "description":
                            var description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            series.Overview = AniDbDescription.Clean(description.TrimStart('*').Trim());

                            break;

                        case "ratings":
                            using (var subtree = reader.ReadSubtree())
                            {
                                ParseRatings(series, subtree);
                            }

                            break;

                        case "characters":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseActors(result, subtree).ConfigureAwait(false);
                            }

                            break;

                        case "tags":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseTags(series, subtree).ConfigureAwait(false);
                            }

                            break;
                    }
                }
            }
        }

        GenreHelper.CleanupGenres(series);
    }

    private static bool IsIgnoredTag(int tagId)
    {
        if (IgnoredTagIds.Contains(tagId))
        {
            return true;
        }

        return !Plugin.Instance.Configuration.IncludeAdultTags && AdultTagIds.Contains(tagId);
    }

    private static async Task ParseTags(Series series, XmlReader reader)
    {
        var configuration = Plugin.Instance.Configuration;
        var blacklist = GetTagBlacklist(configuration.TagBlacklist);
        var genres = new List<GenreInfo>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
            {
                if (!int.TryParse(reader.GetAttribute("weight"), CultureInfo.InvariantCulture, out int weight))
                {
                    weight = 0;
                }

                // AniDB marks the tags it shows in the infobox on the anime's page. Read
                // before the subtree, which moves off the element the attributes are on.
                var infobox = string.Equals(reader.GetAttribute("infobox"), "true", StringComparison.OrdinalIgnoreCase);

                if (int.TryParse(reader.GetAttribute("id"), CultureInfo.InvariantCulture, out int id) && IsIgnoredTag(id))
                {
                    continue;
                }

                if (int.TryParse(reader.GetAttribute("parentid"), CultureInfo.InvariantCulture, out int parentId)
                    && IsIgnoredTag(parentId))
                {
                    continue;
                }

                using var tagSubtree = reader.ReadSubtree();
                while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                {
                    if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                    {
                        var name = await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false);

                        // Decided before any filter: the rating is what parental controls act
                        // on, and it must not depend on how the tag list was narrowed.
                        if (string.Equals(name, "18 restricted", StringComparison.OrdinalIgnoreCase))
                        {
                            series.OfficialRating = AdultOfficialRating;
                        }

                        if (weight >= configuration.MinimumTagWeight
                            && (infobox || !configuration.InfoboxTagsOnly)
                            && !blacklist.Contains(name))
                        {
                            genres.Add(new GenreInfo { Name = name, Weight = weight });
                        }
                    }
                }
            }
        }

        // Descending: the list is later trimmed to the first MaxGenres entries, which must
        // be the ones AniDB weighted highest.
        var ordered = genres.OrderByDescending(g => g.Weight).Select(g => g.Name).ToArray();

        series.Genres = ordered;

        // Every tag that got this far is kept as a tag, whether or not it also names a
        // genre. Which of them become genres is GenreHelper's business, and a tag list
        // assembled there would vanish whenever genres were turned off.
        series.Tags = [.. series.Tags.Concat(ordered).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static HashSet<string> GetTagBlacklist(string value)
    {
        var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in value.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            blacklist.Add(name);
        }

        return blacklist;
    }

    private static async Task ParseActors(MetadataResult<Series> series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "character")
                {
                    using var subtree = reader.ReadSubtree();
                    await ParseActor(series, subtree).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ParseActor(MetadataResult<Series> series, XmlReader reader)
    {
        string? actor = null;
        string? actorPicture = null;
        string? actorId = null;
        string? character = null;
        string? characterPicture = null;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "name":
                        character = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "picture":
                        characterPicture = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "seiyuu":
                        // Read before the content, which moves off the element these are on.
                        actorPicture = reader.GetAttribute("picture");
                        actorId = reader.GetAttribute("id");
                        actor = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                }
            }
        }

        // Jellyfin holds one person per credit and has no kind of its own for a character, so the
        // two swap places rather than both being listed: whichever is named takes the credit and
        // the other becomes the role it is credited with.
        var showCharacters = Plugin.Instance.Configuration.CastShowsCharacters;
        var name = showCharacters ? character : actor;
        var role = showCharacters ? actor : character;

        // A credit needs someone to name. The usual listing names the actor and so needs both,
        // an actor being worth listing only against the character they play; a character with no
        // actor recorded is still a character, and is kept where those are what is listed.
        if (string.IsNullOrEmpty(name) || (!showCharacters && string.IsNullOrEmpty(role)))
        {
            return;
        }

        // A character's AniDB id is not a creator's, and a person's id is written out as a link
        // to a creator, so a character is listed without an id rather than with one pointing at
        // whoever happens to hold that creator id.
        series.AddPerson(CreatePerson(
            ReplaceGraves(name),
            PersonKind.Actor,
            ReplaceGraves(role),
            GetPersonImageUrl(showCharacters ? characterPicture : actorPicture),
            showCharacters ? null : actorId));
    }

    /// <summary>
    /// Replaces the grave accents AniDB romanises a name with, where that is turned on.
    /// </summary>
    /// <param name="value">The name, where there is one.</param>
    /// <returns>The name as it is listed.</returns>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? ReplaceGraves(string? value)
        => Plugin.Instance.Configuration.AniDbReplaceGraves ? value?.Replace('`', '\'') : value;

    private static void ParseRatings(Series series, XmlReader reader)
    {
        if (!Plugin.Instance.Configuration.ImportCommunityRating)
        {
            return;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "permanent")
                {
                    if (float.TryParse(
                        reader.ReadElementContentAsString(),
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out float rating))
                    {
                        series.CommunityRating = (float)Math.Round(rating, 1);
                    }
                }
            }
        }
    }

    private static async Task<(string? Title, string? OriginalTitle)> ParseTitle(XmlReader reader, string preferredMetadataLangauge)
    {
        var titles = new List<Title>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "title")
            {
                var language = reader.GetAttribute("xml:lang");
                var type = reader.GetAttribute("type");
                var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                titles.Add(new Title
                {
                    Language = language,
                    Type = type,
                    Name = name
                });
            }
        }

        string? title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLangauge)?.Name;

        // The original title is the Japanese one, that being the language the anime was made
        // in, written in romaji rather than in kanji: it is the name the show is catalogued
        // under outside Japan, and it reads to anyone, which the kanji does not. It does not
        // follow the displayed title's language.
        string? originalTitle = titles.Localize(TitlePreferenceType.JapaneseRomaji, preferredMetadataLangauge)?.Name;

        return (title, originalTitle);
    }

    private static async Task ParseCreators(MetadataResult<Series> series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "name")
            {
                var type = reader.GetAttribute("type");
                var personId = reader.GetAttribute("id");
                var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                if (type == "Animation Work")
                {
                    series.Item.AddStudio(name);
                }
                else
                {
                    var (kind, role) = GetCreatorRole(type);

                    series.AddPerson(CreatePerson(
                       ReplaceGraves(name),
                       kind,
                       role,
                       null,
                       personId));
                }
            }
        }
    }

    private static (PersonKind Kind, string? Role) GetCreatorRole(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return (PersonKind.Unknown, null);
        }

        if (_creatorTypes.TryGetValue(type, out var mapped))
        {
            return mapped;
        }

        // Several AniDB types are already the name of a Jellyfin kind.
        return Enum.TryParse<PersonKind>(type, true, out var parsed)
            ? (parsed, type)
            : (PersonKind.Unknown, type);
    }

    private static string? GetPersonImageUrl(string? picture)
        => string.IsNullOrEmpty(picture) ? null : PersonImageBaseUrl + picture;

    private static PersonInfo CreatePerson(string name, PersonKind kind, string? role = null, string? imageUrl = null, string? personId = null)
    {
        // todo find nationality of person and conditionally reverse name order
        var person = new PersonInfo
        {
            Name = ReverseNameOrder(name),
            Type = kind,
            Role = role,
            ImageUrl = imageUrl
        };

        if (!string.IsNullOrEmpty(personId))
        {
            person.ProviderIds[ProviderNames.AniDb] = personId;
        }

        return person;
    }

    /// <summary>
    /// The year a lookup should be pinned to, taken from whichever of the item's own year and
    /// its air date is set.
    /// </summary>
    /// <param name="info">The lookup info.</param>
    /// <returns>The year, or <c>null</c> when nothing gives one.</returns>
    private static int? GetLookupYear(ItemLookupInfo info)
        => info.Year ?? info.PremiereDate?.Year;

    /// <summary>
    /// Whether the name on the item is the folder name cut short rather than a name of its own.
    /// </summary>
    /// <remarks>
    /// A show whose name begins with a number and a dot arrives here named with just that
    /// number: "2.43: Seiin High School Boys Volleyball Team" in a folder of that name reaches
    /// this as "2". Whatever cut it is not this plugin - nothing here reads a name apart at a
    /// dot - but searching what is left would match almost any anime, the search being fuzzy and
    /// a single digit appearing in thousands of titles, and a match however wrong would keep the
    /// folder from being tried at all. A name with no letter anywhere in it is the mark of such
    /// a cut: a real title of that shape - "009-1", "001", "663114" - is its folder's name
    /// whole rather than the start of it.
    /// </remarks>
    /// <param name="name">The name on the item.</param>
    /// <param name="folderName">The name of the folder holding it.</param>
    /// <returns><c>true</c> where the name is the folder name cut short.</returns>
    private static bool IsTruncation(string? name, string? folderName)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folderName) || name.Any(char.IsLetter))
        {
            return false;
        }

        return folderName.Length > name.Length
            && folderName.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The year a folder name carries, as "Ranma &#189; (1989)" does. It is what tells two
    /// shows of one name apart, and it belongs to the folder rather than to whatever the item
    /// has since been named.
    /// </summary>
    /// <param name="name">The folder name.</param>
    /// <returns>The year, or <c>null</c> when the name carries none.</returns>
    private static int? YearInName(string name)
    {
        var match = TrailingYearRegex().Match(name);

        return match.Success && int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    /// <summary>
    /// Reverses the order of the parts of a name.
    /// </summary>
    /// <param name="name">The name to reverse.</param>
    /// <returns>The reversed name.</returns>
    public static string ReverseNameOrder(string name)
    {
        return name.Split(' ').Reverse().Aggregate(string.Empty, (n, part) => n + " " + part).Trim();
    }

    private static async Task DownloadSeriesData(string aid, string seriesDataPath, string cachePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(seriesDataPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The series data path does not contain a directory.", nameof(seriesDataPath));
        }

        var httpClient = Plugin.Instance.GetHttpClient();
        var url = string.Format(CultureInfo.InvariantCulture, _seriesQueryUrlFormat, "mediabrowser", aid);

        await WaitForRequestSlot(cancellationToken).ConfigureAwait(false);

        string text;
        using (var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        text = text.Replace("&#x0;", string.Empty, StringComparison.Ordinal);

        // Validate before touching the cache. AniDB answers a ban with an <error> document,
        // and overwriting good cached data with one would force another request on the next
        // scan, exactly when none must be made.
        var errorRegexMatch = ErrorRegex().Match(text);
        if (errorRegexMatch.Success)
        {
            // A ban is reported as either <error>Banned</error> or <error code="500">banned</error>.
            if (BannedRegex().IsMatch(errorRegexMatch.Value))
            {
                var retryAfter = RegisterBan();

                throw new AniDbBannedException(
                    string.Format(CultureInfo.InvariantCulture, "AniDB has banned this client; pausing all AniDB requests for {0}.", retryAfter))
                {
                    RetryAfter = retryAfter
                };
            }

            throw new InvalidOperationException("AniDB API error " + errorRegexMatch.Value);
        }

        // An empty body is how the API turns a request away once it has stopped answering, so
        // it counts as a ban. Treating it as a one-off failure would let a scan carry on
        // asking, which is what turns a short refusal into a ban measured in days.
        if (string.IsNullOrWhiteSpace(text))
        {
            var retryAfter = RegisterBan();

            throw new AniDbBannedException(
                string.Format(CultureInfo.InvariantCulture, "AniDB returned an empty response for anime {0}; pausing all AniDB requests for {1}.", aid, retryAfter))
            {
                RetryAfter = retryAfter
            };
        }

        RegisterSuccess();

        // The payload is known good, so the previous cache may now be replaced.
        Directory.CreateDirectory(directory);
        DeleteXmlFiles(directory);
        await File.WriteAllTextAsync(seriesDataPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        await ExtractEpisodes(directory, seriesDataPath).ConfigureAwait(false);
        await ExtractCast(cachePath, seriesDataPath).ConfigureAwait(false);
    }

    private static void DeleteXmlFiles(string path)
    {
        try
        {
            foreach (var file in new DirectoryInfo(path)
                .EnumerateFiles("*.xml", SearchOption.AllDirectories))
            {
                file.Delete();
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing cached to remove.
        }
    }

    private static async Task ExtractEpisodes(string seriesDataDirectory, string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8);
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "episode")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    await SaveEpsiodeXml(seriesDataDirectory, outerXml).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ExtractCast(string cachePath, string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        var cast = new List<AniDbPersonInfo>();

        using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
        {
            using var reader = XmlReader.Create(streamReader, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "characters")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    cast.AddRange(ParseCharacterList(outerXml));
                }

                if (reader.NodeType == XmlNodeType.Element && reader.Name == "creators")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    cast.AddRange(ParseCreatorsList(outerXml));
                }
            }
        }

        var serializer = new XmlSerializer(typeof(AniDbPersonInfo));
        foreach (var person in cast)
        {
            if (string.IsNullOrEmpty(person.Name))
            {
                continue;
            }

            var path = GetCastPath(person.Name, cachePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path) || person.Image != null)
            {
                try
                {
                    var partialPath = path + ".partial";

                    using (var stream = File.Open(partialPath, FileMode.Create))
                    {
                        serializer.Serialize(stream, person);
                    }

                    File.Move(partialPath, path, true);
                }
                catch (IOException)
                {
                    // Another refresh is writing the same person; either copy will do.
                }
            }
        }
    }

    /// <summary>
    /// Gets the cached information about a person.
    /// </summary>
    /// <param name="cachePath">The cache path.</param>
    /// <param name="name">The name of the person.</param>
    /// <returns>The cached person info, or <c>null</c> when it is not cached.</returns>
    public static AniDbPersonInfo? GetPersonInfo(string cachePath, string name)
    {
        // The cache files a person under the first character of their name, so a blank one
        // has nowhere to be looked up.
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = GetCastPath(name, cachePath);
        var serializer = new XmlSerializer(typeof(AniDbPersonInfo));

        try
        {
            if (File.Exists(path))
            {
                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                using var stream = File.OpenRead(path);
                using var reader = XmlReader.Create(stream, readerSettings);
                return serializer.Deserialize(reader) as AniDbPersonInfo;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // What XmlSerializer throws for a document it cannot read. The file is only
            // rewritten for a person who turns up again with an image, so leaving it would
            // fail this person on every refresh. Removing it means simply not cached.
            Logger?.LogWarning(ex, "Discarding the unreadable cached person file {Path}", path);

            try
            {
                File.Delete(path);
            }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                // The next refresh tries again.
            }

            return null;
        }

        return null;
    }

    private static string GetCastPath(string name, string cachePath)
    {
        name = name.ToLowerInvariant();
        return Path.Combine(cachePath, "anidb-people", name[0].ToString(), name + ".xml");
    }

    private static IEnumerable<AniDbPersonInfo> ParseCharacterList(string xml)
    {
        var doc = XDocument.Parse(xml);
        var people = new List<AniDbPersonInfo>();

        var characters = doc.Element("characters");
        if (characters != null)
        {
            foreach (var character in characters.Descendants("character"))
            {
                var seiyuu = character.Element("seiyuu");
                if (seiyuu != null)
                {
                    var person = new AniDbPersonInfo
                    {
                        Name = ReverseNameOrder(seiyuu.Value)
                    };

                    var picture = seiyuu.Attribute("picture");
                    if (picture != null && !string.IsNullOrEmpty(picture.Value))
                    {
                        person.Image = PersonImageBaseUrl + picture.Value;
                    }

                    var id = seiyuu.Attribute("id");
                    if (id != null && !string.IsNullOrEmpty(id.Value))
                    {
                        person.Id = id.Value;
                    }

                    people.Add(person);
                }
            }
        }

        return people;
    }

    private static IEnumerable<AniDbPersonInfo> ParseCreatorsList(string xml)
    {
        var doc = XDocument.Parse(xml);
        var people = new List<AniDbPersonInfo>();

        var creators = doc.Element("creators");
        if (creators != null)
        {
            foreach (var creator in creators.Descendants("name"))
            {
                var type = creator.Attribute("type");
                if (type != null && type.Value == "Animation Work")
                {
                    continue;
                }

                var person = new AniDbPersonInfo
                {
                    Name = ReverseNameOrder(creator.Value)
                };

                var id = creator.Attribute("id");
                if (id != null && !string.IsNullOrEmpty(id.Value))
                {
                    person.Id = id.Value;
                }

                people.Add(person);
            }
        }

        return people;
    }

    private static async Task SaveXml(string xml, string filename)
    {
        var writerSettings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Async = true
        };

        // Written beside the real file and moved onto it once complete. A crash or a full disk
        // part way through a direct write would leave a truncated document, which is worse than
        // none: it does not look stale, so every read of it fails until the cache window lapses.
        var partialFilename = filename + ".partial";

        using (var writer = XmlWriter.Create(partialFilename, writerSettings))
        {
            await writer.WriteRawAsync(xml).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        File.Move(partialFilename, filename, true);
    }

    private static async Task SaveEpsiodeXml(string seriesDataDirectory, string xml)
    {
        var episodeNumber = await ParseEpisodeNumber(xml).ConfigureAwait(false);

        if (episodeNumber != null)
        {
            var file = Path.Combine(seriesDataDirectory, FormattableString.Invariant($"episode-{episodeNumber}.xml"));
            await SaveXml(xml, file).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ParseEpisodeNumber(string xml)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = new StringReader(xml);
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "epno")
                {
                    var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
                else
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the series data path.
    /// </summary>
    /// <param name="appPaths">The app paths.</param>
    /// <param name="seriesId">The series id.</param>
    /// <returns>System.String.</returns>
    public static string GetSeriesDataPath(IApplicationPaths appPaths, string seriesId)
    {
        return Path.Combine(appPaths.CachePath, "anidb", "series", seriesId);
    }

    [GeneratedRegex(@"<error[^>]*>.*?</error>", RegexOptions.Singleline)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex("banned", RegexOptions.IgnoreCase)]
    private static partial Regex BannedRegex();

    [GeneratedRegex(@"\(([0-9]{4})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearRegex();

    private struct GenreInfo
    {
        public string Name;
        public int Weight;
    }
}
