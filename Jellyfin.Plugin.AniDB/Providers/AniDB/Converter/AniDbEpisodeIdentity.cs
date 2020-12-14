using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Anime.Providers.AniDB.Converter
{
    public struct AniDbEpisodeIdentity
    {
        private static readonly Regex _regex = new Regex(@"(?<series>\d+):(?<type>[S])?(?<epno>\d+)(-(?<epnoend>\d+))?");

        public AniDbEpisodeIdentity(string id)
        {
            this = Parse(id).Value;
        }

        public AniDbEpisodeIdentity(string seriesId, int episodeNumber, int? episodeNumberEnd, string episodeType)
        {
            SeriesId = seriesId;
            EpisodeNumber = episodeNumber;
            EpisodeNumberEnd = episodeNumberEnd;
            EpisodeType = episodeType;
        }

        public string SeriesId { get; private set; }
        public int EpisodeNumber { get; private set; }
        public int? EpisodeNumberEnd { get; private set; }
        public string EpisodeType { get; private set; }

        public override string ToString()
        {
            return string.Format("{0}:{1}{2}",
                SeriesId,
                EpisodeType ?? "",
                EpisodeNumber + (EpisodeNumberEnd != null ? "-" + EpisodeNumberEnd.Value.ToString() : ""));
        }

        public static AniDbEpisodeIdentity? Parse(string id)
        {
            var match = _regex.Match(id);
            if (match.Success)
            {
                return new AniDbEpisodeIdentity(
                    match.Groups["series"].Value,
                    int.Parse(match.Groups["epno"].Value),
                    match.Groups["epnoend"].Success ? int.Parse(match.Groups["epnoend"].Value) : (int?)null,
                    match.Groups["type"].Success ? match.Groups["type"].Value : null);
            }

            return null;
        }
    }
}
