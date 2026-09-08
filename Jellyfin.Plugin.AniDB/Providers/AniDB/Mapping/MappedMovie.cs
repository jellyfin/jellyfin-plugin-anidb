namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// A movie identified from an id another provider had already settled on, and what identified it.
/// </summary>
/// <param name="Episode">The AniDB entry the movie is, and which of its episodes holds it. A movie AniDB registers in its own right is that entry's first ordinary episode; one it holds inside another entry is a later episode, or a special, or one of the other episodes.</param>
/// <param name="Source">Which mapping source answered, as a noun phrase for a log message.</param>
/// <param name="Provider">Which provider's id it was found from.</param>
/// <param name="ProviderId">That id.</param>
internal sealed record MappedMovie(AniDbAnimeListEpisode Episode, string Source, string Provider, string ProviderId);
