using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for series.
/// </summary>
public partial class AniDbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    // AniDB allows one HTTP API request every 2-4 seconds; sending sequential requests
    // closer together than 2500ms triggers an automatic ban, so that is the floor here.
    // Bucket- and window-based limiters cannot express this: they replenish on a fixed
    // cadence that is independent of when tokens are consumed, so a plugin that has been
    // idle long enough to hold a full bucket can issue two requests back to back across a
    // replenishment tick. Gate on the time elapsed since the previous request instead, and
    // measure it with the monotonic clock so that system time changes cannot shorten it.
    private static readonly TimeSpan _minimumRequestInterval = TimeSpan.FromMilliseconds(2500);
    private static readonly long _minimumRequestIntervalTicks = (long)(Stopwatch.Frequency * _minimumRequestInterval.TotalSeconds);
    private static readonly TimeSpan _minimumDelay = TimeSpan.FromMilliseconds(1);
    private static readonly SemaphoreSlim _requestGate = new(1, 1);

    // An AniDB ban is temporary but its remaining time cannot be queried, and reported
    // durations range from 15 minutes to 24 hours depending on how badly the limit was
    // exceeded. Repeatedly probing a banned server extends the ban, so back off for
    // twice as long after each consecutive ban and reset once a request succeeds.
    private static readonly TimeSpan _initialBanBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _maximumBanBackoff = TimeSpan.FromHours(24);
    private static readonly Regex _bannedRegex = new("banned", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Lock _banLock = new();

    private static readonly int[] IgnoredTagIds = [6, 22, 23, 60, 128, 129, 185, 216, 242, 255, 268, 269, 289];
    private static readonly Regex AniDbUrlRegex = MyRegex();
    private static readonly Regex _errorRegex = new(@"<error[^>]*>.*?</error>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly CompositeFormat _seriesQueryUrlFormat = CompositeFormat.Parse("http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver=1&protover=1&aid={1}");

    /// <summary>
    /// The monotonic timestamp before which no further AniDB request may be issued.
    /// Guarded by <see cref="_requestGate"/>.
    /// </summary>
    private static long _nextRequestTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// The UTC time at which the current ban is assumed to have lapsed. This is wall clock
    /// rather than the monotonic clock used for request spacing, because a ban outlives the
    /// process and has to stay meaningful across a restart. Guarded by <see cref="_banLock"/>.
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
    /// Whether the "resuming requests" message has already been logged for the current
    /// ban, so that it is not repeated for every waiting request. Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static bool _resumeLogged;

    private readonly IApplicationPaths _appPaths;

    private readonly Dictionary<string, PersonKind> _typeMappings = new()
    {
        { "Direction", PersonKind.Director },
        { "Music", PersonKind.Composer },
        { "Chief Animation Direction", PersonKind.Director }
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

        if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
        {
            animeId = await Equals_check.XmlFindId(info.Name, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(animeId))
        {
            return await GetMetadataForId(animeId, info, cancellationToken).ConfigureAwait(false);
        }

        return new MetadataResult<Series>();
    }

    /// <summary>
    /// Gets the metadata for the given AniDB id.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="info">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The metadata result.</returns>
    public async Task<MetadataResult<Series>> GetMetadataForId(string animeId, SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>
        {
            Item = new Series(),
            HasMetadata = true
        };

        result.Item.ProviderIds.Add(ProviderNames.AniDb, animeId);

        var seriesDataPath = await GetSeriesData(_appPaths, animeId, cancellationToken).ConfigureAwait(false);
        await FetchSeriesInfo(result, seriesDataPath, info.MetadataLanguage ?? "en").ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var animeId = searchInfo.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        if (!string.IsNullOrEmpty(animeId))
        {
            var resultMetadata = await GetMetadataForId(animeId, searchInfo, cancellationToken).ConfigureAwait(false);

            if (resultMetadata.HasMetadata)
            {
                var imageProvider = new AniDbImageProvider(_appPaths);
                var images = await imageProvider.GetImages(animeId, cancellationToken).ConfigureAwait(false);
                results.Add(MetadataToRemoteSearchResult(resultMetadata, images));
            }
        }

        if (!string.IsNullOrEmpty(searchInfo.Name))
        {
            List<RemoteSearchResult> name_results = await GetSearchResultsByName(searchInfo.Name, searchInfo, cancellationToken).ConfigureAwait(false);

            foreach (var media in name_results)
            {
                results.Add(media);
            }
        }

        return results;
    }

    /// <summary>
    /// Searches AniDB for series matching the given name.
    /// </summary>
    /// <param name="name">The name to search for.</param>
    /// <param name="searchInfo">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The search results.</returns>
    public async Task<List<RemoteSearchResult>> GetSearchResultsByName(string name, SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var imageProvider = new AniDbImageProvider(_appPaths);
        var results = new List<RemoteSearchResult>();

        List<string> ids = await Equals_check.XmlSearch(name, cancellationToken).ConfigureAwait(false);

        foreach (string id in ids)
        {
            var resultMetadata = await GetMetadataForId(id, searchInfo, cancellationToken).ConfigureAwait(false);

            if (resultMetadata.HasMetadata)
            {
                var images = await imageProvider.GetImages(id, cancellationToken).ConfigureAwait(false);
                results.Add(MetadataToRemoteSearchResult(resultMetadata, images));
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
        await WaitForRequestSlot(cancellationToken).ConfigureAwait(false);
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
        var dataPath = GetSeriesDataPath(appPaths, seriesId);
        var seriesDataPath = Path.Combine(dataPath, "series.xml");
        var fileInfo = new FileInfo(seriesDataPath);

        var isEmpty = fileInfo.Exists && fileInfo.Length == 0;
        var isStale = fileInfo.Exists && DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(Plugin.Instance.Configuration.MaxCacheAge);

        if (!fileInfo.Exists || isEmpty || isStale)
        {
            // A stale cache entry is still far better than no metadata at all, so while
            // banned keep reading from disk rather than spending a request that would only
            // be refused and lengthen the ban.
            if (fileInfo.Exists && !isEmpty && GetRemainingBanTime() > TimeSpan.Zero)
            {
                return seriesDataPath;
            }

            await DownloadSeriesData(seriesId, seriesDataPath, appPaths.CachePath, cancellationToken).ConfigureAwait(false);
        }

        return seriesDataPath;
    }

    /// <summary>
    /// Waits until the AniDB rate limit allows another request to be made.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task WaitForRequestSlot(CancellationToken cancellationToken)
    {
        ThrowIfBanned();

        // The gate is held across the wait so that concurrent callers queue behind each
        // other rather than all sleeping in parallel and then firing at the same moment.
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Task.Delay may fire fractionally early, so re-check rather than assuming a
            // single delay was enough to clear the interval.
            for (var remaining = GetRemainingInterval(); remaining > TimeSpan.Zero; remaining = GetRemainingInterval())
            {
                await Task.Delay(remaining < _minimumDelay ? _minimumDelay : remaining, cancellationToken).ConfigureAwait(false);
            }

            // A caller ahead of this one in the queue may have been banned while this one
            // waited, so re-check instead of draining the whole backlog into a banned server.
            ThrowIfBanned();

            _nextRequestTimestamp = Stopwatch.GetTimestamp() + _minimumRequestIntervalTicks;
        }
        finally
        {
            _requestGate.Release();
        }
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
            // Every successful download lands here, so do nothing unless a ban was actually
            // in effect. Otherwise the configuration would be rewritten once per series.
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
            // Losing the record only costs the ban its ability to outlive a restart, so this
            // must not be allowed to fail the metadata request that discovered it.
            Logger?.LogWarning(ex, "Could not persist the AniDB ban state, so it will not survive a restart");
        }
    }

    private static TimeSpan GetRemainingInterval()
        => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _nextRequestTimestamp);

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
                                if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    date = date.ToUniversalTime();
                                    series.PremiereDate = date;
                                }
                            }

                            break;

                        case "enddate":
                            var endDate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(endDate))
                            {
                                if (DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    date = date.ToUniversalTime();
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
                            description = description.TrimStart('*').Trim();
                            series.Overview = ReplaceNewLine(StripAniDbLinks(
                                Plugin.Instance.Configuration.AniDbReplaceGraves ? description.Replace('`', '\'') : description));

                            break;

                        case "ratings":
                            using (var subtree = reader.ReadSubtree())
                            {
                                ParseRatings(series, subtree);
                            }

                            break;

                        case "resources":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseResources(series, subtree).ConfigureAwait(false);
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

                        case "episodes":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseEpisodes(series, subtree).ConfigureAwait(false);
                            }

                            break;
                    }
                }
            }
        }

        GenreHelper.CleanupGenres(series);
    }

    private static async Task ParseEpisodes(Series series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "episode")
            {
                if (int.TryParse(reader.GetAttribute("id"), out int id) && IgnoredTagIds.Contains(id))
                {
                    continue;
                }

                using var episodeSubtree = reader.ReadSubtree();
                while (await episodeSubtree.ReadAsync().ConfigureAwait(false))
                {
                    if (episodeSubtree.NodeType == XmlNodeType.Element)
                    {
                        switch (episodeSubtree.Name)
                        {
                            case "epno":
                                // var epno = episodeSubtree.ReadElementContentAsString();
                                // EpisodeInfo info = new EpisodeInfo();
                                // info.AnimeSeriesIndex = series.AnimeSeriesIndex;
                                // info.IndexNumberEnd = string(epno);
                                // info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb);
                                // episodes.Add(info);
                                break;
                        }
                    }
                }
            }
        }
    }

    private static async Task ParseTags(Series series, XmlReader reader)
    {
        var genres = new List<GenreInfo>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
            {
                if (!int.TryParse(reader.GetAttribute("weight"), out int weight))
                {
                    weight = 0;
                }

                if (int.TryParse(reader.GetAttribute("id"), out int id) && IgnoredTagIds.Contains(id))
                {
                    continue;
                }

                if (int.TryParse(reader.GetAttribute("parentid"), out int parentId)
                    && IgnoredTagIds.Contains(parentId))
                {
                    continue;
                }

                using var tagSubtree = reader.ReadSubtree();
                while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                {
                    if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                    {
                        var name = await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (name == "18 restricted")
                        {
                            series.OfficialRating = "XXX";
                        }

                        if (weight >= 400)
                        {
                            genres.Add(new GenreInfo { Name = name, Weight = weight });
                        }
                    }
                }
            }
        }

        series.Genres = [.. genres.OrderBy(g => g.Weight).Select(g => g.Name)];
    }

    private static async Task ParseResources(Series series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "resource")
            {
                var type = reader.GetAttribute("type");
                switch (type)
                {
                    case "4":
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "url")
                            {
                                await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                break;
                            }
                        }

                        break;
                }
            }
        }
    }

    private static string StripAniDbLinks(string text)
    {
        return AniDbUrlRegex.Replace(text, "${name}");
    }

    /// <summary>
    /// Replaces new lines with HTML line breaks.
    /// </summary>
    /// <param name="text">The text to transform.</param>
    /// <returns>The transformed text.</returns>
    public static string ReplaceNewLine(string text)
    {
        return text.Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private async Task ParseActors(MetadataResult<Series> series, XmlReader reader)
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

    private async Task ParseActor(MetadataResult<Series> series, XmlReader reader)
    {
        string? name = null;
        string? role = null;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "name":
                        role = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "seiyuu":
                        name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                }
            }
        }

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(role)) // && series.People.All(p => p.Name != name))
        {
            series.AddPerson(CreatePerson(
                Plugin.Instance.Configuration.AniDbReplaceGraves ? name.Replace('`', '\'') : name,
                PersonType.Actor,
                role));
        }
    }

    private static void ParseRatings(Series series, XmlReader reader)
    {
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
        string? originalTitle = titles.Localize(Plugin.Instance.Configuration.OriginalTitlePreference, preferredMetadataLangauge)?.Name;

        return (title, originalTitle);
    }

    private async Task ParseCreators(MetadataResult<Series> series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "name")
            {
                var type = reader.GetAttribute("type");
                var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                if (type == "Animation Work")
                {
                    series.Item.AddStudio(name);
                }
                else
                {
                    series.AddPerson(CreatePerson(
                       Plugin.Instance.Configuration.AniDbReplaceGraves ? name.Replace('`', '\'') : name, type));
                }
            }
        }
    }

    private PersonInfo CreatePerson(string name, string? type, string? role = null)
    {
        // todo find nationality of person and conditionally reverse name order

        if (!Enum.TryParse(type, out PersonKind personKind))
        {
            personKind = type is null ? PersonKind.Actor : _typeMappings.GetValueOrDefault(type, PersonKind.Actor);
        }

        return new PersonInfo
        {
            Name = ReverseNameOrder(name),
            Type = personKind,
            Role = role
        };
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
        // and the daily quota is low, so overwriting good cached data with an error would
        // force another request on the very next scan - precisely when no request must be
        // made. On failure the previous cache is left intact.
        var errorRegexMatch = _errorRegex.Match(text);
        if (errorRegexMatch.Success)
        {
            // A ban is reported as either <error>Banned</error> or <error code="500">banned</error>.
            if (_bannedRegex.IsMatch(errorRegexMatch.Value))
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

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("AniDB returned an empty response for anime " + aid);
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
            // No biggie
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
        // Use XmlReader for best performance
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        // Loop through each element
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
            // Use XmlReader for best performance
            using var reader = XmlReader.Create(streamReader, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);

            // Loop through each element
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
                    using var stream = File.Open(path, FileMode.Create);
                    serializer.Serialize(stream, person);
                }
                catch (IOException)
                {
                    // ignore
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
                        person.Image = "https://cdn.anidb.net/images/main/" + picture.Value;
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

        using var writer = XmlWriter.Create(filename, writerSettings);
        await writer.WriteRawAsync(xml).ConfigureAwait(false);
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
        // Use XmlReader for best performance
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        // Loop through each element
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

    [GeneratedRegex(@"https?://anidb.net/\w+(/[0-9]+)? \[(?<name>[^\]]*)\]", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    private struct GenreInfo
    {
        public string Name;
        public int Weight;
    }
}
