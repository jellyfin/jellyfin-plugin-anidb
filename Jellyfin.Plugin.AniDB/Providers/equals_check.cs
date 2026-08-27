using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

namespace Jellyfin.Plugin.AniDB.Providers
{
    /// <summary>
    /// Fuzzy matching helpers used to map a series name onto an AniDB id.
    /// </summary>
    internal static partial class Equals_check
    {
        private static readonly Regex _whitespaceRegex = MyRegex();
        private static readonly Regex _specialCharacterRegex = new(@"[!,–—_=~'`‚‘’„“”:;␣#@<>}\]\/\-]", RegexOptions.Compiled);
        private static readonly Regex _sAtEndBoundaryRegex = new(@"s\b", RegexOptions.Compiled);
        private static readonly Regex _titleRegex = new(@"<title.*>([^<]+)</title>", RegexOptions.Compiled);
        private static readonly Regex _stripYearRegex = new(@" \([0-9]{4}\)$", RegexOptions.Compiled);

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
            a = _whitespaceRegex.Replace(a, ".?.?.?");

            // other characters
            a = _specialCharacterRegex.Replace(a, ".?");

            // "words"
            a = _sAtEndBoundaryRegex.Replace(a, ".?s");
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
            var results = new List<string>();

            try
            {
                string xml = await File.ReadAllTextAsync(GetAnidbXml(), cancellationToken).ConfigureAwait(false);
                string s = "-";
                int x = 0;
                string strippedName = _stripYearRegex.Replace(name, string.Empty);
                Regex searchRegex = new Regex(@"<anime aid=""([0-9]+)"">(?>[^<>]+|<(?!\/anime>)[^<>]*>)*?.*" + FuzzyRegexEscape(ShortenString(strippedName, 6, 20)), RegexOptions.IgnoreCase | RegexOptions.Compiled);
                while (!string.IsNullOrEmpty(s))
                {
                    s = OneLineRegex(searchRegex, xml, 1, x);
                    if (!string.IsNullOrEmpty(s))
                    {
                        results.Add(s);
                    }

                    x++;
                }
            }
            catch (Exception)
            {
                if (x_ == 0)
                {
                    await Task.Run(() => AniDbTitleDownloader.LoadStatic(cancellationToken), cancellationToken).ConfigureAwait(false);
                    return await XmlSearch(name, cancellationToken, 1).ConfigureAwait(false);
                }
            }

            return results;
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
            var results = await XmlSearch(name, cancellationToken).ConfigureAwait(false);

            if (results.Count == 1)
            {
                return results[0];
            }

            string xml = await File.ReadAllTextAsync(GetAnidbXml(), cancellationToken).ConfigureAwait(false);
            int lowestDistance = Plugin.Instance.Configuration.TitleSimilarityThreshold;
            string currentId = string.Empty;

            foreach (string id in results)
            {
                string nameXmlFromId = OneLineRegex(new Regex(@"<anime aid=""" + id + @"""((?s).*?)<\/anime>", RegexOptions.Compiled), xml);

                string[] lines = nameXmlFromId.Split(
                    ["\r\n", "\r", "\n"],
                    StringSplitOptions.None);

                foreach (string line in lines)
                {
                    string nameFromId = OneLineRegex(_titleRegex, line);

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
        /// Gets the path of the AniDB titles.xml file.
        /// </summary>
        /// <returns>The path of the AniDB titles.xml file.</returns>
        private static string GetAnidbXml()
        {
            return AniDbTitleDownloader.StaticTitlesFilePath;
        }

        [GeneratedRegex(@"\s", RegexOptions.Compiled)]
        private static partial Regex MyRegex();
    }
}
