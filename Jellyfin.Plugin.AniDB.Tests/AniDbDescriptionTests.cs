using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

/// <summary>
/// Tests for the description AniDB writes being turned into the markup Jellyfin shows.
/// </summary>
public class AniDbDescriptionTests
{
    /// <summary>
    /// The tags issue #87 is about. AniDB puts the note saying where a description came from in
    /// italics, and both marks were being shown as written.
    /// </summary>
    [Fact]
    public void ItalicsBecomeMarkup()
        => Assert.Equal(
            "Source: Crunchyroll<i>Note: an early screening.</i>",
            AniDbDescription.ConvertMarkup("Source: Crunchyroll[i]Note: an early screening.[/i]"));

    /// <summary>
    /// The other tags with a counterpart.
    /// </summary>
    /// <param name="text">The description as AniDB wrote it.</param>
    /// <param name="expected">The markup it becomes.</param>
    [Theory]
    [InlineData("[b]bold[/b]", "<b>bold</b>")]
    [InlineData("[u]underlined[/u]", "<u>underlined</u>")]
    [InlineData("[s]struck[/s]", "<s>struck</s>")]
    [InlineData("[I]shouted[/I]", "<i>shouted</i>")]
    public void KnownTagsBecomeMarkup(string text, string expected)
        => Assert.Equal(expected, AniDbDescription.ConvertMarkup(text));

    /// <summary>
    /// A tag with no counterpart is removed and what it wrapped is kept.
    /// </summary>
    [Fact]
    public void UnrenderableTagsAreRemoved()
        => Assert.Equal(
            "see the sequel",
            AniDbDescription.ConvertMarkup("[url=https://anidb.net/anime/1]see the sequel[/url]"));

    /// <summary>
    /// A tag left open would run to the end of the description, so it is dropped rather than
    /// turned into an element nothing closes.
    /// </summary>
    [Fact]
    public void UnbalancedTagsAreRemoved()
        => Assert.Equal("half italic", AniDbDescription.ConvertMarkup("[i]half italic"));

    /// <summary>
    /// Square brackets are not markup on their own. A description carries them in its own text,
    /// and AniDB's link syntax writes the name of what it points at in them.
    /// </summary>
    /// <param name="text">The description as AniDB wrote it.</param>
    [Theory]
    [InlineData("an aside [see the sequel] written out")]
    [InlineData("[Note] this is not a tag")]
    [InlineData("no markup at all")]
    public void OrdinaryBracketsAreLeftAlone(string text)
        => Assert.Equal(text, AniDbDescription.ConvertMarkup(text));

    /// <summary>
    /// AniDB writes a link as the URL followed by the name in brackets. Only the name is worth
    /// reading: the URL leads back to AniDB.
    /// </summary>
    [Fact]
    public void LinksBecomeTheNameTheyCarry()
        => Assert.Equal(
            "adapted from Yuru Camp, which aired later",
            AniDbDescription.StripLinks("adapted from https://anidb.net/anime/13043 [Yuru Camp], which aired later"));

    /// <summary>
    /// Every way a line can end becomes a line break, not only the one AniDB usually writes.
    /// </summary>
    /// <param name="text">The description as AniDB wrote it.</param>
    [Theory]
    [InlineData("one\ntwo")]
    [InlineData("one\r\ntwo")]
    [InlineData("one\rtwo")]
    public void EveryLineEndingBecomesABreak(string text)
        => Assert.Equal("one<br>two", AniDbDescription.ReplaceNewLine(text));
}
