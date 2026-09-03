using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Where a single episode is read from, once a mapping source has placed it.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry holding it.</param>
/// <param name="Number">Its number within that entry.</param>
/// <param name="Kind">Which of the entry's episode numberings that number belongs to.</param>
internal sealed record AniDbAnimeListEpisode(string AnimeId, int Number, AniDbEpisodeKind Kind);
