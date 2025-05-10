using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB
{
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
    }
}
