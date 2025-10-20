using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB;

/// <summary>
/// External url provider for AniDB.
/// </summary>
public class AniDbExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId(ProviderNames.AniDb, out var externalId))
        {
            switch (item)
            {
                case Series:
                case Movie:
                    yield return $"https://anidb.net/anime/{externalId}";
                    break;
                case Episode:
                    yield return $"https://anidb.net/episode/{externalId}";
                    break;
            }
        }
    }
}
