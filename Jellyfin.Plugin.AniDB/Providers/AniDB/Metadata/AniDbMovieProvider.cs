using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for movies.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbMovieProvider(IApplicationPaths appPaths) : IRemoteMetadataProvider<Movie, MovieInfo>
{
    private readonly AniDbSeriesProvider _seriesProvider = new AniDbSeriesProvider(appPaths);

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var animeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        var seriesInfo = new SeriesInfo();
        seriesInfo.ProviderIds.Add(ProviderNames.AniDb, animeId);

        if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
        {
            animeId = await Equals_check.XmlFindId(info.Name, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(animeId))
        {
            var seriesResult = await _seriesProvider.GetMetadataForId(animeId, seriesInfo, cancellationToken).ConfigureAwait(false);

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

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var seriesInfo = new SeriesInfo();
        var animeId = searchInfo.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        if (animeId != null)
        {
            seriesInfo.ProviderIds.Add(ProviderNames.AniDb, animeId);
        }

        seriesInfo.Name = searchInfo.Name;

        return await _seriesProvider.GetSearchResults(seriesInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _seriesProvider.GetImageResponse(url, cancellationToken);
    }
}
