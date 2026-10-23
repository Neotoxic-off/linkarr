using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Catalog;
using Linkarr.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Requests;

// Users with the request right search modularr through linkarr (the key stays on the server) and ask for titles.
public static class RequestEndpoints
{
    public sealed record NewRequest(TitleKind Kind, int Id);

    public const string MemberError = "Adding it failed; an admin can retry it.";

    public static RouteGroupBuilder MapRequestApi(this RouteGroupBuilder api)
    {
        api.MapGet("/discover", async (TitleKind kind, string? term, HttpContext context, LinkarrDbContext db, Connections connections, CancellationToken ct) =>
        {
            await RequireRequestRightAsync(context, db, ct);
            if (string.IsNullOrWhiteSpace(term) || term.Length > 200)
            {
                return Array.Empty<DiscoverItem>();
            }

            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            List<TitleRequest> requests = await db.Requests.AsNoTracking().Where(r => r.Kind == kind && r.Approval != Approval.Denied).ToListAsync(ct);
            // Ids only ("tmdb:603") stay modularr's to resolve; plain text is a title search.
            string query = term.Trim().Replace(":", " ", StringComparison.Ordinal);
            IEnumerable<DiscoverItem> results = kind == TitleKind.Movie
                ? (await modularr.LookupMoviesAsync(query, ct)).Select(m => new DiscoverItem(
                    kind, m.TmdbId, null, m.Title, m.Year, m.Overview, ArrImages.Poster(m.Images), m.Id > 0, requests.Any(r => r.TmdbId == m.TmdbId)))
                : (await modularr.LookupSeriesAsync(query, ct)).Select(s => new DiscoverItem(
                    kind, null, s.TvdbId, s.Title, s.Year, s.Overview, ArrImages.Poster(s.Images), s.Id > 0, requests.Any(r => r.TvdbId == s.TvdbId)));
            return results.ToArray();
        });

        api.MapGet("/requests", async (HttpContext context, LinkarrDbContext db, CancellationToken ct) =>
        {
            bool admin = context.User.IsInRole(AuthEndpoints.AdminRole);
            int me = AuthEndpoints.UserId(context.User);
            List<TitleRequest> requests = await db.Requests.AsNoTracking().Where(r => admin || r.AccountId == me).OrderByDescending(r => r.Created).Take(500).ToListAsync(ct);
            Dictionary<string, CatalogItem> catalog = await db.Catalog.AsNoTracking().ToDictionaryAsync(c => c.Key, ct);
            return requests.Select(r =>
            {
                CatalogItem? item = CatalogKey(r) is string key ? catalog.GetValueOrDefault(key) : null;
                // What the app answered is for admins only (it can carry its response body).
                string? error = admin || r.Error is null ? r.Error : MemberError;
                return new { r.Id, r.Username, r.Kind, r.TmdbId, r.TvdbId, r.Title, r.Year, r.Poster, r.Created, Error = error, status = StatusOf(r, item), progress = item?.Progress };
            });
        });

        api.MapPost("/requests", async (NewRequest request, HttpContext context, LinkarrDbContext db, Connections connections, CancellationToken ct) =>
        {
            Account account = await RequireRequestRightAsync(context, db, ct);
            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            List<TitleRequest> forTitle = await db.Requests.Where(r => r.Kind == request.Kind
                && (request.Kind == TitleKind.Movie ? r.TmdbId == request.Id : r.TvdbId == request.Id)).ToListAsync(ct);
            if (forTitle.FirstOrDefault(r => r.Approval != Approval.Denied) is TitleRequest existing)
            {
                // Never who asked: members do not learn each other's requests.
                return Results.Conflict(new { error = $"{existing.Title} was already requested." });
            }

            if (!account.IsAdmin)
            {
                // A denied title stays denied for its requester until an admin approves (re-opens) or deletes that request.
                if (forTitle.FirstOrDefault(r => r.AccountId == account.Id) is TitleRequest denied)
                {
                    return Results.Conflict(new { error = $"{denied.Title} was denied; ask an admin to re-open it." });
                }

                int perDay = (await connections.SettingsAsync(ct)).RequestsPerDay;
                DateTime since = DateTime.UtcNow.AddDays(-1);
                if (perDay > 0 && await db.Requests.CountAsync(r => r.AccountId == account.Id && r.Created > since, ct) >= perDay)
                {
                    return Results.Json(new { error = $"You can request {perDay} titles a day; try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
                }
            }

            // The title as modularr knows it, never as the browser describes it.
            (string title, int year, string? poster) = request.Kind == TitleKind.Movie
                ? (await modularr.LookupMoviesAsync(FormattableString.Invariant($"tmdb:{request.Id}"), ct)).Select(m => (m.Title, m.Year, ArrImages.Poster(m.Images))).FirstOrDefault()
                : (await modularr.LookupSeriesAsync(FormattableString.Invariant($"tvdb:{request.Id}"), ct)).Select(s => (s.Title, s.Year, ArrImages.Poster(s.Images))).FirstOrDefault();
            if (title is null)
            {
                throw new KeyNotFoundException("modularr does not know this title.");
            }

            bool approved = account.IsAdmin || !(await connections.SettingsAsync(ct)).RequestsNeedApproval;
            TitleRequest added = new()
            {
                AccountId = account.Id, Username = account.Username, Kind = request.Kind, Title = title, Year = year, Poster = poster, Created = DateTime.UtcNow,
                TmdbId = request.Kind == TitleKind.Movie ? request.Id : null, TvdbId = request.Kind == TitleKind.Series ? request.Id : null,
                Approval = approved ? Approval.Approved : Approval.Pending,
            };
            if (approved)
            {
                await SendAsync(added, modularr, ct);
            }

            db.Requests.Add(added);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added.Id, status = StatusOf(added, null) });
        });

        // Admin (writes under /api/requests/... are admin-only): approve or retry a failed add, deny, or forget a request.
        api.MapPost("/requests/{id:int}/approve", async (int id, LinkarrDbContext db, Connections connections, CancellationToken ct) =>
        {
            TitleRequest request = await db.Requests.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException("No such request.");
            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            request.Approval = Approval.Approved;
            await SendAsync(request, modularr, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.Id, status = StatusOf(request, null), request.Error });
        }).RequireAuthorization(AuthEndpoints.AdminPolicy);
        api.MapPost("/requests/{id:int}/deny", async (int id, LinkarrDbContext db, CancellationToken ct) =>
        {
            TitleRequest request = await db.Requests.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException("No such request.");
            request.Approval = Approval.Denied;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthEndpoints.AdminPolicy);
        api.MapDelete("/requests/{id:int}", async (int id, LinkarrDbContext db, CancellationToken ct) =>
        {
            await db.Requests.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthEndpoints.AdminPolicy);
        return api;
    }

    public sealed record DiscoverItem(TitleKind Kind, int? TmdbId, int? TvdbId, string Title, int Year, string? Overview, string? Poster, bool InLibrary, bool Requested);

    // requested → downloading → available follows the catalog; pending, denied and failed are the request's own.
    public static string StatusOf(TitleRequest request, CatalogItem? item)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Approval switch
        {
            Approval.Pending => "pending",
            Approval.Denied => "denied",
            _ when request.Error is not null => "failed",
            _ => item?.State switch
            {
                CatalogState.Available => "available",
                CatalogState.Downloading => "downloading",
                _ => "requested",
            },
        };
    }

    internal static string? CatalogKey(TitleRequest request) => request.Kind == TitleKind.Movie
        ? request.TmdbId is int tmdb ? CatalogBuilder.Key(TitleKind.Movie, tmdb) : null
        : request.TvdbId is int tvdb ? CatalogBuilder.Key(TitleKind.Series, tvdb) : null;

    // Monitored and searched right away; a failure is kept on the request for the admin to retry.
    private static async Task SendAsync(TitleRequest request, ModularrClient modularr, CancellationToken ct)
    {
        try
        {
            await TitleAdder.AddAsync(modularr, request.Kind, request.Kind == TitleKind.Movie ? request.TmdbId : request.TvdbId, search: true, ct);
            request.Error = null;
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct) || ex is KeyNotFoundException)
        {
            request.Error = AppHttp.Describe(ex);
        }
    }

    // Checked against the database, not the cookie: rights change without signing the user out.
    private static async Task<Account> RequireRequestRightAsync(HttpContext context, LinkarrDbContext db, CancellationToken ct)
    {
        int id = AuthEndpoints.UserId(context.User);
        Account account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("Unknown user.");
        return account.IsAdmin || account.Request ? account : throw new UnauthorizedAccessException("You may not request titles.");
    }
}
