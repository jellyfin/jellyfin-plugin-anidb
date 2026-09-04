using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

public class Smoke
{
    [Fact]
    public void InternalsAreVisible() => Assert.Equal("tmdb:1", MovieKey.Tmdb("1"));

    [Fact]
    public void SkipWorks()
    {
        Assert.Skip("dynamic skip is available");
    }
}
