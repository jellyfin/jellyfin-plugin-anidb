using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Anime.Providers.AniDB.Identity
{
    /// <summary>
    /// The <see cref="IAniDbTitleDownloader"/> interface defines a type which can download anime titles and their AniDB IDs.
    /// </summary>
    public interface IAniDbTitleDownloader
    {
        /// <summary>
        /// Downloads titles and stores them in an XML file at TitlesFilePath.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task Load(CancellationToken cancellationToken);

        /// <summary>
        /// Gets the path to the titles.xml file.
        /// </summary>
        /// <returns>The path to the titles.xml file.</returns>
        string TitlesFilePath { get; }
    }
}
