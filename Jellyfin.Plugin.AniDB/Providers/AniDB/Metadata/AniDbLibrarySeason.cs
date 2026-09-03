namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// A season as the library holds it, described by the episode numbers it actually spans.
/// </summary>
/// <param name="Number">The Jellyfin season number.</param>
/// <param name="FirstEpisodeNumber">The lowest episode number in the season, which is 1 unless the files are numbered straight through the show.</param>
/// <param name="EpisodeCount">How many episode numbers the season spans, counting the gaps left by episodes the library does not have.</param>
internal sealed record AniDbLibrarySeason(int Number, int FirstEpisodeNumber, int EpisodeCount);
