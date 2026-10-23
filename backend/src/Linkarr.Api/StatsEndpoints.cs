using System.Diagnostics;
using System.Security.Claims;
using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Requests;
using Linkarr.Api.Sync;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api;

// The admin's overview: catalog counts and trends, requests, both apps' health, downloads, storage, the download IP, and who is
// signed in or watching. IPs and devices are admin-only here; members see their own sessions under /api/auth/sessions.
public static class StatsEndpoints
{
    public const int RecentCount = 8;
    public const int TopRequesters = 5;

    public sealed record Requester(string Username, int Count);

    public sealed record ServiceHealth(string App, bool Configured, bool Reachable, string? Version, long? LatencyMs, string? Error);

    public static RouteGroupBuilder MapStatsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/stats", StatsAsync).RequireAuthorization(AuthEndpoints.AdminPolicy);
        return api;
    }

    private static async Task<object> StatsAsync(HttpContext context, LinkarrDbContext db, Connections connections, CancellationToken ct)
    {
        AppSettings settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        ModularrClient? modularr = await connections.ModularrAsync(ct);
        VisularrClient? visularr = await connections.VisularrAsync(ct);

        // Both apps at once: the page waits for the slower one, not for the sum.
        Task<(ModularrStats? Stats, ServiceHealth Health)> modularrProbe = ProbeAsync<ModularrStats>(UserSync.Modularr, modularr is null ? null : modularr.StatsAsync, s => s.Version, ct);
        Task<(JellyfinInfo? Info, ServiceHealth Health)> visularrProbe = ProbeAsync<JellyfinInfo>(UserSync.Visularr, visularr is null ? null : visularr.InfoAsync, i => i.Version, ct);
        Task<IReadOnlyList<ArrQueueItem>> queue = modularr is null ? Task.FromResult<IReadOnlyList<ArrQueueItem>>([]) : OrEmptyAsync(modularr.QueueAsync, ct);
        Task<IReadOnlyList<JellyfinSession>> watching = visularr is null ? Task.FromResult<IReadOnlyList<JellyfinSession>>([]) : OrEmptyAsync(visularr.SessionsAsync, ct);

        Dictionary<CatalogState, int> byState = await db.Catalog.GroupBy(c => c.State).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        Dictionary<TitleKind, int> byKind = await db.Catalog.GroupBy(c => c.Kind).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        List<CatalogSnapshot> trend = await db.Snapshots.AsNoTracking().OrderBy(s => s.Date).ToListAsync(ct);
        List<CatalogItem> imported = await db.Catalog.AsNoTracking().Where(c => c.AvailableSince != null).OrderByDescending(c => c.AvailableSince).Take(RecentCount).ToListAsync(ct);
        List<TitleRequest> requested = await db.Requests.AsNoTracking().OrderByDescending(r => r.Created).Take(RecentCount).ToListAsync(ct);
        List<string> requestedKeys = [.. requested.Select(RequestEndpoints.CatalogKey).OfType<string>()];
        Dictionary<string, CatalogItem> requestedItems = await db.Catalog.AsNoTracking().Where(c => requestedKeys.Contains(c.Key)).ToDictionaryAsync(c => c.Key, ct);
        List<Requester> requesters = [.. (await db.Requests.GroupBy(r => r.Username).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).ThenBy(g => g.Key).Take(TopRequesters).ToListAsync(ct)).Select(g => new Requester(g.Key, g.Count))];
        List<Account> accounts = await db.Accounts.AsNoTracking().OrderByDescending(a => a.LastSignIn).ThenBy(a => a.Username).ToListAsync(ct);
        ILookup<int, Session> sessions = (await db.Sessions.AsNoTracking().OrderByDescending(s => s.LastSeen).ToListAsync(ct)).ToLookup(s => s.AccountId);
        string? current = context.User.FindFirstValue(AuthEndpoints.SessionClaim);

        (ModularrStats? modularrStats, ServiceHealth modularrHealth) = await modularrProbe;
        (_, ServiceHealth visularrHealth) = await visularrProbe;
        long remaining = (await queue).Sum(q => q.Sizeleft);
        Dictionary<string, Account> byVisularrId = accounts.Where(a => a.VisularrId is not null).ToDictionary(a => a.VisularrId!, StringComparer.OrdinalIgnoreCase);

        return new
        {
            catalog = new
            {
                counts = Enum.GetValues<CatalogState>().ToDictionary(s => s.ToString(), s => byState.GetValueOrDefault(s)),
                kinds = Enum.GetValues<TitleKind>().ToDictionary(k => k.ToString(), k => byKind.GetValueOrDefault(k)),
                trend = trend.Select(s => new { date = s.Date, s.Movies, s.Series, s.Available, s.Downloading, s.NotIndexed, s.Untracked, s.Missing, s.Unmonitored }),
                recentlyImported = imported.Select(c => new { c.Key, c.Kind, c.Title, c.Year, c.TmdbId, c.TvdbId, c.Poster, at = c.AvailableSince }),
                recentlyRequested = requested.Select(r => new
                {
                    r.Id, r.Kind, r.Title, r.Year, r.Poster, r.Username, r.Created,
                    status = RequestEndpoints.StatusOf(r, RequestEndpoints.CatalogKey(r) is string key ? requestedItems.GetValueOrDefault(key) : null),
                }),
                topRequesters = requesters,
            },
            downloads = modularrStats is null ? null : new
            {
                modularrStats.Downloads.Active,
                modularrStats.Downloads.DownloadBps,
                modularrStats.Downloads.UploadBps,
                remainingBytes = remaining,
                etaSeconds = modularrStats.Downloads.DownloadBps > 0 && remaining > 0 ? remaining / modularrStats.Downloads.DownloadBps : (long?)null,
            },
            storage = modularrStats?.Storage ?? [],
            publicIp = modularrStats?.PublicIp,
            services = new[] { modularrHealth, visularrHealth },
            sync = new { settings.LastCatalogSync, catalogError = settings.CatalogSyncError, settings.LastUserSync, userError = settings.UserSyncError },
            users = accounts.Select(a => new
            {
                a.Id, a.Username, a.DisplayName, a.Disabled, admin = a.IsAdmin, a.LastSignIn,
                avatar = a.AvatarUpdated is DateTime updated ? FormattableString.Invariant($"/api/avatars/{a.Id}?v={updated.Ticks}") : null,
                sessions = sessions[a.Id].Select(s => SessionDtos.Dto(s, current)),
            }),
            watching = (await watching).Where(s => s.UserName is not null).OrderByDescending(s => s.NowPlayingItem is not null).ThenByDescending(s => s.LastActivityDate).Select(s => new
            {
                user = s.UserName,
                accountId = s.UserId is not null && byVisularrId.TryGetValue(s.UserId, out Account? account) ? account.Id : (int?)null,
                device = s.DeviceName,
                client = string.Join(' ', new[] { s.Client, s.ApplicationVersion }.Where(p => !string.IsNullOrEmpty(p))),
                ip = s.RemoteEndPoint,
                lastActivity = s.LastActivityDate,
                nowPlaying = s.NowPlayingItem is not JellyfinNowPlaying item ? null : new
                {
                    title = item.SeriesName is null ? item.Name : FormattableString.Invariant($"{item.SeriesName} · S{item.ParentIndexNumber ?? 0:00}E{item.IndexNumber ?? 0:00} · {item.Name}"),
                    kind = item.Type,
                    year = item.ProductionYear,
                    paused = s.PlayState?.IsPaused ?? false,
                    progress = item.RunTimeTicks is > 0 && s.PlayState?.PositionTicks is long position ? Math.Clamp((double)position / item.RunTimeTicks.Value, 0, 1) : (double?)null,
                },
            }),
        };
    }

    // Reachable, version and round-trip time of one call; an app that is not configured is reported as such, not as down.
    internal static async Task<(T? Value, ServiceHealth Health)> ProbeAsync<T>(string app, Func<CancellationToken, Task<T>>? call, Func<T, string?> version, CancellationToken ct)
        where T : class
    {
        if (call is null)
        {
            return (null, new ServiceHealth(app, Configured: false, Reachable: false, null, null, null));
        }

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            T value = await call(ct);
            return (value, new ServiceHealth(app, Configured: true, Reachable: true, version(value), watch.ElapsedMilliseconds, null));
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            return (null, new ServiceHealth(app, Configured: true, Reachable: false, null, null, AppHttp.Describe(ex)));
        }
    }

    // Extras around the probe: when they fail the probe already says why, so they are only left empty.
    private static async Task<IReadOnlyList<T>> OrEmptyAsync<T>(Func<CancellationToken, Task<IReadOnlyList<T>>> call, CancellationToken ct)
    {
        try
        {
            return await call(ct);
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            return [];
        }
    }
}
