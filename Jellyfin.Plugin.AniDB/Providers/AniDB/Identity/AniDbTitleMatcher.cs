using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anime.Providers.AniDB.Identity
{
    /// <summary>
    /// The <see cref="AniDbTitleMatcher"/> class loads series titles from the series.xml file in the application data anidb folder,
    /// and provides the means to search for a the AniDB of a series by series title.
    /// </summary>
    public class AniDbTitleMatcher : IAniDbTitleMatcher
    {
        public enum TitleType
        {
            Main = 0,
            Official = 1,
            Short = 2,
            Synonym = 3
        }

        public struct TitleInfo
        {
            public string AniDbId { get; set; }
            public string Title { get; set; }
            public TitleType Type { get; set; }
        }

        //todo replace the singleton IAniDbTitleMatcher with an injected dependency if/when MediaBrowser allows plugins to register their own components
        /// <summary>
        /// Gets or sets the global <see cref="IAniDbTitleMatcher"/> instance.
        /// </summary>
        public static IAniDbTitleMatcher DefaultInstance { get; set; }

        public readonly IAniDbTitleDownloader _downloader;
        private readonly ILogger<AniDbTitleMatcher> _logger;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public static Dictionary<string, TitleInfo> _titles;

        /// <summary>
        /// Creates a new instance of the AniDbTitleMatcher class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="downloader">The AniDB title downloader.</param>
        public AniDbTitleMatcher(ILogger<AniDbTitleMatcher> logger, IAniDbTitleDownloader downloader)
        {
            _logger = logger;
            _downloader = downloader;
        }

        public Task<string> FindSeries(string title)
        {
            return FindSeries(title, CancellationToken.None);
        }

        public async Task<string> FindSeries(string title, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsLoaded)
                {
                    await Load(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _lock.Release();
            }

            return LookupAniDbId(title) ?? LookupAniDbId(GetComparableName(title));
        }

        private string LookupAniDbId(string title)
        {
            if (_titles.TryGetValue(title, out TitleInfo info))
            {
                return info.AniDbId;
            }

            return null;
        }

        public static TitleInfo GetTitleInfos(string title)
        {
            if (!string.IsNullOrEmpty(title)
                && _titles.TryGetValue(title, out TitleInfo info))
            {
                return info;
            }

            return new TitleInfo();
        }

        private const string Remove = "\"'!`?";
        private const string Spacers = "/,.:;\\(){}[]+-_=–*";  // (there are not actually two - in the they are different char codes)

        internal static string GetComparableName(string name)
        {
            name = name.ToLowerInvariant();
            name = name.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (c >= 0x2B0 && c <= 0x0333)
                {
                    // skip char modifier and diacritics
                }
                else if (Remove.IndexOf(c) > -1)
                {
                    // skip chars we are removing
                }
                else if (Spacers.IndexOf(c) > -1)
                {
                    sb.Append(" ");
                }
                else if (c == '&')
                {
                    sb.Append(" and ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            name = sb.ToString();
            name = name.Replace(", the", "");
            name = name.Replace("the ", " ");
            name = name.Replace(" the ", " ");

            string prevName;
            do
            {
                prevName = name;
                name = name.Replace("  ", " ");
            } while (name.Length != prevName.Length);

            return name.Trim();
        }

        public bool IsLoaded
        {
            get { return _titles != null; }
        }

        private async Task Load(CancellationToken cancellationToken)
        {
            if (_titles == null)
            {
                _titles = new Dictionary<string, TitleInfo>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _titles.Clear();
            }

            try
            {
                await _downloader.Load(cancellationToken).ConfigureAwait(false);
                await ReadTitlesFile().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load AniDB titles");
            }
        }

        private async Task ReadTitlesFile()
        {
            _logger.LogDebug("Loading AniDB titles");

            var titlesFile = _downloader.TitlesFilePath;

            var settings = new XmlReaderSettings
            {
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var stream = new StreamReader(titlesFile, Encoding.UTF8))
            using (var reader = XmlReader.Create(stream, settings))
            {
                string aid = null;

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "anime":
                                reader.MoveToAttribute("aid");
                                aid = reader.Value;
                                break;

                            case "title":
                                var title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(aid) && !string.IsNullOrEmpty(title))
                                {
                                    var type = ParseType(reader.GetAttribute("type"));

                                    if (!_titles.TryGetValue(title, out TitleInfo currentTitleInfo) || (int)currentTitleInfo.Type < (int)type)
                                    {
                                        _titles[title] = new TitleInfo { AniDbId = aid, Type = type, Title = title };
                                    }
                                }
                                break;
                        }
                    }
                }
            }

            var comparable = (from pair in _titles
                              let comp = GetComparableName(pair.Key)
                              where !_titles.ContainsKey(comp)
                              select new { Title = comp, Id = pair.Value })
                             .ToArray();

            foreach (var pair in comparable)
            {
                _titles[pair.Title] = pair.Id;
            }
        }

        private TitleType ParseType(string type)
        {
            switch (type)
            {
                case "main":
                    return TitleType.Main;

                case "official":
                    return TitleType.Official;

                case "short":
                    return TitleType.Short;

                case "syn":
                    return TitleType.Synonym;
            }

            return TitleType.Synonym;
        }
    }
}
