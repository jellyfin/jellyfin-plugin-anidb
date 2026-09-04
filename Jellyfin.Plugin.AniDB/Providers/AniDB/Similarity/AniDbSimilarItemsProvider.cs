using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Similarity;

/// <summary>
/// Offers the anime AniDB's users hold to be like the show or film being looked at.
/// </summary>
/// <remarks>
/// <para>
/// The votes come free with the documents the metadata providers already downloaded, so nothing
/// here spends an AniDB request, and an anime whose document was never fetched simply has nothing
/// to say.
/// </para>
/// <para>
/// This resolves the anime it is offered against the library itself rather than handing the ids
/// back for Jellyfin to resolve, because Jellyfin looks only at items of the same kind as the one
/// asked about. AniDB gives every season its own entry, so a show carries the id of its first
/// entry and each of its seasons carries its own: most of what one anime is named alike is
/// therefore held in a library as a season of some other show, which a search across shows alone
/// would never find.
/// </para>
/// </remarks>
public sealed class AniDbSimilarItemsProvider : ILocalSimilarItemsProvider<Series>, ILocalSimilarItemsProvider<Movie>, IBatchLocalSimilarItemsProvider
{
    /// <summary>
    /// What Jellyfin asks for when a caller names no limit of its own.
    /// </summary>
    private const int DefaultLimit = 50;

    /// <summary>
    /// The most baseline items one batch is answered for, matching what Jellyfin's own batch
    /// provider accepts, so that a caller cannot size the queries below off its own input.
    /// </summary>
    private const int MaxBatchSourceItems = 64;

    /// <summary>
    /// The most anime ids looked for in one library query, taken best first. A batch of baselines
    /// can otherwise name several thousand between them.
    /// </summary>
    private const int MaxCandidatesPerQuery = 500;

