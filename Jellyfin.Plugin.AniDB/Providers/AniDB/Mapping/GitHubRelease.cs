using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The GitHub releases API, asked which build a release is currently holding, so that a file
/// already downloaded from it is not downloaded a second time.
/// </summary>
/// <remarks>
/// A release is asked for by tag rather than by asking for the newest one there is. The tag
/// names the schema of the file behind it and the reader here understands one schema, so a
/// release cut for a later one must not be taken up on its own: it is adopted by raising the
/// tag named in the source, alongside whatever the reader needs to understand it. What does
/// move under a tag is the build attached to it - the mappings are rebuilt daily - and that is
/// what this reports.
/// </remarks>
internal static class GitHubRelease
{
    /// <summary>
    /// The named asset of the release under the given tag.
    /// </summary>
    /// <param name="httpClient">The client to ask with, which must carry a user agent: the API refuses a request without one.</param>
    /// <param name="owner">The account the repository belongs to.</param>
    /// <param name="repository">The repository the release is in.</param>
    /// <param name="tag">The tag of the release.</param>
    /// <param name="assetName">The file name of the asset.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Where to download the asset and what identifies this build of it, or <c>null</c> where the release carries no asset of that name.</returns>
    public static async Task<MappingSourceBuild?> ResolveAsset(
        HttpClient httpClient,
        string owner,
        string repository,
        string tag,
        string assetName,
        CancellationToken cancellationToken)
    {
        var url = new Uri(FormattableString.Invariant($"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}"));

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                using (var document = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false))
                {
                    return FindAsset(document, tag, assetName);
                }
            }
        }
    }

    /// <summary>
    /// The asset of that name among the release's own, as the API describes them.
    /// </summary>
    /// <param name="document">The release, as the API returned it.</param>
    /// <param name="tag">The tag of the release, which goes into the version so that raising the tag counts as a new build whatever the assets under it are.</param>
    /// <param name="assetName">The file name of the asset.</param>
    /// <returns>The asset, or <c>null</c> where the release carries none of that name.</returns>
    private static MappingSourceBuild? FindAsset(JsonDocument document, string tag, string assetName)
    {
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), assetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var downloadUrl)
                || downloadUrl.GetString() is not { Length: > 0 } address)
            {
                return null;
            }

            // A rolling tag keeps its name and its publication date while its assets are
            // replaced under it, so neither says which build this is. The asset does: it is
            // given a new id and a new timestamp every time it is uploaded again.
            var id = asset.TryGetProperty("id", out var identifier) && identifier.TryGetInt64(out var value)
                ? value
                : 0;
            var updatedAt = asset.TryGetProperty("updated_at", out var updated) ? updated.GetString() : null;

            return new MappingSourceBuild(address, FormattableString.Invariant($"{tag}/{id}/{updatedAt}"));
        }

        return null;
    }
}
