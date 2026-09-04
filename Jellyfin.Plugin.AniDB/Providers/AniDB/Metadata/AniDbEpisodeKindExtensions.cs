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
        AniDbEpisodeKind.Credits => "C",
        AniDbEpisodeKind.Trailer => "T",
        AniDbEpisodeKind.Parody => "P",
        _ => string.Empty,
    };

    /// <summary>
    /// The kind AniDB's prefix stands for, which is the ordinary numbering where there is none.
    /// </summary>
    /// <param name="prefix">The prefix, as it is written before an episode number.</param>
    /// <returns>The kind, or <c>null</c> where the prefix is not one AniDB uses.</returns>
    public static AniDbEpisodeKind? FromPrefix(string prefix) => prefix switch
    {
        "" => AniDbEpisodeKind.Regular,
        "S" => AniDbEpisodeKind.Special,
        "O" => AniDbEpisodeKind.Other,
        "C" => AniDbEpisodeKind.Credits,
        "T" => AniDbEpisodeKind.Trailer,
        "P" => AniDbEpisodeKind.Parody,
        _ => null,
    };

    /// <summary>
    /// Whether an episode of this kind is one the library can only have filed among its specials.
    /// Jellyfin has one season for everything that is not an ordinary episode, so a special, a
    /// creditless opening, a trailer and a parody all land there.
    /// <para>
    /// <see cref="AniDbEpisodeKind.Other"/> is not among them: it is where AniDB puts the
    /// broadcast run of something released another way, which the mapping sources place into an
    /// ordinary season rather than the specials.
    /// </para>
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether the library files it among its specials.</returns>
    public static bool IsExtra(this AniDbEpisodeKind kind) => kind
        is AniDbEpisodeKind.Special
        or AniDbEpisodeKind.Credits
        or AniDbEpisodeKind.Trailer
        or AniDbEpisodeKind.Parody;
}
