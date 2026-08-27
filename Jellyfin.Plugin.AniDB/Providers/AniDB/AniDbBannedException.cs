using System;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB;

/// <summary>
/// Thrown when AniDB has temporarily banned this client, so no further request may be
/// sent until the ban has been given time to lapse.
/// </summary>
public class AniDbBannedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    public AniDbBannedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public AniDbBannedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbBannedException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public AniDbBannedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the time that must pass before the plugin will contact AniDB again.
    /// </summary>
    public TimeSpan RetryAfter { get; init; }
}
