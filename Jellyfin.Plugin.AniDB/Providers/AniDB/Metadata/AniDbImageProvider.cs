using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    /// <summary>
    /// The AniDB image provider for series, seasons and movies.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public class AniDbImageProvider(IApplicationPaths appPaths) : IRemoteImageProvider
    {
        private readonly IApplicationPaths _appPaths = appPaths;

        /// <inheritdoc />
        public string Name => "AniDB";

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            await AniDbSeriesProvider.WaitForRequestSlot(cancellationToken).ConfigureAwait(false);
            var httpClient = Plugin.Instance.GetHttpClient();

            return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var seriesId = item.GetProviderId(ProviderNames.AniDb);
            return GetImages(seriesId, cancellationToken);
        }

        /// <summary>
        /// Gets the available images for the given AniDB id.
        /// </summary>
        /// <param name="aniDbId">The AniDB id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The available images.</returns>
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(string? aniDbId, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            if (!string.IsNullOrEmpty(aniDbId))
            {
                var seriesDataPath = await AniDbSeriesProvider.GetSeriesData(_appPaths, aniDbId, cancellationToken).ConfigureAwait(false);
                var imageUrl = await FindImageUrl(seriesDataPath).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(imageUrl))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Url = imageUrl
                    });
                }
            }

            return list;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Series || item is Season || item is Movie;
        }

        private static async Task<string?> FindImageUrl(string seriesDataPath)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8);
            using XmlReader reader = XmlReader.Create(streamReader, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "picture")
                {
                    return "https://cdn.anidb.net/images/main/" + await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                }
            }

            return null;
        }
    }
}
