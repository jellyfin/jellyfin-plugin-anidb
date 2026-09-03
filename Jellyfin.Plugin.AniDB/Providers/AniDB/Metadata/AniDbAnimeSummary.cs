using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

/// <summary>
/// The few fields of a cached AniDB anime document that are needed to decide whether that
/// anime is the next season of another one.
/// </summary>
internal sealed class AniDbAnimeSummary
{
    /// <summary>
    /// Gets the AniDB id of the anime.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the AniDB format of the anime, such as "TV Series" or "OVA".
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets the number of regular episodes AniDB records for the anime. Specials are not
    /// counted, and an anime still airing is counted as the episodes it will have.
    /// </summary>
    public int EpisodeCount { get; init; }

    /// <summary>
    /// Gets the date the anime started airing.
    /// </summary>
    public DateTime? StartDate { get; init; }

    /// <summary>
    /// Gets the date the anime finished airing.
    /// </summary>
    public DateTime? EndDate { get; init; }

    /// <summary>
    /// Gets every title the anime is known by, in the order AniDB lists them.
    /// </summary>
    public IReadOnlyList<string> Titles { get; init; } = [];

    /// <summary>
    /// Gets the AniDB ids of the anime that AniDB relates to this one as a sequel.
    /// </summary>
    public IReadOnlyList<string> SequelIds { get; init; } = [];

    /// <summary>
    /// Gets the AniDB ids of the anime that AniDB relates to this one as a prequel. Relations
    /// are recorded from both ends, so these confirm a link found any other way.
    /// </summary>
    public IReadOnlyList<string> PrequelIds { get; init; } = [];
}
