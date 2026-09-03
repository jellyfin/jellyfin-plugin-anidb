using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The <see cref="AniDbEpisodeProvider" /> class provides episode metadata from AniDB.
/// </summary>
/// <remarks>
/// Creates a new instance of the <see cref="AniDbEpisodeProvider" /> class.
/// </remarks>
/// <param name="configurationManager">The configuration manager.</param>
public class AniDbEpisodeProvider(IServerConfigurationManager configurationManager) : IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    private readonly IServerConfigurationManager _configurationManager = configurationManager;

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new MetadataResult<Episode>();

        var animeId = info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        if (string.IsNullOrEmpty(animeId))
        {
            return result;
        }

        var seriesFolder = await FindSeriesFolder(animeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(seriesFolder))
        {
            return result;
        }

        if (!Plugin.Instance.Configuration.IgnoreSeason && info.ParentIndexNumber > 1)
        {
            return result;
        }

        string episodeType = string.Empty;

        if (info.ParentIndexNumber == 0)
        {
            episodeType = "S";
        }

        var xml = GetEpisodeXmlFile(info.IndexNumber, episodeType, seriesFolder);
        if (xml == null || !xml.Exists)
        {
            return result;
        }

        result.Item = new Episode
        {
            IndexNumber = info.IndexNumber,
            ParentIndexNumber = info.ParentIndexNumber ?? 1
        };

        result.HasMetadata = true;

        await ParseEpisodeXml(xml, result.Item, info.MetadataLanguage).ConfigureAwait(false);

        return result;
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

    private async Task<string?> FindSeriesFolder(string seriesId, CancellationToken cancellationToken)
    {
        var seriesDataPath = await AniDbSeriesProvider.GetSeriesData(_configurationManager.ApplicationPaths, seriesId, cancellationToken).ConfigureAwait(false);
        return Path.GetDirectoryName(seriesDataPath);
    }

    private static async Task ParseEpisodeXml(FileInfo xml, Episode episode, string preferredMetadataLanguage)
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

        var title = titles.Localize(Configuration.TitlePreferenceType.Localized, preferredMetadataLanguage)?.Name;
        if (!string.IsNullOrEmpty(title))
        {
            episode.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                ? title.Replace('`', '\'')
                : title;
        }
    }

    private static FileInfo? GetEpisodeXmlFile(int? episodeNumber, string type, string seriesDataPath)
    {
        if (episodeNumber == null)
        {
            return null;
        }

        var filename = Path.Combine(seriesDataPath, FormattableString.Invariant($"episode-{(type ?? string.Empty) + episodeNumber.Value}.xml"));
        return new FileInfo(filename);
    }
}
