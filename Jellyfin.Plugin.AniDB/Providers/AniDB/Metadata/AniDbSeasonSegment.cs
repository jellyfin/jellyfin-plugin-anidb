namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// One AniDB entry, or part of one, that a Jellyfin season is filled from. A season usually has
/// a single segment, and has more than one when the season Jellyfin numbers is split across
/// several AniDB entries, as a season released in two cours is.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry.</param>
/// <param name="FirstEpisodeNumber">The Jellyfin episode number the segment starts at.</param>
/// <param name="EpisodeCount">How many episodes the segment holds, or zero when it runs to the end of the season.</param>
/// <param name="FirstEpisodeInEntry">The entry's own number for that first episode. Not 1 when a season starts part way into an entry, as the second half of a series split across two seasons does.</param>
/// <param name="Kind">Which of the entry's episode numberings those numbers belong to. Ordinary episodes unless the season is filled from the entry's other episodes, as a season of movies broadcast as television episodes is.</param>
internal sealed record AniDbSeasonSegment(
    string AnimeId,
    int FirstEpisodeNumber,
    int EpisodeCount,
    int FirstEpisodeInEntry = 1,
    AniDbEpisodeKind Kind = AniDbEpisodeKind.Regular);
