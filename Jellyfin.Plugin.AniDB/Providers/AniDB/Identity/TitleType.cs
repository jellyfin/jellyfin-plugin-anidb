namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

/// <summary>
/// The type of an AniDB title.
/// </summary>
public enum TitleType
{
    /// <summary>
    /// The main title.
    /// </summary>
    Main = 0,

    /// <summary>
    /// An official title.
    /// </summary>
    Official = 1,

    /// <summary>
    /// A short title.
    /// </summary>
    Short = 2,

    /// <summary>
    /// A synonym.
    /// </summary>
    Synonym = 3
}
