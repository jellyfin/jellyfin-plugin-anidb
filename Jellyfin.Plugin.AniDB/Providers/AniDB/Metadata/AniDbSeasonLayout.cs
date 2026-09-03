using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// How a series is laid out in the library: which seasons it has, and which episode numbers
/// each of them spans. AniDB has no seasons of its own, so this is what its entries are
/// mapped onto.
/// </summary>
internal sealed class AniDbSeasonLayout
{
    /// <summary>
    /// The library id of each series already looked up by AniDB id, so that a scan does not
    /// search the whole library again for every episode it refreshes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Guid> _seriesIds = new(StringComparer.Ordinal);

    private AniDbSeasonLayout(IReadOnlyList<AniDbLibrarySeason> seasons, int specialsCount, string signature)
    {
        Seasons = seasons;
        SpecialsCount = specialsCount;
        Signature = signature;
    }

    /// <summary>
    /// Gets the seasons the series has, in season order. Specials are left out: AniDB keeps
    /// those inside the entry they belong to rather than in an entry of their own.
    /// </summary>
    public IReadOnlyList<AniDbLibrarySeason> Seasons { get; }

    /// <summary>
    /// Gets how many episodes the series has under season 0. AniDB keeps a season's specials in
    /// that season's own entry, so this is what says whether the library holds the same set.
    /// </summary>
    public int SpecialsCount { get; }

    /// <summary>
    /// Gets a value that changes whenever the layout does, so that a mapping built from an
    /// earlier one is not reused after episodes have been added or removed.
    /// </summary>
    public string Signature { get; }

    /// <summary>
    /// Reads the layout of the series with the given AniDB id.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <returns>The layout, or <c>null</c> when the series or its episodes cannot be seen.</returns>
    public static AniDbSeasonLayout? Read(ILibraryManager? libraryManager, string seriesId)
    {
        if (libraryManager == null || string.IsNullOrEmpty(seriesId))
        {
            return null;
        }

        var cached = _seriesIds.TryGetValue(seriesId, out var seriesItemId);

        if (!cached)
        {
            seriesItemId = FindSeries(libraryManager, seriesId);

            if (seriesItemId == Guid.Empty)
            {
                return null;
            }

            _seriesIds[seriesId] = seriesItemId;
        }

        var episodes = ReadEpisodes(libraryManager, seriesItemId);

        // A library removed and added again gives the series a new id, which leaves the cached
        // one pointing at nothing. Nothing else makes a series that was found have no episodes
        // at all, so look it up once more before believing it.
        if (episodes.Count == 0 && cached)
        {
            seriesItemId = FindSeries(libraryManager, seriesId);

            if (seriesItemId == Guid.Empty)
            {
                _seriesIds.TryRemove(seriesId, out _);

                return null;
            }

            _seriesIds[seriesId] = seriesItemId;
            episodes = ReadEpisodes(libraryManager, seriesItemId);
        }

        var spans = new Dictionary<int, (int First, int Last)>();
        var specials = 0;

        foreach (var item in episodes)
        {
            if (item.ParentIndexNumber is not >= 0 || item.IndexNumber is not > 0)
            {
                continue;
            }

            if (item.ParentIndexNumber == 0)
            {
                specials++;

                continue;
            }

            var season = item.ParentIndexNumber.Value;
            var first = item.IndexNumber.Value;

            // A file holding two episodes covers both numbers.
            var last = Math.Max(first, (item as Episode)?.IndexNumberEnd ?? first);

            spans[season] = spans.TryGetValue(season, out var span)
                ? (Math.Min(span.First, first), Math.Max(span.Last, last))
                : (first, last);
        }

        if (spans.Count == 0 && specials == 0)
        {
            return null;
        }

        var seasons = spans
            .OrderBy(entry => entry.Key)
            .Select(entry => new AniDbLibrarySeason(entry.Key, entry.Value.First, entry.Value.Last - entry.Value.First + 1))
            .ToList();

        var signature = string.Join(
            ',',
            seasons.Select(season => FormattableString.Invariant($"{season.Number}:{season.FirstEpisodeNumber}:{season.EpisodeCount}")))
            + FormattableString.Invariant($";S{specials}");

        return new AniDbSeasonLayout(seasons, specials, signature);
    }

    /// <summary>
    /// Finds the library id of the series with the given AniDB id.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <returns>The library id, or <see cref="Guid.Empty"/> when the library holds no such series.</returns>
    private static Guid FindSeries(ILibraryManager libraryManager, string seriesId)
    {
        var matches = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { ProviderNames.AniDb, seriesId } },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });

        return matches.Count == 0 ? Guid.Empty : matches[0].Id;
    }

    /// <summary>
    /// Reads every episode of the given series. Queried under the series rather than under each
    /// season, so that the episodes of a season Jellyfin synthesised for a flat folder are still
    /// found: on the refresh that creates such a season they have not been moved under it yet.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="seriesItemId">The library id of the series.</param>
    /// <returns>The episodes.</returns>
    private static IReadOnlyList<BaseItem> ReadEpisodes(ILibraryManager libraryManager, Guid seriesItemId)
        => libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesItemId],
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });
}
