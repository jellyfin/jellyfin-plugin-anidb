using System.Collections.Generic;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One AniDB anime as the AniBridge mappings place it: which show it belongs to, and span by
/// span, which of its episodes fill which episodes of which season of that show.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry.</param>
/// <param name="SeriesKey">The TVDB series id the entry's episodes are placed against. No entry in the set is placed against more than one.</param>
/// <param name="Spans">Where the entry's episodes go, in the order they were read.</param>
internal sealed record AniBridgeEntry(
    string AnimeId,
    string SeriesKey,
    IReadOnlyList<AniBridgeSpan> Spans);
