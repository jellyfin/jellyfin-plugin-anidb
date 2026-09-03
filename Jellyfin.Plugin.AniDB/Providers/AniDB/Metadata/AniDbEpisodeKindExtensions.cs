namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// Extension methods for <see cref="AniDbEpisodeKind"/>.
/// </summary>
internal static class AniDbEpisodeKindExtensions
{
    /// <summary>
    /// What AniDB puts before the number of an episode of this kind, which is also what the
    /// cached document of one is named after.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The prefix, which is empty for an ordinary episode.</returns>
    public static string Prefix(this AniDbEpisodeKind kind) => kind switch
    {
        AniDbEpisodeKind.Special => "S",
        AniDbEpisodeKind.Other => "O",
        _ => string.Empty,
    };
}
