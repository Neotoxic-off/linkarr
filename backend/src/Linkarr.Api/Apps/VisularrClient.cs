using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Linkarr.Api.Apps;

// The Jellyfin shapes visularr answers (only the fields linkarr reads).
public sealed record JellyfinPolicy(bool IsAdministrator, bool IsDisabled);

public sealed record JellyfinUser(string Id, string Name, JellyfinPolicy? Policy);

public sealed record JellyfinLibrary(string ItemId, string Name, string? CollectionType);

public sealed record JellyfinItem(string Id, string Name, string Type, int? ProductionYear, Dictionary<string, string>? ProviderIds, string? Path);

public sealed record JellyfinItems(IReadOnlyList<JellyfinItem> Items);

public sealed record JellyfinInfo(string? ServerName, string? Version);

public sealed record JellyfinDevice(string Id);

public sealed record JellyfinNowPlaying(string? Name, string? SeriesName, string? Type, int? ProductionYear, int? ParentIndexNumber, int? IndexNumber, long? RunTimeTicks);

public sealed record JellyfinPlayState(long? PositionTicks, bool IsPaused);

public sealed record JellyfinSession(
    string Id, string? UserId, string? UserName, string? Client, string? DeviceName, string? ApplicationVersion, string? RemoteEndPoint,
    DateTimeOffset? LastActivityDate, JellyfinNowPlaying? NowPlayingItem, JellyfinPlayState? PlayState);

public sealed record JellyfinDevices(IReadOnlyList<JellyfinDevice> Items);

// visularr (Jellyfin API) with an API key, which acts as an administrator. Every call goes to the configured base URL only.
public sealed class VisularrClient(HttpClient http, Uri baseUrl, string apiKey)
{
    private const string App = "visularr";

    public Task<IReadOnlyList<JellyfinUser>> UsersAsync(CancellationToken ct) => GetAsync<IReadOnlyList<JellyfinUser>>("Users", ct);

    // Created with its password in the same call: a Jellyfin user without one would open to anyone.
    public async Task<string> CreateUserAsync(string name, string password, CancellationToken ct) =>
        (await AppHttp.ReadAsync<JellyfinUser>(await SendAsync(HttpMethod.Post, "Users/New", new { Name = name, Password = password }, ct), AppHttp.PascalCase, ct)).Id;

    // The whole user as Jellyfin has it, so the fields linkarr does not manage are sent back unchanged. Null when the user is gone.
    public async Task<JsonObject?> UserAsync(string userId, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "Users/" + Uri.EscapeDataString(userId), null, ct, notFoundIsOk: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return (await response.Content.ReadFromJsonAsync<JsonNode>(ct))?.AsObject() ?? throw new HttpRequestException("visularr sent an empty user.");
    }

    public static JsonObject Policy(JsonObject user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user["Policy"]?.AsObject() is JsonObject policy ? (JsonObject)policy.DeepClone() : throw new HttpRequestException("visularr sent a user without a policy.");
    }

    // Jellyfin renames through the user object (POST /Users/{id}), sent back whole with the new name.
    public async Task RenameUserAsync(string userId, JsonObject user, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        JsonObject renamed = (JsonObject)user.DeepClone();
        renamed["Name"] = name;
        (await SendAsync(HttpMethod.Post, "Users/" + Uri.EscapeDataString(userId), renamed, ct)).Dispose();
    }

    // Jellyfin's own sign-out: every device signed in as the user loses its access token.
    public async Task SignOutUserAsync(string userId, CancellationToken ct)
    {
        foreach (JellyfinDevice device in (await GetAsync<JellyfinDevices>("Devices?userId=" + Uri.EscapeDataString(userId), ct)).Items)
        {
            (await SendAsync(HttpMethod.Delete, "Devices?id=" + Uri.EscapeDataString(device.Id), null, ct, notFoundIsOk: true)).Dispose();
        }
    }

    public async Task SetPolicyAsync(string userId, JsonObject policy, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, $"Users/{Uri.EscapeDataString(userId)}/Policy", policy, ct)).Dispose();

    // As an administrator (the API key), Jellyfin sets a new password without the current one.
    public async Task SetPasswordAsync(string userId, string password, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "Users/Password?userId=" + Uri.EscapeDataString(userId), new { NewPw = password }, ct)).Dispose();

    public async Task DeleteUserAsync(string userId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Delete, "Users/" + Uri.EscapeDataString(userId), null, ct, notFoundIsOk: true)).Dispose();

    public Task<IReadOnlyList<JellyfinLibrary>> LibrariesAsync(CancellationToken ct) => GetAsync<IReadOnlyList<JellyfinLibrary>>("Library/VirtualFolders", ct);

    public async Task<IReadOnlyList<JellyfinItem>> ItemsAsync(CancellationToken ct) =>
        (await GetAsync<JellyfinItems>("Items?Recursive=true&IncludeItemTypes=Movie,Series&Fields=ProviderIds,Path&EnableImages=false&EnableUserData=false", ct)).Items;

    // A targeted scan of one folder or file, as modularr's import notification sends it.
    public async Task RefreshPathAsync(string path, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "Library/Media/Updated", new { Updates = new[] { new { Path = path, UpdateType = "Created" } } }, ct)).Dispose();

    public async Task SetManagedAsync(string linkarrUrl, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "Visularr/Managed", new { name = "linkarr", url = linkarrUrl }, ct)).Dispose();

    public async Task<string> PingAsync(CancellationToken ct)
    {
        JellyfinInfo info = await InfoAsync(ct);
        return $"{info.ServerName ?? "visularr"} {info.Version}".Trim();
    }

    public Task<JellyfinInfo> InfoAsync(CancellationToken ct) => GetAsync<JellyfinInfo>("System/Info", ct);

    // Signed-in devices active in the last 16 minutes (Jellyfin's own "active" window), with what each is playing.
    public Task<IReadOnlyList<JellyfinSession>> SessionsAsync(CancellationToken ct) => GetAsync<IReadOnlyList<JellyfinSession>>("Sessions?activeWithinSeconds=960", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        await AppHttp.ReadAsync<T>(await SendAsync(HttpMethod.Get, path, null, ct), AppHttp.PascalCase, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsOk = false)
    {
        using HttpRequestMessage request = new(method, new Uri(baseUrl, path));
        request.Headers.Add("X-Emby-Token", apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: AppHttp.PascalCase);
        }

        return await AppHttp.SendAsync(http, request, App, notFoundIsOk, ct);
    }
}
