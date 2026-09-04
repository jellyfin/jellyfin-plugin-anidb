using System.Text.RegularExpressions;
using Jellyfin.Plugin.AniDB.Providers;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

/// <summary>
/// Tests for the fuzzy name matching the titles file is searched with.
/// </summary>
public class FuzzyMatchingTests
{
    /// <summary>
    /// The pattern matches the name it was built from. Everything else here is a way of
    /// spelling a name differently, so this is the floor under all of it.
    /// </summary>
    /// <param name="name">The name.</param>
    [Theory]
    [InlineData("Cowboy Bebop")]
    [InlineData("Naruto")]
    [InlineData("Fullmetal Alchemist: Brotherhood")]
    [InlineData("K-On!")]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu")]
    [InlineData("Mahoutsukai no Yome")]
    [InlineData("Bakemonogatari")]
    [InlineData("5 Centimeters per Second")]
    public void PatternMatchesItsOwnName(string name)
        => Assert.Matches(Pattern(name), name);

    /// <summary>
    /// The spellings the equivalences exist for are matched by each other's pattern.
    /// </summary>
    /// <param name="name">The name as the library spells it.</param>
    /// <param name="title">The name as AniDB spells it.</param>
    [Theory]
    [InlineData("Higurashi OVA", "Higurashi OAD")]
    [InlineData("Higurashi OAD", "Higurashi OVA")]
    [InlineData("Cardcaptor Sakura", "Kardkaptor Sakura")]
    [InlineData("Fate and Stay", "Fate & Stay")]
    [InlineData("Fate & Stay", "Fate and Stay")]
    [InlineData("Gekijyouban Fate", "Gekijouban Fate")]
    [InlineData("Gekijouban Fate", "Gekijyouban Fate")]
    [InlineData("Mahoutsukai no Yome", "Mahou Tsukai no Yome")]
    [InlineData("To Aru Majutsu no Index", "Toaru Majutsu no Index")]
    [InlineData("Shingeki no Kyojin", "Shingeki no Kyojin")]
    [InlineData("Kimi no Na wa.", "Kimi no Na wa")]
    [InlineData("Steins;Gate", "Steins Gate")]
    [InlineData("Jun`ichi", "Junichi")]
    public void PatternMatchesTheOtherSpelling(string name, string title)
        => Assert.Matches(Pattern(name), title);

    /// <summary>
    /// The rewrite exists because these two rules did not work. "OVA" nested one inside the
    /// other, and the ampersand rule never fired because the rule before it had already
    /// rewritten every n.
    /// </summary>
    [Fact]
    public void EquivalencesAreNotAppliedToEachOthersOutput()
    {
        Assert.Equal("(?:ova|oad)", Equals_check.FuzzyRegexEscape("OVA"));
        Assert.Equal("(?:&|and)", Equals_check.FuzzyRegexEscape("and"));
        Assert.Equal("[ck]", Equals_check.FuzzyRegexEscape("c"));
        Assert.Equal("[ck]", Equals_check.FuzzyRegexEscape("k"));
    }

    /// <summary>
    /// A name that is not the same name is not matched. A pattern that matches everything would
    /// pass every test above and be worth nothing.
    /// </summary>
    /// <param name="name">The name searched for.</param>
    /// <param name="other">A title that is a different show.</param>
    [Theory]
    [InlineData("Cowboy Bebop", "Trigun")]
    [InlineData("Naruto", "Bleach")]
    [InlineData("Monster", "Steins;Gate")]
    [InlineData("Clannad", "Air")]
    public void PatternDoesNotMatchAnotherShow(string name, string other)
        => Assert.DoesNotMatch(Pattern(name), other);

    /// <summary>
    /// The pattern is a valid regular expression whatever is put through it, punctuation that
    /// means something to a regex engine included.
    /// </summary>
    /// <param name="name">The name.</param>
    [Theory]
    [InlineData("[C]")]
    [InlineData("Re:Zero")]
    [InlineData("K-On!")]
    [InlineData("Gochuumon wa Usagi desu ka??")]
    [InlineData("+Tic Nee-san")]
    [InlineData("(Not) a show")]
    [InlineData("^$|*+")]
    [InlineData("")]
    public void PatternIsValid(string name)
        => Assert.NotNull(new Regex(Equals_check.FuzzyRegexEscape(name), RegexOptions.IgnoreCase));

    /// <summary>
    /// The search shortens a name before making a pattern of it, so that a title carrying a
    /// suffix AniDB does not use still matches.
    /// </summary>
    [Fact]
    public void ShortenStringCutsTheGivenShare()
    {
        Assert.Equal("Cowboy Be", Equals_check.ShortenString("Cowboy Bebop", 6, 20));
        Assert.Equal("Cowboy", Equals_check.ShortenString("Cowboy Bebop", 6, 90));
        Assert.Equal("Air", Equals_check.ShortenString("Air", 6, 20));
        Assert.Equal(string.Empty, Equals_check.ShortenString(string.Empty));
    }

    /// <summary>
    /// The pattern as the search itself builds and uses it, which is without regard to case.
    /// </summary>
    /// <param name="name">The name to build a pattern from.</param>
    /// <returns>The compiled pattern.</returns>
    private static Regex Pattern(string name)
        => new(Equals_check.FuzzyRegexEscape(name), RegexOptions.IgnoreCase);
}
