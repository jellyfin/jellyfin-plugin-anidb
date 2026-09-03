using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// A run of consecutive episode numbers, as the AniBridge mappings write one.
/// </summary>
/// <param name="Start">The first episode of the run.</param>
/// <param name="End">The last episode, or <c>null</c> where the run has no end written and so goes on to the end of whatever holds it, as the season now airing does.</param>
internal sealed record AniBridgeRange(int Start, int? End)
{
    /// <summary>
    /// How far a run with no end written is walked out where a ratio has to be applied to it
    /// episode by episode. Nothing a library numbers as one season runs anything like this long,
    /// so a run cut here is cut past the end of whatever it could be filling.
    /// </summary>
    private const int MaxWalkedEpisodes = 2000;

    /// <summary>
    /// Gets how many episodes the run holds, or <c>null</c> where it has no end.
    /// </summary>
    public int? Length => End is { } end ? Math.Max(end - Start + 1, 0) : null;

    /// <summary>
    /// Reads a run as the mappings write one: "5" for a single episode, "42-63" for a range, or
    /// "14-" for one that goes on.
    /// </summary>
    /// <param name="value">The run as written.</param>
    /// <returns>The run, or <c>null</c> where it is not one of those three forms.</returns>
    public static AniBridgeRange? Read(string? value)
    {
        // One range and nothing else. The entry's side of a mapping is written that way and
        // that way only, and so is every part of the season's side once ReadAll has split it on
        // its commas and ReadTarget has taken the ratio off its end.
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny(',', '|') >= 0)
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var separator = span.IndexOf('-');

        if (separator < 0)
        {
            return int.TryParse(span, CultureInfo.InvariantCulture, out var only) ? new AniBridgeRange(only, only) : null;
        }

        if (!int.TryParse(span[..separator], CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        var rest = span[(separator + 1)..];

        if (rest.IsEmpty)
        {
            return new AniBridgeRange(start, null);
        }

        return int.TryParse(rest, CultureInfo.InvariantCulture, out var end) ? new AniBridgeRange(start, end) : null;
    }

    /// <summary>
    /// Reads a run that may list several ranges, as "1-7,9,11-17" does. The schema lists them on
    /// the season's side only, but a hand-written file is read the same way on either.
    /// </summary>
    /// <param name="value">The run as written, with any ratio already taken off it by <see cref="ReadTarget"/>.</param>
    /// <returns>The ranges, in the order they are written, or <c>null</c> where any of them is not a form this reads.</returns>
    public static IReadOnlyList<AniBridgeRange>? ReadAll(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.Contains(',', StringComparison.Ordinal))
        {
            return Read(value) is { } only ? [only] : null;
        }

        var ranges = new List<AniBridgeRange>();

        foreach (var part in value.Split(','))
        {
            // Only the last range of a list may go on without an end: one in the middle has no
            // length, so nothing written after it could be paired against the other side.
            if (Read(part) is not { } range || (ranges.Count > 0 && ranges[^1].End == null))
            {
                return null;
            }

            ranges.Add(range);
        }

        return ranges.Count == 0 ? null : ranges;
    }

    /// <summary>
    /// Reads the season's side of a mapping, which may list several ranges and may end with a
    /// ratio weighting its episodes against the entry's, as "14-|2" does.
    /// </summary>
    /// <remarks>
    /// The ratio is the season's side alone, and says which way round the weighting goes by its
    /// sign, so there is nothing to read on the entry's side and no second direction to write:
    /// "1-2" against "1|-2" is the same statement as "1" against "1-2|2" read backwards.
    /// </remarks>
    /// <param name="value">The side as written.</param>
    /// <returns>The ranges and the ratio, or <c>null</c> where any part of it is not a form this reads. The ratio is 1 where none is written, <c>n</c> where each episode of the entry spans <c>n</c> of the season's, and <c>-n</c> where each episode of the season spans <c>n</c> of the entry's.</returns>
    public static (IReadOnlyList<AniBridgeRange> Ranges, int Ratio)? ReadTarget(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var separator = value.IndexOf('|', StringComparison.Ordinal);

        if (separator < 0)
        {
            return ReadAll(value) is { } plain ? (plain, 1) : null;
        }

        // Written once and last, so a second one anywhere leaves this to fail on the rest. A
        // ratio of nothing per episode maps nothing and is not a mapping.
        var ratio = value.AsSpan(separator + 1).Trim();

        if (!int.TryParse(ratio, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var weight) || weight == 0)
        {
            return null;
        }

        return ReadAll(value[..separator]) is { } ranges ? (ranges, weight) : null;
    }

    /// <summary>
    /// Pairs the two sides of a mapping episode by episode, cutting each side wherever the
    /// other begins a new range.
    /// </summary>
    /// <param name="inEntry">The episodes of the entry, as written.</param>
    /// <param name="inSeason">The episodes of the season they fill, as written.</param>
    /// <param name="ratio">The ratio the season's side carries, from <see cref="ReadTarget"/>, or 1 where it carries none.</param>
    /// <returns>The runs to record, each of the same length on both sides. Episodes past the end of the shorter side are left unpaired.</returns>
    public static IEnumerable<(AniBridgeRange InEntry, AniBridgeRange InSeason)> Pair(
        IReadOnlyList<AniBridgeRange> inEntry,
        IReadOnlyList<AniBridgeRange> inSeason,
        int ratio = 1)
    {
        ArgumentNullException.ThrowIfNull(inEntry);
        ArgumentNullException.ThrowIfNull(inSeason);

        // One episode of the entry to one of the season, whichever sign it is written with, is
        // what a mapping carrying no ratio says as well.
        return ratio is 1 or -1
            ? PairEvenly(inEntry, inSeason)
            : PairByRatio(inEntry, inSeason, ratio);
    }

