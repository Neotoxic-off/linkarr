using System.Net;
using System.Net.Http.Json;
using Linkarr.Api.Auth;
using Linkarr.Api.Catalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Linkarr.Tests;

// The real app on a fresh config directory, background sync off.
public sealed class AuthTests : IDisposable
{
    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> factory;

    public AuthTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("LINKARR_CONFIG_DIR", configDirectory)
            .UseSetting("LINKARR_DISABLE_WORKER", "true"));
    }

    public void Dispose()
    {
        factory.Dispose();
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

    [Fact]
    public async Task LoginIsRateLimitedPerAddress()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);
        using HttpClient anonymous = factory.CreateClient();

        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 11; i++)
        {
            using HttpResponseMessage response = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "owner", password = "wrong password" }, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        // 10 a minute per address, setup included (the test server has one address): 9 wrong logins, then refused.
        Assert.All(statuses.Take(9), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(9), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Fact]
    public async Task SetupOnlyCreatesTheFirstAdmin()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);

        using HttpResponseMessage again = await client.PostAsJsonAsync("/api/auth/setup", new { username = "intruder", password = "password123" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task CrossOriginWritesAreRefused()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/users") { Content = JsonContent.Create(new { username = "eve", password = "password123" }) };
        request.Headers.Add("Origin", "http://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MembersCannotManageUsersOrSettings()
    {
        using HttpClient admin = factory.CreateClient();
        await SetupAsync(admin);
        using HttpResponseMessage created = await admin.PostAsJsonAsync("/api/users", new { username = "member", password = "password123" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using HttpClient member = factory.CreateClient();
        using HttpResponseMessage login = await member.PostAsJsonAsync("/api/auth/login", new { username = "member", password = "password123" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using HttpResponseMessage list = await member.GetAsync(new Uri("/api/users", UriKind.Relative), TestContext.Current.CancellationToken);
        using HttpResponseMessage settings = await member.GetAsync(new Uri("/api/settings", UriKind.Relative), TestContext.Current.CancellationToken);
        using HttpResponseMessage create = await member.PostAsJsonAsync("/api/users", new { username = "other", password = "password123" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task ApiKeysAreNeverReturned()
    {
        using HttpClient client = factory.CreateClient();
        await SetupAsync(client);
        using HttpResponseMessage saved = await client.PutAsJsonAsync("/api/settings",
            new { modularrUrl = "http://modularr:8080", modularrApiKey = "super-secret-key", requestsNeedApproval = false, autoFixNotIndexed = false },
            TestContext.Current.CancellationToken);
        string body = await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain("super-secret-key", body, StringComparison.Ordinal);
        Assert.Contains("\"modularrKeySet\":true", body, StringComparison.Ordinal);
        // Encrypted at rest: neither the database nor its WAL holds the key in clear.
        foreach (string file in Directory.GetFiles(configDirectory, "linkarr.db*"))
        {
            Assert.DoesNotContain("super-secret-key", ReadShared(file), StringComparison.Ordinal);
        }
    }

    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, System.Text.Encoding.Latin1);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task WebhookNeedsItsToken()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(new Uri("/api/hooks/modularr?token=nope", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void RateLimitBucketIsTheAddressOrTheIpv6Slash64()
    {
        Assert.Equal("192.0.2.7", AuthEndpoints.ClientKey(IPAddress.Parse("192.0.2.7")));
        Assert.Equal("192.0.2.7", AuthEndpoints.ClientKey(IPAddress.Parse("::ffff:192.0.2.7")));
        Assert.Equal(AuthEndpoints.ClientKey(IPAddress.Parse("2001:db8:1:2::1")), AuthEndpoints.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(AuthEndpoints.ClientKey(IPAddress.Parse("2001:db8:1:2::1")), AuthEndpoints.ClientKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void WebhookTokenComparison()
    {
        Assert.True(CatalogEndpoints.TokenMatches("abc123", "abc123"));
        Assert.False(CatalogEndpoints.TokenMatches("abc123", "abc124"));
        Assert.False(CatalogEndpoints.TokenMatches("abc123", null));
        Assert.False(CatalogEndpoints.TokenMatches("", ""));
    }

    private static async Task SetupAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
