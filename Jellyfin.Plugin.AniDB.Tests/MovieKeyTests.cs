using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

/// <summary>
/// Tests for the key every mapping source files a movie under, whatever each of them calls it.
/// </summary>
public class MovieKeyTests
{
    /// <summary>
    /// An id each source writes differently comes out as one key.
    /// </summary>
    [Fact]
    public void OneMovieHasOneKeyAcrossTheSources()
    {
        Assert.Equal(MovieKey.Tmdb("128"), MovieKey.FromDescriptor("tmdb_movie:128"));
        Assert.Equal(MovieKey.Imdb("tt0245429"), MovieKey.FromDescriptor("imdb_movie:tt0245429"));
        Assert.Equal(MovieKey.Tvdb("42"), MovieKey.FromDescriptor("tvdb_movie:42"));
    }

    /// <summary>
    /// A provider writing the IMDb prefix in capitals is writing the same id.
    /// </summary>
    [Fact]
    public void ImdbIdsAreKeyedWithoutRegardToCase()
        => Assert.Equal(MovieKey.Imdb("tt0245429"), MovieKey.Imdb("TT0245429"));

    /// <summary>
    /// Anything that is not an id is not a key, so that nothing is ever looked up under one.
    /// </summary>
    /// <param name="id">The value a source wrote where an id was expected.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("12a")]
    public void SomethingThatIsNotAnIdIsNotAKey(string? id)
    {
        Assert.Null(MovieKey.Tmdb(id));
        Assert.Null(MovieKey.Tvdb(id));
        Assert.Null(MovieKey.Imdb(id));
    }

    /// <summary>
    /// A key taken apart names the provider and the id it was made from, which is what has to
    /// happen before a movie found by looking it up can be written back onto an item.
    /// </summary>
    [Fact]
    public void AKeyIsTakenApartIntoWhatItWasMadeFrom()
    {
        Assert.Equal(("tmdb", "128"), MovieKey.Split(MovieKey.Tmdb("128")!));
        Assert.Equal(("imdb", "tt0245429"), MovieKey.Split(MovieKey.Imdb("tt0245429")!));
        Assert.Equal(("tvdb", "42"), MovieKey.Split(MovieKey.Tvdb("42")!));
    }

    /// <summary>
    /// Something that is not a key is not taken apart into a provider and an id.
    /// </summary>
    /// <param name="key">The value to try.</param>
    [Theory]
    [InlineData("")]
    [InlineData("tmdb")]
    [InlineData(":128")]
    [InlineData("tmdb:")]
    public void SomethingThatIsNotAKeyIsNotTakenApart(string key)
        => Assert.Null(MovieKey.Split(key));
}