    private static IEnumerable<(AniBridgeRange InEntry, AniBridgeRange InSeason)> PairEvenly(
        IReadOnlyList<AniBridgeRange> inEntry,
        IReadOnlyList<AniBridgeRange> inSeason)
    {
        // A mapping written as one range against another is passed through as it stands, open
        // end and all, which is every mapping the downloaded sets write.
        if (inEntry.Count == 1 && inSeason.Count == 1)
        {
            yield return (inEntry[0], inSeason[0]);

            yield break;
        }

        var entryIndex = 0;
        var seasonIndex = 0;
        var takenFromEntry = 0;
        var takenFromSeason = 0;

        while (entryIndex < inEntry.Count && seasonIndex < inSeason.Count)
        {
            var entryRun = inEntry[entryIndex];
            var seasonRun = inSeason[seasonIndex];
            var leftInEntry = entryRun.Length - takenFromEntry;
            var leftInSeason = seasonRun.Length - takenFromSeason;

            // A range written backwards holds nothing, and is stepped over rather than read as
            // the end of the list.
            if (leftInEntry == 0)
            {
                entryIndex++;
                takenFromEntry = 0;

                continue;
            }

            if (leftInSeason == 0)
            {
                seasonIndex++;
                takenFromSeason = 0;

                continue;
            }

            var entryStart = entryRun.Start + takenFromEntry;
            var seasonStart = seasonRun.Start + takenFromSeason;

            // Two runs that both go on cannot be cut anywhere, so they are the last pair.
            if (leftInEntry == null && leftInSeason == null)
            {
                yield return (new AniBridgeRange(entryStart, null), new AniBridgeRange(seasonStart, null));

                yield break;
            }

            var take = Math.Min(leftInEntry ?? int.MaxValue, leftInSeason ?? int.MaxValue);

            yield return (
                new AniBridgeRange(entryStart, entryStart + take - 1),
                new AniBridgeRange(seasonStart, seasonStart + take - 1));

            takenFromEntry += take;
            takenFromSeason += take;

            if (leftInEntry == take)
            {
                entryIndex++;
                takenFromEntry = 0;
            }

            if (leftInSeason == take)
            {
                seasonIndex++;
                takenFromSeason = 0;
            }
        }
    }

    /// <summary>
    /// Pairs the two sides where a ratio weights one against the other, an episode of the season
    /// at a time, since a run of them no longer answers to a run of the entry's.
    /// </summary>
    /// <remarks>
    /// A positive ratio has each episode of the entry spanning that many of the season's, which
    /// is a season that numbers a two-part episode as two where AniDB lists it as one: each of
    /// those season episodes is described by the one AniDB episode holding it. A negative ratio
    /// has it the other way, that many of the entry's episodes to one of the season's, which is a
    /// season holding them as a single episode: that episode is described by the first of them,
    /// there being one record to take and nothing here that numbers a second.
    /// </remarks>
    /// <param name="inEntry">The episodes of the entry, as written.</param>
    /// <param name="inSeason">The episodes of the season they fill, as written.</param>
    /// <param name="ratio">The ratio, which is neither 1 nor -1.</param>
    /// <returns>The runs to record, one episode each.</returns>
    private static IEnumerable<(AniBridgeRange InEntry, AniBridgeRange InSeason)> PairByRatio(
        IReadOnlyList<AniBridgeRange> inEntry,
        IReadOnlyList<AniBridgeRange> inSeason,
        int ratio)
    {
        var entryEpisodes = Walk(inEntry);
        var seasonEpisodes = Walk(inSeason);

        for (var index = 0; index < seasonEpisodes.Count; index++)
        {
            // Which episode of the entry describes this one of the season: every ratio episodes
            // along where the entry's are the wider, ratio episodes along each time where the
            // season's are.
            var inEntryIndex = ratio > 0 ? index / ratio : index * -ratio;

            if (inEntryIndex >= entryEpisodes.Count)
            {
                yield break;
            }

            yield return (
                new AniBridgeRange(entryEpisodes[inEntryIndex], entryEpisodes[inEntryIndex]),
                new AniBridgeRange(seasonEpisodes[index], seasonEpisodes[index]));
        }
    }

    /// <summary>
    /// The episode numbers a side covers, in the order it lists them.
    /// </summary>
    /// <param name="ranges">The ranges of the side.</param>
    /// <returns>The numbers, a run with no end written walked out to <see cref="MaxWalkedEpisodes"/>.</returns>
    private static List<int> Walk(IReadOnlyList<AniBridgeRange> ranges)
    {
        var episodes = new List<int>();

        foreach (var range in ranges)
        {
            // A range written backwards holds nothing and contributes nothing.
            var last = range.End ?? int.MaxValue;

            for (var episode = range.Start; episode <= last && episodes.Count < MaxWalkedEpisodes; episode++)
            {
                episodes.Add(episode);
            }
        }

        return episodes;
    }
}
