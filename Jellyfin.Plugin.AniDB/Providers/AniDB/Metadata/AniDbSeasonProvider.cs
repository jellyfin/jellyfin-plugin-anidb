using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for seasons.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{AniDbSeasonProvider}"/> interface.</param>
public class AniDbSeasonProvider(IApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<AniDbSeasonProvider> logger) : IRemoteMetadataProvider<Season, SeasonInfo>
{
    private readonly AniDbSeriesProvider _seriesProvider = new AniDbSeriesProvider(appPaths);
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<AniDbSeasonProvider> _logger = logger;

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

        var seriesId = info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        var seasonId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        // Whether the season is the whole of the entry it comes from, or a run of episodes part
        // way into one. A long-running show is often a single AniDB entry that the season
        // numbering breaks into several seasons, and those seasons share none of its dates.
        var wholeEntry = true;

        if (string.IsNullOrEmpty(seasonId) && !string.IsNullOrEmpty(seriesId))
        {
            try
            {
                var segment = await AniDbSeasonResolver.ResolveSeason(appPaths, _libraryManager, seriesId, info.IndexNumber, _logger, cancellationToken).ConfigureAwait(false);

                seasonId = segment?.AnimeId;
                wholeEntry = segment?.FirstEpisodeInEntry <= 1;
            }
            catch (AniDbBannedException ex)
            {
                _logger.LogWarning(
                    "Season {SeasonNumber} of AniDB series {SeriesId} could not be identified because AniDB has banned this client. It stays without metadata until the ban lapses, in {RetryAfter}, and the next refresh after that will fill it in",
                    info.IndexNumber,
                    seriesId,
                    ex.RetryAfter);

                return result;
            }
        }

        if (string.IsNullOrEmpty(seasonId))
        {
            return result;
        }

        _logger.LogDebug(
            "Season {SeasonNumber} of AniDB series {SeriesId} filled from anime {SeasonId}",
            info.IndexNumber,
            seriesId,
            seasonId);

        // Recorded so the episode and image providers see the season's own entry instead of
        // resolving it again.
        result.Item.ProviderIds[ProviderNames.AniDb] = seasonId;

        // Specials have no entry of their own, which is why the id above is the series'. The
        // episodes need it, but the season must not be filled from it: the specials did not
        // air on the series' dates, and are not named or rated as the series is. A season that
        // is one run of episodes out of a longer entry is the same case.
        if (info.IndexNumber <= 0 || !wholeEntry)
        {
            return result;
        }

        var seriesInfo = new SeriesInfo
        {
            MetadataLanguage = info.MetadataLanguage,
            MetadataCountryCode = info.MetadataCountryCode
        };

        seriesInfo.ProviderIds.Add(ProviderNames.AniDb, seasonId);

        var seriesResult = await _seriesProvider.GetMetadata(seriesInfo, cancellationToken).ConfigureAwait(false);
        if (seriesResult.HasMetadata)
        {
            // The first season is the series entry, so taking its title would name the season
            // after the show it sits under. A later season has a title of its own.
            if (Plugin.Instance.Configuration.UseAniDbSeasonNames
                && !string.Equals(seasonId, seriesId, StringComparison.Ordinal))
            {
                result.Item.Name = seriesResult.Item.Name;
            }

            result.Item.Overview = seriesResult.Item.Overview;
            result.Item.PremiereDate = seriesResult.Item.PremiereDate;
            result.Item.EndDate = seriesResult.Item.EndDate;
            result.Item.CommunityRating = seriesResult.Item.CommunityRating;
            result.Item.Studios = seriesResult.Item.Studios;
            result.Item.Genres = seriesResult.Item.Genres;
            result.Item.Tags = seriesResult.Item.Tags;
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
