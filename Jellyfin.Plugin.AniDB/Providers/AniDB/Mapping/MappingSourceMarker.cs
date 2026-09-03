using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// What is known of a downloaded mapping file beyond the copy itself: which build of the source
/// that copy is, and when the source was last asked whether it holds a newer one. Kept in a
/// small file beside the copy, so that emptying the cache by hand leaves nothing behind that
/// would be believed about a file no longer there.
/// </summary>
internal sealed class MappingSourceMarker
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Gets or sets what identifies the cached build, as its source named it: the release asset
    /// a rolling tag currently carries, or the entity tag a plain URL answered with. Nothing
    /// here reads it, so which of the two it is does not matter - only whether the source still
    /// names the same one.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets when the source was last asked what it holds, whether or not that produced
    /// a download. This is what the maximum age is counted from: a check that found nothing new
    /// leaves the copy on disk untouched, and dating the copy by its own timestamp would ask
    /// again on every lookup for the rest of the week.
    /// </summary>
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>
    /// The marker beside the given cached copy.
    /// </summary>
    /// <param name="path">Where the cached copy is.</param>
    /// <returns>The marker, or <c>null</c> where there is none or it could not be read. It is only ever a shortcut past a download, so one that will not parse is treated as one that is not there.</returns>
    public static MappingSourceMarker? Read(string path)
    {
        try
        {
            var markerPath = PathFor(path);

            if (!File.Exists(markerPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MappingSourceMarker>(File.ReadAllBytes(markerPath), _options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records that the source was asked just now, and that what is cached is the given build.
    /// </summary>
    /// <param name="path">Where the cached copy is.</param>
    /// <param name="version">What identifies the cached build, or <c>null</c> where the source named none.</param>
    public static void Write(string path, string? version)
    {
        try
        {
            var marker = new MappingSourceMarker { Version = version, CheckedAtUtc = DateTime.UtcNow };

            File.WriteAllBytes(PathFor(path), JsonSerializer.SerializeToUtf8Bytes(marker, _options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Costs a download that need not have happened, and nothing else: the next check
            // finds no marker and asks the source outright.
        }
    }

    /// <summary>
    /// Forgets what was known of a copy that is being thrown away, so that the copy downloaded
    /// in its place is not taken for the build this one was.
    /// </summary>
    /// <param name="path">Where the cached copy was.</param>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(PathFor(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The version it names is compared against the source's before it is trusted, so
            // one left behind costs at worst a download that was not needed.
        }
    }

    private static string PathFor(string path) => path + ".state.json";
}
