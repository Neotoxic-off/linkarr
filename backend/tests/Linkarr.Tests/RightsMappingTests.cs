using System.Text.Json.Nodes;
using Linkarr.Api.Data;
using Linkarr.Api.Users;

namespace Linkarr.Tests;

public sealed class RightsMappingTests
{
    private static Account With(Rights rights, bool disabled = false) =>
        new() { Username = "bob", PasswordHash = "x", Rights = rights, Disabled = disabled };

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

    // Jellyfin refuses to disable an administrator: a disabled account is never one.
    [Fact]
    public void DisabledAdminIsNotAJellyfinAdministrator()
    {
        JsonObject policy = RightsMapping.ApplyPolicy([], With(new Rights(Admin: true), disabled: true));

        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.True(policy["IsDisabled"]!.GetValue<bool>());
    }
}
