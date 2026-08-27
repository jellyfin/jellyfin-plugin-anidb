using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for seasons.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbSeasonProvider(IApplicationPaths appPaths) : IRemoteMetadataProvider<Season, SeasonInfo>
{
    private readonly AniDbSeriesProvider _seriesProvider = new AniDbSeriesProvider(appPaths);

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season>
        {
            HasMetadata = true,
            Item = new Season
            {
                Name = info.Name,
                IndexNumber = info.IndexNumber
            }
        };

        var seriesId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);
        if (seriesId == null)
        {
            return result;
        }

        var seriesInfo = new SeriesInfo();
        seriesInfo.ProviderIds.Add(ProviderNames.AniDb, seriesId);

        var seriesResult = await _seriesProvider.GetMetadata(seriesInfo, cancellationToken).ConfigureAwait(false);
        if (seriesResult.HasMetadata)
        {
            result.Item.Name = seriesResult.Item.Name;
            result.Item.Overview = seriesResult.Item.Overview;
            result.Item.PremiereDate = seriesResult.Item.PremiereDate;
            result.Item.EndDate = seriesResult.Item.EndDate;
            result.Item.CommunityRating = seriesResult.Item.CommunityRating;
            result.Item.Studios = seriesResult.Item.Studios;
            result.Item.Genres = seriesResult.Item.Genres;
        }

        return result;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
