using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Similarity;

/// <summary>
/// Reads the similar anime list out of an anime's cached AniDB document.
/// </summary>
/// <remarks>
/// Nothing here downloads. The list comes free with the document the metadata providers already
/// fetched, and a document that was never fetched simply has no list: spending an AniDB request
/// to fill a row of recommendations would earn the address a ban for something nobody asked for.
/// </remarks>
internal static class AniDbSimilarAnimeReader
{
    /// <summary>
    /// Lists already read, keyed by anime id. A show's row is rendered on every visit to its
    /// page, and a batch of recommendations reads the same entries again for each baseline.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CachedList> _lists = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the anime AniDB's users hold to be like the given one.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="animeId">The AniDB id of the anime to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The similar anime, in the order AniDB lists them, or nothing where the anime's document is not cached.</returns>
    public static async Task<IReadOnlyList<AniDbSimilarAnime>> Read(IApplicationPaths appPaths, string animeId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AniDbSeriesProvider.GetSeriesDataPath(appPaths, animeId), "series.xml");
        var fileInfo = new FileInfo(path);

        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return [];
        }

        // A stale document is read as it stands. Its age matters to metadata, which is refreshed
        // on a schedule of its own, but who voted an anime alike last week does not.
        if (_lists.TryGetValue(animeId, out var cached) && cached.Matches(fileInfo))
        {
            return cached.Similar;
        }

        var similar = await Parse(path, cancellationToken).ConfigureAwait(false);

        _lists[animeId] = new CachedList(fileInfo.LastWriteTimeUtc, fileInfo.Length, similar);

        return similar;
    }

    private static async Task<IReadOnlyList<AniDbSimilarAnime>> Parse(string path, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        var similar = new List<AniDbSimilarAnime>();

        using (var streamReader = new StreamReader(path, Encoding.UTF8))
        using (var reader = XmlReader.Create(streamReader, settings))
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "similaranime":
                        await ReadSimilar(reader, similar).ConfigureAwait(false);

                        return similar;

                    case "characters":
                    case "episodes":
                        // The list is written above both of these, which hold the bulk of the
                        // document, so reaching either means the anime has none.
                        return similar;
                }
            }
        }

        return similar;
    }

    private static async Task ReadSimilar(XmlReader reader, List<AniDbSimilarAnime> similar)
    {
        using var subtree = reader.ReadSubtree();

        while (await subtree.ReadAsync().ConfigureAwait(false))
        {
            if (subtree.NodeType != XmlNodeType.Element || subtree.Name != "anime")
            {
                continue;
            }

            var animeId = subtree.GetAttribute("id");

            if (string.IsNullOrEmpty(animeId))
            {
                continue;
            }

            _ = int.TryParse(subtree.GetAttribute("approval"), CultureInfo.InvariantCulture, out var approval);
            _ = int.TryParse(subtree.GetAttribute("total"), CultureInfo.InvariantCulture, out var total);

            similar.Add(new AniDbSimilarAnime
            {
                AnimeId = animeId,
                Approval = approval,
                Total = total
            });
        }
    }

    /// <summary>
    /// A list read from a document as it stood. Both the time and the length are held because a
    /// document rewritten within the resolution of the file system's timestamp is otherwise
    /// indistinguishable from the copy it replaced.
    /// </summary>
    /// <param name="WrittenAtUtc">When the document was last written.</param>
    /// <param name="Length">How long the document was.</param>
    /// <param name="Similar">The list read from it.</param>
    private sealed record CachedList(DateTime WrittenAtUtc, long Length, IReadOnlyList<AniDbSimilarAnime> Similar)
    {
        public bool Matches(FileInfo fileInfo)
            => fileInfo.LastWriteTimeUtc == WrittenAtUtc && fileInfo.Length == Length;
    }
}
