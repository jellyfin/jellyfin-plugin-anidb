using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    public class AniDbSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly AniDbSeriesProvider _seriesProvider;

        public string Name => "AniDB";

        public AniDbSeasonProvider(IApplicationPaths appPaths)
        {
            _seriesProvider = new AniDbSeriesProvider(appPaths);
        }

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var animeId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            // Get animeId from parent (series) if appropriate
            if (string.IsNullOrEmpty(animeId) && (info.IndexNumber == 1 || (Plugin.Instance.Configuration.IgnoreSeason && info.IndexNumber > 0)))
            {
                animeId = info.SeriesProviderIds.GetOrDefault(ProviderNames.AniDb);
            }

            /* if (!string.IsNullOrEmpty(animeId))
            {
                var seriesResult = await _seriesProvider.GetMetadataForId(animeId, info.MetadataLanguage, cancellationToken);

                if (seriesResult.HasMetadata)
                {
                    return new MetadataResult<Season>
                    {
                        HasMetadata = true,
                        Item = new Season
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
            } */

            return new MetadataResult<Season>();
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);

            var list = new List<RemoteSearchResult>();

            if (metadata.HasMetadata)
            {
                var res = new RemoteSearchResult
                {
                    Name = metadata.Item.Name,
                    PremiereDate = metadata.Item.PremiereDate,
                    ProductionYear = metadata.Item.ProductionYear,
                    ProviderIds = metadata.Item.ProviderIds,
                    SearchProviderName = Name
                };

                list.Add(res);
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
