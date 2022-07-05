using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    public class AniDbMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly AniDbSeriesProvider _seriesProvider;
        private readonly ILogger<AniDbMovieProvider> _logger;

        public string Name => "AniDB";

        public AniDbMovieProvider(IApplicationPaths appPaths, ILogger<AniDbMovieProvider> logger)
        {
            _seriesProvider = new AniDbSeriesProvider(appPaths);
            _logger = logger;
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var animeId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            var seriesInfo = new SeriesInfo();
            seriesInfo.ProviderIds.Add(ProviderNames.AniDb, animeId);

            if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
            {
                animeId = await Equals_check.XmlFindId(info.Name, cancellationToken);
            }

            if (!string.IsNullOrEmpty(animeId))
            {
                var seriesResult = await _seriesProvider.GetMetadataForId(animeId, seriesInfo, cancellationToken);

                if (seriesResult.HasMetadata)
                {
                    return new MetadataResult<Movie>
                    {
                        HasMetadata = true,
                        Item = new Movie
                        {
                            Name = seriesResult.Item.Name,
                            OriginalTitle = seriesResult.Item.OriginalTitle,
                            Overview = seriesResult.Item.Overview,
                            PremiereDate = seriesResult.Item.PremiereDate,
                            ProductionYear = seriesResult.Item.ProductionYear,
                            EndDate = seriesResult.Item.EndDate,
                            CommunityRating = seriesResult.Item.CommunityRating,
                            Studios = seriesResult.Item.Studios,
                            Genres = seriesResult.Item.Genres,
                            ProviderIds = seriesResult.Item.ProviderIds
                        },
                        People = seriesResult.People,
                        Images = seriesResult.Images
                    };
                }
            }

            return new MetadataResult<Movie>();
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var seriesInfo = new SeriesInfo();
            var animeId = searchInfo.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            if (animeId != null)
            {
                seriesInfo.ProviderIds.Add(ProviderNames.AniDb, animeId);
            }

            seriesInfo.Name = searchInfo.Name;

            return await _seriesProvider.GetSearchResults(seriesInfo, cancellationToken);
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _seriesProvider.GetImageResponse(url, cancellationToken);
        }
    }
}
