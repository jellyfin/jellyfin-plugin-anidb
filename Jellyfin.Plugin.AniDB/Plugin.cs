using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
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
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="matcherLogger">Instance of the <see cref="ILogger{AniDbTitleMatcher}"/> interface.</param>
    /// <param name="downloaderLogger">Instance of the <see cref="ILogger{AniDbTitleDownloader}"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<AniDbTitleMatcher> matcherLogger,
        ILogger<AniDbTitleDownloader> downloaderLogger,
        IHttpClientFactory httpClientFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _httpClientFactory = httpClientFactory;

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
    public override string Name => Constants.PluginName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(Constants.PluginGuid);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured with the plugin user agent.
    /// </summary>
    /// <returns>The configured <see cref="HttpClient"/>.</returns>
    public HttpClient GetHttpClient()
    {
        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(Name, Version.ToString()));

        return httpClient;
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
