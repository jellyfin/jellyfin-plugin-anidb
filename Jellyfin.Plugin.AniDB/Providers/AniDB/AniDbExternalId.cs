using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB
{
    public class AniDbExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName
            => "AniDB";

        public string Key
            => ProviderNames.AniDb;

        public ExternalIdMediaType? Type
            => null;

        public string UrlFormatString
            => "https://anidb.net/anime/{0}";
    }

    public class AniDbExternalEpisodeId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Episode;

        public string ProviderName
            => "AniDB";

        public string Key
            => ProviderNames.AniDb;

        public ExternalIdMediaType? Type
            => null;

        public string UrlFormatString
            => "https://anidb.net/episode/{0}";
    }
}
