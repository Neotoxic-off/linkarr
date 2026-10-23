using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Linkarr.Tests;

// Roles and groups through the real API (no app configured, background sync off), and the migration of per-user rights.
public sealed class RolesTests : IDisposable
{
    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), "linkarr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> factory;

    public RolesTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("LINKARR_CONFIG_DIR", configDirectory)
            .UseSetting("LINKARR_DISABLE_WORKER", "true"));
    }

    public void Dispose()
    {
        factory.Dispose();
        SqliteConnection.ClearAllPools();
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

    private async Task<HttpClient> AdminAsync()
    {
        HttpClient client = factory.CreateClient();
        using HttpResponseMessage setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "owner", password = "password123" }, Ct);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        return client;
    }

    private static async Task<JsonNode> OkAsync(Task<HttpResponseMessage> call)
    {
        using HttpResponseMessage response = await call;
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task BadAsync(Task<HttpResponseMessage> call, string because)
    {
        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(because, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static async Task<int> CreateUserAsync(HttpClient admin, string username, object? roleId = null, int[]? groupIds = null) =>
        (await OkAsync(admin.PostAsJsonAsync("/api/users", new { username, password = "password123", roleId, groupIds }, Ct)))["id"]!.GetValue<int>();

    private static async Task<JsonNode> UserAsync(HttpClient admin, int id) =>
        (await OkAsync(admin.GetAsync(new Uri("/api/users", UriKind.Relative), Ct))).AsArray().First(u => u!["id"]!.GetValue<int>() == id)!;

    [Fact]
    public async Task OnlyAdminsManageRolesAndGroups()
    {
        using HttpClient admin = await AdminAsync();
        await CreateUserAsync(admin, "member");
        using HttpClient member = factory.CreateClient();
        await OkAsync(member.PostAsJsonAsync("/api/auth/login", new { username = "member", password = "password123" }, Ct));

        using HttpResponseMessage roles = await member.GetAsync(new Uri("/api/roles", UriKind.Relative), Ct);
        using HttpResponseMessage groups = await member.GetAsync(new Uri("/api/groups", UriKind.Relative), Ct);
        using HttpResponseMessage newRole = await member.PostAsJsonAsync("/api/roles", new { name = "Mine", rights = new { admin = true } }, Ct);
        using HttpResponseMessage newGroup = await member.PostAsJsonAsync("/api/groups", new { name = "Mine", allLibraries = true }, Ct);
        using HttpResponseMessage editAdmin = await member.PutAsJsonAsync("/api/roles/2", new { name = "Member", rights = new { admin = true } }, Ct);

        Assert.All(new[] { roles, groups, newRole, newGroup, editAdmin }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal("Member", (await OkAsync(admin.GetAsync(new Uri("/api/roles", UriKind.Relative), Ct))).AsArray()[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task BuiltInRolesStayAndUsedRolesCannotBeDeleted()
    {
        using HttpClient admin = await AdminAsync();
        JsonArray roles = (await OkAsync(admin.GetAsync(new Uri("/api/roles", UriKind.Relative), Ct))).AsArray();
        Assert.Equal(["Admin", "Member", "Kid", "Guest"], roles.Select(r => r!["name"]!.GetValue<string>()));

        await BadAsync(admin.PutAsJsonAsync("/api/roles/1", new { name = "Admin", rights = new { admin = false } }, Ct), "cannot be changed");
        await BadAsync(admin.DeleteAsync(new Uri("/api/roles/3", UriKind.Relative), Ct), "built-in");

        // Member is a built-in that can be edited.
        JsonNode member = await OkAsync(admin.PutAsJsonAsync("/api/roles/2", new { name = "Member", rights = new { watch = true, request = true, transcoding = false } }, Ct));
        Assert.False(member["rights"]!["transcoding"]!.GetValue<bool>());

        int custom = (await OkAsync(admin.PostAsJsonAsync("/api/roles", new { name = "Friends", rights = new { watch = true } }, Ct)))["id"]!.GetValue<int>();
        await BadAsync(admin.PostAsJsonAsync("/api/roles", new { name = "friends", rights = new { } }, Ct), "already taken");
        int bob = await CreateUserAsync(admin, "bob", custom);
        await BadAsync(admin.DeleteAsync(new Uri($"/api/roles/{custom}", UriKind.Relative), Ct), "still has users");
        await OkAsync(admin.PatchAsJsonAsync($"/api/users/{bob}", new { roleId = Role.MemberId }, Ct));
        using HttpResponseMessage deleted = await admin.DeleteAsync(new Uri($"/api/roles/{custom}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task TheLastAdminsKeepTheirAdminRight()
    {
        using HttpClient admin = await AdminAsync();
        JsonNode owner = (await OkAsync(admin.GetAsync(new Uri("/api/users", UriKind.Relative), Ct))).AsArray()[0]!;
        int ownerId = owner["id"]!.GetValue<int>();

        // Not through the user: the owner is the last admin.
        await BadAsync(admin.PatchAsJsonAsync($"/api/users/{ownerId}", new { roleId = Role.MemberId }, Ct), "own admin right");
        await BadAsync(admin.PatchAsJsonAsync($"/api/users/{ownerId}", new { disabled = true }, Ct), "own account");

        // Nor through a custom admin role holding the only enabled admin.
        int boss = (await OkAsync(admin.PostAsJsonAsync("/api/roles", new { name = "Boss", rights = new { admin = true } }, Ct)))["id"]!.GetValue<int>();
        await OkAsync(admin.PatchAsJsonAsync($"/api/users/{ownerId}", new { roleId = boss }, Ct));
        await BadAsync(admin.PutAsJsonAsync($"/api/roles/{boss}", new { name = "Boss", rights = new { admin = false } }, Ct), "last enabled admins");

        // A second admin elsewhere: now the role can lose it, and the other admin's last-admin check guards the rest.
        int second = await CreateUserAsync(admin, "second", Role.AdminId);
        await OkAsync(admin.PatchAsJsonAsync($"/api/users/{ownerId}", new { roleId = Role.AdminId }, Ct));
        await OkAsync(admin.PatchAsJsonAsync($"/api/users/{second}", new { disabled = true }, Ct));
        await BadAsync(admin.DeleteAsync(new Uri($"/api/users/{ownerId}", UriKind.Relative), Ct), "own account");
        Assert.True((await UserAsync(admin, ownerId))["rights"]!["admin"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GroupsGiveLibrariesButNeverARole()
    {
        using HttpClient admin = await AdminAsync();
        await BadAsync(admin.PostAsJsonAsync("/api/groups", new { name = "Family", defaultRoleId = Role.AdminId }, Ct), "cannot be an admin role");

        JsonNode family = await OkAsync(admin.PostAsJsonAsync("/api/groups", new { name = "Family", libraries = (string[])["films", "shows"], defaultRoleId = Role.MemberId }, Ct));
        int familyId = family["id"]!.GetValue<int>();
        int kidsId = (await OkAsync(admin.PostAsJsonAsync("/api/groups", new { name = "Kids", libraries = (string[])["cartoons", "films"], defaultRoleId = Role.KidId }, Ct)))["id"]!.GetValue<int>();

        // No role picked: the first group's default role.
        int tim = await CreateUserAsync(admin, "tim", groupIds: [kidsId]);
        int guest = await CreateUserAsync(admin, "guest", Role.GuestId);
        Assert.Equal("Kid", (await UserAsync(admin, tim))["role"]!["name"]!.GetValue<string>());

        // Joining a group whose default role is Member changes neither Kid nor Guest: only the libraries add up.
        await OkAsync(admin.PutAsJsonAsync($"/api/groups/{familyId}", new { name = "Family", libraries = (string[])["films", "shows"], defaultRoleId = Role.MemberId, members = (int[])[tim, guest] }, Ct));
        await OkAsync(admin.PatchAsJsonAsync($"/api/users/{guest}", new { groupIds = (int[])[kidsId, familyId] }, Ct));
        JsonNode timNow = await UserAsync(admin, tim);
        JsonNode guestNow = await UserAsync(admin, guest);
        Assert.Equal("Kid", timNow["role"]!["name"]!.GetValue<string>());
        Assert.Equal(10, timNow["rights"]!["maxParentalRating"]!.GetValue<int>());
        Assert.False(timNow["rights"]!["request"]!.GetValue<bool>());
        Assert.Equal("Guest", guestNow["role"]!["name"]!.GetValue<string>());
        Assert.False(guestNow["rights"]!["allLibraries"]!.GetValue<bool>());
        Assert.False(guestNow["rights"]!["canDownload"]!.GetValue<bool>());
        Assert.False(guestNow["rights"]!["request"]!.GetValue<bool>());
        Assert.Equal(["cartoons", "films", "shows"], guestNow["rights"]!["libraries"]!.AsArray().Select(l => l!.GetValue<string>()).Order());
        Assert.Equal(["Family", "Kids"], timNow["groups"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()));

        // A role some group hands out cannot become an admin role.
        await BadAsync(admin.PutAsJsonAsync($"/api/roles/{Role.KidId}", new { name = "Kid", rights = new { admin = true } }, Ct), "default role");

        // Deleting a group takes its libraries away from its members.
        using HttpResponseMessage deleted = await admin.DeleteAsync(new Uri($"/api/groups/{familyId}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(["cartoons", "films"], (await UserAsync(admin, guest))["rights"]!["libraries"]!.AsArray().Select(l => l!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task ChangingARoleReprovisionsItsUsersShortlyAfter()
    {
        using HttpClient admin = await AdminAsync();
        int bob = await CreateUserAsync(admin, "bob", Role.GuestId);
        int ann = await CreateUserAsync(admin, "ann");
        SyncState state = factory.Services.GetRequiredService<SyncState>();
        Assert.Null(state.UsersDue);

        JsonNode guest = await OkAsync(admin.PutAsJsonAsync($"/api/roles/{Role.GuestId}", new { name = "Guest", rights = new { watch = true, allLibraries = false, canDownload = false, remoteAccess = false } }, Ct));

        Assert.Equal([bob], guest["members"]!.AsArray().Select(m => m!.GetValue<int>()));
        Assert.Equal("Pending", (await UserAsync(admin, bob))["syncStatus"]!.GetValue<string>());
        Assert.NotEqual("Pending", (await UserAsync(admin, ann))["syncStatus"]!.GetValue<string>());
        Assert.NotNull(state.UsersDue);
        Assert.False(state.TakeUsersRequest());
    }

    // Admins become Admin; a user with exactly Member's rights Member; anyone else a custom role of its own rights, its library
    // list a group of its own: nobody's effective rights change.
    [Fact]
    public async Task ExistingAccountsKeepTheirRightsThroughTheMigration()
    {
        Directory.CreateDirectory(configDirectory);
        string path = Path.Combine(configDirectory, "migrate.db");
        DbContextOptions<LinkarrDbContext> options = new DbContextOptionsBuilder<LinkarrDbContext>().UseSqlite($"Data Source={path}").Options;
        await using (LinkarrDbContext db = new(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261023194512_AppLinksAndRequestCap", Ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Username, PasswordHash, Disabled, Created, IsAdmin, Watch, AllLibraries, Libraries, Request, ManageDownloads, ModularrAccess, ExternalId, SyncStatus, PasswordPending) VALUES
                ('root', 'h', 0, '2026-01-01', 1, 0, 0, '["x"]', 0, 0, 0, 'e1', 'Synced', 0),
                ('mia', 'h', 0, '2026-01-01', 0, 1, 1, '[]', 1, 0, 0, 'e2', 'Synced', 0),
                ('dl', 'h', 0, '2026-01-01', 0, 0, 1, '[]', 1, 1, 1, 'e3', 'Synced', 0),
                ('lim', 'h', 0, '2026-01-01', 0, 1, 0, '["a","b"]', 1, 0, 0, 'e4', 'Synced', 0);
                """, Ct);
            await db.Database.MigrateAsync(Ct);
        }

        await using (LinkarrDbContext db = new(options))
        {
            Dictionary<string, Account> accounts = await db.Accounts.ToDictionaryAsync(a => a.Username, Ct);
            Assert.Equal(Role.AdminId, accounts["root"].RoleId);
            Assert.True(accounts["root"].Rights.Admin);
            Assert.Equal(Role.MemberId, accounts["mia"].RoleId);
            Assert.Empty(accounts["mia"].Groups);

            Assert.Equal("Custom: dl", accounts["dl"].Role.Name);
            Assert.Equal(new Rights(Watch: false, Request: true, ManageDownloads: true, ModularrAccess: true) with { Libraries = null },
                accounts["dl"].Rights with { Libraries = null });

            Assert.Equal("Custom: lim", accounts["lim"].Role.Name);
            Assert.Equal("Libraries: lim", Assert.Single(accounts["lim"].Groups).Name);
            Rights lim = accounts["lim"].Rights;
            Assert.True(lim is { Watch: true, Request: true, AllLibraries: false, ManageDownloads: false, ModularrAccess: false, CanDownload: true });
            Assert.Equal(["a", "b"], lim.Libraries!);
            Assert.Equal(6, await db.Roles.CountAsync(Ct));
        }
    }
}
