using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Linkarr.Api;
using Linkarr.Api.Apps;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkarr.Tests;

// The go-links, modularr's webhook and the managed push, on the real app with both apps faked behind the outbound client.
public sealed class LinkTests : IDisposable
{
    private const string Visularr = "https://watch.example";
    private const string Modularr = "https://manage.example";

    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApp apps = new((method, path, _) => (method, path) switch
    {
        ("PUT", "/api/provision/managed") or ("POST", "/Library/Media/Updated") => (HttpStatusCode.NoContent, ""),
        ("GET", "/api/provision/users") => (HttpStatusCode.OK, "[]"),
        ("PUT", _) when path.StartsWith("/api/provision/users/", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"id":1}"""),
        _ => (HttpStatusCode.NotFound, ""),
    });

    private readonly WebApplicationFactory<Program> factory;

    public LinkTests()
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

    // Signed out, the target never depends on the catalog (it would reveal what is in it); signed in, the catalog resolves it.
    [Theory]
    [InlineData("/go/watch?tmdb=603", true, Visularr + "/web/item/?id=abc123")]
    [InlineData("/go/watch?tmdb=603", false, Visularr + "/web/open/?tmdb=603")]
    [InlineData("/go/watch?tvdb=81189", true, Visularr + "/web/open/?tvdb=81189")]
    [InlineData("/go/manage?tmdb=603", true, Modularr + "/title/?type=movie&id=12")]
    [InlineData("/go/manage?tmdb=603", false, Modularr + "/explore/?q=tmdb%3A603")]
    [InlineData("/go/manage?tvdb=81189", true, Modularr + "/explore/?q=Breaking%20Bad")]
    [InlineData("/go/manage?tvdb=81189", false, Modularr + "/explore/?q=tvdb%3A81189")]
    [InlineData("/go/manage?tmdb=999", true, Modularr + "/explore/?q=tmdb%3A999")]
    [InlineData("/go/app/modularr", false, Modularr + "/")]
    [InlineData("/go/app/visularr", false, Visularr + "/")]
    public async Task GoLinksRedirectToTheConfiguredApp(string link, bool signedIn, string target)
    {
        using HttpClient client = Client();
        await SeedAsync(publicUrls: true);
        if (signedIn)
        {
            using HttpResponseMessage setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, Ct);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        }

        using HttpResponseMessage response = await client.GetAsync(new Uri(link, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/go/watch")]
    [InlineData("/go/watch?tmdb=abc")]
    [InlineData("/go/watch?tmdb=-1")]
    [InlineData("/go/watch?tmdb=+1")]
    [InlineData("/go/watch?tmdb=0")]
    [InlineData("/go/watch?tmdb=1e3")]
    [InlineData("/go/watch?tmdb=%EF%BC%91")]
    [InlineData("/go/watch?tmdb=99999999999")]
    [InlineData("/go/watch?tmdb=1&tvdb=2")]
    [InlineData("/go/watch?tmdb=https://evil.example")]
    [InlineData("/go/manage?tvdb=//evil.example")]
    public async Task GoLinksTakeOneDigitsOnlyId(string link)
    {
        using HttpClient client = Client();
        await SeedAsync(publicUrls: true);

        using HttpResponseMessage response = await client.GetAsync(new Uri(link, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task UnknownAppIsNotFound()
    {
        using HttpClient client = Client();
        await SeedAsync(publicUrls: true);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/go/app/evil.example", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task WithoutPublicUrlAPageExplainsIt()
    {
        using HttpClient client = Client();
        await SeedAsync(publicUrls: false);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/go/watch?tmdb=603", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("visularr has no public URL", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public void IdsAreParsedStrictly()
    {
        Assert.Equal(("tmdb", 603), GoEndpoints.ParseId("603", null));
        Assert.Equal(("tvdb", 81189), GoEndpoints.ParseId(null, "81189"));
        Assert.Null(GoEndpoints.ParseId(" 603", null));
        Assert.Null(GoEndpoints.ParseId("٦٠٣", null));
        Assert.Null(GoEndpoints.ParseId("1", "2"));
        Assert.Null(GoEndpoints.ParseId(null, null));
    }

    // modularr's generic webhook: an import refreshes the title's folder in visularr at once and schedules a catalog sync.
    [Fact]
    public async Task ImportHookRefreshesTheFolderAndSchedulesACatalogSync()
    {
        using HttpClient client = Client();
        string token = await SeedAsync(publicUrls: true, visularrKey: true, autoFix: true);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/hooks/modularr")
        {
            Content = JsonContent.Create(new { @event = "import", title = "The Matrix (1999)", message = "1080p", seriesId = (int?)null, movieId = 12, time = DateTime.UtcNow }),
        };
        request.Headers.Add("X-Linkarr-Token", token);
        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonNode body = JsonNode.Parse(Assert.Single(apps.Requests, r => r.Path == "/Library/Media/Updated").Body)!;
        Assert.Equal("/media/movies/The Matrix (1999)", body["Updates"]![0]!["Path"]!.GetValue<string>());
        Assert.NotNull(factory.Services.GetRequiredService<SyncState>().CatalogDue);
    }

    [Fact]
    public async Task OtherEventsOnlyScheduleACatalogSync()
    {
        using HttpClient client = Client();
        string token = await SeedAsync(publicUrls: true, visularrKey: true, autoFix: true);

        using HttpResponseMessage grab = await client.PostAsJsonAsync($"/api/hooks/modularr?token={token}",
            new { @event = "grab", title = "The Matrix (1999)", movieId = 12, time = DateTime.UtcNow }, Ct);
        using HttpResponseMessage bad = await client.PostAsync(new Uri($"/api/hooks/modularr?token={token}", UriKind.Relative),
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.Accepted, grab.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.DoesNotContain(apps.Requests, r => r.Path == "/Library/Media/Updated");
        Assert.NotNull(factory.Services.GetRequiredService<SyncState>().CatalogDue);
    }

    // An event's own Path is only trusted when it is a catalog title's folder: a forged event cannot aim visularr's scan elsewhere.
    [Theory]
    [InlineData("/media/movies/The Matrix (1999)/", true)]
    [InlineData("/etc", false)]
    [InlineData("/media/movies/The Matrix (1999)/../../..", false)]
    public async Task ImportHookPathMustBeACatalogFolder(string path, bool refreshed)
    {
        using HttpClient client = Client();
        string token = await SeedAsync(publicUrls: true, visularrKey: true, autoFix: true);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/hooks/modularr")
        {
            Content = JsonContent.Create(new { @event = "import", title = "x", movieId = 999, path, time = DateTime.UtcNow }),
        };
        request.Headers.Add("X-Linkarr-Token", token);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(refreshed ? ["/media/movies/The Matrix (1999)"] : [],
            apps.Requests.Where(r => r.Path == "/Library/Media/Updated").Select(r => JsonNode.Parse(r.Body)!["Updates"]![0]!["Path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task WrongHookTokenIsRefused()
    {
        using HttpClient client = Client();
        await SeedAsync(publicUrls: true);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/hooks/modularr") { Content = JsonContent.Create(new { @event = "grab" }) };
        request.Headers.Add("X-Linkarr-Token", "not-the-token");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void HookEventsAreDebounced()
    {
        SyncState state = new();
        state.RequestCatalog(TimeSpan.FromMinutes(1));
        Assert.False(state.TakeCatalogRequest());
        state.RequestCatalog(TimeSpan.Zero);
        Assert.True(state.TakeCatalogRequest());
        Assert.False(state.TakeCatalogRequest());
        Assert.Null(state.CatalogDue);
    }

    // The managed push hands modularr the webhook token, so its events reach linkarr without any setup there.
    [Fact]
    public async Task ManagedPushSendsTheHookToken()
    {
        using HttpClient client = Client();
        using HttpResponseMessage setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, Ct);
        using HttpResponseMessage saved = await client.PutAsJsonAsync("/api/settings", new
        {
            modularrUrl = "http://modularr:8080", modularrApiKey = "modularr-key", linkarrPublicUrl = "http://linkarr.example:8070",
            requestsNeedApproval = false, autoFixNotIndexed = false,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using HttpResponseMessage sync = await client.PostAsync(new Uri("/api/users/sync", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        (string _, string _, string pushed, string? key) = Assert.Single(apps.Requests, r => r is { Method: "PUT", Path: "/api/provision/managed" });
        JsonNode managed = JsonNode.Parse(pushed)!;
        Assert.Equal("modularr-key", key);
        Assert.Equal("linkarr", managed["name"]!.GetValue<string>());
        Assert.Equal("http://linkarr.example:8070", managed["url"]!.GetValue<string>());
        Assert.Equal(await TokenAsync(), managed["hookToken"]!.GetValue<string>());

        // Rotating makes a new token and pushes it to modularr at once.
        string before = await TokenAsync();
        using HttpResponseMessage rotated = await client.PostAsync(new Uri("/api/settings/webhook-token", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        string after = await TokenAsync();
        Assert.NotEqual(before, after);
        Assert.Equal(after, JsonNode.Parse(apps.Requests.Last(r => r is { Method: "PUT", Path: "/api/provision/managed" }).Body)!["hookToken"]!.GetValue<string>());
    }

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> TokenAsync()
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<LinkarrDbContext>().Settings.FirstAsync(Ct)).WebhookToken;
    }

    // Public URLs, the catalog (one movie in both apps, one series only in visularr) and optionally a visularr connection.
    private async Task<string> SeedAsync(bool publicUrls, bool visularrKey = false, bool autoFix = false)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
        AppSettings settings = await db.Settings.FirstAsync(Ct);
        settings.VisularrPublicUrl = publicUrls ? Visularr : null;
        settings.ModularrPublicUrl = publicUrls ? Modularr : null;
        settings.AutoFixNotIndexed = autoFix;
        if (visularrKey)
        {
            settings.VisularrUrl = "http://visularr:8096";
            settings.VisularrKey = scope.ServiceProvider.GetRequiredService<Connections>().Protect("visularr-key");
        }

        db.Catalog.AddRange(
            new CatalogItem { Key = "movie:tmdb:603", Kind = TitleKind.Movie, Title = "The Matrix", TmdbId = 603, ModularrId = 12, VisularrId = "abc123", ModularrPath = "/media/movies/The Matrix (1999)" },
            new CatalogItem { Key = "series:tvdb:81189", Kind = TitleKind.Series, Title = "Breaking Bad", TvdbId = 81189 });
        await db.SaveChangesAsync(Ct);
        return settings.WebhookToken;
    }
}