    /// <summary>
    /// Whether a batch is already being answered on behalf of another provider. Two plugins that
    /// both stand in for the provider behind them would otherwise hand the same batch back and
    /// forth until the stack ran out.
    /// </summary>
    private static readonly AsyncLocal<bool> _standingIn = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ISimilarItemsManager _similarItemsManager;
    private readonly ILogger<AniDbSimilarItemsProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbSimilarItemsProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="similarItemsManager">The similar items manager, asked for the provider to fall back on.</param>
    /// <param name="logger">The logger.</param>
    public AniDbSimilarItemsProvider(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ISimilarItemsManager similarItemsManager,
        ILogger<AniDbSimilarItemsProvider> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _similarItemsManager = similarItemsManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ProviderNames.AniDb;

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    /// <summary>
    /// Gets the anime like the given show.
    /// </summary>
    /// <param name="item">The show to find anime like.</param>
    /// <param name="query">The query options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items found, best first.</returns>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(Series item, SimilarItemsQuery query, CancellationToken cancellationToken)
        => GetForOne(item, query, cancellationToken);

    /// <summary>
    /// Gets the anime like the given film.
    /// </summary>
    /// <param name="item">The film to find anime like.</param>
    /// <param name="query">The query options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items found, best first.</returns>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(Movie item, SimilarItemsQuery query, CancellationToken cancellationToken)
        => GetForOne(item, query, cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<Guid, IReadOnlyList<BaseItem>>> GetBatchSimilarItemsAsync(
        IReadOnlyList<BaseItem> sourceItems,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(query);

        var results = new Dictionary<Guid, IReadOnlyList<BaseItem>>();
        var limit = query.Limit ?? DefaultLimit;

        if (sourceItems.Count > MaxBatchSourceItems)
        {
            sourceItems = [.. sourceItems.Take(MaxBatchSourceItems)];
        }

        if (Plugin.Instance.Configuration.EnableSimilarItems)
        {
            await AddAniDbItems(sourceItems, query, limit, results, cancellationToken).ConfigureAwait(false);
        }

        await FillFromOtherProvider(sourceItems, query, limit, results, cancellationToken).ConfigureAwait(false);

        return results;
    }

    bool ILocalSimilarItemsProvider.Supports(Type itemType)
        => typeof(Series).IsAssignableFrom(itemType) || typeof(Movie).IsAssignableFrom(itemType);

    Task<IReadOnlyList<BaseItem>> ILocalSimilarItemsProvider.GetSimilarItemsAsync(BaseItem item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item switch
        {
            Series series => GetSimilarItemsAsync(series, query, cancellationToken),
            Movie movie => GetSimilarItemsAsync(movie, query, cancellationToken),
            _ => throw new ArgumentException($"Unsupported item type {item.GetType()}", nameof(item))
        };
    }

    private async Task<IReadOnlyList<BaseItem>> GetForOne(BaseItem item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);

        if (!Plugin.Instance.Configuration.EnableSimilarItems)
        {
            return [];
        }

        var results = new Dictionary<Guid, IReadOnlyList<BaseItem>>();

        await AddAniDbItems([item], query, query.Limit ?? DefaultLimit, results, cancellationToken).ConfigureAwait(false);

        return results.TryGetValue(item.Id, out var items) ? items : [];
    }

    private async Task AddAniDbItems(
        IReadOnlyList<BaseItem> sourceItems,
        SimilarItemsQuery query,
        int limit,
        Dictionary<Guid, IReadOnlyList<BaseItem>> results,
        CancellationToken cancellationToken)
    {
        var seeds = new Dictionary<Guid, IReadOnlySet<string>>();

        foreach (var source in sourceItems)
        {
            var animeIds = GetSeedAnimeIds(source, query);

            if (animeIds.Count > 0)
            {
                seeds[source.Id] = animeIds;
            }
        }

        if (seeds.Count == 0)
        {
            return;
        }

        var firstHop = new Dictionary<Guid, IReadOnlyList<AniDbRankedAnime>>();

        foreach (var (sourceId, animeIds) in seeds)
        {
            var ranked = await AniDbSimilarityRanker.FirstHop(_appPaths, animeIds, cancellationToken).ConfigureAwait(false);

            if (ranked.Count > 0)
            {
                firstHop[sourceId] = ranked;
            }
        }

        if (firstHop.Count == 0)
        {
            return;
        }

        var excludeIds = new HashSet<Guid>(query.ExcludeItemIds);
        var resolved = ResolveAnimeIds(CollectCandidates(firstHop.Values), query);
        var picks = new Dictionary<Guid, Picks>();

        foreach (var source in sourceItems)
        {
            if (!firstHop.TryGetValue(source.Id, out var ranked))
            {
                continue;
            }

            var pick = new Picks(source, excludeIds);

            Take(pick, ranked, resolved, limit);
            picks[source.Id] = pick;
        }

        await ExpandShortPicks(picks, seeds, firstHop, query, limit, cancellationToken).ConfigureAwait(false);

        foreach (var (sourceId, pick) in picks)
        {
            if (pick.Items.Count > 0)
            {
                results[sourceId] = pick.Items;
            }
        }
    }

    /// <summary>
    /// Follows the ranking a step further for the items a library held too little of to fill a
    /// row. Held apart from the first pass so that the anime reached this way are looked for in
    /// one query across every item that needs them.
    /// </summary>
    private async Task ExpandShortPicks(
        Dictionary<Guid, Picks> picks,
        Dictionary<Guid, IReadOnlySet<string>> seeds,
        Dictionary<Guid, IReadOnlyList<AniDbRankedAnime>> firstHop,
        SimilarItemsQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        var shortOfLimit = picks.Where(pick => pick.Value.Items.Count < limit).Select(pick => pick.Key).ToList();

        if (shortOfLimit.Count == 0)
        {
            return;
        }

        var secondHop = new Dictionary<Guid, IReadOnlyList<AniDbRankedAnime>>();

        foreach (var sourceId in shortOfLimit)
        {
            var ranked = firstHop[sourceId];
            var exclude = new HashSet<string>(seeds[sourceId], StringComparer.Ordinal);

            exclude.UnionWith(ranked.Select(entry => entry.AnimeId));

            var expanded = await AniDbSimilarityRanker.SecondHop(_appPaths, ranked, exclude, cancellationToken).ConfigureAwait(false);

            if (expanded.Count > 0)
            {
                secondHop[sourceId] = expanded;
            }
        }

        if (secondHop.Count == 0)
        {
            return;
        }

        var resolved = ResolveAnimeIds(CollectCandidates(secondHop.Values), query);

        foreach (var (sourceId, expanded) in secondHop)
        {
            Take(picks[sourceId], expanded, resolved, limit);
        }
    }

    /// <summary>
    /// Hands the items this provider could not answer for to the provider that would have been
    /// asked in its place.
    /// </summary>
    /// <remarks>
    /// Jellyfin asks only the first batch provider it finds for the rows on the recommendations
    /// screen, and a plugin's providers are found before its own. Without this, installing the
    /// plugin would empty those rows for every film in the library that is not anime.
    /// </remarks>
    private async Task FillFromOtherProvider(
        IReadOnlyList<BaseItem> sourceItems,
        SimilarItemsQuery query,
        int limit,
        Dictionary<Guid, IReadOnlyList<BaseItem>> results,
        CancellationToken cancellationToken)
    {
        if (_standingIn.Value)
        {
            return;
        }

        var unanswered = sourceItems
            .Where(source => !results.TryGetValue(source.Id, out var items) || items.Count < limit)
            .ToList();

        if (unanswered.Count == 0)
        {
            return;
        }

        // The batch interface is used for films alone, so the film providers are what it stands in
        // the way of.
        var fallback = _similarItemsManager.GetSimilarItemsProviders<Movie>()
            .OfType<IBatchLocalSimilarItemsProvider>()
            .FirstOrDefault(provider => !ReferenceEquals(provider, this));

        if (fallback is null)
        {
            return;
        }

        Dictionary<Guid, IReadOnlyList<BaseItem>> filler;

        _standingIn.Value = true;

        try
        {
            filler = await fallback.GetBatchSimilarItemsAsync(unanswered, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The similar items provider {ProviderName} failed to answer for what AniDB could not", fallback.Name);

            return;
        }
        finally
        {
            _standingIn.Value = false;
        }

        foreach (var (sourceId, items) in filler)
        {
            if (items.Count == 0)
            {
                continue;
            }

            if (!results.TryGetValue(sourceId, out var picked) || picked.Count == 0)
            {
                results[sourceId] = items.Count > limit ? [.. items.Take(limit)] : items;

                continue;
            }

            var merged = new List<BaseItem>(picked);
            var mergedIds = new HashSet<Guid>(picked.Select(item => item.Id));

            foreach (var item in items)
            {
                if (merged.Count >= limit)
                {
                    break;
                }

                if (mergedIds.Add(item.Id))
                {
                    merged.Add(item);
                }
            }

            results[sourceId] = merged;
        }
    }

    /// <summary>
    /// The AniDB entries a library item is made of. A show carries the id of its first entry
    /// only, and AniDB records what an anime is like against each entry separately, so a show is
    /// asked about under every entry its seasons were filled from as well.
    /// </summary>
    private IReadOnlySet<string> GetSeedAnimeIds(BaseItem item, SimilarItemsQuery query)
    {
        var seeds = new HashSet<string>(StringComparer.Ordinal);

        if (item.TryGetProviderId(ProviderNames.AniDb, out var animeId))
        {
            seeds.Add(animeId);
        }

        if (item is Series series)
        {
            var seasons = _libraryManager.GetItemList(new InternalItemsQuery(query.User)
            {
                Parent = series,
                IncludeItemTypes = [BaseItemKind.Season],
                DtoOptions = query.DtoOptions ?? new DtoOptions(true),
                EnableTotalRecordCount = false
            });

            foreach (var season in seasons)
            {
                if (season.TryGetProviderId(ProviderNames.AniDb, out var seasonAnimeId))
                {
                    seeds.Add(seasonAnimeId);
                }
            }
        }

        return seeds;
    }

    /// <summary>
    /// The anime ids to look for in the library, best first and capped, so that a batch cannot put
    /// a query together out of every id its baselines named between them.
    /// </summary>
    private static IReadOnlyCollection<string> CollectCandidates(IEnumerable<IReadOnlyList<AniDbRankedAnime>> rankings)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var ranking in rankings)
        {
            foreach (var entry in ranking)
            {
                if (!best.TryGetValue(entry.AnimeId, out var score) || score < entry.Score)
                {
                    best[entry.AnimeId] = entry.Score;
                }
            }
        }

        if (best.Count <= MaxCandidatesPerQuery)
        {
            return best.Keys;
        }

        return [.. best
            .OrderByDescending(entry => entry.Value)
            .Take(MaxCandidatesPerQuery)
            .Select(entry => entry.Key)];
    }

