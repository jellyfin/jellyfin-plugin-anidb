namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    /// <summary>
    /// Information about a person as provided by AniDB.
    /// </summary>
    public class AniDbPersonInfo
    {
        /// <summary>
        /// Gets or sets the name of the person.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the AniDB id of the person.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the image url of the person.
        /// </summary>
        public string? Image { get; set; }
    }
}
