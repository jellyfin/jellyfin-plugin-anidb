using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AniDB.Configuration;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AniDB.Providers;

public static class GenreHelper
{
    /// <summary>
    /// Maps the AniDB tags that describe what a show is onto the genre names to display, and
    /// by doing so decides which tags are genres at all. Of AniDB's ~4700 tags, nearly all name
    /// a plot element, a setting or a production detail; anything absent from this table is
    /// left as a tag only.
    /// </summary>
    private static readonly Dictionary<string, string> GenreMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core genres.
        { "action", "Action" },
        { "adventure", "Adventure" },
        { "comedy", "Comedy" },
        { "parody", "Comedy" },
        { "horror", "Horror" },
        { "mystery", "Mystery" },
        { "detective", "Mystery" },
        { "romance", "Romance" },
        { "thriller", "Thriller" },
        { "psychological", "Psychological Thriller" },
        { "tragedy", "Tragedy" },
        { "crime", "Crime" },
        { "police", "Crime" },
        { "historical", "Historical" },
        { "samurai", "Historical" },
        { "military", "Military" },
        { "war", "Military" },
        { "music", "Music" },
        { "idol", "Music" },
        { "sports", "Sports" },
        { "cooking", "Cooking" },

        // Fantasy and the creature tags that stand in for it.
        { "fantasy", "Fantasy" },
        { "contemporary fantasy", "Fantasy" },
        { "dark fantasy", "Fantasy" },
        { "high fantasy", "Fantasy" },
        { "magic", "Fantasy" },
        { "dragon", "Fantasy" },
        { "demon", "Supernatural" },
        { "angel", "Supernatural" },
        { "ghost", "Supernatural" },
        { "vampire", "Supernatural" },
        { "zombie", "Supernatural" },
        { "magical girl", "Mahou Shoujo" },
        { "isekai", "Isekai" },

        // Science fiction and its sub-genres.
        { "science fiction", "Sci-Fi" },
        { "space opera", "Sci-Fi" },
        { "cyberpunk", "Sci-Fi" },
        { "post-apocalyptic", "Sci-Fi" },
        { "space", "Sci-Fi" },
        { "time travel", "Sci-Fi" },
        { "android", "Sci-Fi" },
        { "mecha", "Mecha" },
        { "robot", "Mecha" },

        // Setting and combat tags that read as genres.
        { "daily life", "Slice of Life" },
        { "school life", "School Life" },
        { "high school", "School Life" },
        { "martial arts", "Martial Arts" },
        { "super power", "Super Power" },
        { "swordplay", "Action" },
        { "gunfights", "Action" },

        // Target audience.
        { "shounen", "Shounen" },
        { "shoujo", "Shoujo" },
        { "seinen", "Seinen" },
        { "josei", "Josei" },
        { "kodomo", "Kodomo" },

        // Romance sub-genres and content rating.
        { "harem", "Harem" },
        { "reverse harem", "Harem" },
        { "ecchi", "Ecchi" },
        { "yuri", "Yuri" },
        { "shoujo ai", "Yuri" },
        { "yaoi", "Yaoi" },
        { "shounen ai", "Yaoi" },
        { "18 restricted", "Adult" },
        { "pornography", "Adult" },
    };

    /// <summary>
    /// Genres that are implied by a more specific one, and so are dropped when it is present.
    /// Keyed by the specific genre, valued by the one it makes redundant.
    /// </summary>
    private static readonly Dictionary<string, string> IgnoreIfPresent = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Psychological Thriller", "Thriller" }
    };

    private static readonly string[] second = ["Animation", "Anime"];

    public static void CleanupGenres(Series series)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;

        // Before mapping, so the names the table produces keep the casing it spells them
        // with: title casing "Slice of Life" would give "Slice Of Life".
        if (config.TitleCaseGenres)
        {
            series.Genres = [.. series.Genres.Select(TitleCase)];
            series.Tags = [.. series.Tags.Select(TitleCase)];
        }

        if (!config.ImportTags)
        {
            series.Tags = [];
        }

        if (!config.ImportGenres)
        {
            series.Genres = [];

            return;
        }

        if (config.TidyGenreList)
        {
            TidyGenres(series);

            series.Genres = [.. RemoveRedundantGenres(series.Genres).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        if (config.AnimeDefaultGenre != AnimeDefaultGenreType.None)
        {
            series.Genres = [.. series.Genres
                .Except(second, StringComparer.OrdinalIgnoreCase)
                .Prepend(config.AnimeDefaultGenre.ToString())];
        }

        if (config.MaxGenres > 0)
        {
            series.Genres = [.. series.Genres.Take(config.MaxGenres)];
        }

        series.Genres = [.. series.Genres.OrderBy(i => i)];
    }

    /// <summary>
    /// Keeps only the AniDB tags that name a genre, under the genre name to display them by.
    /// The rest are already held as tags.
    /// </summary>
    /// <param name="series">The series whose genres to sort out.</param>
    public static void TidyGenres(Series series)
    {
        // Insertion order is kept so the genres AniDB weighted highest survive the MaxGenres
        // trim, which happens before the list is sorted for display.
        var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string genre in series.Genres)
        {
            if (GenreMappings.TryGetValue(genre, out var mapped))
            {
                genres.Add(mapped);
            }
        }

        series.Genres = [.. genres];
    }

    private static string TitleCase(string value)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);

    public static IEnumerable<string> RemoveRedundantGenres(IEnumerable<string> genres)
    {
        var list = genres as IList<string> ?? [.. genres];

        var toRemove = list.Where(IgnoreIfPresent.ContainsKey).Select(genre => IgnoreIfPresent[genre]).ToList();
        return list.Where(genre => !toRemove.Contains(genre, StringComparer.OrdinalIgnoreCase));
    }
}
