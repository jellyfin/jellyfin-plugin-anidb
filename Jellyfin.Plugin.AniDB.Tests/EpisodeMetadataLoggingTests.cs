using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AniDB.Tests;

[CollectionDefinition(nameof(EpisodeMetadataCollection), DisableParallelization = true)]
public sealed class EpisodeMetadataCollection
{
}

[Collection(nameof(EpisodeMetadataCollection))]
public sealed class EpisodeMetadataLoggingTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory();
    private readonly Plugin? _previousInstance = Plugin.Instance;
    private readonly IAniDbTitleMatcher _previousTitleMatcher = AniDbTitleMatcher.DefaultInstance;
    private readonly ILogger<AniDbSeriesProvider>? _previousSeriesLogger = AniDbSeriesProvider.Logger;
    private readonly CaptureLogger<AniDbEpisodeProvider> _logger = new();
    private readonly AniDbEpisodeProvider _provider;
    private readonly string _seriesDirectory;

    public EpisodeMetadataLoggingTests()
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.CachePath).Returns(_temporary.FullName);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_temporary.FullName);
        paths.SetupGet(p => p.PluginsPath).Returns(_temporary.FullName);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        var plugin = new Plugin(
            paths.Object,
            serializer.Object,
            NullLogger<AniDbTitleMatcher>.Instance,
            NullLogger<AniDbTitleDownloader>.Instance,
            NullLogger<AniDbSeriesProvider>.Instance,
            new Mock<IHttpClientFactory>(MockBehavior.Strict).Object);
        plugin.Configuration.IgnoreSeason = true;
        plugin.Configuration.MaxCacheAge = 14;
        plugin.Configuration.UseAniBridgeMappings = false;

        var configuration = new Mock<IServerConfigurationManager>();
        configuration.SetupGet(c => c.ApplicationPaths).Returns(paths.Object);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        _provider = new AniDbEpisodeProvider(configuration.Object, library.Object, _logger);
        _seriesDirectory = AniDbSeriesProvider.GetSeriesDataPath(paths.Object, "123");
        Directory.CreateDirectory(_seriesDirectory);
        File.WriteAllText(Path.Combine(_seriesDirectory, "series.xml"), "<anime id=\"123\" />");
        File.WriteAllText(Path.Combine(AniDbTitleDownloader.GetDataPath(paths.Object), "anime-list.xml"), "<anime-list />");
    }

    [Theory]
    [InlineData("regular", true, LogLevel.Debug)]
    [InlineData("regular", false, LogLevel.Warning)]
    [InlineData("empty-specials", true, LogLevel.Debug)]
    [InlineData("empty-specials", false, LogLevel.Warning)]
    [InlineData("unmatched-special", true, LogLevel.Debug)]
    [InlineData("unmatched-special", false, LogLevel.Warning)]
    public async Task MissingMetadata_OnlyWarnsForEpisodesWithFiles(string scenario, bool missing, LogLevel expected)
    {
        if (scenario == "unmatched-special")
        {
            await File.WriteAllTextAsync(
                Path.Combine(_seriesDirectory, "episode-S1.xml"),
                "<episode id=\"100\"><title xml:lang=\"en\">Known special</title></episode>",
                TestContext.Current.CancellationToken);
        }

        var info = Lookup(scenario == "regular" ? 1 : 0, missing);
        info.Name = "Entirely different episode";

        var result = await _provider.GetMetadata(info, TestContext.Current.CancellationToken);

        Assert.False(result.HasMetadata);
        var entry = Assert.Single(_logger.Entries, e => e.Message.Contains("without metadata", StringComparison.Ordinal));
        Assert.Equal(expected, entry.Level);
    }

    [Fact]
    public async Task PlaceholderStillGetsMetadataWhenTheEpisodeAppears()
    {
        var info = Lookup(1, true);
        Assert.False((await _provider.GetMetadata(info, TestContext.Current.CancellationToken)).HasMetadata);
        await File.WriteAllTextAsync(
            Path.Combine(_seriesDirectory, "episode-25.xml"),
            "<episode id=\"101\"><title xml:lang=\"en\">New episode</title></episode>",
            TestContext.Current.CancellationToken);

        var result = await _provider.GetMetadata(info, TestContext.Current.CancellationToken);

        Assert.True(result.HasMetadata);
        Assert.Equal("New episode", result.Item.Name);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previousInstance);
        if (AniDbTitleMatcher.DefaultInstance is IDisposable matcher)
        {
            matcher.Dispose();
        }

        AniDbTitleMatcher.DefaultInstance = _previousTitleMatcher;
        AniDbSeriesProvider.Logger = _previousSeriesLogger;
        _temporary.Delete(recursive: true);
    }

    private static EpisodeInfo Lookup(int season, bool missing) => new()
    {
        ParentIndexNumber = season,
        IndexNumber = 25,
        IsMissingEpisode = missing,
        MetadataLanguage = "en",
        SeriesProviderIds = new Dictionary<string, string> { [ProviderNames.AniDb] = "123" }
    };

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
