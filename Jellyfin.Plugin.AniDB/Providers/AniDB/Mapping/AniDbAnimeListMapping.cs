using System.Collections.Generic;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One rule from an entry's mapping list, correcting where the entry's episodes sit in the
/// series the seasons are numbered by.
/// </summary>
/// <param name="AnidbSeason">1 when the numbers below are the entry's ordinary episodes, 0 when they are its specials.</param>
/// <param name="TvdbSeason">The season the rule places them in.</param>
/// <param name="Start">The first of the entry's episodes the rule covers, or <c>null</c> when it names episodes individually.</param>
/// <param name="End">The last of the entry's episodes the rule covers.</param>
/// <param name="Offset">Added to the entry's episode number to give the season's.</param>
/// <param name="Pairs">Episodes named individually, from the entry's number to the season's. A season number of 0 means the episode has no counterpart there.</param>
internal sealed record AniDbAnimeListMapping(
    int AnidbSeason,
    int TvdbSeason,
    int? Start,
    int? End,
    int Offset,
    IReadOnlyList<KeyValuePair<int, int>> Pairs);
