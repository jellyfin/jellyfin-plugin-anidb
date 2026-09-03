using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One run of an AniDB entry's episodes, and the run of a season's episodes they fill. The
/// AniBridge mappings state both outright, where the anime list leaves the second to be worked
/// out from an offset.
/// </summary>
/// <param name="Season">The season the run fills, or 0 for the specials.</param>
/// <param name="InEntry">The episodes of the entry the run covers.</param>
/// <param name="InSeason">The episodes of the season they fill.</param>
/// <param name="Kind">Which of the entry's episode numberings <paramref name="InEntry"/> counts.</param>
internal sealed record AniBridgeSpan(int Season, AniBridgeRange InEntry, AniBridgeRange InSeason, AniDbEpisodeKind Kind);
