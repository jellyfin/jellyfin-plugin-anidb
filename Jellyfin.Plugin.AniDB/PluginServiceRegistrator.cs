using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniDB;

/// <summary>
/// Registers the <see cref="HttpClient"/> every AniDB request is sent with.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);

        serviceCollection.AddHttpClient(Plugin.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                    applicationHost.Name.Replace(' ', '-'),
                    applicationHost.ApplicationVersionString));
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                    "jellyfin-plugin-anidb",
                    ResolveVersion()));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
            })
            // Mirrors the handler Jellyfin configures for its own named clients, which a plugin
            // client does not inherit. The titles dump is fetched as a gzip file and unpacked by
            // hand, so it depends on this matching what the default client used to do.
            .ConfigurePrimaryHttpMessageHandler(_ => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8
            });
    }

    /// <summary>
    /// Resolves the plugin version for the user agent. The assembly version is used because
    /// the informational version may carry a build metadata suffix, which is not a legal
    /// product version token and throws when parsed.
    /// </summary>
    /// <returns>The plugin version.</returns>
    private static string ResolveVersion()
    {
        var version = typeof(Plugin).Assembly.GetName().Version;

        return version is null
            ? "0.0.0.0"
            : version.ToString();
    }
}
