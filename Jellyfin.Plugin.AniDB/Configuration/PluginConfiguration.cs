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
    /// The shortest cache window AniDB metadata may be reused for. AniDB permits few requests
    /// per IP per day, and a shorter window would get a library of any size banned mid-scan.
    /// </summary>
    public const int MinimumCacheAgeDays = 7;

    /// <summary>
    /// The shortest gap between two AniDB requests. AniDB bans a client that sends them
    /// closer together than roughly two seconds, so no lower value is accepted.
    /// </summary>
    public const int MinimumRequestIntervalMs = 2500;

    /// <summary>
    /// The tag weight AniDB itself treats as the line between a tag that describes the show
    /// and one a single user attached to it.
    /// </summary>
    public const int DefaultMinimumTagWeight = 400;

    private int _maxCacheAge = MinimumCacheAgeDays;

    private int _requestIntervalMs = MinimumRequestIntervalMs;

    private string _tagBlacklist = string.Empty;

    public PluginConfiguration()
    {
        TitlePreference = TitlePreferenceType.Localized;
        IgnoreSeason = false;
        TitleSimilarityThreshold = 50;
        MaxGenres = 5;
        TidyGenreList = true;
        TitleCaseGenres = true;
        AnimeDefaultGenre = AnimeDefaultGenreType.Anime;
        MaxCacheAge = 7;
        AniDbReplaceGraves = true;
        IncludeAdultTags = false;
        ImportGenres = true;
        ImportTags = true;
        UseAniDbSeasonNames = true;
        RequestIntervalMs = MinimumRequestIntervalMs;
        MinimumTagWeight = DefaultMinimumTagWeight;
        InfoboxTagsOnly = false;
        TagBlacklist = string.Empty;
    }

    /// <summary>
    /// Gets or sets the language the displayed title is taken from. The original title is not
    /// affected: it is always the romaji title, that being the Japanese title of the anime in
    /// the alphabet the rest of the world reads it in.
    /// </summary>
    public TitlePreferenceType TitlePreference { get; set; }

    public bool IgnoreSeason { get; set; }

    public int TitleSimilarityThreshold { get; set; }

    public int MaxGenres { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB's tags are shown as genres at all. With
    /// this off the genre list is left to whichever other provider fills it.
    /// </summary>
    public bool ImportGenres { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB's tags are kept as tags. Independent of
    /// <see cref="ImportGenres"/>.
    /// </summary>
    public bool ImportTags { get; set; }

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

    /// <summary>
    /// Gets or sets a value indicating whether a season is named after the AniDB entry it was
    /// filled from. With this off the season keeps the name Jellyfin gave it.
    /// </summary>
    public bool UseAniDbSeasonNames { get; set; }

    /// <summary>
    /// Gets or sets the shortest gap, in milliseconds, between two AniDB requests. Clamped to
    /// at least <see cref="MinimumRequestIntervalMs"/>. AniDB counts requests per IP, so raise
    /// this to leave room for another client on the same address.
    /// </summary>
    public int RequestIntervalMs
    {
        get => Math.Max(_requestIntervalMs, MinimumRequestIntervalMs);
        set => _requestIntervalMs = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the tags AniDB flags as 18+ content are kept.
    /// They are dropped by default. Anime AniDB flags as adult are rated as such either way,
    /// which is what parental controls act on.
    /// </summary>
    public bool IncludeAdultTags { get; set; }

    /// <summary>
    /// Gets or sets the weight a tag needs before it is imported. AniDB weights a tag by how
    /// much of the show it describes, so raising this keeps only the tags that characterise
    /// it.
    /// </summary>
    public int MinimumTagWeight { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only the tags AniDB shows in the infobox on the
    /// anime's page are imported. Those are the ones AniDB considers to describe the show
    /// rather than its setting or its cast.
    /// </summary>
    public bool InfoboxTagsOnly { get; set; }

    /// <summary>
    /// Gets or sets the tag names never to import, one per line or separated by commas.
    /// Matched whole and without regard to case.
    /// </summary>
    public string TagBlacklist
    {
        get => _tagBlacklist;
        set => _tagBlacklist = value ?? string.Empty;
    }

    /// <summary>
    /// Gets or sets the UTC time at which an AniDB ban is assumed to lapse. Persisted so that
    /// restarting the server does not hand a banned client a fresh allowance. Runtime state
    /// rather than a user setting.
    /// </summary>
    public DateTime AniDbBannedUntilUtc { get; set; }

    /// <summary>
    /// Gets or sets the backoff, in minutes, applied to the next detected AniDB ban. Runtime
    /// state rather than a user setting.
    /// </summary>
    public int AniDbBanBackoffMinutes { get; set; }
}
