namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Which build of a downloaded mapping file its source is currently holding: where to fetch it
/// from, and what tells that build apart from the one before it.
/// </summary>
/// <param name="Url">Where the build is downloaded from, which for a release asset is the URL of the build rather than of the source.</param>
/// <param name="Version">What identifies the build, or <c>null</c> where the source said nothing about which build it is holding.</param>
internal sealed record MappingSourceBuild(string Url, string? Version);
