using System.Text.Json.Nodes;
using Linkarr.Api.Data;
using Linkarr.Api.Users;

namespace Linkarr.Tests;

public sealed class RightsMappingTests
{
    private static Account With(Rights rights, bool disabled = false) =>
        new() { Username = "bob", PasswordHash = "x", Role = new Role { Name = "test", Rights = rights }, Disabled = disabled };

    [Fact]
    public void AdminGetsEverythingInBothApps()
    {
        Account admin = With(new Rights(Admin: true, Watch: false, AllLibraries: false, Libraries: ["lib1"], ModularrAccess: false));
        JsonObject policy = RightsMapping.ApplyPolicy(new JsonObject { ["AuthenticationProviderId"] = "keep" }, admin);

        Assert.True(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.True(policy["EnableMediaPlayback"]!.GetValue<bool>());
        Assert.True(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Empty(policy["EnabledFolders"]!.AsArray());
        Assert.Equal("keep", policy["AuthenticationProviderId"]!.GetValue<string>());
        Assert.True(RightsMapping.HasVisularrAccount(admin));
        Assert.True(RightsMapping.HasModularrAccount(admin));
        Assert.Equal("Admin", RightsMapping.ModularrRole(admin));
    }

    [Fact]
    public void WatcherIsLimitedToTheChosenLibraries()
    {
        Account watcher = With(new Rights(Watch: true, AllLibraries: false, Libraries: ["a", "b", "a", " "]));
        JsonObject policy = RightsMapping.ApplyPolicy([], watcher);

        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(policy["IsDisabled"]!.GetValue<bool>());
        Assert.True(policy["EnableMediaPlayback"]!.GetValue<bool>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Equal(["a", "b"], policy["EnabledFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(RightsMapping.HasModularrAccount(watcher));
    }

    [Fact]
    public void ManageDownloadsMakesAModularrAdmin()
    {
        Assert.Equal("Admin", RightsMapping.ModularrRole(With(new Rights(ManageDownloads: true, ModularrAccess: true))));
        Assert.Equal("User", RightsMapping.ModularrRole(With(new Rights(ModularrAccess: true))));
    }

    [Fact]
    public void NoWatchRightMeansNoVisularrAccount()
    {
        Assert.False(RightsMapping.HasVisularrAccount(With(new Rights(Watch: false))));
    }

    [Fact]
    public void RoleLimitsBecomeJellyfinPolicy()
    {
        Account kid = With(new Rights(Request: false, MaxParentalRating: 10, BlockUnrated: true, CanDownload: false, RemoteAccess: false,
            MaxActiveSessions: 2, Transcoding: false, BitrateLimit: 8_000_000));
        JsonObject policy = RightsMapping.ApplyPolicy([], kid);

        Assert.Equal(10, policy["MaxParentalRating"]!.GetValue<int>());
        Assert.Contains("Movie", policy["BlockUnratedItems"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(policy["EnableContentDownloading"]!.GetValue<bool>());
        Assert.False(policy["EnableSyncTranscoding"]!.GetValue<bool>());
        Assert.False(policy["EnableRemoteAccess"]!.GetValue<bool>());
        Assert.Equal(2, policy["MaxActiveSessions"]!.GetValue<int>());
        Assert.False(policy["EnableVideoPlaybackTranscoding"]!.GetValue<bool>());
        Assert.False(policy["EnableAudioPlaybackTranscoding"]!.GetValue<bool>());
        Assert.False(policy["EnablePlaybackRemuxing"]!.GetValue<bool>());
        Assert.Equal(8_000_000, policy["RemoteClientBitrateLimit"]!.GetValue<int>());

        // No limit: Jellyfin's defaults.
        JsonObject open = RightsMapping.ApplyPolicy([], With(new Rights()));
        Assert.Null(open["MaxParentalRating"]);
        Assert.Empty(open["BlockUnratedItems"]!.AsArray());
        Assert.True(open["EnableContentDownloading"]!.GetValue<bool>());
        Assert.Equal(0, open["RemoteClientBitrateLimit"]!.GetValue<int>());
    }

    // Groups add their libraries to the role's and nothing else; one group with all libraries gives all.
    [Fact]
    public void GroupsOnlyAddLibraries()
    {
        Account guest = With(new Rights(AllLibraries: false, Libraries: ["own"], CanDownload: false, Request: false));
        guest.Groups.Add(new Group { Name = "family", Libraries = ["a", "b"] });
        guest.Groups.Add(new Group { Name = "friends", Libraries = ["b", "c"] });

        Rights rights = guest.Rights;
        JsonObject policy = RightsMapping.ApplyPolicy([], guest);

        Assert.Equal(["own", "a", "b", "c"], policy["EnabledFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(rights.CanDownload || rights.Request || rights.Admin);

        guest.Groups.Add(new Group { Name = "everything", AllLibraries = true });
        Assert.True(RightsMapping.ApplyPolicy([], guest)["EnableAllFolders"]!.GetValue<bool>());
    }

    // Jellyfin refuses to disable an administrator: a disabled account is never one.
    [Fact]
    public void DisabledAdminIsNotAJellyfinAdministrator()
    {
        JsonObject policy = RightsMapping.ApplyPolicy([], With(new Rights(Admin: true), disabled: true));

        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.True(policy["IsDisabled"]!.GetValue<bool>());
    }
}
