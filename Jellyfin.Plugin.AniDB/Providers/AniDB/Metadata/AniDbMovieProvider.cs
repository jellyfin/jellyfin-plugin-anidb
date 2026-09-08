using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for movies.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{AniDbMovieProvider}"/> interface.</param>
public class AniDbMovieProvider(IApplicationPaths appPaths, ILogger<AniDbMovieProvider>? logger = null) : IRemoteMetadataProvider<Movie, MovieInfo>
{
    private readonly AniDbSeriesProvider _seriesProvider = new AniDbSeriesProvider(appPaths);
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var animeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        MappedMovie? mapped = null;

        // An id another provider has already settled on names the movie outright, where the name
        // is the weaker evidence: AniDB spells a great many titles differently, and a movie shares
        // its title with the series it was made from as often as not.
        if (string.IsNullOrEmpty(animeId))
        {
            mapped = await AniDbMappings.ResolveMovieId(
                appPaths,
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb)),
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Imdb)),
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb)),
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (mapped != null)
            {
                animeId = mapped.Episode.AnimeId;

                _logger.LogInformation(
                    "{MovieName} is {EpisodeNumberInEntry} of AniDB anime {AnimeId}, which {Source} file under {Provider} {ProviderId}",
                    info.Name,
                    mapped.Episode.Kind.Prefix() + mapped.Episode.Number,
                    mapped.Episode.AnimeId,
                    mapped.Source,
                    mapped.Provider,
                    mapped.ProviderId);
            }
        }

        var seriesInfo = new SeriesInfo();
        seriesInfo.ProviderIds.Add(ProviderNames.AniDb, animeId);

        if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
        {
            animeId = await Equals_check.XmlFindId(info.Name, info.Year ?? info.PremiereDate?.Year, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(animeId))
        {
            var seriesResult = await _seriesProvider.GetMetadataForId(animeId, seriesInfo, cancellationToken).ConfigureAwait(false);

            if (seriesResult.HasMetadata)
            {
                var movie = new Movie
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
                    Tags = seriesResult.Item.Tags,
                    ProviderIds = seriesResult.Item.ProviderIds
                };

                if (mapped != null)
                {
                    await ReadFromEpisode(movie, mapped, info.MetadataLanguage).ConfigureAwait(false);
                }

                return new MetadataResult<Movie>
                {
                    HasMetadata = true,
                    Item = movie,
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

    /// <summary>
    /// Takes what an entry records about the one episode a movie is, over what it records about
    /// itself.
    /// </summary>
    /// <remarks>
    /// A movie AniDB registered in its own right is its entry's first ordinary episode, and the
    /// entry's own record is already the movie's: there is nothing to take. The rest - a movie
    /// AniDB holds among another entry's other episodes or specials, which is how it holds
    /// Berserk's Memorial Edition and the theatrical cuts of several television series - would
    /// otherwise be given the name, date and running time of whatever it was released
    /// alongside, and a library holding several such movies of one show would show them as
    /// several copies of the same thing.
    /// <para>
    /// The cast, studios, genres and rating are left as the entry's. They belong to the
    /// production rather than to the episode, and AniDB records none of them per episode.
    /// </para>
    /// </remarks>
    /// <param name="movie">The movie, already filled from its entry.</param>
    /// <param name="mapped">What identified it, and which episode of the entry it is.</param>
    /// <param name="preferredMetadataLanguage">The language its title is wanted in.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task ReadFromEpisode(Movie movie, MappedMovie mapped, string preferredMetadataLanguage)
    {
        if (mapped.Episode.Kind == AniDbEpisodeKind.Regular && mapped.Episode.Number <= 1)
        {
            return;
        }

        var folder = AniDbSeriesProvider.GetSeriesDataPath(appPaths, mapped.Episode.AnimeId);
        var xml = AniDbEpisodeProvider.GetEpisodeXmlFile(mapped.Episode.Number, mapped.Episode.Kind.Prefix(), folder);

        if (xml?.Exists != true)
        {
            _logger.LogWarning(
                "{MovieName} is {EpisodeNumberInEntry} of AniDB anime {AnimeId}, where {Source} place it, but that entry records no such episode, so the movie is described by the entry as a whole",
                movie.Name,
                mapped.Episode.Kind.Prefix() + mapped.Episode.Number,
                mapped.Episode.AnimeId,
                mapped.Source);

            return;
        }

        // Read into an episode of its own and copied across, rather than parsed straight onto
        // the movie: the reader fills in what the document holds and says nothing about the rest,
        // and an episode with no summary of its own must not empty the movie's.
        var episode = new Episode();

        await AniDbEpisodeProvider.ParseEpisodeXml(xml, episode, preferredMetadataLanguage).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(episode.Name))
        {
            movie.Name = episode.Name;
        }

        if (!string.IsNullOrEmpty(episode.Overview))
        {
            movie.Overview = episode.Overview;
        }

        if (episode.PremiereDate != null)
        {
            movie.PremiereDate = episode.PremiereDate;
            movie.ProductionYear = episode.PremiereDate.Value.Year;
        }

        if (episode.RunTimeTicks is > 0)
        {
            movie.RunTimeTicks = episode.RunTimeTicks;
        }

        _logger.LogDebug(
            "{MovieName} is named and dated from {EpisodeNumberInEntry} of AniDB anime {AnimeId} rather than from the entry as a whole",
            movie.Name,
            mapped.Episode.Kind.Prefix() + mapped.Episode.Number,
            mapped.Episode.AnimeId);
    }
}
