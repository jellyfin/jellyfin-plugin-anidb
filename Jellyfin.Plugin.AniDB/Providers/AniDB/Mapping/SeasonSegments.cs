using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Puts the segments claiming one season into the order its episodes run through them. Both
/// mapping sources produce their claims entry by entry, in no particular order, and neither
/// always says how long a claim is.
/// </summary>
internal static class SeasonSegments
{
    /// <summary>
    /// The longest season this will lay out episode by episode. One Piece's longest is under two
    /// hundred, so anything past this is a mapping to be taken whole rather than walked.
    /// </summary>
    private const int MaxEpisodesPerSeason = 2000;

    /// <summary>
    /// Orders the claims and gives each open-ended one the room up to the next.
    /// </summary>
    /// <param name="claims">The segments claiming the season, in any order.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Order(List<AniDbSeasonSegment> claims)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        // A segment with no count has to come last among those starting together, or it would
        // swallow the one after it.
        var segments = claims
            .OrderBy(segment => segment.FirstEpisodeNumber)
            .ThenBy(segment => segment.EpisodeCount == 0 ? 1 : 0)
            .ToList();

        // An entry only runs to the end of the season if no other entry starts later in it. A
        // season released in parts is one entry per part, each saying where it starts and none
        // of them how long it is, so without this the first part would answer for the whole
        // season and every episode past it would be looked up in the entry before its own.
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var room = segments[index + 1].FirstEpisodeNumber - segments[index].FirstEpisodeNumber;

            if (segments[index].EpisodeCount == 0 && room > 0)
            {
                segments[index] = segments[index] with { EpisodeCount = room };
            }
        }

        return segments;
    }

    /// <summary>
    /// A segment written out for a log message.
    /// </summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The description.</returns>
    public static string Describe(AniDbSeasonSegment segment)
        => segment.EpisodeCount > 0
            ? FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber}-{segment.FirstEpisodeNumber + segment.EpisodeCount - 1} from anime {segment.AnimeId} episodes {segment.FirstEpisodeInEntry}-{segment.FirstEpisodeInEntry + segment.EpisodeCount - 1}")
            : FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber} onwards from anime {segment.AnimeId} episode {segment.FirstEpisodeInEntry} onwards");

    /// <summary>
    /// Settles the claims several of an entry's numberings make on one season.
    /// </summary>
    /// <remarks>
    /// Two numberings of one entry usually describe different parts of a season rather than the
    /// same part: K-On!'s first season is twelve ordinary episodes followed by two of the
    /// entry's specials, and Bakemonogatari's is twelve followed by three. Taking one numbering
    /// and dropping the other left those episodes unidentified.
    /// <para>
    /// Where two numberings do claim the same episode, one of them is a stub - anime 13473 maps
    /// a single ordinary episode onto a season its other episodes describe in full, and anime
    /// 11350 the reverse - so the numbering covering more of the season wins the episodes they
    /// disagree about. On an even tie the one drawing on more of the entry's own episodes wins,
    /// a season read from three episodes being better told than the same season read three times
    /// from one, and ordinary episodes win from there.
    /// </para>
    /// </remarks>
    /// <param name="claims">The claims on the season, by the numbering that made them.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Resolve(Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>> claims)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        return Order(claims.Count == 1 ? claims.First().Value : Merge(claims));
    }

    /// <summary>
    /// Records a claim under the numbering it reads from.
    /// </summary>
    /// <param name="claims">The claims so far.</param>
    /// <param name="segment">The claim.</param>
    public static void Add(Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>> claims, AniDbSeasonSegment segment)
    {
        if (!claims.TryGetValue(segment.Kind, out var byKind))
        {
            byKind = [];
            claims[segment.Kind] = byKind;
        }

        byKind.Add(segment);
    }

    /// <summary>
    /// How many of a season's episodes one numbering accounts for.
    /// </summary>
    /// <param name="segments">The claims that numbering made.</param>
    /// <returns>The episode count, or <see cref="long.MaxValue"/> where a claim runs to the end of the season.</returns>
    private static long Coverage(List<AniDbSeasonSegment> segments)
    {
        long covered = 0;

        foreach (var segment in segments)
        {
            // A claim with no end accounts for the rest of the season whatever its length, and
            // two of them must not be added together: counted as a number each, they overflowed.
            if (segment.EpisodeCount <= 0)
            {
                return long.MaxValue;
            }

            covered += segment.EpisodeCount;
        }

        return covered;
    }

    private static List<AniDbSeasonSegment> Merge(Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>> claims)
    {
        var ordered = claims
            .OrderByDescending(pair => Coverage(pair.Value))
            .ThenByDescending(pair => pair.Value.Select(segment => segment.FirstEpisodeInEntry).Distinct().Count())
            .ThenBy(pair => pair.Key == AniDbEpisodeKind.Regular ? 0 : 1)
            .ToList();

        // A run with no end cannot be laid out episode by episode, and neither can a season of
        // implausible length. Where one turns up, the numbering covering most of the season
        // answers for the whole of it.
        if (ordered.Exists(pair => pair.Value.Exists(segment => segment.EpisodeCount <= 0 || segment.EpisodeCount > MaxEpisodesPerSeason)))
        {
            return ordered[0].Value;
        }

        var placed = new Dictionary<int, AniDbSeasonSegment>();

        foreach (var (_, segments) in ordered)
        {
            foreach (var segment in segments)
            {
                for (var offset = 0; offset < segment.EpisodeCount; offset++)
                {
                    var episode = segment.FirstEpisodeNumber + offset;

                    // The first numbering to claim an episode keeps it.
                    if (!placed.ContainsKey(episode))
                    {
                        placed[episode] = new AniDbSeasonSegment(
                            segment.AnimeId,
                            episode,
                            1,
                            segment.FirstEpisodeInEntry + offset,
                            segment.Kind);
                    }
                }
            }
        }

        var merged = new List<AniDbSeasonSegment>();

        foreach (var episode in placed.Keys.Order())
        {
            var one = placed[episode];

            // Episodes read consecutively from the same run of the same entry are one segment
            // again, so that a season no other numbering interrupts is described as plainly as
            // it was before.
            if (merged.Count > 0
                && merged[^1] is { } last
                && string.Equals(last.AnimeId, one.AnimeId, StringComparison.Ordinal)
                && last.Kind == one.Kind
                && last.FirstEpisodeNumber + last.EpisodeCount == one.FirstEpisodeNumber
                && last.FirstEpisodeInEntry + last.EpisodeCount == one.FirstEpisodeInEntry)
            {
                merged[^1] = last with { EpisodeCount = last.EpisodeCount + 1 };

                continue;
            }

            merged.Add(one);
        }

        return merged;
    }
}
