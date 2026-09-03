using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AniDB.Configuration;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

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
            // English comes last among the candidates, after the language actually asked for.
            // AniDB holds an official English title for nearly every anime, and it, like the
            // rest of the candidates, is written in the Latin alphabet, which is what a
            // displayed title wants to be. Romaji is a transliteration of the Japanese title
            // rather than a title anyone gave the show, so it is left to the original title
            // and only used here where there is nothing else.
            var candidates = LanguageCandidates(metadataLanguage).Append("en").Distinct(StringComparer.Ordinal);
            var localized = InLanguage(titlesList, candidates);

            if (localized != null)
            {
                return localized;
            }
        }

        if (preference == TitlePreferenceType.Japanese)
        {
            var japanese = InLanguage(titlesList, ["ja"]);

            if (japanese != null)
            {
                return japanese;
            }
        }

        // The main title, which is romaji.
        return titlesList.FirstOrDefault(t => IsLanguage(t, "x-jat") && t.Type == "main") ??
               titlesList.FirstOrDefault(t => t.Type == "main") ??
               titlesList.FirstOrDefault();
    }

    /// <summary>
    /// The first title in one of the given languages, taking the candidates in the order given
    /// and, within a language, an official title over a synonym.
    /// </summary>
    /// <param name="titles">The available titles.</param>
    /// <param name="candidates">The language tags to look for, most preferred first.</param>
    /// <returns>The matching title, or <c>null</c> where none of the languages is present.</returns>
    private static Title? InLanguage(IList<Title> titles, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = titles.FirstOrDefault(t => IsLanguage(t, candidate) && t.Type == "main") ??
                        titles.FirstOrDefault(t => IsLanguage(t, candidate) && t.Type == "official") ??
                        titles.FirstOrDefault(t => IsLanguage(t, candidate) && t.Type == "synonym");

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a title is written in the language of the given tag. A tag naming only a
    /// language matches any of AniDB's regional or script variants of it, so that a candidate
    /// of <c>pt</c> is answered by AniDB's <c>pt-BR</c> and one of <c>zh</c> by <c>zh-Hans</c>.
    /// </summary>
    /// <param name="title">The title to test.</param>
    /// <param name="candidate">The language tag to test it against, in lower case.</param>
    /// <returns><c>true</c> where the title is in that language.</returns>
    private static bool IsLanguage(Title title, string candidate)
    {
        var language = title.Language;

        if (string.IsNullOrEmpty(language))
        {
            return false;
        }

        return string.Equals(language, candidate, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(PrimaryTag(language), candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The language tags to try for the metadata language Jellyfin asked for, most preferred
    /// first. Jellyfin names a language by a code that may carry a country, such as
    /// <c>pt-br</c> or <c>zh-cn</c>, whereas AniDB names one either plainly, as <c>pt</c>, or
    /// by script, as <c>zh-Hans</c>. Asking for the exact code alone therefore misses a title
    /// AniDB does hold, and everything falls through to romaji.
    /// </summary>
    /// <param name="metadataLanguage">The metadata language Jellyfin asked for.</param>
    /// <returns>The language tags to look for, in lower case.</returns>
    private static IEnumerable<string> LanguageCandidates(string metadataLanguage)
    {
        if (string.IsNullOrWhiteSpace(metadataLanguage))
        {
            return [];
        }

        var requested = metadataLanguage.Trim().ToLowerInvariant();
        var primary = PrimaryTag(requested);
        var candidates = new List<string> { requested };

        // AniDB writes Chinese by script rather than by country. Which script a country
        // reads is not something the country code itself says, so map the ones AniDB's
        // titles are actually written in, and offer the other script last: a title in the
        // wrong Chinese script is still closer than a romaji one.
        if (primary == "zh")
        {
            var simplified = requested is "zh" or "zh-cn" or "zh-sg" or "zh-my" or "zh-hans";

            candidates.Add(simplified ? "zh-hans" : "zh-hant");
            candidates.Add(simplified ? "zh-hant" : "zh-hans");
        }

        candidates.Add(primary);

        return candidates.Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// The part of a language tag naming the language itself, without the country or script
    /// that may follow it.
    /// </summary>
    /// <param name="tag">The language tag.</param>
    /// <returns>The tag's first subtag.</returns>
    private static string PrimaryTag(string tag)
    {
        var separator = tag.IndexOf('-', StringComparison.Ordinal);

        return separator < 0 ? tag : tag[..separator];
    }
}
