using System;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

/// <summary>
/// A title of an anime, together with the AniDB id it belongs to.
/// </summary>
public struct TitleInfo : IEquatable<TitleInfo>
{
    /// <summary>
    /// Gets or sets the AniDB id.
    /// </summary>
    public string? AniDbId { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the type of the title.
    /// </summary>
    public TitleType Type { get; set; }

    /// <summary>
    /// Compares two <see cref="TitleInfo"/> instances for equality.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><c>true</c> when both are equal; otherwise <c>false</c>.</returns>
    public static bool operator ==(TitleInfo left, TitleInfo right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two <see cref="TitleInfo"/> instances for inequality.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><c>true</c> when both differ; otherwise <c>false</c>.</returns>
    public static bool operator !=(TitleInfo left, TitleInfo right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(TitleInfo other)
    {
        return string.Equals(AniDbId, other.AniDbId, StringComparison.Ordinal)
               && string.Equals(Title, other.Title, StringComparison.Ordinal)
               && Type == other.Type;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is TitleInfo other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(AniDbId, Title, Type);
    }
}
