using System.Collections.Generic;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One mapping source's account of how a season is filled, and which source gave it.
/// </summary>
/// <param name="Segments">The entries the season is filled from, in the order its episodes run through them.</param>
/// <param name="Source">Which source placed it, as a noun phrase for a log message.</param>
/// <param name="Authoritative">Whether the placement is to be used as it stands wherever AniDB holds the episodes it names, rather than weighed against the other sources. True of a placement written by hand: the episodes it leaves out of a season are the ones its author left out, so measuring it against the length of the season would reject exactly the placements it exists to state.</param>
internal sealed record SeasonPlacement(
    IReadOnlyList<AniDbSeasonSegment> Segments,
    string Source,
    bool Authoritative = false);
