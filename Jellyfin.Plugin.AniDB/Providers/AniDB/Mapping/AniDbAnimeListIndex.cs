using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The community anime list, as read from one downloaded copy of it.
/// </summary>
internal sealed class AniDbAnimeListIndex
{
    /// <summary>
    /// Where AniDB's other episodes begin in the numbering the list writes the specials in.
    /// </summary>
    private const int OtherEpisodeBand = 400;

    private readonly IReadOnlyDictionary<string, AniDbAnimeListEntry> _byAnimeId;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AniDbAnimeListEntry>> _bySeries;
    private readonly IReadOnlyDictionary<string, AniDbAnimeListEpisode> _movies;

    private AniDbAnimeListIndex(
        IReadOnlyDictionary<string, AniDbAnimeListEntry> byAnimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AniDbAnimeListEntry>> bySeries,
        IReadOnlyDictionary<string, AniDbAnimeListEpisode> movies)
    {
        _byAnimeId = byAnimeId;
        _bySeries = bySeries;
        _movies = movies;
    }

    /// <summary>
    /// Gets the placement worked out for every season already asked about, keyed by series id
    /// and season number, holding an empty list for a season the list does not place. Each of a
    /// season's episodes asks the same question, and the answer changes only when the list is
    /// read again, which replaces this along with it.
    /// </summary>
    public ConcurrentDictionary<string, IReadOnlyList<AniDbSeasonSegment>> Placements { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets how many AniDB entries the list places against a TVDB series.
    /// </summary>
    public int EntryCount => _byAnimeId.Count;

    /// <summary>
    /// Reads a downloaded copy of the list.
    /// </summary>
    /// <param name="path">Where the copy is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cachedAtUtc">When the copy was written.</param>
    /// <returns>The list.</returns>
    public static AniDbAnimeListIndex Parse(string path, ILogger logger, DateTime cachedAtUtc)
    {
        var byAnimeId = new Dictionary<string, AniDbAnimeListEntry>(StringComparer.Ordinal);
        var bySeries = new Dictionary<string, List<AniDbAnimeListEntry>>(StringComparer.Ordinal);
        var movies = new Dictionary<string, AniDbAnimeListEpisode>(StringComparer.Ordinal);

        foreach (var element in XDocument.Load(path).Root?.Elements("anime") ?? [])
        {
            var animeId = element.Attribute("anidbid")?.Value;
            var seriesKey = element.Attribute("tvdbid")?.Value;

            if (string.IsNullOrEmpty(animeId))
            {
                continue;
            }

            // Read before the series below, because the entries carrying a movie id are largely
            // the ones with no series to be placed against: 893 of them are filed under the
            // word "movie" where a TVDB id would go.
            ReadMovieIds(element, animeId, movies);

            // A movie or an OVA the list files under no series has nothing to place it against.
            if (string.IsNullOrEmpty(seriesKey) || !seriesKey.All(char.IsAsciiDigit))
            {
                continue;
            }

            var entry = new AniDbAnimeListEntry(
                animeId,
                seriesKey,
                element.Attribute("defaulttvdbseason")?.Value,
                ReadInt(element.Attribute("episodeoffset")?.Value),
                [.. element.Descendants("mapping").Select(ReadMapping).OfType<AniDbAnimeListMapping>()]);

            byAnimeId[animeId] = entry;

            if (!bySeries.TryGetValue(seriesKey, out var siblings))
            {
                siblings = [];
                bySeries[seriesKey] = siblings;
            }

            siblings.Add(entry);
        }

        logger.LogInformation(
            "The anime list cached on {CachedAt} places {EntryCount} AniDB entries across {SeriesCount} shows and identifies {MovieCount} movies",
            cachedAtUtc,
            byAnimeId.Count,
            bySeries.Count,
            movies.Values.Distinct().Count());

        return new AniDbAnimeListIndex(
            byAnimeId,
            bySeries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AniDbAnimeListEntry>)pair.Value, StringComparer.Ordinal),
            movies);
    }

    /// <summary>
    /// Every entry the list files under the same show as the given one.
    /// </summary>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The entries, or <c>null</c> where the list does not place that one.</returns>
    public IReadOnlyList<AniDbAnimeListEntry>? Siblings(string animeId)
    {
        if (!_byAnimeId.TryGetValue(animeId, out var self))
        {
            return null;
        }

        return _bySeries.TryGetValue(self.SeriesKey, out var siblings) ? siblings : null;
    }

    /// <summary>
    /// The AniDB entry a movie is. The list carries a movie's ids on the entry itself, so what it
    /// answers is always that entry's own first episode.
    /// </summary>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <returns>The episode, or <c>null</c> where the list identifies no movie under that id.</returns>
    public AniDbAnimeListEpisode? ResolveMovie(string key)
        => _movies.GetValueOrDefault(key);

    /// <summary>
    /// The show an entry is filed under, as the id its seasons are numbered against. What a
    /// sparsely written set of mappings is reached through, which names one entry of a show
    /// rather than the entry the show is identified as.
    /// </summary>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The series key, or <c>null</c> where the list does not place that entry.</returns>
    public string? SeriesKeyOf(string animeId)
        => _byAnimeId.TryGetValue(animeId, out var entry) ? entry.SeriesKey : null;

    /// <summary>
    /// Every key the list files a movie under, given the AniDB episode it is. Answers for an
    /// entry whose episode is not known only where the entry holds a single movie: an entry
    /// holding a trilogy is one AniDB id and three movie ids, and nothing but the episode says
    /// which of them a film is.
    /// </summary>
    /// <param name="animeId">The AniDB id of the entry holding the movie.</param>
    /// <param name="episode">Which of its episodes the movie is, where that is known.</param>
    /// <returns>The keys, in no particular order, which is empty where the movie is not identified.</returns>
    public IReadOnlyList<string> MovieKeysOf(string animeId, AniDbAnimeListEpisode? episode)
    {
        var held = _movies
            .Where(pair => string.Equals(pair.Value.AnimeId, animeId, StringComparison.Ordinal))
            .ToList();

        if (held.Count == 0)
        {
            return [];
        }

        var wanted = episode ?? (held.DistinctBy(pair => pair.Value).Count() == 1 ? held[0].Value : null);

        return wanted == null
            ? []
            : [.. held.Where(pair => pair.Value == wanted).Select(pair => pair.Key)];
    }

    /// <summary>
    /// The entry a show begins in, found from the TVDB id another provider has already settled
    /// on. The list keys its entries by TVDB id, so this identifies a show outright where
    /// matching on the name cannot: where AniDB spells the name differently, and where two
    /// shows share one name and only the id tells them apart.
    /// </summary>
    /// <param name="tvdbId">The TVDB series id.</param>
    /// <returns>The AniDB id, or <c>null</c> where the list files nothing under that id.</returns>
    public string? FirstSeasonByTvdb(string tvdbId)
        => _bySeries.TryGetValue(tvdbId, out var siblings) ? PickFirstSeason(siblings) : null;

    /// <summary>
    /// The entry a show begins in, given an entry of it the list files as a later season.
    /// </summary>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the list does not place the entry or already places it at the show's first season.</returns>
    public string? WalkBackToFirstSeason(string animeId)
    {
        // Only an entry the list files as a second season or later is walked back. An entry it
        // already files at season 1 is the show's own start, and moving it could only hand the
        // show to whatever else shares its TVDB id: the list groups a handful of unrelated
        // shows under one id, and two adaptations of one book sit that way under season 1.
        if (!_byAnimeId.TryGetValue(animeId, out var self) || SeasonOf(self) <= 1)
        {
            return null;
        }

        if (!_bySeries.TryGetValue(self.SeriesKey, out var siblings))
        {
            return null;
        }

        var first = PickFirstSeason(siblings);

        return string.Equals(first, animeId, StringComparison.Ordinal) ? null : first;
    }

    /// <summary>
    /// Which of the entries filed under one show the show begins in.
    /// </summary>
    /// <param name="siblings">Every entry the list files under the same show.</param>
    /// <returns>The AniDB id of the earliest entry, or <c>null</c> when none of them fills a season.</returns>
    public static string? PickFirstSeason(IReadOnlyList<AniDbAnimeListEntry> siblings)
    {
        // The show begins in the entry filling its earliest season, and where that season was
        // released in parts, in the part starting at its first episode. Where a season is
        // filled by several entries starting together - a show and the recap or alternate
        // version filed beside it - the oldest of them is the show itself, AniDB having
        // registered it before whatever was made from it.
        //
        // Season 0 is an entry holding nothing but specials, which is never where a show
        // begins, and a season that will not parse is one the list cannot place at all.
        return siblings
            .Select(entry => (Entry: entry, Season: SeasonOf(entry)))
            .Where(candidate => candidate.Season >= 1)
            .OrderBy(candidate => candidate.Season)
            .ThenBy(candidate => candidate.Entry.EpisodeOffset)
            .ThenBy(candidate => int.TryParse(candidate.Entry.AnimeId, CultureInfo.InvariantCulture, out var id) ? id : int.MaxValue)
            .Select(candidate => candidate.Entry.AnimeId)
            .FirstOrDefault();
    }

    /// <summary>
    /// The season an entry fills, as a number. An entry numbered straight through the whole
    /// show carries "a" rather than a season, and covers that show from its first episode.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The season number, or -1 where the entry names no season this can read.</returns>
    public static int SeasonOf(AniDbAnimeListEntry entry)
    {
        if (string.Equals(entry.DefaultSeason, "a", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return int.TryParse(entry.DefaultSeason, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }

    /// <summary>
    /// Works out which of a series' AniDB entries fill the given season, and which of their
    /// episodes each one contributes.
    /// </summary>
    /// <param name="siblings">Every entry the list files under the same series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Place(IReadOnlyList<AniDbAnimeListEntry> siblings, int seasonNumber)
    {
        var claims = new Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>>();

        foreach (var entry in siblings)
        {
            // Which of the season's episodes this entry's rules account for. What is left over
            // is what the season the entry names is for.
            var named = new HashSet<int>();

            foreach (var mapping in entry.Mappings)
            {
                if (mapping.TvdbSeason != seasonNumber)
                {
                    continue;
                }

                // A rule may name the season's episodes one by one rather than as a run, which
                // is how a season whose episodes do not correspond one to one with the entry's
                // is written. Each named episode is a segment of its own, one episode long.
                foreach (var pair in mapping.Pairs)
                {
                    // A season number of 0 says the episode has no counterpart there.
                    if (pair.Value <= 0)
                    {
                        continue;
                    }

                    var (kind, number) = Source(mapping, pair.Key);

                    SeasonSegments.Add(claims, new AniDbSeasonSegment(entry.AnimeId, pair.Value, 1, number, kind));
                    named.Add(pair.Value);
                }

                if (mapping.Start is not { } start || mapping.End < start)
                {
                    continue;
                }

                // A rule with no end runs to the end of the entry. That is how the list places
                // the season now airing, whose last episode nobody knows yet, and dropping such
                // a rule left that season with no placement at all.
                var count = mapping.End is { } end ? end - start + 1 : 0;
                var first = start + mapping.Offset;
                var source = Source(mapping, start);

                SeasonSegments.Add(claims, new AniDbSeasonSegment(entry.AnimeId, first, count, source.Number, source.Kind));

                named.Add(first);

                for (var offset = 1; offset < count; offset++)
                {
                    named.Add(first + offset);
                }
            }

            if (!int.TryParse(entry.DefaultSeason, CultureInfo.InvariantCulture, out var defaultSeason)
                || defaultSeason != seasonNumber)
            {
                continue;
            }

            var startsAt = entry.EpisodeOffset + 1;

            // A rule already names the episode the rest of the entry would begin at, so the
            // rules are the whole of what this entry gives the season.
            if (named.Contains(startsAt))
            {
                continue;
            }

            // The list does not say how long an entry is, so the claim runs to the end of the
            // season. It stops early where a rule hands the rest of the entry to another season,
            // as a series split across two of them does, and where a rule names a later episode
            // of this season: K-On!'s first season is the entry's twelve episodes and then two
            // of its specials, so the twelve stop where the specials begin.
            var handedOver = entry.Mappings
                .Where(mapping => mapping.TvdbSeason != seasonNumber && mapping.AnidbSeason != 0 && mapping.Start.HasValue)
                .Select(mapping => mapping.Start!.Value)
                .DefaultIfEmpty(0)
                .Min();

            var nextNamed = named.Where(episode => episode > startsAt).DefaultIfEmpty(0).Min();

            SeasonSegments.Add(
                claims,
                new AniDbSeasonSegment(
                    entry.AnimeId,
                    startsAt,
                    nextNamed > 0 ? nextNamed - startsAt : Math.Max(handedOver - 1, 0),
                    1));
        }

        return SeasonSegments.Resolve(claims);
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="siblings">Every entry the list files under the same series.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <returns>The episode, or <c>null</c> when the list does not place it.</returns>
    public static AniDbAnimeListEpisode? PlaceSpecial(IReadOnlyList<AniDbAnimeListEntry> siblings, int episodeNumber)
    {
        // A rule naming this episode outright beats anything worked out from an offset.
        foreach (var entry in siblings)
        {
            foreach (var mapping in entry.Mappings)
            {
                if (mapping.TvdbSeason != 0)
                {
                    continue;
                }

                foreach (var pair in mapping.Pairs)
                {
                    // A season number of 0 says the episode has no counterpart to name.
                    if (pair.Value == episodeNumber && pair.Value != 0)
                    {
                        var (pairKind, pairNumber) = Source(mapping, pair.Key);

                        return new AniDbAnimeListEpisode(entry.AnimeId, pairNumber, pairKind);
                    }
                }

                if (mapping.Start is { } start)
                {
                    var number = episodeNumber - mapping.Offset;

                    if (number >= start && number <= (mapping.End ?? int.MaxValue))
                    {
                        var source = Source(mapping, number);

                        return new AniDbAnimeListEpisode(entry.AnimeId, source.Number, source.Kind);
                    }
                }
            }
        }

        // Otherwise the entry that starts closest below this episode is the one holding it. An
        // entry that has a rule for the specials has already had its say above.
        AniDbAnimeListEntry? holder = null;

        foreach (var entry in siblings)
        {
            if (!string.Equals(entry.DefaultSeason, "0", StringComparison.Ordinal)
                || entry.EpisodeOffset >= episodeNumber
                || entry.Mappings.Any(mapping => mapping.TvdbSeason == 0))
            {
                continue;
            }

            if (holder == null || entry.EpisodeOffset > holder.EpisodeOffset)
            {
                holder = entry;
            }
        }

        return holder == null ? null : new AniDbAnimeListEpisode(holder.AnimeId, episodeNumber - holder.EpisodeOffset, AniDbEpisodeKind.Regular);
    }

    /// <summary>
    /// Which of an entry's numberings a rule reads a given episode from, and that episode's
    /// number within it.
    /// </summary>
    /// <remarks>
    /// The list writes the numbering as a season of its own: 0 for everything that is not an
    /// ordinary episode, within which the specials are numbered from 1 and AniDB's other
    /// episodes from 401. The band therefore belongs to each number a rule names rather than to
    /// the rule, one rule of Owarimonogatari (2017) naming 402 through 409.
    /// </remarks>
    /// <param name="mapping">The rule.</param>
    /// <param name="number">The number the rule names on the entry's side.</param>
    /// <returns>The numbering and the number within it.</returns>
    private static (AniDbEpisodeKind Kind, int Number) Source(AniDbAnimeListMapping mapping, int number)
    {
        if (mapping.AnidbSeason != 0)
        {
            return (AniDbEpisodeKind.Regular, number);
        }

        return number >= OtherEpisodeBand
            ? (AniDbEpisodeKind.Other, number - OtherEpisodeBand)
            : (AniDbEpisodeKind.Special, number);
    }

    /// <summary>
    /// Files an entry under whatever movie ids it carries.
    /// </summary>
    /// <remarks>
    /// The list carries IMDb and TMDB ids for the 1,634 entries that are movies, an entry's
    /// IMDb attribute holding several ids where one AniDB entry covers a movie released in
    /// parts. The first entry to claim an id keeps it: the list is written in AniDB order, so
    /// that is the earliest entry, and where two claim one movie the later is a remake or a
    /// recut listed against the same release.
    /// </remarks>
    /// <param name="element">The anime element.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <param name="movies">Every movie identified so far, by key.</param>
    private static void ReadMovieIds(XElement element, string animeId, Dictionary<string, AniDbAnimeListEpisode> movies)
    {
        var episode = new AniDbAnimeListEpisode(animeId, 1, AniDbEpisodeKind.Regular);

        foreach (var written in (element.Attribute("imdbid")?.Value ?? string.Empty).Split(','))
        {
            if (MovieKey.Imdb(written) is { } key)
            {
                movies.TryAdd(key, episode);
            }
        }

        if (MovieKey.Tmdb(element.Attribute("tmdbid")?.Value) is { } tmdbKey)
        {
            movies.TryAdd(tmdbKey, episode);
        }
    }

    private static AniDbAnimeListMapping? ReadMapping(XElement element)
    {
        if (element.Attribute("tvdbseason") == null)
        {
            return null;
        }

        var pairs = new List<KeyValuePair<int, int>>();

        foreach (var pair in (element.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('-', StringComparison.Ordinal);

            if (separator <= 0 || !int.TryParse(pair[..separator], CultureInfo.InvariantCulture, out var inEntry))
            {
                continue;
            }

            // One episode of the entry can fill several of the season's, written "1-1+2+3".
            // That is a movie the season numbering breaks into three episodes, as every season
            // of Ginga Eiyuu Densetsu: Die Neue These past the first is. Reading only up to the
            // plus left the whole rule unparsed, and the season was then placed as though the
            // two sides ran one to one, which sent every episode past the first movie's to an
            // episode of the entry that does not exist.
            foreach (var inSeason in pair[(separator + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(inSeason, CultureInfo.InvariantCulture, out var number))
                {
                    pairs.Add(new KeyValuePair<int, int>(inEntry, number));
                }
            }
        }

        return new AniDbAnimeListMapping(
            ReadInt(element.Attribute("anidbseason")?.Value),
            ReadInt(element.Attribute("tvdbseason")?.Value),
            ReadNullableInt(element.Attribute("start")?.Value),
            ReadNullableInt(element.Attribute("end")?.Value),
            ReadInt(element.Attribute("offset")?.Value),
            pairs);
    }

    private static int ReadInt(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int? ReadNullableInt(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
