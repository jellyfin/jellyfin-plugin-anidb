using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

namespace Jellyfin.Plugin.AniDB.Providers;

/// <summary>
/// Fuzzy matching helpers used to map a series name onto an AniDB id.
/// </summary>
internal static partial class Equals_check
{
    /// <summary>
    /// Cut p(%) away from the string.
    /// </summary>
    /// <param name="input">The string to shorten.</param>
    /// <param name="minLength">The minimum length of the result.</param>
    /// <param name="p">The percentage to cut away.</param>
    /// <returns>The shortened string.</returns>
    public static string ShortenString(string input, int minLength = 0, int p = 50)
    {
        if (input.Length <= minLength)
        {
            return input;
        }

        int newLength = (int)(input.Length - ((input.Length / 100f) * p));

        if (newLength < minLength)
        {
            newLength = minLength;
        }

        return input[..newLength];
    }

    /// <summary>
    /// Escape string for regex, but fuzzy.
    /// </summary>
    /// <param name="a">The string to escape.</param>
    /// <returns>The fuzzy regex pattern.</returns>
    public static string FuzzyRegexEscape(string a)
    {
        a = Regex.Escape(a);

        // make characters that were escaped fuzzy
        a = a.Replace(@"\\", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\*", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\+", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\?", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\|", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\{", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\[", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\(", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\)", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\^", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\$", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\.", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\#", ".?", StringComparison.Ordinal);

        // whitespace
        a = a.Replace(@"\ ", ".?.?.?", StringComparison.Ordinal);
        a = WhitespaceRegex().Replace(a, ".?.?.?");

        // other characters
        a = SpecialCharacterRegex().Replace(a, ".?");

        // "words"
        a = SAtEndBoundaryRegex().Replace(a, ".?s");
        a = a.Replace("Gekijyouban", "Gekijouban", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("Mahoutsukai", "Mahou ?tsukai", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("to aru", "to ?aru", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("re", "re.?", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("OVA", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("OAD", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("wo", "w?o", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("c", "(c|k)", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("k", "(c|k)", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("n", "n`?", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("&", "(&|(and))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("and", "(&|(and))", StringComparison.OrdinalIgnoreCase);

        return a;
    }

    /// <summary>
    /// simple regex.
    /// </summary>
    /// <param name="regex">The regex to match with.</param>
    /// <param name="input">The input to match against.</param>
    /// <param name="group">The capture group to return.</param>
    /// <param name="matchInt">The index of the match to return.</param>
    /// <returns>The captured value, or an empty string when there is no match.</returns>
    public static string OneLineRegex(Regex regex, string input, int group = 1, int matchInt = 0)
    {
        int x = 0;
        foreach (Match match in regex.Matches(input))
        {
            if (x == matchInt)
            {
                return match.Groups[group].Value;
            }

            x++;
        }

        return string.Empty;
    }

    /// <summary>
    /// Searches for possible AniDB IDs for name.
    /// </summary>
    /// <param name="name">The name to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="x_">The current attempt; the titles file is downloaded once when it cannot be read.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation, containing the matching AniDB IDs.</returns>
    public static async Task<List<string>> XmlSearch(string name, CancellationToken cancellationToken, int x_ = 0)
    {
        string? xml = await ReadTitlesXml(x_, cancellationToken).ConfigureAwait(false);

        return xml is null ? [] : SearchTitlesXml(xml, name);
    }

    /// <summary>
    /// Finds an AniDB ID for name.
    /// </summary>
    /// <param name="name">The name to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="x_">The current attempt; the titles file is downloaded once when it cannot be read.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation, containing the best matching AniDB ID.</returns>
    public static async Task<string> XmlFindId(string name, CancellationToken cancellationToken, int x_ = 0)
    {
        // Read the titles file once and reuse it for both the search and the comparison
        // below; it is several megabytes, and this used to read it twice per lookup.
        string? xml = await ReadTitlesXml(x_, cancellationToken).ConfigureAwait(false);
        if (xml is null)
        {
            return string.Empty;
        }

        var results = SearchTitlesXml(xml, name);

        if (results.Count == 1)
        {
            return results[0];
        }

        int lowestDistance = Plugin.Instance.Configuration.TitleSimilarityThreshold;
        string currentId = string.Empty;

        // Index every entry once with a constant pattern, rather than building and compiling a
        // fresh regex per candidate id inside the loop. Keep the first entry for an id so the
        // behaviour matches the previous "first match wins" lookup.
        var entriesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match entry in AnimeEntryRegex().Matches(xml))
        {
            entriesById.TryAdd(entry.Groups[1].Value, entry.Groups[2].Value);
        }

        foreach (string id in results)
        {
            if (!entriesById.TryGetValue(id, out string? nameXmlFromId))
            {
                continue;
            }

            string[] lines = nameXmlFromId.Split(
                ["\r\n", "\r", "\n"],
                StringSplitOptions.None);

            foreach (string line in lines)
            {
                string nameFromId = OneLineRegex(TitleRegex(), line);

                if (!string.IsNullOrEmpty(nameFromId))
                {
                    int stringDistance = LevenshteinDistance(name, nameFromId);
                    if (lowestDistance > stringDistance)
                    {
                        lowestDistance = stringDistance;
                        currentId = id;
                    }
                }
            }
        }

        return currentId;
    }

    /// <summary>
    /// Calculates the Levenshtein distance - a metric for measuring the difference between two strings.
    /// The higher the number, the more different the two strings are.
    /// </summary>
    /// <param name="str1">The first string.</param>
    /// <param name="str2">The second string.</param>
    /// <returns>The Levenshtein distance between both strings.</returns>
    private static int LevenshteinDistance(string str1, string str2)
    {
        var str1Length = str1.Length;
        var str2Length = str2.Length;

        if (str1Length == 0)
        {
            return str2Length;
        }

        if (str2Length == 0)
        {
            return str1Length;
        }

        var matrix = new int[str1Length + 1][];

        for (var i = 0; i <= str1Length; i++)
        {
            matrix[i] = new int[str2Length + 1];
            matrix[i][0] = i;
        }

        for (var j = 0; j <= str2Length; j++)
        {
            matrix[0][j] = j;
        }

        for (var i = 1; i <= str1Length; i++)
        {
            for (var j = 1; j <= str2Length; j++)
            {
                var cost = (str2[j - 1] == str1[i - 1]) ? 0 : 1;
                matrix[i][j] = Math.Min(
                    Math.Min(matrix[i - 1][j] + 1, matrix[i][j - 1] + 1),
                    matrix[i - 1][j - 1] + cost);
            }
        }

        return matrix[str1Length][str2Length];
    }

    /// <summary>
    /// Reads the AniDB titles file, downloading it once if it cannot be read.
    /// </summary>
    /// <param name="attempt">The current attempt; the file is downloaded only on the first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file contents, or <see langword="null"/> when it could not be read.</returns>
    private static async Task<string?> ReadTitlesXml(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(GetAnidbXml(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (attempt != 0)
            {
                return null;
            }
        }

        await Task.Run(() => AniDbTitleDownloader.LoadStatic(cancellationToken), cancellationToken).ConfigureAwait(false);

        return await ReadTitlesXml(1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects the AniDB ids whose entry fuzzily matches a name.
    /// </summary>
    /// <param name="xml">The contents of the AniDB titles file.</param>
    /// <param name="name">The name to search for.</param>
    /// <returns>The matching AniDB ids.</returns>
    private static List<string> SearchTitlesXml(string xml, string name)
    {
        var results = new List<string>();
        string strippedName = StripYearRegex().Replace(name, string.Empty);

        // The pattern embeds the search term as a fuzzy expression, so it cannot be a
        // [GeneratedRegex]. Compiled is worth its build cost here: the atomic group
        // backtracks heavily across a multi-megabyte document.
        var searchRegex = new Regex(
            @"<anime aid=""([0-9]+)"">(?>[^<>]+|<(?!\/anime>)[^<>]*>)*?.*" + FuzzyRegexEscape(ShortenString(strippedName, 6, 20)),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Enumerate the matches once. Indexing into them one at a time re-scanned the whole
        // document for every result, which made a common name quadratic in its match count.
        foreach (Match match in searchRegex.Matches(xml))
        {
            string id = match.Groups[1].Value;
            if (string.IsNullOrEmpty(id))
            {
                break;
            }

            results.Add(id);
        }

        return results;
    }

    /// <summary>
    /// Gets the path of the AniDB titles.xml file.
    /// </summary>
    /// <returns>The path of the AniDB titles.xml file.</returns>
    private static string GetAnidbXml()
    {
        return AniDbTitleDownloader.StaticTitlesFilePath;
    }

    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[!,–—_=~'`‚‘’„“”:;␣#@<>}\]\/\-]")]
    private static partial Regex SpecialCharacterRegex();

    [GeneratedRegex(@"s\b")]
    private static partial Regex SAtEndBoundaryRegex();

    [GeneratedRegex(@"<title.*>([^<]+)</title>")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@" \([0-9]{4}\)$")]
    private static partial Regex StripYearRegex();

    [GeneratedRegex(@"<anime aid=""([0-9]+)""((?s).*?)</anime>")]
    private static partial Regex AnimeEntryRegex();
}
