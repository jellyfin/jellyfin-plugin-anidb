using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

/// <summary>
/// Tests for AniDB's episode numberings, which is what tells a special from a creditless
/// opening from an ordinary episode.
/// </summary>
public class AniDbEpisodeKindTests
{
    /// <summary>
    /// The prefix and the kind name each other, which is what lets a cached document be found
    /// from a kind and read back into one.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="prefix">The prefix AniDB writes before the number.</param>
    [Theory]
    [InlineData(AniDbEpisodeKind.Regular, "")]
    [InlineData(AniDbEpisodeKind.Special, "S")]
    [InlineData(AniDbEpisodeKind.Other, "O")]
    [InlineData(AniDbEpisodeKind.Credits, "C")]
    [InlineData(AniDbEpisodeKind.Trailer, "T")]
    [InlineData(AniDbEpisodeKind.Parody, "P")]
    internal void PrefixAndKindAgree(AniDbEpisodeKind kind, string prefix)
    {
        Assert.Equal(prefix, kind.Prefix());
        Assert.Equal(kind, AniDbEpisodeKindExtensions.FromPrefix(prefix));
    }

    /// <summary>
    /// A prefix AniDB does not use names no kind, rather than being read as an ordinary episode.
    /// </summary>
    [Fact]
    public void AnUnknownPrefixNamesNothing()
        => Assert.Null(AniDbEpisodeKindExtensions.FromPrefix("X"));

    /// <summary>
    /// Everything the library can only file among its specials, and nothing else. An ordinary
    /// episode is not one, and neither is AniDB's other numbering, which holds the broadcast run
    /// of something released another way and belongs in an ordinary season.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="expected">Whether the library files it among its specials.</param>
    [Theory]
    [InlineData(AniDbEpisodeKind.Regular, false)]
    [InlineData(AniDbEpisodeKind.Other, false)]
    [InlineData(AniDbEpisodeKind.Special, true)]
    [InlineData(AniDbEpisodeKind.Credits, true)]
    [InlineData(AniDbEpisodeKind.Trailer, true)]
    [InlineData(AniDbEpisodeKind.Parody, true)]
    internal void OnlyTheExtrasAreFiledAmongTheSpecials(AniDbEpisodeKind kind, bool expected)
        => Assert.Equal(expected, kind.IsExtra());
}
