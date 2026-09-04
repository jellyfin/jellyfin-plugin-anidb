using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Similarity;

/// <summary>
/// Orders the anime AniDB's users hold to be like the ones asked about.
/// </summary>
internal static class AniDbSimilarityRanker
{
    /// <summary>
    /// The standard score for a 95% confidence interval, which is what <see cref="Agreement"/>
    /// takes the lower bound of.
    /// </summary>
    private const double ConfidenceZ = 1.959964;

    /// <summary>
    /// What an anime reached over another anime is worth against one named outright. Chosen so
    /// that a pair agreed on unanimously at one remove still ranks below a pair that most of a
    /// dozen voters agreed on directly.
    /// </summary>
    private const double SecondHopWeight = 0.45;

    /// <summary>
    /// How many of the first ranking's entries are followed a step further. They are followed
    /// best first, and each costs reading a cached document.
    /// </summary>
    private const int MaxExpandedEntries = 12;

    /// <summary>
    /// The longest ranking returned. Far more than a row of recommendations holds, but a library
    /// carries only some of what AniDB names and the rest is what covers that.
    /// </summary>
    private const int MaxRankedEntries = 80;

    /// <summary>
    /// Ranks the anime AniDB names as being like any of the given ones.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="seedAnimeIds">The AniDB ids of the anime asked about, which are themselves left out of the ranking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The anime named, best first.</returns>
    public static async Task<IReadOnlyList<AniDbRankedAnime>> FirstHop(
        IApplicationPaths appPaths,
        IReadOnlySet<string> seedAnimeIds,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var seedAnimeId in seedAnimeIds)
        {
            var similar = await AniDbSimilarAnimeReader.Read(appPaths, seedAnimeId, cancellationToken).ConfigureAwait(false);

            foreach (var entry in similar)
            {
                // A show is asked about under every entry it is made of, and its own entries
                // name each other often enough that leaving them in would offer a show as being
                // like itself.
                if (seedAnimeIds.Contains(entry.AnimeId))
                {
                    continue;
                }

                Keep(scores, entry.AnimeId, Agreement(entry));
            }
        }

        return Order(scores);
    }

    /// <summary>
    /// Ranks the anime named as being like the ones a first ranking produced. What a show is like
    /// is a shallow list - a third of AniDB's entries name two or fewer - and this is what fills a
    /// row for a show whose own list a library holds almost none of.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="firstHop">The ranking to expand, best first.</param>
    /// <param name="exclude">The AniDB ids already accounted for, which are left out of the ranking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The anime reached, best first.</returns>
    public static async Task<IReadOnlyList<AniDbRankedAnime>> SecondHop(
        IApplicationPaths appPaths,
        IReadOnlyList<AniDbRankedAnime> firstHop,
        IReadOnlySet<string> exclude,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var parent in firstHop.Take(MaxExpandedEntries))
        {
            var similar = await AniDbSimilarAnimeReader.Read(appPaths, parent.AnimeId, cancellationToken).ConfigureAwait(false);

            foreach (var entry in similar)
            {
                if (exclude.Contains(entry.AnimeId))
                {
                    continue;
                }

                // Both steps count: an anime reached over a pairing few agreed with is a weaker
                // offer than one reached over a pairing everybody agreed with.
                Keep(scores, entry.AnimeId, parent.Score * Agreement(entry) * SecondHopWeight);
            }
        }

        return Order(scores);
    }

    /// <summary>
    /// How much the votes on a pairing support it, as the lower bound of a confidence interval
    /// on the share who agreed.
    /// </summary>
    /// <remarks>
    /// The plain share cannot be compared across pairings: one person agreeing on their own reads
    /// as complete agreement and outranks the fifty-three of sixty-eight who agreed that Akira is
    /// like Freedom. Taking the lower bound instead asks what share the votes actually establish,
    /// which is what puts the pairing a dozen people weighed above the pairing one person did.
    /// </remarks>
    /// <param name="entry">The entry to score.</param>
    /// <returns>The supported share of agreement, from 0 to 1.</returns>
    public static double Agreement(AniDbSimilarAnime entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var total = entry.Total;

        if (total <= 0)
        {
            return 0;
        }

        var approval = Math.Clamp(entry.Approval, 0, total);
        var observed = (double)approval / total;
        var zSquared = ConfidenceZ * ConfidenceZ;
        var centre = observed + (zSquared / (2 * total));
        var margin = ConfidenceZ * Math.Sqrt(((observed * (1 - observed)) + (zSquared / (4.0 * total))) / total);

        return Math.Max(0, (centre - margin) / (1 + (zSquared / total)));
    }

    /// <summary>
    /// Keeps the strongest score an anime was reached with. An anime named by several entries of
    /// one show is one offer, made as well as the entry that makes it best.
    /// </summary>
    private static void Keep(Dictionary<string, double> scores, string animeId, double score)
    {
        if (score <= 0)
        {
            return;
        }

        if (!scores.TryGetValue(animeId, out var existing) || existing < score)
        {
            scores[animeId] = score;
        }
    }

    private static IReadOnlyList<AniDbRankedAnime> Order(Dictionary<string, double> scores)
        => [.. scores
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxRankedEntries)
            .Select(entry => new AniDbRankedAnime(entry.Key, entry.Value))];
}
