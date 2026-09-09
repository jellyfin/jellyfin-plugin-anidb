namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Similarity;

/// <summary>
/// One entry of an anime's similar anime list: another anime, and how AniDB's users voted on
/// whether the two are alike. A vote is cast for or against, so the two counts together say both
/// how well the pair is thought to match and how many people said so.
/// </summary>
internal sealed class AniDbSimilarAnime
{
    /// <summary>
    /// Gets the AniDB id of the anime this entry points at.
    /// </summary>
    public required string AnimeId { get; init; }

    /// <summary>
    /// Gets the number of users who agreed the two anime are alike.
    /// </summary>
    public required int Approval { get; init; }

    /// <summary>
    /// Gets the number of users who voted either way.
    /// </summary>
    public required int Total { get; init; }
}
