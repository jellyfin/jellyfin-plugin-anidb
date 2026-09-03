using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Api;

/// <summary>
/// Reports what the plugin is doing with AniDB, so that the configuration page can say
/// whether requests are flowing, queued or paused by a ban.
/// </summary>
/// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("AniDB")]
[Produces(MediaTypeNames.Application.Json)]
public class AniDbStatusController(IApplicationPaths applicationPaths, ILogger<AniDbStatusController> logger) : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths = applicationPaths;
    private readonly ILogger<AniDbStatusController> _logger = logger;

    /// <summary>
    /// Gets the plugin's current standing with AniDB.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AniDbStatus>> GetStatus(CancellationToken cancellationToken)
    {
        var (banRemaining, queued, untilNext, sent, lastSentUtc) = AniDbSeriesProvider.GetRequestStatus();
        var (cachedAtUtc, checkedAtUtc, entryCount, maxAgeInDays) = AniDbAnimeList.GetStatus(_applicationPaths);
        var (bridgeCachedAtUtc, bridgeCheckedAtUtc, bridgeEntryCount, bridgeMaxAgeInDays) = AniBridgeMappings.GetStatus(_applicationPaths);

        // Read from disk as it stands rather than as it was last read, which is what makes an
        // edit to it show up on the page it is made for.
        var (overridesPath, overridesWrittenAtUtc, overridesEntryCount, overridesShowCount, overridesMovieCount, overridesError) =
            await AniDbMappingOverrides.GetStatus(_applicationPaths, _logger, cancellationToken).ConfigureAwait(false);

        return new AniDbStatus
        {
            IsBanned = banRemaining > TimeSpan.Zero,
            BanRemainingSeconds = banRemaining.TotalSeconds,
            QueuedRequests = queued,
            NextRequestInSeconds = untilNext.TotalSeconds,
            RequestsSent = sent,
            LastRequestUtc = lastSentUtc,
            RequestIntervalMs = Plugin.Instance.Configuration.RequestIntervalMs,
            AnimeListCachedAtUtc = cachedAtUtc,
            AnimeListCheckedAtUtc = checkedAtUtc,
            AnimeListEntryCount = entryCount,
            AnimeListMaxAgeDays = maxAgeInDays,
            AniBridgeCachedAtUtc = bridgeCachedAtUtc,
            AniBridgeCheckedAtUtc = bridgeCheckedAtUtc,
            AniBridgeEntryCount = bridgeEntryCount,
            AniBridgeMaxAgeDays = bridgeMaxAgeInDays,
            AniBridgeEnabled = Plugin.Instance.Configuration.UseAniBridgeMappings,
            OverridesPath = overridesPath,
            OverridesWrittenAtUtc = overridesWrittenAtUtc,
            OverridesEntryCount = overridesEntryCount,
            OverridesShowCount = overridesShowCount,
            OverridesMovieCount = overridesMovieCount,
            OverridesError = overridesError
        };
    }

    /// <summary>
    /// Asks both downloaded mapping sources what they hold now, whatever the age of the copies
    /// cached, and downloads whichever has changed since. The overrides are not among them:
    /// nothing downloads that file, and it is read afresh whenever the status above is asked
    /// for. Neither is the AniDB titles list, which comes from AniDB itself and is paced to
    /// keep off its ban list rather than fetched on request.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Sources checked.</response>
    /// <returns>What each check came to.</returns>
    [HttpPost("Sources/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AniDbSourceCheck>> CheckSources(CancellationToken cancellationToken)
    {
        // Both at once: they come from different hosts, and neither waits behind AniDB's rate
        // limit. Each holds its own gate, so a scan asking for the same source meanwhile waits
        // rather than downloading it a second time.
        var bridge = AniBridgeMappings.CheckNow(_applicationPaths, _logger, cancellationToken);
        var list = AniDbAnimeList.CheckNow(_applicationPaths, _logger, cancellationToken);

        var outcomes = await Task.WhenAll(bridge, list).ConfigureAwait(false);

        return new AniDbSourceCheck
        {
            AniBridge = Describe(outcomes[0]),
            AnimeList = Describe(outcomes[1])
        };
    }

    /// <summary>
    /// How a check reads on the page that asked for it.
    /// </summary>
    /// <param name="check">What the check came to.</param>
    /// <returns>The wording to show.</returns>
    private static string Describe(MappingSourceCheck check) => check switch
    {
        MappingSourceCheck.Updated => "a newer copy was downloaded",
        MappingSourceCheck.Unchanged => "already the current copy",
        MappingSourceCheck.NotUsed => "not used",
        MappingSourceCheck.NotDownloaded => "not downloaded",
        _ => "could not be checked - see the server log"
    };
}
