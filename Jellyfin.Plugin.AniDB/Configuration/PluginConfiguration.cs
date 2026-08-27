using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniDB.Configuration;

public enum TitlePreferenceType
{
    /// <summary>
    /// Use titles in the local metadata language.
    /// </summary>
    Localized,

    /// <summary>
    /// Use titles in Japanese.
    /// </summary>
    Japanese,

    /// <summary>
    /// Use titles in Japanese romaji.
    /// </summary>
    JapaneseRomaji
}

public enum AnimeDefaultGenreType
{
    None,
    Anime,
    Animation
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The shortest cache window AniDB metadata may be reused for. AniDB permits only a low
    /// number of requests per IP per day, so a shorter window would make a library of any
    /// size impossible to scan without being banned.
    /// </summary>
    public const int MinimumCacheAgeDays = 7;

    private int _maxCacheAge = MinimumCacheAgeDays;

    public PluginConfiguration()
    {
        TitlePreference = TitlePreferenceType.Localized;
        OriginalTitlePreference = TitlePreferenceType.JapaneseRomaji;
        IgnoreSeason = false;
        TitleSimilarityThreshold = 50;
        MaxGenres = 5;
        TidyGenreList = true;
        TitleCaseGenres = false;
        AnimeDefaultGenre = AnimeDefaultGenreType.Anime;
        MaxCacheAge = 7;
        AniDbReplaceGraves = true;
    }

    public TitlePreferenceType TitlePreference { get; set; }

    public TitlePreferenceType OriginalTitlePreference { get; set; }

    public bool IgnoreSeason { get; set; }

    public int TitleSimilarityThreshold { get; set; }

    public int MaxGenres { get; set; }

    public bool TidyGenreList { get; set; }

    public bool TitleCaseGenres { get; set; }

    public AnimeDefaultGenreType AnimeDefaultGenre { get; set; }

    /// <summary>
    /// Gets or sets the number of days cached series metadata is reused before AniDB is
    /// queried again. Clamped to at least <see cref="MinimumCacheAgeDays"/> days.
    /// </summary>
    public int MaxCacheAge
    {
        get => Math.Max(_maxCacheAge, MinimumCacheAgeDays);
        set => _maxCacheAge = value;
    }

    public bool AniDbReplaceGraves { get; set; }
}