    /// <summary>
    /// Finds what a library holds of the given anime, as the items a library screen can show: a
    /// season is answered for by the show it belongs to, that being where its episodes are found.
    /// </summary>
    private Dictionary<string, List<BaseItem>> ResolveAnimeIds(IReadOnlyCollection<string> animeIds, SimilarItemsQuery query)
    {
        var resolved = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);

        if (animeIds.Count == 0)
        {
            return resolved;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery(query.User)
        {
            HasAnyProviderIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [ProviderNames.AniDb] = [.. animeIds]
            },
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Movie],
            DtoOptions = query.DtoOptions ?? new DtoOptions(true),
            EnableTotalRecordCount = false
        });

        foreach (var item in items)
        {
            if (!item.TryGetProviderId(ProviderNames.AniDb, out var animeId))
            {
                continue;
            }

            var offered = item is Season season ? _libraryManager.GetItemById(season.SeriesId) : item;

            if (offered is null)
            {
                continue;
            }

            if (!resolved.TryGetValue(animeId, out var forAnime))
            {
                forAnime = [];
                resolved[animeId] = forAnime;
            }

            // Several of a show's seasons can be filled from one entry, and several films can be
            // episodes of one, so an id can be reached more than once.
            if (!forAnime.Exists(existing => existing.Id == offered.Id))
            {
                forAnime.Add(offered);
            }
        }

        return resolved;
    }

    private static void Take(Picks pick, IReadOnlyList<AniDbRankedAnime> ranked, Dictionary<string, List<BaseItem>> resolved, int limit)
    {
        foreach (var entry in ranked)
        {
            if (pick.Items.Count >= limit)
            {
                return;
            }

            if (!resolved.TryGetValue(entry.AnimeId, out var items))
            {
                continue;
            }

            foreach (var item in items)
            {
                if (pick.Items.Count >= limit)
                {
                    return;
                }

                pick.Offer(item);
            }
        }
    }

    /// <summary>
    /// What has been picked for one item so far, and what may not be picked for it.
    /// </summary>
    private sealed class Picks
    {
        private readonly BaseItem _source;
        private readonly HashSet<Guid> _excludeIds;
        private readonly HashSet<Guid> _picked = [];
        private readonly HashSet<string> _pickedKeys = new(StringComparer.OrdinalIgnoreCase);

        public Picks(BaseItem source, HashSet<Guid> excludeIds)
        {
            _source = source;
            _excludeIds = excludeIds;
            _pickedKeys.Add(source.GetPresentationUniqueKey());
        }

        public List<BaseItem> Items { get; } = [];

        /// <summary>
        /// Adds an item unless it is the item asked about, one the caller ruled out, or one
        /// already picked. A show reached over two of its seasons is one offer.
        /// </summary>
        /// <param name="item">The item offered.</param>
        public void Offer(BaseItem item)
        {
            if (item.Id.Equals(_source.Id) || _excludeIds.Contains(item.Id))
            {
                return;
            }

            if (!_picked.Add(item.Id))
            {
                return;
            }

            if (!_pickedKeys.Add(item.GetPresentationUniqueKey()))
            {
                return;
            }

            Items.Add(item);
        }
    }
}
