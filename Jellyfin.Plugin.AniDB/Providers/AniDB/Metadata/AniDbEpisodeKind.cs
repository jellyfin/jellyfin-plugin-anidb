namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// Which of an AniDB entry's episode numberings an episode belongs to. AniDB numbers each type
/// separately and writes the type into the number itself, so "1", "S1" and "O1" are three
/// different episodes of one entry.
/// </summary>
internal enum AniDbEpisodeKind
{
    /// <summary>
    /// An ordinary episode, numbered from 1.
    /// </summary>
    Regular,

    /// <summary>
    /// A special, numbered from S1.
    /// </summary>
    Special,

    /// <summary>
    /// One of AniDB's other episodes, numbered from O1. A movie released as a season and
    /// broadcast as a run of television episodes is recorded twice: as the entry's ordinary
    /// episodes, one per movie, and again here, one per broadcast episode.
    /// </summary>
    Other,

    /// <summary>
    /// A creditless opening or ending, numbered from C1. AniDB files both under the one
    /// numbering, telling them apart by title alone.
    /// </summary>
    Credits,

    /// <summary>
    /// A trailer, promotion or advertisement, numbered from T1.
    /// </summary>
    Trailer,

    /// <summary>
    /// A parody, numbered from P1.
    /// </summary>
    Parody
}
