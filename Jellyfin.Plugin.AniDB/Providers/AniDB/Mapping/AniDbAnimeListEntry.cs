using System.Collections.Generic;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One AniDB anime as the anime list places it: which series it belongs to, which season of that
/// series it fills, and where in that season its episodes start.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry.</param>
/// <param name="SeriesKey">The id of the series the entry belongs to, which is a TVDB series id where the entry has one.</param>
/// <param name="DefaultSeason">The season the entry fills, as written: a number, "0" for the specials, or "a" for an entry numbered straight through the whole show.</param>
/// <param name="EpisodeOffset">Added to the entry's episode number to give the season's.</param>
/// <param name="Mappings">Rules correcting the placement above, episode range by episode range.</param>
internal sealed record AniDbAnimeListEntry(
    string AnimeId,
    string SeriesKey,
    string? DefaultSeason,
    int EpisodeOffset,
    IReadOnlyList<AniDbAnimeListMapping> Mappings);
