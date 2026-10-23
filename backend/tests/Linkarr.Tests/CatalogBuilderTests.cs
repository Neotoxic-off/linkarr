using Linkarr.Api.Apps;
using Linkarr.Api.Catalog;
using Linkarr.Api.Data;

namespace Linkarr.Tests;

public sealed class CatalogBuilderTests
{
    private static ArrMovie Movie(int id, int tmdbId, bool hasFile, bool monitored = true) =>
        new(id, $"Movie {id}", tmdbId, 2020, monitored, hasFile, $"/media/movies/Movie {id} (2020)", "/media/movies", 1, null, null);

    private static ArrSeries Series(int id, int tvdbId, int files) =>
        new(id, $"Show {id}", tvdbId, 2019, true, $"/media/tv/Show {id}", "/media/tv", 1, null, new ArrStatistics(files), null);

    private static JellyfinItem Item(string id, string type, string? provider, string? value, string? path = null) =>
        new(id, $"Item {id}", type, 2020, provider is null ? null : new Dictionary<string, string> { [provider] = value! }, path);

    [Fact]
    public void ComputesEveryState()
    {
        IReadOnlyList<CatalogItem> catalog = CatalogBuilder.Build(
            [Movie(1, 100, hasFile: true), Movie(2, 200, hasFile: true), Movie(3, 300, hasFile: false), Movie(4, 400, hasFile: false), Movie(5, 500, hasFile: false, monitored: false)],
            [Series(10, 1000, files: 3)],
            [new ArrQueueItem(1, "Movie 4 release", Size: 1000, Sizeleft: 250, MovieId: 4, SeriesId: null)],
            [Item("v1", "Movie", "Tmdb", "100"), Item("v10", "Series", "Tvdb", "1000"), Item("v9", "Movie", "Tmdb", "900")]);

        Dictionary<string, CatalogItem> byKey = catalog.ToDictionary(c => c.Key);
        Assert.Equal(CatalogState.Available, byKey["movie:tmdb:100"].State);
        Assert.Equal("v1", byKey["movie:tmdb:100"].VisularrId);
        Assert.Equal(CatalogState.NotIndexed, byKey["movie:tmdb:200"].State);
        Assert.Equal(CatalogState.Missing, byKey["movie:tmdb:300"].State);
        Assert.Equal(CatalogState.Downloading, byKey["movie:tmdb:400"].State);
        Assert.Equal(0.75, byKey["movie:tmdb:400"].Progress);
        Assert.Equal(CatalogState.Unmonitored, byKey["movie:tmdb:500"].State);
        Assert.Equal(CatalogState.Available, byKey["series:tvdb:1000"].State);
        Assert.Equal(CatalogState.Untracked, byKey["movie:tmdb:900"].State);
        Assert.Null(byKey["movie:tmdb:900"].ModularrId);
    }

    // Jellyfin's movie path is the file inside modularr's folder; an item without provider ids still matches by it.
    [Fact]
    public void MatchesByPathWhenProviderIdsAreMissing()
    {
        IReadOnlyList<CatalogItem> catalog = CatalogBuilder.Build(
            [Movie(1, 100, hasFile: true)], [], [],
            [Item("v1", "Movie", null, null, "/media/movies/Movie 1 (2020)/Movie 1 (2020).mkv")]);

        CatalogItem only = Assert.Single(catalog);
        Assert.Equal(CatalogState.Available, only.State);
    }

    [Fact]
    public void UntrackedItemWithoutIdKeepsVisularrKey()
    {
        CatalogItem only = Assert.Single(CatalogBuilder.Build([], [], [], [Item("abc", "Series", null, null)]));

        Assert.Equal("visularr:abc", only.Key);
        Assert.Equal(TitleKind.Series, only.Kind);
        Assert.Equal(CatalogState.Untracked, only.State);
    }

    [Fact]
    public void RequestStatusFollowsTheCatalog()
    {
        TitleRequest request = new() { Username = "bob", Title = "T", Approval = Approval.Approved, TmdbId = 1 };

        Assert.Equal("requested", Linkarr.Api.Requests.RequestEndpoints.StatusOf(request, null));
        Assert.Equal("downloading", Linkarr.Api.Requests.RequestEndpoints.StatusOf(request, new CatalogItem { Key = "k", Title = "T", State = CatalogState.Downloading }));
        Assert.Equal("available", Linkarr.Api.Requests.RequestEndpoints.StatusOf(request, new CatalogItem { Key = "k", Title = "T", State = CatalogState.Available }));
        Assert.Equal("pending", Linkarr.Api.Requests.RequestEndpoints.StatusOf(new TitleRequest { Username = "b", Title = "T", Approval = Approval.Pending }, null));
        request.Error = "modularr: down";
        Assert.Equal("failed", Linkarr.Api.Requests.RequestEndpoints.StatusOf(request, null));
    }

    [Fact]
    public void VisularrCopyWinsOverAnUpgradeInProgress()
    {
        Assert.Equal(CatalogState.Available, CatalogBuilder.StateOf(inVisularr: true, downloading: true, hasFile: true, monitored: true));
        Assert.Equal(CatalogState.Downloading, CatalogBuilder.StateOf(inVisularr: false, downloading: true, hasFile: true, monitored: true));
    }
}
