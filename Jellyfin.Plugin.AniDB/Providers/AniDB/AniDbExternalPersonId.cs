using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB;

/// <summary>
/// The AniDB external id for people.
/// </summary>
public class AniDbExternalPersonId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName
        => "AniDB";

    /// <inheritdoc />
    public string Key
        => ProviderNames.AniDb;

    /// <inheritdoc />
    public ExternalIdMediaType? Type
        => ExternalIdMediaType.Person;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item)
        => item is Person;
}
