using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Anime.Providers.AniDB
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
            => "https://anidb.net/perl-bin/animedb.pl?show=anime&aid={0}";
    }
}
