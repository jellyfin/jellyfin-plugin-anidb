using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers
{
    internal class Equals_check
    {
        public readonly ILogger<Equals_check> _logger;

        public Equals_check(ILogger<Equals_check> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Cut p(%) away from the string
        /// </summary>
        /// <param name="input"></param>
        /// <param name="minLength"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        public static string ShortenString(string input, int minLength = 0, int p = 50)
        {
            if (input.Length <= minLength)
            {
                return input;
            }

            int newLength = (int)((float)input.Length - (((float)input.Length / 100f) * (float)p));

            if (newLength < minLength)
            {
                newLength = minLength;
            }

            return input.Substring(0, newLength);
        }

        /// <summary>
        /// Escape string for regex, but fuzzy
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        public static string FuzzyRegexEscape(string a)
        {
            a = Regex.Escape(a);

            // make characters that were escaped fuzzy
            a = a.Replace(@"\\", ".?");
            a = a.Replace(@"\*", ".?");
            a = a.Replace(@"\+", ".?");
            a = a.Replace(@"\?", ".?");
            a = a.Replace(@"\|", ".?");
            a = a.Replace(@"\{", ".?");
            a = a.Replace(@"\[", ".?");
            a = a.Replace(@"\(", ".?");
            a = a.Replace(@"\)", ".?");
            a = a.Replace(@"\^", ".?");
            a = a.Replace(@"\$", ".?");
            a = a.Replace(@"\.", ".?");
            a = a.Replace(@"\#", ".?");

            // whitespace
            a = a.Replace(@"\ ", ".?.?.?");
            a = Regex.Replace(a, @"\s", ".?.?.?");

            // other characters
            a = Regex.Replace(a, @"[!,–—_=~'`‚‘’„“”:;␣#@<>}\]\/\-]", ".?");

            // "words"
            a = Regex.Replace(a, @"s\b", ".?s");
            a = a.Replace("c", "(c|k)", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("k", "(c|k)", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("&", "(&|(and))", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("and", "(&|(and))", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("OVA", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("OAD", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("re", "re.?", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("Gekijyouban", "Gekijouban", StringComparison.OrdinalIgnoreCase);
            a = a.Replace("to aru", "to.?aru", StringComparison.OrdinalIgnoreCase);

            return a;
        }

        /// <summary>
        /// simple regex
        /// </summary>
        /// <param name="pattern"></param>
        /// <param name="input"></param>
        /// <param name="group"></param>
        /// <param name="matchInt"></param>
        /// <returns></returns>
        public async static Task<string> OneLineRegex(string pattern, string input, CancellationToken cancellationToken, int group = 1, int matchInt = 0)
        {
            int x = 0;
            foreach (Match match in Regex.Matches(input, pattern, RegexOptions.IgnoreCase))
            {
                if (x == matchInt)
                {
                    return await Task.Run(() => match.Groups[group].Value, cancellationToken);
                }
                x++;
            }
            return "";
        }

        /// <summary>
        /// Searches for possible AniDB IDs for name
        /// </summary>
        public async static Task<List<string>> XmlSearch(string name, CancellationToken cancellationToken, int x_ = 0)
        {
            var results = new List<string>();

            try
            {
                string xml = File.ReadAllText(GetAnidbXml());
                string s = "-";
                int x = 0;
                while (!string.IsNullOrEmpty(s))
                {
                    s = await OneLineRegex(@"<anime aid=""(\d+)"">(?>[^<>]+|<(?!\/anime>)[^<>]*>)*?.*" + await Task.Run(() => FuzzyRegexEscape(ShortenString(name, 6, 20)), cancellationToken), xml, cancellationToken, 1, x);
                    if (s != "")
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
                    await Task.Run(() => AniDbTitleDownloader.Load_static(cancellationToken), cancellationToken);
                    return await XmlSearch(name, cancellationToken, 1);
                }
            }

            return results;
        }

        /// <summary>
        /// Finds an AniDB ID for name
        /// </summary>
        public async static Task<string> XmlFindId(string name, CancellationToken cancellationToken, int x_ = 0)
        {
            var results = await XmlSearch(name, cancellationToken);

            if (results.Count == 1)
            {
                return results[0];
            }

            string xml = File.ReadAllText(GetAnidbXml());
            int lowestDistance = Plugin.Instance.Configuration.TitleSimilarityThreshold;
            string currentId = "";
            foreach (string id in results)
            {
                string nameXmlFromId = await OneLineRegex(@"<anime aid=""" + id + @"""((?s).*?)<\/anime>", xml, cancellationToken);

                string[] lines = nameXmlFromId.Split(
                    new string[] { "\r\n", "\r", "\n" },
                    StringSplitOptions.None
                );

                foreach (string line in lines)
                {
                    string nameFromId = await OneLineRegex(@"<title.*>([^<]+)</title>", line, cancellationToken);

                    if (!String.IsNullOrEmpty(nameFromId))
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
        private static int LevenshteinDistance(string str1, string str2)
        {
            var str1Length = str1.Length;
            var str2Length = str2.Length;

            if (str1Length == 0)
                return str2Length;

            if (str2Length == 0)
                return str1Length;

            var matrix = new int[str1Length + 1, str2Length + 1];

            for (var i = 0; i <= str1Length; matrix[i, 0] = i++) { }
            for (var j = 0; j <= str2Length; matrix[0, j] = j++) { }
            for (var i = 1; i <= str1Length; i++)
            {
                for (var j = 1; j <= str2Length; j++)
                {
                    var cost = (str2[j - 1] == str1[i - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }
            return matrix[str1Length, str2Length];
        }

        /// <summary>
        /// Gets the path of the AniDB titles.xml file
        /// </summary>
        /// <returns></returns>
        private static string GetAnidbXml()
        {
            return AniDbTitleDownloader.TitlesFilePath_;
        }
    }
}