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

// The deployment's connections from the environment, and the owner created by setup in both apps: the real app, both apps faked.
public sealed class OwnerSetupTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApp apps;
    private readonly WebApplicationFactory<Program> factory;

    // Whether both apps answer (a stack still starting refuses connections), and whether the owner was made a visularr admin.
    private volatile bool up = true;
    private volatile bool ownerAdmin;
    private volatile bool ownerCreated;

    public OwnerSetupTests()
    {
        apps = new FakeApp(Route);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("LINKARR_CONFIG_DIR", configDirectory)
            .UseSetting("LINKARR_DISABLE_WORKER", "true")
            .UseSetting("MODULARR_URL", "http://modularr:8080")
            .UseSetting("MODULARR_API_KEY", "modularr-key")
            .UseSetting("MODULARR_PUBLIC_URL", "http://localhost:8080")
            .UseSetting("VISULARR_URL", "http://visularr:8096")
            .UseSetting("VISULARR_API_KEY", "visularr-key")
            .UseSetting("LINKARR_PUBLIC_URL", "http://localhost:8070")
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

    // visularr always lists its bootstrap admin (the fake keeps no state for deletes).
    private (HttpStatusCode, string)? Route(string method, string path, string body)
    {
        if (!up)
        {
            return null;
        }

        string owner = $$$"""{"Id":"o1","Name":"owner","Policy":{"IsAdministrator":{{{(ownerAdmin ? "true" : "false")}}},"IsDisabled":false}}""";
        switch (method, path)
        {
            case ("GET", "/Users"):
                return (HttpStatusCode.OK, """[{"Id":"boot","Name":"visularr-bootstrap","Policy":{"IsAdministrator":true,"IsDisabled":false}}""" + (ownerCreated ? "," + owner : "") + "]");
            case ("POST", "/Users/New"):
                ownerCreated = true;
                return (HttpStatusCode.OK, """{"Id":"o1","Name":"owner"}""");
            case ("GET", "/Users/o1"):
                return (HttpStatusCode.OK, owner);
            case ("POST", "/Users/o1/Policy"):
                ownerAdmin = JsonNode.Parse(body)!["IsAdministrator"]!.GetValue<bool>();
                return (HttpStatusCode.NoContent, "");
            case ("DELETE", "/Users/boot") or ("POST", "/Visularr/Managed") or ("PUT", "/api/provision/managed"):
                return (HttpStatusCode.NoContent, "");
            case ("GET", "/api/provision/users"):
                return (HttpStatusCode.OK, "[]");
            case ("PUT", _) when path.StartsWith("/api/provision/users/", StringComparison.Ordinal):
                return (HttpStatusCode.OK, """{"id":1}""");
            default:
                return (HttpStatusCode.NotFound, "");
        }
    }

    [Fact]
    public async Task DeploymentConnectionsAreSeededAndWinOverSettings()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);

        using HttpResponseMessage saved = await client.PutAsJsonAsync("/api/settings", new
        {
            modularrUrl = "http://elsewhere:1", modularrApiKey = "typed-key", visularrUrl = "http://elsewhere:2", visularrPublicUrl = "http://watch.example",
            requestsNeedApproval = true, autoFixNotIndexed = false,
        }, Ct);
        string body = await saved.Content.ReadAsStringAsync(Ct);
        JsonNode settings = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("http://modularr:8080", settings["modularrUrl"]!.GetValue<string>());
        Assert.Equal("http://visularr:8096", settings["visularrUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:8070", settings["linkarrPublicUrl"]!.GetValue<string>());
        // Not set by the deployment: the admin's value.
        Assert.Equal("http://watch.example", settings["visularrPublicUrl"]!.GetValue<string>());
        Assert.True(settings["modularrKeySet"]!.GetValue<bool>());
        List<string> fields = [.. settings["deploymentFields"]!.AsArray().Select(f => f!.GetValue<string>())];
        Assert.Contains("modularrApiKey", fields);
        Assert.DoesNotContain("visularrPublicUrl", fields);
        Assert.DoesNotContain("modularr-key", body, StringComparison.Ordinal);

        // The environment's key, not the typed one, is what modularr gets.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Connections connections = scope.ServiceProvider.GetRequiredService<Connections>();
        Assert.Equal("modularr-key", connections.Unprotect((await connections.SettingsAsync(Ct)).ModularrKey));
    }

    [Fact]
    public async Task SetupMakesTheOwnerAdminInBothAppsThenDeletesVisularrsBootstrapAdmin()
    {
        using HttpClient client = factory.CreateClient();
        JsonNode me = await SetupAsync(client);

        Assert.False(me["passwordPending"]!.GetValue<bool>());
        JsonObject upsert = JsonNode.Parse(Assert.Single(apps.Requests, r => r.Path.StartsWith("/api/provision/users/", StringComparison.Ordinal)).Body)!.AsObject();
        Assert.Equal("Admin", upsert["role"]!.GetValue<string>());
        Assert.Equal(Password, upsert["password"]!.GetValue<string>());
        (string _, string _, string created, string? key) = Assert.Single(apps.Requests, r => r.Path == "/Users/New");
        Assert.Equal("visularr-key", key);
        Assert.Equal(Password, JsonNode.Parse(created)!["Password"]!.GetValue<string>());

        int adminPolicy = apps.Requests.FindIndex(r => r.Path == "/Users/o1/Policy" && JsonNode.Parse(r.Body)!["IsAdministrator"]!.GetValue<bool>());
        int bootstrapDeleted = apps.Requests.FindIndex(r => r is { Method: "DELETE", Path: "/Users/boot" });
        Assert.True(adminPolicy >= 0);
        Assert.True(bootstrapDeleted > adminPolicy);
    }

    [Fact]
    public async Task OwnerIsProvisionedOnceTheAppsComeUp()
    {
        up = false;
        using HttpClient client = factory.CreateClient();
        JsonNode me = await SetupAsync(client);
        Assert.True(me["passwordPending"]!.GetValue<bool>());

        up = true;
        await factory.Services.GetRequiredService<OwnerProvisioning>().Retrying.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Account owner = await scope.ServiceProvider.GetRequiredService<LinkarrDbContext>().Accounts.SingleAsync(Ct);
        Assert.False(owner.PasswordPending);
        Assert.Equal(SyncStatus.Synced, owner.SyncStatus);
        Assert.Equal(Password, JsonNode.Parse(Assert.Single(apps.Requests, r => r.Path == "/Users/New").Body)!["Password"]!.GetValue<string>());
        Assert.Contains(apps.Requests, r => r is { Method: "DELETE", Path: "/Users/boot" });
    }

    [Fact]
    public async Task VisularrsBootstrapAdminIsNeverAnUnmanagedUser()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);

        using HttpResponseMessage sync = await client.PostAsync(new Uri("/api/users/sync", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Empty(JsonNode.Parse(await sync.Content.ReadAsStringAsync(Ct))!["unmanaged"]!.AsArray());
    }

    private static async Task<JsonNode> SetupAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }
}
