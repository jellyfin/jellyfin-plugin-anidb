using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniDB.Configuration
{
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
        None, Anime, Animation
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
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
            AniDbRateLimit = 2000;
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

        public int AniDbRateLimit { get; set; }

        public int MaxCacheAge { get; set; }

        public bool AniDbReplaceGraves { get; set; }
    }
}
