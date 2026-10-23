using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Linkarr.Api.Apps;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkarr.Tests;

// Sessions recorded at sign-in, the daily catalog snapshots and the admin's /api/stats, against fake apps.
public sealed class StatsTests : IDisposable
{
    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApp apps = new((method, path, _) => (method, path) switch
    {
        ("GET", "/api/v3/movie") => (HttpStatusCode.OK, """[{"id":12,"title":"The Matrix","tmdbId":603,"year":1999,"monitored":true,"hasFile":true,"path":"/media/movies/The Matrix (1999)","qualityProfileId":1}]"""),
        ("GET", "/api/v3/series") => (HttpStatusCode.OK, "[]"),
        ("GET", _) when path.StartsWith("/api/v3/queue", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"records":[{"id":1,"title":"x","size":3000,"sizeleft":2000,"movieId":12}]}"""),
        ("GET", "/api/provision/stats") => (HttpStatusCode.OK, """
            {"publicIp":{"ip":"198.51.100.7","country":"SE","isp":"VPN AB","checkedAt":"2026-10-23T10:00:00+00:00"},
             "downloads":{"active":1,"downloadBps":100,"uploadBps":5},"storage":[{"path":"/media/movies","freeBytes":10,"totalBytes":40}],"version":"1.0.0"}
            """),
        ("GET", "/System/Info") => (HttpStatusCode.OK, """{"ServerName":"visularr","Version":"10.11.0"}"""),
        ("GET", "/Sessions?activeWithinSeconds=960") => (HttpStatusCode.OK, """
            [{"Id":"s1","UserId":"u1","UserName":"owner","Client":"Web","DeviceName":"Firefox","ApplicationVersion":"10.11.0","RemoteEndPoint":"203.0.113.9",
              "NowPlayingItem":{"Name":"Pilot","SeriesName":"Breaking Bad","Type":"Episode","ParentIndexNumber":1,"IndexNumber":1,"RunTimeTicks":1000},
              "PlayState":{"PositionTicks":250,"IsPaused":false}},
             {"Id":"s2","DeviceName":"Server"}]
            """),
        _ => (HttpStatusCode.NotFound, ""),
    });

    private readonly WebApplicationFactory<Program> factory;

    public StatsTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("LINKARR_CONFIG_DIR", configDirectory)
            .UseSetting("LINKARR_DISABLE_WORKER", "true")
            .ConfigureTestServices(services => services.AddHttpClient(AppHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => apps)));
    }

    public void Dispose()
    {
        factory.Dispose();
        apps.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(configDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held on Windows; the temp folder is cleaned eventually.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignInRecordsTheDeviceAndAnotherSessionCanBeEnded()
    {
        using HttpClient laptop = await SignedInAsync(setup: true, "Mozilla/5.0 (Windows NT 10.0) Firefox/140.0");
        using HttpClient phone = await SignedInAsync(setup: false, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) Safari/604.1");

        JsonArray sessions = (await laptop.GetFromJsonAsync<JsonArray>("/api/auth/sessions", Ct))!;
        Assert.Equal(2, sessions.Count);
        JsonNode mine = Assert.Single(sessions, s => (bool)s!["current"]!)!;
        JsonNode other = Assert.Single(sessions, s => !(bool)s!["current"]!)!;
        Assert.Contains("Firefox", (string?)mine["userAgent"], StringComparison.Ordinal);
        Assert.Contains("iPhone", (string?)other["userAgent"], StringComparison.Ordinal);
        // The cookie's secret never leaves the server: the id shown is a short hash.
        Assert.Equal(16, ((string)other["id"]!).Length);

        using HttpResponseMessage ended = await laptop.PostAsJsonAsync("/api/auth/sessions/signout", new { id = (string)other["id"]! }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        using HttpResponseMessage after = await phone.GetAsync(new Uri("/api/auth/sessions", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using HttpResponseMessage again = await laptop.PostAsJsonAsync("/api/auth/sessions/signout", new { id = (string)other["id"]! }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.NotNull((await laptop.GetFromJsonAsync<JsonObject>("/api/auth/state", Ct))!["user"]!["id"]);
    }

    [Fact]
    public async Task MembersSeeOnlyTheirOwnSessionsAndNoStats()
    {
        using HttpClient admin = await SignedInAsync(setup: true, "Admin browser");
        using HttpResponseMessage created = await admin.PostAsJsonAsync("/api/users", new { username = "member", password = "password123" }, Ct);
        JsonObject user = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        using HttpClient member = factory.CreateClient();
        member.DefaultRequestHeaders.UserAgent.ParseAdd("MemberPhone/1.0");
        using HttpResponseMessage login = await member.PostAsJsonAsync("/api/auth/login", new { username = "member", password = "password123" }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        JsonNode own = Assert.Single((await member.GetFromJsonAsync<JsonArray>("/api/auth/sessions", Ct))!)!;
        Assert.Equal("MemberPhone/1.0", (string?)own["userAgent"]);
        using HttpResponseMessage stats = await member.GetAsync(new Uri("/api/stats", UriKind.Relative), Ct);
        using HttpResponseMessage others = await member.GetAsync(new Uri($"/api/users/{(int)user["id"]!}/sessions", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, stats.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, others.StatusCode);

        // The admin sees and ends the member's session.
        JsonNode seen = Assert.Single((await admin.GetFromJsonAsync<JsonArray>($"/api/users/{(int)user["id"]!}/sessions", Ct))!)!;
        Assert.False((bool)seen["current"]!);
        using HttpResponseMessage ended = await admin.PostAsJsonAsync($"/api/users/{(int)user["id"]!}/sessions/signout", new { id = (string)seen["id"]! }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        using HttpResponseMessage after = await member.GetAsync(new Uri("/api/auth/sessions", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task CatalogSyncWritesOneSnapshotPerDayAndDropsOldOnes()
    {
        using HttpClient admin = await SignedInAsync(setup: true, "Admin browser");
        await ConnectAsync(visularr: false);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
            db.Snapshots.Add(new CatalogSnapshot { Date = today.AddDays(-CatalogSnapshot.SnapshotDays - 1), Movies = 99 });
            db.Snapshots.Add(new CatalogSnapshot { Date = today.AddDays(-3), Movies = 7 });
            await db.SaveChangesAsync(Ct);
        }

        for (int i = 0; i < 2; i++)
        {
            using HttpResponseMessage sync = await admin.PostAsync(new Uri("/api/catalog/sync", UriKind.Relative), null, Ct);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        }

        await using AsyncServiceScope check = factory.Services.CreateAsyncScope();
        List<CatalogSnapshot> snapshots = await check.ServiceProvider.GetRequiredService<LinkarrDbContext>().Snapshots.OrderBy(s => s.Date).ToListAsync(Ct);
        Assert.Equal([today.AddDays(-3), today], snapshots.Select(s => s.Date));
        Assert.Equal(1, snapshots[1].Movies);
        Assert.Equal(1, snapshots[1].Downloading);
    }

    [Fact]
    public void AvailabilityDatesCarryOverAndTheFirstSyncDatesNothing()
    {
        DateTime then = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime now = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        CatalogItem Item(string key, CatalogState state, DateTime? since = null) => new() { Key = key, Title = key, State = state, AvailableSince = since };

        List<CatalogItem> first = [Item("a", CatalogState.Available)];
        CatalogSync.CarryAvailability(first, new Dictionary<string, CatalogItem>(), now);
        Assert.Null(first[0].AvailableSince);

        List<CatalogItem> next = [Item("a", CatalogState.Available), Item("b", CatalogState.Available), Item("c", CatalogState.Available), Item("d", CatalogState.Missing)];
        Dictionary<string, CatalogItem> previous = new()
        {
            ["a"] = Item("a", CatalogState.Available, then),
            ["c"] = Item("c", CatalogState.Downloading),
            ["d"] = Item("d", CatalogState.Available, then),
        };
        CatalogSync.CarryAvailability(next, previous, now);
        Assert.Equal([then, now, now, null], next.Select(c => c.AvailableSince));
    }

    [Fact]
    public async Task StatsGatherBothAppsDownloadsAndWatching()
    {
        using HttpClient admin = await SignedInAsync(setup: true, "Mozilla/5.0 Firefox/140.0");
        await ConnectAsync(visularr: true);

        JsonObject stats = (await admin.GetFromJsonAsync<JsonObject>("/api/stats", Ct))!;

        JsonArray services = stats["services"]!.AsArray();
        Assert.All(services, s => Assert.True((bool)s!["reachable"]!));
        Assert.Equal("1.0.0", (string?)services[0]!["version"]);
        Assert.Equal("10.11.0", (string?)services[1]!["version"]);
        Assert.Equal("198.51.100.7", (string?)stats["publicIp"]!["ip"]);
        Assert.Equal(1, (int)stats["downloads"]!["active"]!);
        // 2000 bytes left at 100 B/s.
        Assert.Equal(20, (long)stats["downloads"]!["etaSeconds"]!);
        Assert.Equal(40, (long)stats["storage"]![0]!["totalBytes"]!);
        JsonNode watching = Assert.Single(stats["watching"]!.AsArray())!;
        Assert.Equal("203.0.113.9", (string?)watching["ip"]);
        Assert.Equal("Breaking Bad · S01E01 · Pilot", (string?)watching["nowPlaying"]!["title"]);
        Assert.Equal(0.25, (double)watching["nowPlaying"]!["progress"]!);
        JsonNode owner = Assert.Single(stats["users"]!.AsArray())!;
        Assert.NotNull(owner["lastSignIn"]);
        Assert.Contains("Firefox", (string?)Assert.Single(owner["sessions"]!.AsArray())!["userAgent"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatsReportAFailingAppWithoutFailing()
    {
        using HttpClient admin = await SignedInAsync(setup: true, "Admin browser");
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
            AppSettings settings = await db.Settings.FirstAsync(Ct);
            // Under a base path the fake app does not serve: every call answers 404, like a modularr too old for /api/provision/stats.
            settings.ModularrUrl = "http://modularr:8080/old";
            settings.ModularrKey = scope.ServiceProvider.GetRequiredService<Connections>().Protect("modularr-key");
            await db.SaveChangesAsync(Ct);
        }

        JsonObject stats = (await admin.GetFromJsonAsync<JsonObject>("/api/stats", Ct))!;

        JsonNode modularr = stats["services"]![0]!;
        Assert.True((bool)modularr["configured"]!);
        Assert.False((bool)modularr["reachable"]!);
        Assert.Contains("404", (string?)modularr["error"], StringComparison.Ordinal);
        Assert.Null(stats["downloads"]);
        // visularr is not configured: reported so, not as down.
        Assert.False((bool)stats["services"]![1]!["configured"]!);
        Assert.Empty(stats["watching"]!.AsArray());
    }

    private async Task<HttpClient> SignedInAsync(bool setup, string userAgent)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        using HttpResponseMessage response = await client.PostAsJsonAsync(setup ? "/api/auth/setup" : "/api/auth/login", new { username = "owner", password = "password123" }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task ConnectAsync(bool visularr)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
        Connections connections = scope.ServiceProvider.GetRequiredService<Connections>();
        AppSettings settings = await db.Settings.FirstAsync(Ct);
        settings.ModularrUrl = "http://modularr:8080";
        settings.ModularrKey = connections.Protect("modularr-key");
        if (visularr)
        {
            settings.VisularrUrl = "http://visularr:8096";
            settings.VisularrKey = connections.Protect("visularr-key");
        }

        await db.SaveChangesAsync(Ct);
    }
}
