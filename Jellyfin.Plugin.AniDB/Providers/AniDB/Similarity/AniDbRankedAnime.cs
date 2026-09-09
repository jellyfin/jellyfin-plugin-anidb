namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Similarity;

/// <summary>
/// An anime offered as being like the one asked about, and how strongly. The score is comparable
/// only against others from the same ranking: it exists to order them, not to be shown.
/// </summary>
/// <param name="AnimeId">The AniDB id of the anime.</param>
/// <param name="Score">How strongly the anime is held to be alike, from 0 to 1.</param>
internal sealed record AniDbRankedAnime(string AnimeId, double Score);
