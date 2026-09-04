using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB image provider for people.
/// </summary>
/// <param name="paths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbPersonImageProvider(IApplicationPaths paths) : IRemoteImageProvider
{
    private readonly IApplicationPaths _paths = paths;

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Person;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var infos = new List<RemoteImageInfo>();

        var person = AniDbSeriesProvider.GetPersonInfo(_paths.CachePath, item.Name);
        if (person != null && !string.IsNullOrEmpty(person.Image))
        {
            infos.Add(new RemoteImageInfo
            {
                Url = person.Image,
                Type = ImageType.Primary,
                ProviderName = Name
            });
        }

        return Task.FromResult<IEnumerable<RemoteImageInfo>>(infos);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        await AniDbSeriesProvider.WaitForImageSlot(cancellationToken).ConfigureAwait(false);
        var httpClient = Plugin.Instance.GetHttpClient();

        return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }
}
