using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB;

/// <summary>
/// Class Plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Identify the plugin rather than the service it talks to: AniDB bans by client, so the
    // header should say what this actually is. Built once, since it never varies.
    private static readonly ProductInfoHeaderValue _userAgentProduct = new("jellyfin-plugin-anidb", ResolveVersion());

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="matcherLogger">Instance of the <see cref="ILogger{AniDbTitleMatcher}"/> interface.</param>
    /// <param name="downloaderLogger">Instance of the <see cref="ILogger{AniDbTitleDownloader}"/> interface.</param>
    /// <param name="seriesLogger">Instance of the <see cref="ILogger{AniDbSeriesProvider}"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<AniDbTitleMatcher> matcherLogger,
        ILogger<AniDbTitleDownloader> downloaderLogger,
        ILogger<AniDbSeriesProvider> seriesLogger,
        IHttpClientFactory httpClientFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _httpClientFactory = httpClientFactory;

        // The AniDB ban state is global to the plugin, so its logger has to be too. The
        // logger must be in place before the ban is restored, or the warning is lost.
        AniDbSeriesProvider.Logger = seriesLogger;
        AniDbSeriesProvider.RestoreBanState(Configuration);

        AniDbTitleMatcher.DefaultInstance = new AniDbTitleMatcher(
            matcherLogger,
            new AniDbTitleDownloader(downloaderLogger, applicationPaths));
    }

    /// <summary>
    /// Gets the instance.
    /// </summary>
    /// <value>The instance.</value>
    public static Plugin Instance { get; private set; } = null!;

    /// <inheritdoc />
    public override string Name => "AniDB";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a2b2a7ed-aa28-4521-a64a-63d86901f246");

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured with the plugin user agent.
    /// </summary>
    /// <returns>The configured <see cref="HttpClient"/>.</returns>
    public HttpClient GetHttpClient()
    {
        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.DefaultRequestHeaders.UserAgent.Add(_userAgentProduct);

        return httpClient;
    }

    /// <summary>
    /// Resolves the plugin version for the user agent. The assembly version is used rather
    /// than the informational version because the latter may carry a build metadata suffix,
    /// which is not a legal product version token and would throw when parsed.
    /// </summary>
    /// <returns>The plugin version.</returns>
    private static string ResolveVersion()
    {
        var version = typeof(Plugin).Assembly.GetName().Version;

        return version is null
            ? "0.0.0.0"
            : version.ToString();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
