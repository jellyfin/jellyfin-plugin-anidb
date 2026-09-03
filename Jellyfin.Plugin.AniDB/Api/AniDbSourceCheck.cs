namespace Jellyfin.Plugin.AniDB.Api;

/// <summary>
/// What asking the downloaded mapping sources what they hold came to, as the configuration
/// page reports it after the button that asks for one.
/// </summary>
public class AniDbSourceCheck
{
    /// <summary>
    /// Gets or sets what checking the AniBridge mappings came to.
    /// </summary>
    public string AniBridge { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what checking the anime list came to.
    /// </summary>
    public string AnimeList { get; set; } = string.Empty;
}
