using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Linkarr.Api.Apps;
using Linkarr.Api.Data;
using Linkarr.Api.Requests;
using Linkarr.Api.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkarr.Tests;

// Setup token, headers, adoption, the request limits and what members see of app errors: the real app, both apps faked.
public sealed class SecurityTests : IDisposable
{
    private const string Secret = "secret-internal-detail";

    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApp apps;
    private WebApplicationFactory<Program>? factory;
    private readonly Dictionary<string, int> modularrIds = [];
    private volatile bool ownerCreated;

    public SecurityTests() => apps = new FakeApp(Route);

    public void Dispose()
    {
        factory?.Dispose();
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

    private static string JellyfinUser(string id, string name) =>
        $$$"""{"Id":"{{{id}}}","Name":"{{{name}}}","Policy":{"IsAdministrator":false,"IsDisabled":false}}""";

    // visularr has an unmanaged "alice"; modularr an unmanaged local "carol" (id 5). A lookup of "boom" fails with a body excerpt.
    private (HttpStatusCode, string)? Route(string method, string path, string body)
    {
        switch (method, path)
        {
            case ("GET", "/Users"):
                return (HttpStatusCode.OK, "[" + JellyfinUser("v-alice", "alice") + (ownerCreated ? "," + JellyfinUser("v-owner", "owner") : "") + "]");
            case ("POST", "/Users/New"):
                ownerCreated = true;
                return (HttpStatusCode.OK, JellyfinUser("v-owner", "owner"));
            case ("GET", "/Users/v-owner"):
                return (HttpStatusCode.OK, JellyfinUser("v-owner", "owner"));
            case ("GET", "/Users/v-alice"):
                return (HttpStatusCode.OK, JellyfinUser("v-alice", "alice"));
            case ("GET", "/api/provision/users"):
                return (HttpStatusCode.OK, """[{"id":5,"username":"carol","role":"User","externalId":null}]""");
            case ("PUT", _) when path.StartsWith("/api/provision/users/", StringComparison.Ordinal):
                // Like modularr: one user per externalId; adopt takes carol over.
                lock (modularrIds)
                {
                    string externalId = path["/api/provision/users/".Length..];
                    if (!modularrIds.TryGetValue(externalId, out int userId))
                    {
                        userId = JsonNode.Parse(body)!["adopt"]!.GetValue<bool>() ? 5 : modularrIds.Count + 10;
                        modularrIds[externalId] = userId;
                    }

                    return (HttpStatusCode.OK, $$"""{"id":{{userId}}}""");
                }

            case ("GET", _) when path.StartsWith("/api/v3/movie/lookup?term=tmdb%3A", StringComparison.Ordinal):
                string id = path["/api/v3/movie/lookup?term=tmdb%3A".Length..];
                return (HttpStatusCode.OK, $$"""[{"id":0,"title":"Movie {{id}}","tmdbId":{{id}},"year":2000,"monitored":false,"hasFile":false,"path":"","rootFolderPath":"/media/movies","qualityProfileId":1}]""");
            case ("GET", _) when path.StartsWith("/api/v3/movie/lookup", StringComparison.Ordinal):
            case ("POST", "/api/v3/movie"):
                return (HttpStatusCode.InternalServerError, Secret);
            default:
                return method == "GET" ? (HttpStatusCode.NotFound, "") : (HttpStatusCode.NoContent, "");
        }
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LINKARR_CONFIG_DIR", configDirectory).UseSetting("LINKARR_DISABLE_WORKER", "true");
            foreach ((string key, string value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.AddHttpClient(AppHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => apps));
        });
        return factory;
    }

    private WebApplicationFactory<Program> WithApps() => Factory(
        ("MODULARR_URL", "http://modularr:8080"), ("MODULARR_API_KEY", "modularr-key"),
        ("VISULARR_URL", "http://visularr:8096"), ("VISULARR_API_KEY", "visularr-key"));

