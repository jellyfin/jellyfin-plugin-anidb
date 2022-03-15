using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using System.Net.Http;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    public class AniDbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
    {
        private const string SeriesDataFile = "series.xml";
        private const string SeriesQueryUrl = "http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver=1&protover=1&aid={1}";
        private const string ClientName = "mediabrowser";

        // AniDB has very low request rate limits, a minimum of 2 seconds between requests, and an average of 4 seconds between requests
        public static readonly RateLimiter RequestLimiter = new RateLimiter(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        private static readonly int[] IgnoredTagIds = { 6, 22, 23, 60, 128, 129, 185, 216, 242, 255, 268, 269, 289 };
        private static readonly Regex AniDbUrlRegex = new Regex(@"https?://anidb.net/\w+ \[(?<name>[^\]]*)\]");
        private readonly IApplicationPaths _appPaths;

        private readonly Dictionary<string, string> _typeMappings = new Dictionary<string, string>
        {
            {"Direction", PersonType.Director},
            {"Music", PersonType.Composer},
            {"Chief Animation Direction", PersonType.Director}
        };

        public AniDbSeriesProvider(IApplicationPaths appPaths)
        {
            _appPaths = appPaths;
            TitleMatcher = AniDbTitleMatcher.DefaultInstance;
            Current = this;
        }

        private static AniDbSeriesProvider Current { get; set; }
        private IAniDbTitleMatcher TitleMatcher { get; set; }
        public int Order => -1;
        public string Name => "AniDB";

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var animeId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
            {
                animeId = await Equals_check.XmlFindId(info.Name, cancellationToken);
            }

            if (!string.IsNullOrEmpty(animeId))
            {
                return await GetMetadataForId(animeId, info, cancellationToken);
            }

            return new MetadataResult<Series>();
        }

        private async Task<MetadataResult<Series>> GetMetadataForId(string animeId, SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();

            result.Item = new Series();
            result.HasMetadata = true;

            result.Item.ProviderIds.Add(ProviderNames.AniDb, animeId);

            var seriesDataPath = await GetSeriesData(_appPaths, animeId, cancellationToken);
            await FetchSeriesInfo(result, seriesDataPath, info.MetadataLanguage ?? "en").ConfigureAwait(false);

            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            var animeId = searchInfo.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            if (!string.IsNullOrEmpty(animeId))
            {
                var resultMetadata = await GetMetadataForId(animeId, searchInfo, cancellationToken);

                if (resultMetadata.HasMetadata)
                {
                    var imageProvider = new AniDbImageProvider(_appPaths);
                    var images = await imageProvider.GetImages(animeId, cancellationToken);
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

        public async Task<List<RemoteSearchResult>> GetSearchResultsByName(string name, SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var imageProvider = new AniDbImageProvider(_appPaths);
            var results = new List<RemoteSearchResult>();

            List<string> ids = await Equals_check.XmlSearch(name, cancellationToken);

            foreach (string id in ids)
            {
                var resultMetadata = await GetMetadataForId(id, searchInfo, cancellationToken);

                if (resultMetadata.HasMetadata)
                {
                    var images = await imageProvider.GetImages(id, cancellationToken);
                    results.Add(MetadataToRemoteSearchResult(resultMetadata, images));
                }
            }
            return results;
        }

        public RemoteSearchResult MetadataToRemoteSearchResult(MetadataResult<Series> metadata, IEnumerable<RemoteImageInfo> images)
        {
            return new RemoteSearchResult
            {
                Name = metadata.Item.Name,
                ProductionYear = metadata.Item.PremiereDate?.Year,
                PremiereDate = metadata.Item.PremiereDate,
                ImageUrl = images.Any() ? images.First().Url : null,
                ProviderIds = metadata.Item.ProviderIds,
                SearchProviderName = ProviderNames.AniDb
            };
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = Plugin.Instance.GetHttpClient();

            return await httpClient.GetAsync(url).ConfigureAwait(false);
        }

        public static async Task<string> GetSeriesData(IApplicationPaths appPaths, string seriesId, CancellationToken cancellationToken)
        {
            var dataPath = GetSeriesDataPath(appPaths, seriesId);
            var seriesDataPath = Path.Combine(dataPath, SeriesDataFile);
            var fileInfo = new FileInfo(seriesDataPath);

            // download series data if not present or out of date
            if (!fileInfo.Exists || DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(7))
            {
                await DownloadSeriesData(seriesId, seriesDataPath, appPaths.CachePath, cancellationToken).ConfigureAwait(false);
            }

            return seriesDataPath;
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
                                        series.ProductionYear = date.Year;
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

        private async Task ParseEpisodes(Series series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "episode")
                {
                    if (int.TryParse(reader.GetAttribute("id"), out int id) && IgnoredTagIds.Contains(id))
                    {
                        continue;
                    }

                    using (var episodeSubtree = reader.ReadSubtree())
                    {
                        while (await episodeSubtree.ReadAsync().ConfigureAwait(false))
                        {
                            if (episodeSubtree.NodeType == XmlNodeType.Element)
                            {
                                switch (episodeSubtree.Name)
                                {
                                    case "epno":
                                        //var epno = episodeSubtree.ReadElementContentAsString();
                                        //EpisodeInfo info = new EpisodeInfo();
                                        //info.AnimeSeriesIndex = series.AnimeSeriesIndex;
                                        //info.IndexNumberEnd = string(epno);
                                        //info.SeriesProviderIds.GetOrDefault(ProviderNames.AniDb);
                                        //episodes.Add(info);
                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task ParseTags(Series series, XmlReader reader)
        {
            var genres = new List<GenreInfo>();

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
                {
                    if (!int.TryParse(reader.GetAttribute("weight"), out int weight) || weight < 400)
                    {
                        continue;
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

                    using (var tagSubtree = reader.ReadSubtree())
                    {
                        while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                        {
                            if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                            {
                                var name = await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                genres.Add(new GenreInfo { Name = name, Weight = weight });
                            }
                        }
                    }
                }
            }

            series.Genres = genres.OrderBy(g => g.Weight).Select(g => g.Name).ToArray();
        }

        private async Task ParseResources(Series series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "resource")
                {
                    var type = reader.GetAttribute("type");
                    switch (type)
                    {
                        case "4":
                            while (reader.Read())
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

        private string StripAniDbLinks(string text)
        {
            return AniDbUrlRegex.Replace(text, "${name}");
        }

        public static string ReplaceNewLine(string text)
        {
            return text.Replace("\n", "<br>");
        }

        private async Task ParseActors(MetadataResult<Series> series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "character")
                    {
                        using (var subtree = reader.ReadSubtree())
                        {
                            await ParseActor(series, subtree).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private async Task ParseActor(MetadataResult<Series> series, XmlReader reader)
        {
            string name = null;
            string role = null;

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
                series.AddPerson(CreatePerson(name, PersonType.Actor, role));
            }
        }

        private void ParseRatings(Series series, XmlReader reader)
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

        private async Task<(string, string)> ParseTitle(XmlReader reader, string preferredMetadataLangauge)
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

            string title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLangauge).Name;
            string originalTitle = titles.Localize(Plugin.Instance.Configuration.OriginalTitlePreference, preferredMetadataLangauge).Name;

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

        private PersonInfo CreatePerson(string name, string type, string role = null)
        {
            // todo find nationality of person and conditionally reverse name order

            if (!_typeMappings.TryGetValue(type, out string mappedType))
            {
                mappedType = type;
            }

            return new PersonInfo
            {
                Name = ReverseNameOrder(name),
                Type = mappedType,
                Role = role
            };
        }

        public static string ReverseNameOrder(string name)
        {
            return name.Split(' ').Reverse().Aggregate(string.Empty, (n, part) => n + " " + part).Trim();
        }

        private static async Task DownloadSeriesData(string aid, string seriesDataPath, string cachePath, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(seriesDataPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            DeleteXmlFiles(directory);

            var httpClient = Plugin.Instance.GetHttpClient();
            var url = string.Format(SeriesQueryUrl, ClientName, aid);

            await RequestLimiter.Tick().ConfigureAwait(false);
            await Task.Delay(Plugin.Instance.Configuration.AniDbRateLimit).ConfigureAwait(false);

            using (var response = await httpClient.GetAsync(url).ConfigureAwait(false))
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            using (var file = File.Open(seriesDataPath, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(file))
            {
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                text = text.Replace("&#x0;", "");

                await writer.WriteAsync(text).ConfigureAwait(false);
            }

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

            using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, settings))
                {
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
                using (var reader = XmlReader.Create(streamReader, settings))
                {
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
            }

            var serializer = new XmlSerializer(typeof(AniDbPersonInfo));
            foreach (var person in cast)
            {
                var path = GetCastPath(person.Name, cachePath);
                var directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);

                if (!File.Exists(path) || person.Image != null)
                {
                    try
                    {
                        using (var stream = File.Open(path, FileMode.Create))
                        {
                            serializer.Serialize(stream, person);
                        }
                    }
                    catch (IOException)
                    {
                        // ignore
                    }
                }
            }
        }

        public static AniDbPersonInfo GetPersonInfo(string cachePath, string name)
        {
            var path = GetCastPath(name, cachePath);
            var serializer = new XmlSerializer(typeof(AniDbPersonInfo));

            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        return serializer.Deserialize(stream) as AniDbPersonInfo;
                    }
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

            using (var writer = XmlWriter.Create(filename, writerSettings))
            {
                await writer.WriteRawAsync(xml).ConfigureAwait(false);
            }
        }

        private static async Task SaveEpsiodeXml(string seriesDataDirectory, string xml)
        {
            var episodeNumber = await ParseEpisodeNumber(xml).ConfigureAwait(false);

            if (episodeNumber != null)
            {
                var file = Path.Combine(seriesDataDirectory, string.Format("episode-{0}.xml", episodeNumber));
                await SaveXml(xml, file);
            }
        }

        private static async Task<string> ParseEpisodeNumber(string xml)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = new StringReader(xml))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, settings))
                {
                    reader.MoveToContent();

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

        private struct GenreInfo
        {
            public string Name;
            public int Weight;
        }
    }

    public class Title
    {
        public string Language { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
    }

    public static class TitleExtensions
    {
        public static Title Localize(this IEnumerable<Title> titles, TitlePreferenceType preference, string metadataLanguage)
        {
            var titlesList = titles as IList<Title> ?? titles.ToList();

            if (preference == TitlePreferenceType.Localized)
            {
                // prefer an official title, else look for a synonym
                var localized = titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "main") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "official") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "synonym");

                if (localized != null)
                {
                    return localized;
                }
            }

            if (preference == TitlePreferenceType.Japanese)
            {
                // prefer an official title, else look for a synonym
                var japanese = titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "main") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "official") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "synonym");

                if (japanese != null)
                {
                    return japanese;
                }
            }

            // return the main title (romaji)
            return titlesList.FirstOrDefault(t => t.Language == "x-jat" && t.Type == "main") ??
                   titlesList.FirstOrDefault(t => t.Type == "main") ??
                   titlesList.FirstOrDefault();
        }
    }
}
