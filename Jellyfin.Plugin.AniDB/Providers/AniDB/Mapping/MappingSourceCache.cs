using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One mapping file, and whatever was parsed from it. A file with a URL is fetched when the
/// copy on disk is missing or has gone stale; one without is only ever read, being written by
/// whoever runs the server. Either is parsed once per copy, and what came out of it is kept in
/// memory until that copy changes underneath it.
/// </summary>
/// <typeparam name="TIndex">What the file is parsed into. Placements worked out from one copy belong to it, so anything memoised per lookup belongs on this rather than beside the cache.</typeparam>
/// <param name="fileName">What the copy on disk is called, within the folder named below.</param>
/// <param name="url">Where the file is downloaded from, or <c>null</c> for a file nobody downloads: one written by hand, which is allowed not to be there and is never thrown away.</param>
/// <param name="description">How the file is named in log messages, as a noun phrase: "the anime list".</param>
/// <param name="maxAgeDays">How long a downloaded copy is used before it is fetched again. Means nothing for a file that is not downloaded.</param>
/// <param name="parse">Reads a copy on disk, given its path, a logger and the time it was written.</param>
/// <param name="folder">Where the file lives, given the application paths. Defaults to the plugin's data folder, which is where a downloaded copy belongs.</param>
/// <param name="resolve">Asks the source which build it is currently holding, for a source that can be asked outright: a GitHub release names the asset attached to it. Where this is <c>null</c> the question is put to the server as part of the download instead, by offering back the entity tag the last one answered with, and the file is sent again only where it has changed.</param>
internal sealed class MappingSourceCache<TIndex>(
    string fileName,
    string? url,
    string description,
    int maxAgeDays,
    Func<string, ILogger, DateTime, TIndex> parse,
    Func<IApplicationPaths, string>? folder = null,
    Func<HttpClient, CancellationToken, Task<MappingSourceBuild?>>? resolve = null)
    : IDisposable
    where TIndex : class
{
    /// <summary>
    /// How long to wait before trying again once the file could not be read at all. Without a
    /// pause a scan would ask for it once per series and fail every time; without a retry a
    /// server that started while its network was down would never get the file.
    /// </summary>
    private readonly TimeSpan _retryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the copy in memory is used before the cached file is looked at again. Reading
    /// the file's timestamp is cheap, but a scan asks once per episode, and the file only
    /// changes when this class downloads it or someone replaces it by hand.
    /// </summary>
    private readonly TimeSpan _recheckInterval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>
    /// What was parsed from the copy on disk. Every lookup is answered from here.
    /// </summary>
    private TIndex? _index;

    private DateTime _failedAtUtc = DateTime.MinValue;

    /// <summary>
    /// When the cached file was last compared against the copy in memory, and the timestamp of
    /// the copy last read - whether or not it could be parsed. A server left running for weeks
    /// would otherwise keep what it read at startup for as long as it ran, never learning where
    /// the season that started since belongs, nor noticing a file replaced underneath it.
    /// </summary>
    private DateTime _checkedAtUtc = DateTime.MinValue;
    private DateTime _sourceWrittenAtUtc = DateTime.MinValue;

    /// <summary>
    /// Why the copy on disk could not be read, or <c>null</c> where it was read. Kept so that
    /// the configuration page can say a file written by hand is broken: whoever wrote it is
    /// looking at that page rather than at the log.
    /// </summary>
    private string? _error;

    /// <summary>
    /// Gets how long a downloaded copy is used before it is fetched again.
    /// </summary>
    public int MaxAgeInDays => maxAgeDays;

    /// <summary>
    /// Gets how long to wait before looking at a file that could not be read again. A
    /// downloaded one is waited out: nothing here can mend it, and the next attempt is a fresh
    /// download. A file written by hand is mended by editing it, which is a minute's work, so
    /// it is looked at again as often as any other change to it would be noticed.
    /// </summary>
    private TimeSpan RetryPause => url == null ? _recheckInterval : _retryAfterFailure;

    /// <summary>
    /// What the file holds, downloading and parsing it where what is in memory will not do.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="reread">Whether to look at the file even where what is in memory would otherwise do. For the configuration page, which is asked what the file on disk says now rather than what it said when the last episode was scanned. Means nothing for a downloaded file.</param>
    /// <returns>The parsed file, or <c>null</c> when it could not be read.</returns>
    public async Task<TIndex?> GetIndex(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken, bool reread = false)
    {
        // A re-read looks at the file whatever the interval says, but never at one that is
        // downloaded: there it would mean a download, and the page that asks for it is polled.
        var lookNow = reread && url == null;

        // The common path, taken once per lookup: the file is already in memory and current.
        if (!lookNow && IsCurrent())
        {
            return _index;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!lookNow && IsCurrent())
            {
                return _index;
            }

            var path = GetPath(appPaths);

            await Refresh(path, logger, cancellationToken).ConfigureAwait(false);

            Reload(path, logger);
        }
        catch (IOException ex)
        {
            _failedAtUtc = DateTime.UtcNow;

            logger.LogWarning(ex, "Could not read {Source}, so whatever it would have placed is placed some other way", description);
        }
        finally
        {
            _loadGate.Release();
        }

        return _index;
    }

    /// <summary>
    /// Asks the source what it holds now, whatever the age of the copy cached, and downloads
    /// and reads it where that is not the copy already on disk. For the button on the
    /// configuration page: everything else waits the maximum age out, which is what keeps a
    /// scan from asking the source once per episode.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the check came to.</returns>
    public async Task<MappingSourceCheck> CheckNow(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
    {
        if (url == null)
        {
            // Nothing to ask: a file written by hand is read again within minutes of being
            // changed, which is a check of its own and needs no button.
            return MappingSourceCheck.NotDownloaded;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = GetPath(appPaths);
            var outcome = await Refresh(path, logger, cancellationToken, force: true).ConfigureAwait(false);

            // Read here rather than at the next lookup, so that the page which asked for the
            // check is answered with what came of it: a copy downloaded but unreadable is
            // worth saying now, and the entry count it reports is the new copy's.
            Reload(path, logger);

            return _error == null ? outcome : MappingSourceCheck.Failed;
        }
        catch (IOException ex)
        {
            _failedAtUtc = DateTime.UtcNow;

            logger.LogWarning(ex, "Could not read {Source}, so whatever it would have placed is placed some other way", description);

            return MappingSourceCheck.Failed;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Reads the copy on disk into memory where it is not the copy already read. Called under
    /// the load gate, with whatever the refresh left on disk.
    /// </summary>
    /// <param name="path">Where the file is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    private void Reload(string path, ILogger logger)
    {
        var file = new FileInfo(path);

        if (!file.Exists || file.Length == 0)
        {
            // No file at all is the ordinary state of one written by hand, so it is noted
            // as looked at rather than as failed. Noting it is what keeps a scan from
            // asking the file system once per episode, and the recheck interval is what
            // brings a file written since into use without a restart.
            if (url == null)
            {
                _index = null;
                _error = null;
                _sourceWrittenAtUtc = DateTime.MinValue;
                _checkedAtUtc = DateTime.UtcNow;

                return;
            }

            _failedAtUtc = DateTime.UtcNow;

            return;
        }

        // The file is the copy of record. Where it is the one already read there is nothing
        // to do; where it is not - downloaded just now, or replaced by hand - what is in
        // memory is out of date whatever its own age. Read rather than parsed, so that a
        // file which would not parse is not parsed again on every look.
        if (file.LastWriteTimeUtc == _sourceWrittenAtUtc)
        {
            _checkedAtUtc = DateTime.UtcNow;

            return;
        }

        try
        {
            _index = parse(path, logger, file.LastWriteTimeUtc);
            _error = null;
            _checkedAtUtc = DateTime.UtcNow;
            _sourceWrittenAtUtc = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is XmlException or JsonException)
        {
            _error = ex.Message;

            if (url == null)
            {
                // Not this class's to throw away: it is the only copy there is. It stays
                // where it is, and nothing new is taken from it - a copy read before this
                // one goes on being used, an edit half written having broken it - until it
                // is fixed.
                logger.LogError(ex, "{Source} at {Path} is not valid JSON, so nothing new is taken from it. Fix or remove the file", description, path);

                // Noted as read so that the same broken copy is not parsed again on every
                // look, the configuration page asking for one every few seconds.
                _checkedAtUtc = DateTime.UtcNow;
                _sourceWrittenAtUtc = file.LastWriteTimeUtc;
            }
            else
            {
                // A truncated or half-written file would otherwise be read again on every
                // start until it went stale. Dropping it means the next start downloads afresh.
                logger.LogWarning(ex, "The cached copy of {Source} at {Path} could not be read and has been discarded", description, path);

                TryDelete(path);
                MappingSourceMarker.Delete(path);
            }

            _failedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// What is known of the file, for the status the configuration page shows. Reads nothing
    /// but its timestamp, so it costs little to ask often.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>When the cached copy was downloaded, when the source was last asked whether it holds a newer one, what was parsed from the copy, how many days one is used for, and why the copy on disk could not be read where it could not.</returns>
    public (DateTime? CachedAtUtc, DateTime? CheckedAtUtc, TIndex? Index, int MaxAgeInDays, string? Error) GetStatus(IApplicationPaths appPaths)
    {
        DateTime? cachedAtUtc = null;
        DateTime? checkedAtUtc = null;

        try
        {
            var path = GetPath(appPaths);
            var file = new FileInfo(path);

            if (file.Exists && file.Length > 0)
            {
                cachedAtUtc = file.LastWriteTimeUtc;

                // Worth reporting apart from the download: a check that finds the newest build
                // already cached leaves the copy's own date where it was, and a page showing
                // only that would read as a source that had stopped being looked at.
                checkedAtUtc = MappingSourceMarker.Read(path)?.CheckedAtUtc;
            }
        }
        catch (IOException)
        {
            // The status is worth less than the page it is shown on.
        }

        return (cachedAtUtc, checkedAtUtc, _index, maxAgeDays, _error);
    }

    /// <summary>
    /// Releases the gate that keeps two lookups from downloading or parsing at once. Each
    /// source is held for as long as the plugin is loaded, so this is for the analyzer's sake
    /// rather than for a caller's.
    /// </summary>
    public void Dispose() => _loadGate.Dispose();

    /// <summary>
    /// Whether a lookup may be answered from what is in memory, either because the cached file
    /// was checked against it recently enough or because reading it failed recently enough to
    /// be worth a pause.
    /// </summary>
    /// <returns><c>true</c> when the cached file need not be looked at.</returns>
    private bool IsCurrent()
    {
        var now = DateTime.UtcNow;

        // Checked against the time of the last look rather than against what it produced: a
        // look that found no file is as good an answer as one that found a whole mapping set.
        return now - _failedAtUtc < RetryPause
            || (_checkedAtUtc != DateTime.MinValue && now - _checkedAtUtc < _recheckInterval);
    }

    /// <summary>
    /// Where the file is. Public so that a page can tell the user where to put one they write
    /// themselves.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>The full path.</returns>
    public string GetPath(IApplicationPaths appPaths)
        => Path.Combine(folder == null ? AniDbTitleDownloader.GetDataPath(appPaths) : folder(appPaths), fileName);

    /// <summary>
    /// Asks the source what it holds once the copy on disk has gone stale, and downloads it
    /// where that is not the copy already cached. A copy that is merely stale is kept when the
    /// source cannot be reached: an old mapping for a show already in the library is almost
    /// always still the right one, and is certainly better than none.
    /// </summary>
    /// <param name="path">Where the file is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to ask the source however lately it was last asked. For a check asked for by hand, which is the one case where waiting the maximum age out is not what was wanted.</param>
    /// <returns>What the check came to.</returns>
    private async Task<MappingSourceCheck> Refresh(string path, ILogger logger, CancellationToken cancellationToken, bool force = false)
    {
        if (url == null)
        {
            return MappingSourceCheck.NotDownloaded;
        }

        var file = new FileInfo(path);
        var cached = file.Exists && file.Length > 0;
        var marker = cached ? MappingSourceMarker.Read(path) : null;

        // Counted from the last time the source was asked rather than from the download,
        // because a check that finds the newest build already cached leaves the copy on disk
        // untouched: dating it by its own timestamp would ask again on every lookup for the
        // rest of the week. A copy cached before any of this was recorded has only its
        // timestamp to go by.
        var askedAtUtc = marker?.CheckedAtUtc ?? file.LastWriteTimeUtc;

        if (cached && !force && (DateTime.UtcNow - askedAtUtc).TotalDays <= maxAgeDays)
        {
            return MappingSourceCheck.Unchanged;
        }

        // Not paced by the AniDB request gate: this comes from the file's own host, and holding
        // up a scan behind AniDB's rate limit for it would be for nothing.
        var httpClient = Plugin.Instance.GetHttpClient();
        var known = cached ? marker?.Version : null;

        try
        {
            var build = await Resolve(httpClient, logger, cancellationToken).ConfigureAwait(false);

            // The source named the build it holds and it is the one already on disk, so there
            // is nothing to fetch: only the date it was last asked about moves on.
            if (known != null && build?.Version != null && string.Equals(known, build.Version, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Not downloading {Source} again: the source still holds the build cached on {CachedAt}",
                    description,
                    file.LastWriteTimeUtc);

                MappingSourceMarker.Write(path, known);

                return MappingSourceCheck.Unchanged;
            }

            // Only the plain URL asks the server: where the build was resolved above, the
            // question has been answered already and the entity tag of the last download is
            // the tag of a different address.
            var (downloaded, version) = await Download(
                build?.Url ?? url,
                path,
                build == null ? known : null,
                httpClient,
                logger,
                cancellationToken).ConfigureAwait(false);

            if (!downloaded)
            {
                logger.LogInformation(
                    "Not downloading {Source} again: it is unchanged since the copy cached on {CachedAt}",
                    description,
                    file.LastWriteTimeUtc);
            }

            MappingSourceMarker.Write(path, build?.Version ?? version);

            return downloaded ? MappingSourceCheck.Updated : MappingSourceCheck.Unchanged;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            if (!cached)
            {
                throw new IOException(FormattableString.Invariant($"{description} could not be downloaded and no copy is cached"), ex);
            }

            logger.LogWarning(
                ex,
                "{Source} could not be downloaded, so the copy cached on {CachedAt} is used instead",
                description,
                file.LastWriteTimeUtc);

            return MappingSourceCheck.Failed;
        }
    }

    /// <summary>
    /// Asks the source which build it is currently holding, for a source that can be asked.
    /// </summary>
    /// <param name="httpClient">The client to ask with.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The build, or <c>null</c> where the source cannot be asked or would not say.</returns>
    private async Task<MappingSourceBuild?> Resolve(HttpClient httpClient, ILogger logger, CancellationToken cancellationToken)
    {
        if (resolve == null)
        {
            return null;
        }

        try
        {
            var build = await resolve(httpClient, cancellationToken).ConfigureAwait(false);

            if (build != null)
            {
                return build;
            }

            logger.LogWarning(
                "The source of {Source} does not name the build it is holding, so it is downloaded from {Url} without asking",
                description,
                url);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            // Asking is what saves the download, not what makes it possible: the URL names the
            // same file either way. GitHub's API is rate limited per address and this is the
            // only thing here that uses it, so a refusal is answered by fetching outright
            // rather than by leaving the copy on disk to go on ageing.
            logger.LogWarning(
                ex,
                "Could not ask which build of {Source} is current, so it is downloaded from {Url} without asking",
                description,
                url);
        }

        return null;
    }

    /// <summary>
    /// Fetches the file, unless the server answers that the copy already cached is the current
    /// one.
    /// </summary>
    /// <param name="sourceUrl">Where to fetch it from.</param>
    /// <param name="path">Where the file is cached.</param>
    /// <param name="knownVersion">The entity tag the last download answered with, to be offered back so that an unchanged file is not sent again, or <c>null</c> to fetch outright.</param>
    /// <param name="httpClient">The client to fetch with.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the copy on disk was replaced, and what identifies what is now cached.</returns>
    private async Task<(bool Downloaded, string? Version)> Download(
        string sourceUrl,
        string path,
        string? knownVersion,
        HttpClient httpClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(sourceUrl)))
        {
            if (knownVersion != null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", knownVersion);
            }

            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return (false, knownVersion);
                }

                response.EnsureSuccessStatusCode();

                logger.LogInformation("Downloading {Source} from {Url}", description, sourceUrl);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var temporaryFile = path + ".tmp";

                try
                {
                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    using (var writer = File.Open(temporaryFile, FileMode.Create, FileAccess.Write))
                    {
                        await stream.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
                    }

                    File.Move(temporaryFile, path, true);
                }
                catch
                {
                    TryDelete(temporaryFile);

                    throw;
                }

                // Offered back on the next check, where nothing else names the build: the
                // server answers an unchanged file with "not modified" rather than with the
                // file, which for the anime list is the only way to ask at all.
                return (true, response.Headers.ETag?.ToString());
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start tries again.
        }
    }
}
