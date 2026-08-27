using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

/// <summary>
/// The AniDbTitleDownloader class downloads the anime titles file from AniDB and stores it.
/// </summary>
public class AniDbTitleDownloader : IAniDbTitleDownloader
{
    private readonly ILogger<AniDbTitleDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbTitleDownloader"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{AniDbTitleDownloader}"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public AniDbTitleDownloader(ILogger<AniDbTitleDownloader> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        Paths = GetDataPath(applicationPaths);
    }

    /// <summary>
    /// Gets the path to the anidb data folder.
    /// </summary>
    public static string Paths { get; private set; } = null!;

    /// <summary>
    /// Gets the path to the titles.xml file, without requiring an instance.
    /// </summary>
    public static string StaticTitlesFilePath
    {
        get
        {
            Directory.CreateDirectory(Paths);

            return Path.Combine(Paths, "titles.xml");
        }
    }

    /// <inheritdoc />
    public string TitlesFilePath
    {
        get
        {
            Directory.CreateDirectory(Paths);

            return Path.Combine(Paths, "titles.xml");
        }
    }

    /// <summary>
    /// Gets the path to the anidb data folder.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <returns>The path to the anidb data folder.</returns>
    public static string GetDataPath(IApplicationPaths applicationPaths)
    {
        return Path.Combine(applicationPaths.CachePath, "anidb");
    }

    /// <summary>
    /// Downloads the titles file if needed, without requiring an instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task LoadStatic(CancellationToken cancellationToken)
    {
        var titlesFile = StaticTitlesFilePath;
        var titlesFileInfo = new FileInfo(titlesFile);

        // download titles if we do not already have them, or have not updated for a week
        if (!titlesFileInfo.Exists || (DateTime.UtcNow - titlesFileInfo.LastWriteTimeUtc).TotalDays > 7)
        {
            await DownloadTitlesStatic(titlesFile, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task Load(CancellationToken cancellationToken)
    {
        var titlesFile = TitlesFilePath;
        var titlesFileInfo = new FileInfo(titlesFile);

        // download titles if we do not already have them, or have not updated for a week
        if (!titlesFileInfo.Exists || (DateTime.UtcNow - titlesFileInfo.LastWriteTimeUtc).TotalDays > 7)
        {
            await DownloadTitles(titlesFile, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Downloads an xml file from AniDB which contains all of the titles for every anime, and their IDs,
    /// and saves it to disk.
    /// </summary>
    /// <param name="titlesFile">The destination file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task DownloadTitlesStatic(string titlesFile, CancellationToken cancellationToken)
    {
        var httpClient = Plugin.Instance.GetHttpClient();
        await AniDbSeriesProvider.WaitForRequestSlot(cancellationToken).ConfigureAwait(false);
        // The URL for retrieving a list of all anime titles and their AniDB IDs.
        using var stream = await httpClient.GetStreamAsync(new Uri("https://anidb.net/api/anime-titles.xml.gz"), cancellationToken).ConfigureAwait(false);
        using var unzipped = new GZipStream(stream, CompressionMode.Decompress);
        using var writer = File.Open(titlesFile, FileMode.Create, FileAccess.Write);
        await unzipped.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads an xml file from AniDB which contains all of the titles for every anime, and their IDs,
    /// and saves it to disk.
    /// </summary>
    /// <param name="titlesFile">The destination file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private Task DownloadTitles(string titlesFile, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Downloading new AniDB titles file.");
        return DownloadTitlesStatic(titlesFile, cancellationToken);
    }
}
