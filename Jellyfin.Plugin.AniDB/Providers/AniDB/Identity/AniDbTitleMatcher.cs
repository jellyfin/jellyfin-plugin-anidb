using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

/// <summary>
/// Loads series titles from the titles file in the application data anidb folder and searches
/// them for the AniDB id of a series.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AniDbTitleMatcher"/> class.
/// </remarks>
/// <param name="logger">The logger.</param>
/// <param name="downloader">The AniDB title downloader.</param>
public sealed class AniDbTitleMatcher(ILogger<AniDbTitleMatcher> logger, IAniDbTitleDownloader downloader) : IAniDbTitleMatcher, IDisposable
{
    private static Dictionary<string, TitleInfo>? _titles;

    private readonly IAniDbTitleDownloader _downloader = downloader;
    private readonly ILogger<AniDbTitleMatcher> _logger = logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // todo replace the singleton IAniDbTitleMatcher with an injected dependency if/when MediaBrowser allows plugins to register their own components

    /// <summary>
    /// Gets or sets the global <see cref="IAniDbTitleMatcher"/> instance.
    /// </summary>
    public static IAniDbTitleMatcher DefaultInstance { get; set; } = null!;

    private static bool IsLoaded => _titles != null;

    /// <summary>
    /// Gets the title info for the given title.
    /// </summary>
    /// <param name="title">The title to look up.</param>
    /// <returns>The title info, or the default value when the title is unknown.</returns>
    public static TitleInfo GetTitleInfos(string title)
    {
        if (!string.IsNullOrEmpty(title)
            && _titles != null
            && _titles.TryGetValue(title, out TitleInfo info))
        {
            return info;
        }

        return default;
    }

    /// <summary>
    /// Finds the AniDB id for the series with the given title.
    /// </summary>
    /// <param name="title">The title of the series to search for.</param>
    /// <returns>The AniDB id of the series if found; else <c>null</c>.</returns>
    public Task<string?> FindSeries(string title)
    {
        return FindSeries(title, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<string?> FindSeries(string title, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsLoaded)
            {
                await Load(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }

        return LookupAniDbId(title) ?? LookupAniDbId(GetComparableName(title));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static string GetComparableName(string name)
    {
        name = name.ToLowerInvariant();
        name = name.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (c >= 0x2B0 && c <= 0x0333)
            {
                // Skip character modifiers and diacritics.
            }
            else if ("\"'!`?".Contains(c, StringComparison.Ordinal))
            {
                // Skip the characters being removed.
            }
            else if ("/,.:;\\(){}[]+-_=–*".Contains(c, StringComparison.Ordinal)) // (there are not actually two - in the they are different char codes)
            {
                sb.Append(' ');
            }
            else if (c == '&')
            {
                sb.Append(" and ");
            }
            else
            {
                sb.Append(c);
            }
        }

        name = sb.ToString();
        name = name.Replace(", the", string.Empty, StringComparison.Ordinal);
        name = name.Replace("the ", " ", StringComparison.Ordinal);
        name = name.Replace(" the ", " ", StringComparison.Ordinal);

        string prevName;
        do
        {
            prevName = name;
            name = name.Replace("  ", " ", StringComparison.Ordinal);
        }
        while (name.Length != prevName.Length);

        return name.Trim();
    }

    private static string? LookupAniDbId(string title)
    {
        if (_titles != null && _titles.TryGetValue(title, out TitleInfo info))
        {
            return info.AniDbId;
        }

        return null;
    }

    private static TitleType ParseType(string? type)
    {
        return type switch
        {
            "main" => TitleType.Main,
            "official" => TitleType.Official,
            "short" => TitleType.Short,
            "syn" => TitleType.Synonym,
            _ => TitleType.Synonym,
        };
    }

    private async Task Load(CancellationToken cancellationToken)
    {
        if (_titles == null)
        {
            _titles = new Dictionary<string, TitleInfo>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _titles.Clear();
        }

        try
        {
            await _downloader.Load(cancellationToken).ConfigureAwait(false);
            await ReadTitlesFile().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load AniDB titles");
        }
    }

    private async Task ReadTitlesFile()
    {
        _logger.LogDebug("Loading AniDB titles");

        var titles = _titles;
        if (titles == null)
        {
            return;
        }

        var titlesFile = _downloader.TitlesFilePath;

        var settings = new XmlReaderSettings
        {
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using (var stream = new StreamReader(titlesFile, Encoding.UTF8))
        using (var reader = XmlReader.Create(stream, settings))
        {
            string? aid = null;

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "anime":
                            reader.MoveToAttribute("aid");
                            aid = reader.Value;
                            break;

                        case "title":
                            var title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(aid) && !string.IsNullOrEmpty(title))
                            {
                                var type = ParseType(reader.GetAttribute("type"));

                                if (!titles.TryGetValue(title, out TitleInfo currentTitleInfo) || (int)currentTitleInfo.Type < (int)type)
                                {
                                    titles[title] = new TitleInfo { AniDbId = aid, Type = type, Title = title };
                                }
                            }

                            break;
                    }
                }
            }
        }

        var comparable = (from pair in titles
                          let comp = GetComparableName(pair.Key)
                          where !titles.ContainsKey(comp)
                          select new { Title = comp, Id = pair.Value })
                         .ToArray();

        foreach (var pair in comparable)
        {
            titles[pair.Title] = pair.Id;
        }
    }
}
