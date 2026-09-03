namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// What asking a mapping source what it holds came to, for the page that asked for it.
/// </summary>
internal enum MappingSourceCheck
{
    /// <summary>
    /// The source holds what is already cached, so nothing was downloaded.
    /// </summary>
    Unchanged = 0,

    /// <summary>
    /// The source held something newer, and it has been downloaded and read.
    /// </summary>
    Updated = 1,

    /// <summary>
    /// The source could not be reached, or what came back could not be read. Whatever was
    /// cached before is still being used.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Nothing downloads the file: it is written by whoever runs the server, and is read again
    /// within minutes of being changed rather than being checked for.
    /// </summary>
    NotDownloaded = 3,

    /// <summary>
    /// The source is switched off in the configuration, so it is not consulted and there is no
    /// reason to fetch it.
    /// </summary>
    NotUsed = 4
}
