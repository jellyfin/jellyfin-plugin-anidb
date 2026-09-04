using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// Turns a description as AniDB writes it into the markup Jellyfin shows.
/// <para>
/// AniDB writes a description as plain text with two things in it that mean nothing to a
/// browser: its own link syntax, which names the entry it points at in square brackets after
/// the URL, and a handful of BBCode tags, most often <c>[i]</c> around the note saying where
/// the description came from.
/// </para>
/// </summary>
internal static partial class AniDbDescription
{
    /// <summary>
    /// The BBCode tags with an HTML counterpart, and the element each becomes. Every other tag
    /// the pattern recognises is removed and its content kept.
    /// </summary>
    private static readonly Dictionary<string, string> _html = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i"] = "i",
        ["b"] = "b",
        ["u"] = "u",
        ["s"] = "s",
    };

    /// <summary>
    /// Prepares a description for display: graves, links, markup and line breaks, in the one
    /// order that works. Links are resolved before the markup, both being written in square
    /// brackets, and the line breaks last, so nothing else has to preserve them.
    /// </summary>
    /// <param name="text">The description as AniDB wrote it.</param>
    /// <returns>The description as Jellyfin shows it, which is empty where nothing was given.</returns>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = Plugin.Instance.Configuration.AniDbReplaceGraves
            ? text.Replace('`', '\'')
            : text;

        return ReplaceNewLine(ConvertMarkup(StripLinks(cleaned)));
    }

    /// <summary>
    /// Replaces AniDB's links with the name they carry, that being the only part of one worth
    /// reading: the URL leads back to AniDB rather than anywhere the reader can go from here.
    /// </summary>
    /// <param name="text">The text to transform.</param>
    /// <returns>The transformed text.</returns>
    public static string StripLinks(string text)
    {
        return AniDbUrlRegex().Replace(text, "${name}");
    }

    /// <summary>
    /// Turns the BBCode tags AniDB uses into HTML, and removes the ones with nothing to turn
    /// into. A tag whose opening and closing marks do not pair up is removed rather than
    /// converted: an element left open would run to the end of the description.
    /// </summary>
    /// <param name="text">The text to transform.</param>
    /// <returns>The transformed text.</returns>
    public static string ConvertMarkup(string text)
    {
        var matches = MarkupRegex().Matches(text);

        if (matches.Count == 0)
        {
            return text;
        }

        var balance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var tag = match.Groups["tag"].Value;

            if (_html.ContainsKey(tag))
            {
                balance[tag] = balance.GetValueOrDefault(tag) + (match.Groups["close"].Length == 0 ? 1 : -1);
            }
        }

        return MarkupRegex().Replace(
            text,
            match =>
            {
                var tag = match.Groups["tag"].Value;

                return _html.TryGetValue(tag, out var element) && balance.GetValueOrDefault(tag) == 0
                    ? FormattableString.Invariant($"<{match.Groups["close"].Value}{element}>")
                    : string.Empty;
            });
    }

    /// <summary>
    /// Replaces new lines with HTML line breaks, however the line was ended.
    /// </summary>
    /// <param name="text">The text to transform.</param>
    /// <returns>The transformed text.</returns>
    public static string ReplaceNewLine(string text)
    {
        return NewLineRegex().Replace(text, "<br>");
    }

    [GeneratedRegex(@"https?://anidb.net/\w+(/[0-9]+)? \[(?<name>[^\]]*)\]")]
    private static partial Regex AniDbUrlRegex();

    /// <summary>
    /// A BBCode tag, named rather than open ended: a description carries square brackets of its
    /// own, and an aside written "[see the sequel]" is text rather than markup.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\[(?<close>/?)(?<tag>i|b|u|s|url|code|spoiler|quote|center|color|size)(?:=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewLineRegex();
}