    private static async Task SetupAsync(HttpClient client, object? extra = null)
    {
        using HttpResponseMessage setup = await client.PostAsJsonAsync("/api/auth/setup", extra ?? new { username = "owner", password = "password123" }, Ct);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
    }

    [Fact]
    public async Task SetupNeedsTheDeploymentsTokenWhenOneIsSet()
    {
        using HttpClient client = Factory(("LINKARR_SETUP_TOKEN", "let-me-in")).CreateClient();

        JsonNode state = (await client.GetFromJsonAsync<JsonNode>("/api/auth/state", Ct))!;
        using HttpResponseMessage none = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, Ct);
        using HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123", setupToken = "guess" }, Ct);

        Assert.True(state["setupTokenRequired"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        await SetupAsync(client, new { username = "owner", password = "password123", setupToken = "let-me-in" });
    }

    [Fact]
    public async Task ResponsesCarryTheContentSecurityPolicy()
    {
        using HttpClient client = Factory().CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/auth/state", UriKind.Relative), Ct);

        Assert.Equal(Program.Csp, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task AnHttpsPublicUrlMakesTheSessionCookieSecure()
    {
        using HttpClient client = Factory(("LINKARR_PUBLIC_URL", "https://linkarr.example")).CreateClient();

        using HttpResponseMessage setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, Ct);

        Assert.Contains("secure", Assert.Single(setup.Headers.GetValues("Set-Cookie"), c => c.StartsWith("linkarr=", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
    }

    // A saved key only goes to the address it was entered for.
    [Fact]
    public async Task ANewAddressWithoutANewKeyDropsTheSavedKey()
    {
        using HttpClient client = Factory().CreateClient();
        await SetupAsync(client);

        async Task<bool> SaveAsync(string url, string? key)
        {
            using HttpResponseMessage saved = await client.PutAsJsonAsync("/api/settings",
                new { modularrUrl = url, modularrApiKey = key, requestsNeedApproval = false, autoFixNotIndexed = false }, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            return (await saved.Content.ReadFromJsonAsync<JsonNode>(Ct))!["modularrKeySet"]!.GetValue<bool>();
        }

        Assert.True(await SaveAsync("http://modularr-a:8080", "key-a"));
        Assert.True(await SaveAsync("http://modularr-a:8080", null));
        Assert.False(await SaveAsync("http://elsewhere.example", null));
        Assert.True(await SaveAsync("http://elsewhere.example", "key-b"));
    }

    // An existing app user is linked only by an admin's adopt, by its id (visularr) or modularr's explicit adopt flag.
    [Fact]
    public async Task AdoptLinksExactlyThatUserAndOnlyOnce()
    {
        WebApplicationFactory<Program> app = WithApps();
        using HttpClient client = app.CreateClient();
        await SetupAsync(client);
        using HttpResponseMessage sync = await client.PostAsync(new Uri("/api/users/sync", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.DoesNotContain(apps.Requests, r => r.Path == "/Users/v-alice/Policy");
        Assert.All(apps.Requests.Where(r => r is { Method: "PUT" } && r.Path.StartsWith("/api/provision/users/", StringComparison.Ordinal)),
            r => Assert.False(JsonNode.Parse(r.Body)!["adopt"]!.GetValue<bool>()));

        using HttpResponseMessage alice = await client.PostAsJsonAsync("/api/users/unmanaged/adopt", new { app = "visularr", remoteId = "v-alice" }, Ct);
        using HttpResponseMessage carol = await client.PostAsJsonAsync("/api/users/unmanaged/adopt", new { app = "modularr", remoteId = "5" }, Ct);

        Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
        Assert.Equal("v-alice", (await alice.Content.ReadFromJsonAsync<JsonNode>(Ct))!["visularrId"]!.GetValue<string>());
        Assert.Contains(apps.Requests, r => r.Path == "/Users/v-alice/Policy");
        Assert.Equal(HttpStatusCode.OK, carol.StatusCode);
        Assert.Equal(5, (await carol.Content.ReadFromJsonAsync<JsonNode>(Ct))!["modularrId"]!.GetValue<int>());
        Assert.Contains(apps.Requests, r => r.Method == "PUT" && r.Body.Contains("\"adopt\":true", StringComparison.Ordinal));

        // The same visularr user again (a stale list): refused, and the database refuses a second link too.
        SyncState state = app.Services.GetRequiredService<SyncState>();
        state.Unmanaged = [new UnmanagedUser(UserSync.Visularr, "v-alice", "owner", Admin: false, Linkable: true)];
        using HttpResponseMessage again = await client.PostAsJsonAsync("/api/users/unmanaged/adopt", new { app = "visularr", remoteId = "v-alice" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
        Account owner = await db.Accounts.FirstAsync(a => a.Username == "owner", Ct);
        owner.VisularrId = "v-alice";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task MembersAreCappedAndSeeNeitherOthersNorAppDetails()
    {
        WebApplicationFactory<Program> app = WithApps();
        using HttpClient admin = app.CreateClient();
        await SetupAsync(admin);
        using HttpResponseMessage role = await admin.PostAsJsonAsync("/api/roles", new { name = "Requester", rights = new { watch = false, request = true } }, Ct);
        int roleId = (await role.Content.ReadFromJsonAsync<JsonNode>(Ct))!["id"]!.GetValue<int>();
        using HttpResponseMessage created = await admin.PostAsJsonAsync("/api/users",
            new { username = "member", password = "password123", roleId }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
        {
            LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
            (await db.Settings.FirstAsync(Ct)).RequestsPerDay = 3;
            await db.SaveChangesAsync(Ct);
        }

        using HttpClient member = app.CreateClient();
        using (HttpResponseMessage login = await member.PostAsJsonAsync("/api/auth/login", new { username = "member", password = "password123" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        async Task<(HttpStatusCode Status, string Body)> RequestAsync(HttpClient client, int id)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync("/api/requests", new { kind = "Movie", id }, Ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.OK, (await RequestAsync(admin, 50)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RequestAsync(member, 1)).Status);
        (HttpStatusCode taken, string takenBody) = await RequestAsync(member, 50);
        Assert.Equal(HttpStatusCode.Conflict, taken);
        Assert.DoesNotContain("owner", takenBody, StringComparison.Ordinal);

        // Denied: the member cannot ask again until an admin re-opens it.
        int deniedId = (await admin.GetFromJsonAsync<JsonArray>("/api/requests", Ct))!.First(r => r!["tmdbId"]!.GetValue<int>() == 1)!["id"]!.GetValue<int>();
        using (HttpResponseMessage deny = await admin.PostAsync(new Uri($"/api/requests/{deniedId}/deny", UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, deny.StatusCode);
        }

        (HttpStatusCode denied, string deniedBody) = await RequestAsync(member, 1);
        Assert.Equal(HttpStatusCode.Conflict, denied);
        Assert.Contains("denied", deniedBody, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await RequestAsync(member, 2)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RequestAsync(member, 3)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RequestAsync(member, 4)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RequestAsync(admin, 5)).Status);

        // modularr's answer (with its body excerpt) reaches admins only, live and stored.
        string mine = await member.GetStringAsync(new Uri("/api/requests", UriKind.Relative), Ct);
        Assert.DoesNotContain(Secret, mine, StringComparison.Ordinal);
        Assert.Contains(RequestEndpoints.MemberError, mine, StringComparison.Ordinal);
        Assert.Contains(Secret, await admin.GetStringAsync(new Uri("/api/requests", UriKind.Relative), Ct), StringComparison.Ordinal);
        using HttpResponseMessage memberSearch = await member.GetAsync(new Uri("/api/discover?kind=Movie&term=boom", UriKind.Relative), Ct);
        using HttpResponseMessage adminSearch = await admin.GetAsync(new Uri("/api/discover?kind=Movie&term=boom", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.BadGateway, memberSearch.StatusCode);
        Assert.DoesNotContain(Secret, await memberSearch.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains(Secret, await adminSearch.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
