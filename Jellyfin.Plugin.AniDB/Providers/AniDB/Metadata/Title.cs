namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// A single title as provided by AniDB.
/// </summary>
public class Title
{
    /// <summary>
    /// Gets or sets the language of the title.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the type of the title.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the title itself.
    /// </summary>
    public string? Name { get; set; }
}
