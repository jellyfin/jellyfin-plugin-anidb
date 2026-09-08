using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The <see cref="AniDbEpisodeProvider" /> class provides episode metadata from AniDB.
/// </summary>
/// <remarks>
/// Creates a new instance of the <see cref="AniDbEpisodeProvider" /> class.
/// </remarks>
/// <param name="configurationManager">The configuration manager.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{AniDbEpisodeProvider}"/> interface.</param>
public partial class AniDbEpisodeProvider(IServerConfigurationManager configurationManager, ILibraryManager libraryManager, ILogger<AniDbEpisodeProvider> logger) : IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    private readonly IServerConfigurationManager _configurationManager = configurationManager;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<AniDbEpisodeProvider> _logger = logger;

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new MetadataResult<Episode>();

        var seriesId = info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        if (string.IsNullOrEmpty(seriesId))
        {
            return result;
        }

        FileInfo? xml;

        try
        {
            xml = info.ParentIndexNumber == 0
                ? await FindSpecialXml(info, seriesId, cancellationToken).ConfigureAwait(false)
                : await FindEpisodeXml(info, seriesId, cancellationToken).ConfigureAwait(false);
        }
        catch (AniDbBannedException ex)
        {
            _logger.LogWarning(
                "Season {SeasonNumber} episode {EpisodeNumber} of AniDB series {SeriesId} could not be looked up because AniDB has banned this client. It stays without metadata until the ban lapses, in {RetryAfter}",
                info.ParentIndexNumber,
                info.IndexNumber,
                seriesId,
                ex.RetryAfter);

            return result;
        }

        if (xml == null || !xml.Exists)
        {
            return result;
        }

        result.Item = new Episode
        {
            IndexNumber = info.IndexNumber,
            ParentIndexNumber = info.ParentIndexNumber
        };

        result.HasMetadata = true;

        await ParseEpisodeXml(xml, result.Item, info.MetadataLanguage).ConfigureAwait(false);

        return result;
    }

    private async Task<FileInfo?> FindEpisodeXml(EpisodeInfo info, string seriesId, CancellationToken cancellationToken)
    {
        var (animeId, numberInEntry, kind) = await GetEpisodeSource(info, seriesId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(animeId) || numberInEntry is null)
        {
            return null;
        }

        var seriesFolder = await FindSeriesFolder(animeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(seriesFolder))
        {
            return null;
        }

        var xml = GetEpisodeXmlFile(numberInEntry, kind.Prefix(), seriesFolder);

        if (xml == null || !xml.Exists)
        {
            _logger.LogWarning(
                "Season {SeasonNumber} episode {EpisodeNumber} of AniDB series {SeriesId} has no counterpart in anime {AnimeId}, where it would be episode {EpisodeNumberInEntry}, and stays without metadata",
                info.ParentIndexNumber,
                info.IndexNumber,
                seriesId,
                animeId,
                numberInEntry);

            return null;
        }

        _logger.LogDebug(
            "Season {SeasonNumber} episode {EpisodeNumber} of AniDB series {SeriesId} read from episode {EpisodeNumberInEntry} of anime {AnimeId}",
            info.ParentIndexNumber,
            info.IndexNumber,
            seriesId,
            numberInEntry,
            animeId);

        return xml;
    }

    /// <summary>
    /// Finds the cached document of a special. Jellyfin gathers a show's specials into one
    /// season and numbers them straight through, while AniDB keeps each season's specials in
    /// that season's own entry, numbered from S1. The number alone would therefore read season
    /// 1's S1 for season 2's S1, so the whole chain of entries is searched instead.
    /// </summary>
    /// <param name="info">The episode lookup info.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The special's document, or <c>null</c> when it cannot be identified.</returns>
    private async Task<FileInfo?> FindSpecialXml(EpisodeInfo info, string seriesId, CancellationToken cancellationToken)
    {
        // The mapping sources are where a movie that the season numbering files among the
        // specials is recorded. Nothing about AniDB's own specials can turn one up: it is an
        // anime of its own there, with ordinary episodes.
        if (info.IndexNumber is { } specialNumber)
        {
            var placements = await AniDbMappings.ResolveSpecials(
                _configurationManager.ApplicationPaths,
                seriesId,
                specialNumber,
                _logger,
                cancellationToken).ConfigureAwait(false);

            foreach (var placed in placements)
            {
                var placedFolder = await FindSeriesFolder(placed.AnimeId, cancellationToken).ConfigureAwait(false);
                var placedXml = string.IsNullOrEmpty(placedFolder)
                    ? null
                    : GetEpisodeXmlFile(placed.Number, placed.Kind.Prefix(), placedFolder);

                if (placedXml?.Exists == true)
                {
                    _logger.LogDebug(
                        "Special {EpisodeNumber} of AniDB series {SeriesId} read from {EpisodeNumberInEntry} of anime {AnimeId}, where the mapping sources place it",
                        info.IndexNumber,
                        seriesId,
                        placed.Number,
                        placed.AnimeId);

                    return placedXml;
                }
            }

            // A placement naming an episode that does not exist is not the end of the search.
            // It means the source is wrong about this special, or the entry it names has not
            // been fetched yet, and either way the show's own specials are still worth reading:
            // that is how every special the sources do not place is identified.
            if (placements.Count > 0)
            {
                _logger.LogDebug(
                    "The mapping sources place special {EpisodeNumber} of AniDB series {SeriesId} at {Placement}, none of which holds such an episode, so it is matched against the show's own specials instead",
                    info.IndexNumber,
                    seriesId,
                    string.Join(
                        ", ",
                        placements.Select(placed => FormattableString.Invariant($"{placed.Number} of anime {placed.AnimeId}"))));
            }
        }

        var chain = await AniDbSeasonResolver.GetCachedSeasonChain(_configurationManager.ApplicationPaths, seriesId, cancellationToken).ConfigureAwait(false);

        // Make sure the one entry a single-entry chain has is on disk. The rest of the chain is
        // whatever was cached, which is all a special can be matched against without spending
        // a request on every entry of the show.
        if (chain.Count == 1)
        {
            var seriesFolder = await FindSeriesFolder(seriesId, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(seriesFolder))
            {
                return null;
            }
        }

        var specials = await LoadSpecials(chain, cancellationToken).ConfigureAwait(false);

        if (specials.Count == 0)
        {
            _logger.LogWarning(
                "None of the {EntryCount} AniDB entries of series {SeriesId} has any special, so special {EpisodeNumber} stays without metadata",
                chain.Count,
                seriesId,
                info.IndexNumber);

            return null;
        }

        // Position is the one route that reads nothing about the episode itself, so it only
        // holds where the library and AniDB agree on what the specials are. A library whose
        // specials season also holds a movie, a trailer or an episode AniDB files elsewhere
        // lines up with nothing, and numbering straight down the list would give every special
        // after the first difference the wrong entry.
        var libraryCount = AniDbSeasonLayout.Read(_libraryManager, seriesId)?.SpecialsCount;
        var aligned = Align(specials, libraryCount);

        var match = MatchById(specials, info)
            ?? MatchByTitle(specials, info)
            ?? MatchByDate(specials, info)
            ?? (aligned == null ? null : MatchByPosition(aligned, info));

        if (match == null)
        {
            _logger.LogWarning(
                "Special {EpisodeNumber} of AniDB series {SeriesId} matches none of the {SpecialCount} specials across its {EntryCount} AniDB entries by id, title or air date, so it stays without metadata. The library has {LibraryCount} specials, which line up with neither the whole of that list nor any single entry of it, so they cannot be numbered straight through. Set its AniDB id by hand to fill it in",
                info.IndexNumber,
                seriesId,
                specials.Count,
                chain.Count,
                libraryCount);

            return null;
        }

        _logger.LogDebug(
            "Special {EpisodeNumber} of AniDB series {SeriesId} read from {EpisodeNumberInEntry} of anime {AnimeId}",
            info.IndexNumber,
            seriesId,
            match.Number,
            match.AnimeId);

        return new FileInfo(match.Path);
    }

    /// <summary>
    /// The specials the library's specials season can be numbered straight through, or
    /// <c>null</c> where nothing lines up with it.
    /// </summary>
    /// <remarks>
    /// The whole chain lines up where the library holds every special every entry of the show
    /// lists. Where it does not, one entry's own specials still may: a show whose later seasons'
    /// specials were never released, or never kept, holds exactly the specials of the entry they
    /// belong to, and AniDB numbering them S1 upwards is the same order the library numbers them
    /// in. Only one entry may hold that many for this to be evidence rather than a guess.
    /// </remarks>
    /// <param name="specials">Every special across the show's entries, in order.</param>
    /// <param name="libraryCount">How many specials the library holds, or <c>null</c> where it cannot be read.</param>
    /// <returns>The specials to count through, or <c>null</c>.</returns>
    private static IReadOnlyList<AniDbSpecial>? Align(IReadOnlyList<AniDbSpecial> specials, int? libraryCount)
    {
        if (libraryCount is not > 0)
        {
            return null;
        }

        if (libraryCount == specials.Count)
        {
            return specials;
        }

        var entries = specials
            .GroupBy(special => special.AnimeId, StringComparer.Ordinal)
            .Where(entry => entry.Count() == libraryCount)
            .ToList();

        return entries.Count == 1 ? [.. entries[0].OrderBy(special => special.Number)] : null;
    }

    /// <summary>
    /// Reads every special held by the given AniDB entries, in season order and, within an
    /// entry, in AniDB's own numbering.
    /// </summary>
    /// <param name="chain">The AniDB entries the series spans, in season order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The specials.</returns>
    private async Task<IReadOnlyList<AniDbSpecial>> LoadSpecials(IReadOnlyList<string> chain, CancellationToken cancellationToken)
    {
        var specials = new List<AniDbSpecial>();

        foreach (var animeId in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The cached path, not GetSeriesData: this runs once per special, and a stale
            // entry must not turn a local lookup into a download.
            var folder = AniDbSeriesProvider.GetSeriesDataPath(_configurationManager.ApplicationPaths, animeId);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var inEntry = new List<AniDbSpecial>();

            foreach (var path in Directory.EnumerateFiles(folder, "episode-S*.xml"))
            {
                var number = SpecialNumberRegex().Match(Path.GetFileName(path));
                if (!number.Success || !int.TryParse(number.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var index))
                {
                    continue;
                }

                inEntry.Add(await ParseSpecial(path, animeId, index).ConfigureAwait(false));
            }

            // Directory order is the file system's. AniDB's numbering is the real order.
            specials.AddRange(inEntry.OrderBy(special => special.Number));
        }

        return specials;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        if (!searchInfo.IndexNumber.HasValue)
        {
            return [];
        }

        var metadataResult = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);

        if (!metadataResult.HasMetadata)
        {
            return [];
        }

        var item = metadataResult.Item;

        return
        [
            new RemoteSearchResult
            {
                IndexNumber = item.IndexNumber,
                Name = item.Name,
                ParentIndexNumber = item.ParentIndexNumber,
                PremiereDate = item.PremiereDate,
                ProductionYear = item.ProductionYear,
                ProviderIds = item.ProviderIds,
                SearchProviderName = Name,
                IndexNumberEnd = item.IndexNumberEnd
            }
        ];
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var imageProvider = new AniDbImageProvider(_configurationManager.ApplicationPaths);
        return imageProvider.GetImageResponse(url, cancellationToken);
    }

    /// <summary>
    /// Finds the AniDB entry an episode is read from, and its number within that entry. The two
    /// numbers differ when the season it is shown under is split across several AniDB entries.
    /// </summary>
    /// <param name="info">The episode lookup info.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entry and the episode's number in it, or nulls when neither can be identified.</returns>
    private async Task<(string? AnimeId, int? NumberInEntry, AniDbEpisodeKind Kind)> GetEpisodeSource(EpisodeInfo info, string seriesId, CancellationToken cancellationToken)
    {
        if (info.IndexNumber is not { } episodeNumber)
        {
            return (null, null, AniDbEpisodeKind.Regular);
        }

        // Every AniDB anime numbers its episodes from one, so an episode can only be looked up
        // against the entry holding its own season. Specials are the exception: AniDB keeps
        // them in the entry they belong to, under their own numbering.
        if (Plugin.Instance.Configuration.IgnoreSeason || info.ParentIndexNumber is null or <= 0)
        {
            return (seriesId, episodeNumber, AniDbEpisodeKind.Regular);
        }

        var segments = await AniDbSeasonResolver.ResolveSeasonSegments(
            _configurationManager.ApplicationPaths,
            _libraryManager,
            seriesId,
            info.ParentIndexNumber.Value,
            _logger,
            cancellationToken).ConfigureAwait(false);

        // The season provider stores the entry a season starts in, so an id that is not that
        // one was set by hand in the metadata editor. That names the entry to read, and its
        // episodes are numbered from one there.
        var seasonId = info.SeasonProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        if (!string.IsNullOrEmpty(seasonId)
            && (segments == null || !string.Equals(seasonId, segments[0].AnimeId, StringComparison.Ordinal)))
        {
            return (seasonId, episodeNumber, AniDbEpisodeKind.Regular);
        }

        if (segments == null)
        {
            return (null, null, AniDbEpisodeKind.Regular);
        }

        var segment = AniDbSeasonResolver.PickSegment(segments, episodeNumber);

        return (segment.AnimeId, segment.FirstEpisodeInEntry + (episodeNumber - segment.FirstEpisodeNumber), segment.Kind);
    }

    private async Task<string?> FindSeriesFolder(string seriesId, CancellationToken cancellationToken)
    {
        var seriesDataPath = await AniDbSeriesProvider.GetSeriesData(_configurationManager.ApplicationPaths, seriesId, cancellationToken).ConfigureAwait(false);
        return Path.GetDirectoryName(seriesDataPath);
    }

    private static async Task<AniDbSpecial> ParseSpecial(string path, string animeId, int number)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        string? episodeId = null;
        DateTime? airDate = null;
        var titles = new List<string>();

        using (var streamReader = new StreamReader(path))
        using (var reader = XmlReader.Create(streamReader, settings))
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);

            episodeId = reader.GetAttribute("id");

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "airdate":
                        var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                        {
                            airDate = parsed;
                        }

                        break;

                    case "title":
                        var title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            titles.Add(title);
                        }

                        break;
                }
            }
        }

        return new AniDbSpecial(animeId, path, number, episodeId, airDate, titles);
    }

    /// <summary>
    /// Matches the special whose AniDB id the item already carries. Set by hand in the
    /// metadata editor, so it is the one signal that cannot be wrong.
    /// </summary>
    /// <param name="specials">The specials to match against.</param>
    /// <param name="info">The episode lookup info.</param>
    /// <returns>The matching special, or <c>null</c>.</returns>
    private static AniDbSpecial? MatchById(IReadOnlyList<AniDbSpecial> specials, EpisodeInfo info)
    {
        var episodeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        return string.IsNullOrEmpty(episodeId)
            ? null
            : specials.FirstOrDefault(special => string.Equals(special.EpisodeId, episodeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Matches on the name the file was scanned under. Only an unambiguous hit counts, since
    /// two seasons of the same show routinely name their specials alike.
    /// </summary>
    /// <param name="specials">The specials to match against.</param>
    /// <param name="info">The episode lookup info.</param>
    /// <returns>The matching special, or <c>null</c>.</returns>
    private static AniDbSpecial? MatchByTitle(IReadOnlyList<AniDbSpecial> specials, EpisodeInfo info)
    {
        var name = Normalize(info.Name);

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var matches = specials
            .Where(special => special.Titles.Any(title => string.Equals(Normalize(title), name, StringComparison.Ordinal)))
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Matches on the air date another provider has already filled in. Only an unambiguous
    /// hit counts.
    /// </summary>
    /// <param name="specials">The specials to match against.</param>
    /// <param name="info">The episode lookup info.</param>
    /// <returns>The matching special, or <c>null</c>.</returns>
    private static AniDbSpecial? MatchByDate(IReadOnlyList<AniDbSpecial> specials, EpisodeInfo info)
    {
        if (info.PremiereDate is not { } premiereDate)
        {
            return null;
        }

        var matches = specials
            .Where(special => special.AirDate?.Date == premiereDate.Date)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Falls back to where the special sits in the season. Jellyfin numbers specials straight
    /// through in season order, which is the order they are gathered in here, so the numbers
    /// line up as long as the library holds every special the entries list.
    /// </summary>
    /// <param name="specials">The specials to match against.</param>
    /// <param name="info">The episode lookup info.</param>
    /// <returns>The matching special, or <c>null</c>.</returns>
    private static AniDbSpecial? MatchByPosition(IReadOnlyList<AniDbSpecial> specials, EpisodeInfo info)
    {
        var position = info.IndexNumber - 1;

        return position >= 0 && position < specials.Count ? specials[position.Value] : null;
    }

    /// <summary>
    /// Reduces a title to its letters and digits, so that punctuation, spacing and case
    /// cannot keep two spellings of the same name apart.
    /// </summary>
    /// <param name="value">The title to reduce.</param>
    /// <returns>The reduced title.</returns>
    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// Fills an episode from its cached document. Internal because a movie AniDB holds inside
    /// another entry is one of these episodes, and the movie provider reads its own name and
    /// air date from here rather than from the whole entry's record.
    /// </summary>
    /// <param name="xml">The episode's cached document.</param>
    /// <param name="episode">The episode to fill.</param>
    /// <param name="preferredMetadataLanguage">The language its title is wanted in.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ParseEpisodeXml(FileInfo xml, Episode episode, string preferredMetadataLanguage)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = xml.OpenText();
        using var reader = XmlReader.Create(streamReader, settings);
        var titles = new List<Title>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "episode":
                        var episodeId = reader.GetAttribute("id");
                        if (!string.IsNullOrEmpty(episodeId))
                        {
                            episode.ProviderIds.Add(ProviderNames.AniDb, episodeId);
                        }

                        break;

                    case "length":
                        var length = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(length))
                        {
                            if (long.TryParse(length, CultureInfo.InvariantCulture, out var duration))
                            {
                                episode.RunTimeTicks = TimeSpan.FromMinutes(duration).Ticks;
                            }
                        }

                        break;

                    case "airdate":
                        var airdate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(airdate))
                        {
                            if (DateTime.TryParse(airdate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                            {
                                episode.PremiereDate = date;
                            }
                        }

                        break;

                    case "rating":
                        if (int.TryParse(reader.GetAttribute("votes"), NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        {
                            var ratingText = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            if (float.TryParse(ratingText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating))
                            {
                                episode.CommunityRating = rating;
                            }
                        }

                        break;

                    case "title":
                        var language = reader.GetAttribute("xml:lang");
                        var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                        titles.Add(new Title
                        {
                            Language = language,
                            Type = "main",
                            Name = name
                        });

                        break;

                    case "summary":
                        var overview = AniDbSeriesProvider.ReplaceNewLine(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
                        episode.Overview = Plugin.Instance.Configuration.AniDbReplaceGraves ? overview.Replace('`', '\'') : overview;

                        break;
                }
            }
        }

        var title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLanguage)?.Name;
        if (!string.IsNullOrEmpty(title))
        {
            episode.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                ? title.Replace('`', '\'')
                : title;
        }
    }

    /// <summary>
    /// The cached document of one episode of an entry.
    /// </summary>
    /// <param name="episodeNumber">The episode's number within the entry.</param>
    /// <param name="type">The prefix of its numbering, from <see cref="AniDbEpisodeKindExtensions.Prefix"/>.</param>
    /// <param name="seriesDataPath">Where the entry's documents are cached.</param>
    /// <returns>The document, which may not exist, or <c>null</c> where no number was given.</returns>
    internal static FileInfo? GetEpisodeXmlFile(int? episodeNumber, string type, string seriesDataPath)
    {
        if (episodeNumber == null)
        {
            return null;
        }

        var filename = Path.Combine(seriesDataPath, FormattableString.Invariant($"episode-{(type ?? string.Empty) + episodeNumber.Value}.xml"));
        return new FileInfo(filename);
    }

    [GeneratedRegex(@"^episode-S(\d+)\.xml$")]
    private static partial Regex SpecialNumberRegex();

    /// <summary>
    /// A special held by one AniDB entry.
    /// </summary>
    /// <param name="AnimeId">The AniDB id of the entry holding it.</param>
    /// <param name="Path">The path of its cached document.</param>
    /// <param name="Number">Its number within that entry.</param>
    /// <param name="EpisodeId">Its own AniDB episode id.</param>
    /// <param name="AirDate">The date it aired.</param>
    /// <param name="Titles">Every title AniDB records for it.</param>
    private sealed record AniDbSpecial(
        string AnimeId,
        string Path,
        int Number,
        string? EpisodeId,
        DateTime? AirDate,
        IReadOnlyList<string> Titles);
}
