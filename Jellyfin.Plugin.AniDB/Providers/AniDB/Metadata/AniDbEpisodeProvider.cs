using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.Anime.Providers.AniDB.Converter;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Anime.Providers.AniDB.Metadata
{
    /// <summary>
    /// The <see cref="AniDbEpisodeProvider" /> class provides episode metadata from AniDB.
    /// </summary>
    public class AniDbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        private readonly IServerConfigurationManager _configurationManager;

        /// <summary>
        /// Creates a new instance of the <see cref="AniDbEpisodeProvider" /> class.
        /// </summary>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="httpClient">The HTTP client.</param>
        public AniDbEpisodeProvider(IServerConfigurationManager configurationManager)
        {
            _configurationManager = configurationManager;
        }

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new MetadataResult<Episode>();

            var aniDbId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);
            if (string.IsNullOrEmpty(aniDbId))
            {
                return result;
            }

            var id = AniDbEpisodeIdentity.Parse(aniDbId);
            if (id == null)
            {
                return result;
            }

            var seriesFolder = await FindSeriesFolder(id.Value.SeriesId, cancellationToken);
            if (string.IsNullOrEmpty(seriesFolder))
            {
                return result;
            }

            var xml = GetEpisodeXmlFile(id.Value.EpisodeNumber, id.Value.EpisodeType, seriesFolder);
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

            if (id.Value.EpisodeNumberEnd != null && id.Value.EpisodeNumberEnd > id.Value.EpisodeNumber)
            {
                for (var i = id.Value.EpisodeNumber + 1; i <= id.Value.EpisodeNumberEnd; i++)
                {
                    var additionalXml = GetEpisodeXmlFile(i, id.Value.EpisodeType, seriesFolder);
                    if (additionalXml == null || !additionalXml.Exists)
                    {
                        continue;
                    }

                    await ParseAdditionalEpisodeXml(additionalXml, result.Item, info.MetadataLanguage).ConfigureAwait(false);
                }
            }

            return result;
        }

        public string Name => "AniDB";

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            var list = new List<RemoteSearchResult>();

            var id = AniDbEpisodeIdentity.Parse(searchInfo.ProviderIds.GetOrDefault(ProviderNames.AniDb));
            if (id == null)
            {
                return list;
            }

            await AniDbSeriesProvider.GetSeriesData(
                _configurationManager.ApplicationPaths,
                id.Value.SeriesId,
                cancellationToken).ConfigureAwait(false);

            try
            {
                var metadataResult = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);

                if (metadataResult.HasMetadata)
                {
                    var item = metadataResult.Item;

                    list.Add(new RemoteSearchResult
                    {
                        IndexNumber = item.IndexNumber,
                        Name = item.Name,
                        ParentIndexNumber = item.ParentIndexNumber,
                        PremiereDate = item.PremiereDate,
                        ProductionYear = item.ProductionYear,
                        ProviderIds = item.ProviderIds,
                        SearchProviderName = Name,
                        IndexNumberEnd = item.IndexNumberEnd
                    });
                }
            }
            catch (FileNotFoundException)
            {
                // Don't fail the provider because this will just keep on going and going.
            }
            catch (DirectoryNotFoundException)
            {
                // Don't fail the provider because this will just keep on going and going.
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var imageProvider = new AniDbImageProvider(_configurationManager.ApplicationPaths);
            return imageProvider.GetImageResponse(url, cancellationToken);
        }

        private async Task ParseAdditionalEpisodeXml(FileInfo xml, Episode episode, string metadataLanguage)
        {
            var settings = new XmlReaderSettings
            {
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = xml.OpenText())
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                var titles = new List<Title>();

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "length":
                                var length = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(length))
                                {
                                    long duration;
                                    if (long.TryParse(length, out duration))
                                    {
                                        episode.RunTimeTicks += TimeSpan.FromMinutes(duration).Ticks;
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
                        }
                    }
                }

                var title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, metadataLanguage).Name;
                if (!string.IsNullOrEmpty(title))
                {
                    title = ", " + title;
                    episode.Name += Plugin.Instance.Configuration.AniDbReplaceGraves
                        ? title.Replace('`', '\'')
                        : title;
                }
            }
        }

        private async Task<string> FindSeriesFolder(string seriesId, CancellationToken cancellationToken)
        {
            var seriesDataPath = await AniDbSeriesProvider.GetSeriesData(_configurationManager.ApplicationPaths, seriesId, cancellationToken).ConfigureAwait(false);
            return Path.GetDirectoryName(seriesDataPath);
        }

        private async Task ParseEpisodeXml(FileInfo xml, Episode episode, string preferredMetadataLanguage)
        {
            var settings = new XmlReaderSettings
            {
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = xml.OpenText())
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                var titles = new List<Title>();

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "length":
                                var length = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(length))
                                {
                                    long duration;
                                    if (long.TryParse(length, out duration))
                                    {
                                        episode.RunTimeTicks = TimeSpan.FromMinutes(duration).Ticks;
                                    }
                                }

                                break;

                            case "airdate":
                                var airdate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(airdate))
                                {
                                    DateTime date;
                                    if (DateTime.TryParse(airdate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out date))
                                    {
                                        episode.PremiereDate = date;
                                    }
                                }

                                break;

                            case "rating":
                                int count;
                                float rating;
                                if (int.TryParse(reader.GetAttribute("count"), NumberStyles.Any, CultureInfo.InvariantCulture, out count) &&
                                    float.TryParse(reader.ReadElementContentAsString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rating))
                                {
                                    episode.CommunityRating = rating;
                                }

                                break;

                            case "title":
                                var language = reader.GetAttribute("xml:lang");
                                var name = reader.ReadElementContentAsString();

                                titles.Add(new Title
                                {
                                    Language = language,
                                    Type = "main",
                                    Name = name
                                });

                                break;
                        }
                    }
                }

                var title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLanguage).Name;
                if (!string.IsNullOrEmpty(title))
                {
                    episode.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                        ? title.Replace('`', '\'')
                        : title;
                }
            }
        }

        private FileInfo GetEpisodeXmlFile(int? episodeNumber, string type, string seriesDataPath)
        {
            if (episodeNumber == null)
            {
                return null;
            }

            const string nameFormat = "episode-{0}.xml";
            var filename = Path.Combine(seriesDataPath, string.Format(nameFormat, (type ?? "") + episodeNumber.Value));
            return new FileInfo(filename);
        }
    }
}
