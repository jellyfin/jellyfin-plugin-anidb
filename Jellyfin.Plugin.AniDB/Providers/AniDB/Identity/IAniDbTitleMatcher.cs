using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Anime.Providers.AniDB.Identity
{
    /// <summary>
    /// The <see cref="IAniDbTitleMatcher"/> interface defines a type which can match series titles to AniDB IDs.
    /// </summary>
    public interface IAniDbTitleMatcher
    {
        /// <summary>
        /// Finds the AniDB for the series with the given title.
        /// </summary>
        /// <param name="title">The title of the series to search for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The AniDB ID of the series is found; else <c>null</c>.</returns>
        Task<string> FindSeries(string title, CancellationToken cancellationToken);
    }
}
