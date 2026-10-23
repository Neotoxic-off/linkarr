using System.Globalization;
using System.Net.Http.Json;

namespace Linkarr.Api.Apps;

// The Sonarr/Radarr v3 shapes modularr answers (only the fields linkarr reads).
public sealed record ArrImage(string CoverType, string? RemoteUrl);

public sealed record ArrStatistics(int EpisodeFileCount);

public sealed record ArrMovie(
    int Id, string Title, int TmdbId, int Year, bool Monitored, bool HasFile, string Path, string? RootFolderPath, int QualityProfileId,
    string? Overview, IReadOnlyList<ArrImage>? Images);

public sealed record ArrSeries(
    int Id, string Title, int TvdbId, int Year, bool Monitored, string Path, string? RootFolderPath, int QualityProfileId,
    string? Overview, ArrStatistics? Statistics, IReadOnlyList<ArrImage>? Images);

public sealed record ArrQueueItem(int Id, string Title, long Size, long Sizeleft, int? MovieId, int? SeriesId);

public sealed record ArrQueuePage(IReadOnlyList<ArrQueueItem> Records);

public sealed record ArrQualityProfile(int Id, string Name);

public sealed record ArrRootFolder(int Id, string Path);

// modularr's provisioning API (contract shared with modularr).
public sealed record ProvisionedUser(int Id, string Username, string Role, string? ExternalId);

// Adopt: only linkarr's explicit adopt action sets it; modularr then links a never-linked local account of that name.
public sealed record ProvisionUserBody(string Username, string Role, string? Password, bool Disabled, bool Adopt = false);

public sealed record ProvisionResult(int Id);

// GET /api/provision/stats: the address downloads leave from, live transfer rates and the media folders' space.
public sealed record ModularrPublicIp(string Ip, string? Country, string? Isp, DateTimeOffset? CheckedAt);

public sealed record ModularrTransfers(int Active, long DownloadBps, long UploadBps);

public sealed record ModularrVolume(string Path, long FreeBytes, long TotalBytes);

public sealed record ModularrStats(ModularrPublicIp? PublicIp, ModularrTransfers Downloads, IReadOnlyList<ModularrVolume> Storage, string? Version);

public static class ArrImages
{
    public static string? Poster(IReadOnlyList<ArrImage>? images) =>
        images?.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl;
}

// modularr, with its API key (X-Api-Key). Every call goes to the configured base URL only.
public sealed class ModularrClient(HttpClient http, Uri baseUrl, string apiKey)
{
    private const string App = "modularr";

    public Task<IReadOnlyList<ArrMovie>> MoviesAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ArrMovie>>("api/v3/movie", ct);

    public Task<IReadOnlyList<ArrSeries>> SeriesAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ArrSeries>>("api/v3/series", ct);

    public async Task<IReadOnlyList<ArrQueueItem>> QueueAsync(CancellationToken ct) =>
        (await GetAsync<ArrQueuePage>("api/v3/queue?page=1&pageSize=1000&includeMovie=true&includeSeries=true", ct)).Records;

    public Task<IReadOnlyList<ArrQualityProfile>> QualityProfilesAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ArrQualityProfile>>("api/v3/qualityprofile", ct);

    public Task<IReadOnlyList<ArrRootFolder>> RootFoldersAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ArrRootFolder>>("api/v3/rootfolder", ct);

    public Task<IReadOnlyList<ArrMovie>> LookupMoviesAsync(string term, CancellationToken ct) =>
        GetAsync<IReadOnlyList<ArrMovie>>("api/v3/movie/lookup?term=" + Uri.EscapeDataString(term), ct);

    public Task<IReadOnlyList<ArrSeries>> LookupSeriesAsync(string term, CancellationToken ct) =>
        GetAsync<IReadOnlyList<ArrSeries>>("api/v3/series/lookup?term=" + Uri.EscapeDataString(term), ct);

    public async Task<ArrMovie> AddMovieAsync(ArrMovie movie, bool search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(movie);
        object body = new
        {
            tmdbId = movie.TmdbId, title = movie.Title, year = movie.Year, qualityProfileId = movie.QualityProfileId, rootFolderPath = movie.RootFolderPath,
            monitored = true, addOptions = new { searchForMovie = search },
        };
        return await AppHttp.ReadAsync<ArrMovie>(await SendAsync(HttpMethod.Post, "api/v3/movie", body, ct), AppHttp.CamelCase, ct);
    }

    public async Task<ArrSeries> AddSeriesAsync(ArrSeries series, bool search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(series);
        object body = new
        {
            tvdbId = series.TvdbId, title = series.Title, year = series.Year, qualityProfileId = series.QualityProfileId, rootFolderPath = series.RootFolderPath,
            monitored = true, addOptions = new { searchForMissingEpisodes = search },
        };
        return await AppHttp.ReadAsync<ArrSeries>(await SendAsync(HttpMethod.Post, "api/v3/series", body, ct), AppHttp.CamelCase, ct);
    }

    public Task<IReadOnlyList<ProvisionedUser>> UsersAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ProvisionedUser>>("api/provision/users", ct);

    public async Task<int> UpsertUserAsync(string externalId, ProvisionUserBody user, CancellationToken ct) =>
        (await AppHttp.ReadAsync<ProvisionResult>(await SendAsync(HttpMethod.Put, "api/provision/users/" + Uri.EscapeDataString(externalId), user, ct), AppHttp.CamelCase, ct)).Id;

    // Already gone counts as deleted.
    public async Task DeleteUserAsync(string externalId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Delete, "api/provision/users/" + Uri.EscapeDataString(externalId), null, ct, notFoundIsOk: true)).Dispose();

    // modularr then links its user pages here and posts its events to {url}/api/hooks/modularr (hookToken in X-Linkarr-Token).
    // hookUrl: linkarr's address as modularr's container reaches it (compose: http://linkarr:8070); the public url when null.
    public async Task SetManagedAsync(string linkarrUrl, string hookToken, string? hookUrl, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Put, "api/provision/managed", new { name = "linkarr", url = linkarrUrl, hookToken, hookUrl }, ct)).Dispose();

    public Task<ModularrStats> StatsAsync(CancellationToken ct) => GetAsync<ModularrStats>("api/provision/stats", ct);

    // For the Test button: a read that needs the key.
    public async Task<string> PingAsync(CancellationToken ct)
    {
        int profiles = (await QualityProfilesAsync(ct)).Count;
        return string.Create(CultureInfo.InvariantCulture, $"modularr, {profiles} quality profiles");
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        await AppHttp.ReadAsync<T>(await SendAsync(HttpMethod.Get, path, null, ct), AppHttp.CamelCase, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsOk = false)
    {
        using HttpRequestMessage request = new(method, new Uri(baseUrl, path));
        request.Headers.Add("X-Api-Key", apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: AppHttp.CamelCase);
        }

        return await AppHttp.SendAsync(http, request, App, notFoundIsOk, ct);
    }
}
