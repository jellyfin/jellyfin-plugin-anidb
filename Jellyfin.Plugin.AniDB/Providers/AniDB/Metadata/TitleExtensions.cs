using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AniDB.Configuration;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    /// <summary>
    /// Extension methods for collections of <see cref="Title"/>.
    /// </summary>
    public static class TitleExtensions
    {
        /// <summary>
        /// Picks the title which best matches the given preference and metadata language.
        /// </summary>
        /// <param name="titles">The available titles.</param>
        /// <param name="preference">The title preference.</param>
        /// <param name="metadataLanguage">The preferred metadata language.</param>
        /// <returns>The best matching title, or <c>null</c> when there are no titles.</returns>
        public static Title? Localize(this IEnumerable<Title> titles, TitlePreferenceType preference, string metadataLanguage)
        {
            var titlesList = titles as IList<Title> ?? [.. titles];

            if (preference == TitlePreferenceType.Localized)
            {
                // prefer an official title, else look for a synonym
                var localized = titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "main") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "official") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "synonym");

                if (localized != null)
                {
                    return localized;
                }
            }

            if (preference == TitlePreferenceType.Japanese)
            {
                // prefer an official title, else look for a synonym
                var japanese = titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "main") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "official") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "synonym");

                if (japanese != null)
                {
                    return japanese;
                }
            }

            // return the main title (romaji)
            return titlesList.FirstOrDefault(t => t.Language == "x-jat" && t.Type == "main") ??
                   titlesList.FirstOrDefault(t => t.Type == "main") ??
                   titlesList.FirstOrDefault();
        }
    }
}
