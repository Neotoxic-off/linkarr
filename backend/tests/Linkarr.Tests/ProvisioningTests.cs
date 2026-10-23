using System.Net;
using System.Text.Json.Nodes;
using Linkarr.Api.Data;
using Linkarr.Api.Users;

namespace Linkarr.Tests;

public sealed class ProvisioningTests
{
    private const string Password = "correct horse battery";

    private static FakeApp Visularr(bool admin = false) => new((method, path, _) => (method, path) switch
    {
        ("GET", "/Users") => (HttpStatusCode.OK, "[]"),
        ("POST", "/Users/New") => (HttpStatusCode.OK, """{"Id":"j1","Name":"bob"}"""),
        ("GET", "/Users/j1") => (HttpStatusCode.OK,
            $$$"""{"Id":"j1","Name":"bob","Policy":{"IsAdministrator":{{{(admin ? "true" : "false")}}},"IsDisabled":false,"AuthenticationProviderId":"Default"}}"""),
        ("POST", "/Users/j1/Policy") or ("POST", "/Users/Password?userId=j1") or ("DELETE", "/Users/j1") or ("POST", "/Users/j1") => (HttpStatusCode.NoContent, ""),
        _ => (HttpStatusCode.NotFound, ""),
    });

    private static FakeApp Modularr() => new((method, path, _) => method switch
    {
        "PUT" when path.StartsWith("/api/provision/users/", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"id":7}"""),
        "DELETE" => (HttpStatusCode.NoContent, ""),
        _ => (HttpStatusCode.NotFound, ""),
    });

    private static Account Bob(Rights rights) => new() { Username = "bob", PasswordHash = "hash", Role = new Role { Name = "test", Rights = rights } };

    [Fact]
    public async Task NewUserIsCreatedInBothAppsWithItsPasswordAndRights()
    {
        FakeApp visularr = Visularr();
        FakeApp modularr = Modularr();
        Account bob = Bob(new Rights(Watch: true, AllLibraries: false, Libraries: ["lib1"], ModularrAccess: true, ManageDownloads: true));

        await UserProvisioner.SyncAsync(bob, Password, modularr.Modularr(), visularr.Visularr(), CancellationToken.None);

        Assert.Equal(SyncStatus.Synced, bob.SyncStatus);
        Assert.Null(bob.SyncError);
        Assert.False(bob.PasswordPending);
        Assert.Equal("j1", bob.VisularrId);
        Assert.Equal(7, bob.ModularrId);

        // Created with the password in the same call, never passwordless; not sent a second time.
        (string _, string _, string createBody, string? key) = Assert.Single(visularr.Requests, r => r.Path == "/Users/New");
        Assert.Equal("visularr-key", key);
        Assert.Equal(Password, JsonNode.Parse(createBody)!["Password"]!.GetValue<string>());
        Assert.DoesNotContain(visularr.Requests, r => r.Path.StartsWith("/Users/Password", StringComparison.Ordinal));

        JsonObject policy = JsonNode.Parse(Assert.Single(visularr.Requests, r => r.Path == "/Users/j1/Policy").Body)!.AsObject();
        Assert.Equal("Default", policy["AuthenticationProviderId"]!.GetValue<string>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Equal("lib1", policy["EnabledFolders"]![0]!.GetValue<string>());

        JsonObject upsert = JsonNode.Parse(Assert.Single(modularr.Requests).Body)!.AsObject();
        Assert.Equal($"/api/provision/users/{bob.ExternalId}", Assert.Single(modularr.Requests).Path);
        Assert.Equal("modularr-key", Assert.Single(modularr.Requests).ApiKey);
        Assert.Equal("Admin", upsert["role"]!.GetValue<string>());
        Assert.Equal(Password, upsert["password"]!.GetValue<string>());
        Assert.False(upsert["disabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OneAppDownMarksOutOfSyncAndKeepsTheOtherInSync()
    {
        FakeApp modularr = Modularr();
        Account bob = Bob(new Rights(Watch: true, ModularrAccess: true));

        await UserProvisioner.SyncAsync(bob, Password, modularr.Modularr(), FakeApp.Down().Visularr(), CancellationToken.None);

        Assert.Equal(SyncStatus.OutOfSync, bob.SyncStatus);
        Assert.StartsWith("visularr:", bob.SyncError, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, bob.SyncError, StringComparison.Ordinal);
        Assert.DoesNotContain("visularr-key", bob.SyncError, StringComparison.Ordinal);
        // visularr never got this password: it must be entered again.
        Assert.True(bob.PasswordPending);
        Assert.Null(bob.VisularrId);
        Assert.Equal(7, bob.ModularrId);
    }

    [Fact]
    public async Task ReconcileWithoutPasswordRetriesTheOtherFieldsButKeepsThePasswordPending()
    {
        Account bob = Bob(new Rights(Watch: true));
        await UserProvisioner.SyncAsync(bob, Password, null, FakeApp.Down().Visularr(), CancellationToken.None);
        FakeApp visularr = Visularr();

        await UserProvisioner.SyncAsync(bob, password: null, null, visularr.Visularr(), CancellationToken.None);

        Assert.Equal(SyncStatus.Synced, bob.SyncStatus);
        Assert.Equal("j1", bob.VisularrId);
        Assert.True(bob.PasswordPending);
        // Created with a random password, not the old one nor none.
        string created = JsonNode.Parse(Assert.Single(visularr.Requests, r => r.Path == "/Users/New").Body)!["Password"]!.GetValue<string>();
        Assert.NotEqual(Password, created);
        Assert.True(created.Length >= 32);
    }

    [Fact]
    public async Task PasswordChangeOfALinkedUserIsSetByAdminReset()
    {
        FakeApp visularr = Visularr();
        Account bob = Bob(new Rights(Watch: true));
        bob.VisularrId = "j1";

        await UserProvisioner.SyncAsync(bob, "a brand new password", null, visularr.Visularr(), CancellationToken.None);

        JsonNode body = JsonNode.Parse(Assert.Single(visularr.Requests, r => r.Path == "/Users/Password?userId=j1").Body)!;
        Assert.Equal("a brand new password", body["NewPw"]!.GetValue<string>());
        Assert.False(bob.PasswordPending);
    }

    [Fact]
    public async Task LosingRightsDeletesTheAppAccounts()
    {
        FakeApp visularr = Visularr();
        FakeApp modularr = Modularr();
        Account bob = Bob(new Rights(Watch: false, ModularrAccess: false));
        bob.VisularrId = "j1";
        bob.ModularrId = 7;

        await UserProvisioner.SyncAsync(bob, null, modularr.Modularr(), visularr.Visularr(), CancellationToken.None);

        Assert.Contains(visularr.Requests, r => r is { Method: "DELETE", Path: "/Users/j1" });
        Assert.Contains(modularr.Requests, r => r.Method == "DELETE" && r.Path == $"/api/provision/users/{bob.ExternalId}");
        Assert.Null(bob.VisularrId);
        Assert.Null(bob.ModularrId);
        Assert.Equal(SyncStatus.Synced, bob.SyncStatus);
    }

    // Jellyfin refuses to disable an administrator, so the admin is demoted first.
    [Fact]
    public async Task DisablingAnAdminDemotesBeforeDisabling()
    {
        FakeApp visularr = Visularr(admin: true);
        Account bob = Bob(new Rights(Admin: true));
        bob.VisularrId = "j1";
        bob.Disabled = true;

        await UserProvisioner.SyncAsync(bob, null, null, visularr.Visularr(), CancellationToken.None);

        List<JsonObject> posts = [.. visularr.Requests.Where(r => r.Path == "/Users/j1/Policy").Select(r => JsonNode.Parse(r.Body)!.AsObject())];
        Assert.Equal(2, posts.Count);
        Assert.False(posts[0]["IsAdministrator"]!.GetValue<bool>());
        Assert.False(posts[0]["IsDisabled"]!.GetValue<bool>());
        Assert.True(posts[1]["IsDisabled"]!.GetValue<bool>());
    }

    // A rename in linkarr: visularr gets its user object back with the new name, modularr the new username for the same externalId.
    [Fact]
    public async Task RenamedUserIsRenamedInBothApps()
    {
        FakeApp visularr = Visularr();
        FakeApp modularr = Modularr();
        Account robert = Bob(new Rights(Watch: true, ModularrAccess: true));
        robert.Username = "robert";
        robert.VisularrId = "j1";
        robert.ModularrId = 7;

        await UserProvisioner.SyncAsync(robert, null, modularr.Modularr(), visularr.Visularr(), CancellationToken.None);

        JsonObject renamed = JsonNode.Parse(Assert.Single(visularr.Requests, r => r is { Method: "POST", Path: "/Users/j1" }).Body)!.AsObject();
        Assert.Equal("robert", renamed["Name"]!.GetValue<string>());
        Assert.Equal("j1", renamed["Id"]!.GetValue<string>());
        Assert.Equal("robert", JsonNode.Parse(Assert.Single(modularr.Requests).Body)!["username"]!.GetValue<string>());
        Assert.Equal(SyncStatus.Synced, robert.SyncStatus);
    }

    // visularr lets users rename themselves (before visularr's own fix): a name never links, only an admin's explicit adopt does.
    [Fact]
    public async Task AVisularrUserOfTheSameNameIsNeverTakenOver()
    {
        FakeApp visularr = new((method, path, _) => (method, path) switch
        {
            ("GET", "/Users") => (HttpStatusCode.OK, """[{"Id":"mallory","Name":"BOB","Policy":{"IsAdministrator":false,"IsDisabled":false}}]"""),
            _ => (HttpStatusCode.NoContent, ""),
        });
        Account bob = Bob(new Rights(Admin: true));

        await UserProvisioner.SyncAsync(bob, Password, null, visularr.Visularr(), CancellationToken.None);

        Assert.Null(bob.VisularrId);
        Assert.Equal(SyncStatus.OutOfSync, bob.SyncStatus);
        Assert.Contains("a visularr user named bob already exists: adopt or delete it in Unmanaged users", bob.SyncError, StringComparison.Ordinal);
        // Nothing was sent to that user: no policy (admin), no password, no new user.
        Assert.Equal(["GET /Users"], visularr.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    // modularr committed the upsert but its answer was lost (timeout): no id here, yet losing the right still deletes the user.
    [Fact]
    public async Task ALostUpsertAnswerIsStillDeprovisioned()
    {
        Account bob = Bob(new Rights(Watch: false, ModularrAccess: true));
        bool committed = false;
        FakeApp modularr = new((method, path, _) =>
        {
            switch (method)
            {
                case "PUT":
                    committed = true;
                    throw new TaskCanceledException("The request timed out.");
                case "GET" when path == "/api/provision/users":
                    return (HttpStatusCode.OK, committed ? $$"""[{"id":7,"username":"bob","role":"User","externalId":"{{bob.ExternalId}}"}]""" : "[]");
                case "DELETE":
                    committed = false;
                    return (HttpStatusCode.NoContent, "");
                default:
                    return (HttpStatusCode.NotFound, "");
            }
        });

        await UserProvisioner.SyncAsync(bob, Password, modularr.Modularr(), null, CancellationToken.None);
        Assert.Null(bob.ModularrId);
        Assert.Equal(SyncStatus.OutOfSync, bob.SyncStatus);

        bob.Role.ModularrAccess = false;
        await UserProvisioner.SyncAsync(bob, null, modularr.Modularr(), null, CancellationToken.None);

        Assert.Contains(modularr.Requests, r => r.Method == "DELETE" && r.Path == $"/api/provision/users/{bob.ExternalId}");
        Assert.False(committed);
        Assert.Null(bob.ModularrId);
        Assert.Equal(SyncStatus.Synced, bob.SyncStatus);
    }

    [Fact]
    public async Task RemoveAlwaysDeletesFromModularrByExternalId()
    {
        FakeApp modularr = Modularr();
        Account bob = Bob(new Rights(ModularrAccess: true));

        Assert.Null(await UserProvisioner.RemoveAsync(bob, modularr.Modularr(), null, CancellationToken.None));

        Assert.Equal([$"DELETE /api/provision/users/{bob.ExternalId}"], modularr.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task RemoveReportsTheAppThatIsDown()
    {
        Account bob = Bob(new Rights());
        bob.VisularrId = "j1";

        string? error = await UserProvisioner.RemoveAsync(bob, null, FakeApp.Down().Visularr(), CancellationToken.None);

        Assert.NotNull(error);
        Assert.Equal("j1", bob.VisularrId);
    }
}
