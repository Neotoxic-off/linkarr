using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Catalog;

// modularr's generic webhook payload (its Notifier): ids are modularr's; Path, the imported folder, when modularr sends it.
public sealed record HookEvent(string? Event, string? Title, string? Message, int? SeriesId, int? MovieId, DateTime? Time, string? Path)
{
    public const string Import = "import";
}

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogApi(this RouteGroupBuilder api)
    {
        RouteGroupBuilder catalog = api.MapGroup("/catalog").RequireAuthorization(AuthEndpoints.AdminPolicy);
        catalog.MapGet("/", async (CatalogState? state, TitleKind? kind, string? search, LinkarrDbContext db, CancellationToken ct) =>
        {
            IQueryable<CatalogItem> query = db.Catalog.AsNoTracking();
            if (state is CatalogState s)
            {
                query = query.Where(c => c.State == s);
            }

            if (kind is TitleKind k)
            {
                query = query.Where(c => c.Kind == k);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string term = search.Trim();
                query = query.Where(c => EF.Functions.Like(c.Title, "%" + term + "%"));
            }

            return await query.OrderBy(c => c.Title).Take(500).ToListAsync(ct);
        });

        catalog.MapPost("/sync", async (CatalogSync sync, LinkarrDbContext db, CancellationToken ct) =>
        {
            await sync.RunAsync(ct);
            AppSettings settings = await db.Settings.AsNoTracking().FirstAsync(ct);
            return new { settings.LastCatalogSync, settings.CatalogSyncError };
        });

        // notIndexed: a targeted refresh of the title's folder in visularr.
        catalog.MapPost("/refresh", async (string key, LinkarrDbContext db, CatalogSync sync, CancellationToken ct) =>
        {
            await sync.RefreshAsync(await FindAsync(db, key, ct), ct);
            return Results.NoContent();
        });

        // untracked: modularr starts monitoring the title (no search: visularr already has the files).
        catalog.MapPost("/monitor", async (string key, LinkarrDbContext db, Connections connections, CancellationToken ct) =>
        {
            CatalogItem item = await FindAsync(db, key, ct);
            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            await TitleAdder.AddAsync(modularr, item.Kind, item.Kind == TitleKind.Movie ? item.TmdbId : item.TvdbId, search: false, ct);
            item.State = CatalogState.Missing;
            item.Monitored = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // modularr posts its events here (the token is sent with the managed push); the token is compared in constant time.
        // Any event updates the catalog (debounced); an import also gets visularr's targeted refresh right away with the auto-fix on.
        // The token comes in the X-Linkarr-Token header; ?token= is still read for a modularr that predates it (drop next release).
        api.MapPost("/hooks/modularr", async (string? token, HttpContext context, LinkarrDbContext db, SyncState state, CatalogSync sync, CancellationToken ct) =>
        {
            AppSettings settings = await db.Settings.AsNoTracking().FirstAsync(ct);
            if (!TokenMatches(settings.WebhookToken, context.Request.Headers[HookTokenHeader].FirstOrDefault() ?? token))
            {
                return Results.Json(new { error = "Invalid token." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            HookEvent? hook = null;
            if (context.Request.HasJsonContentType())
            {
                try
                {
                    hook = await context.Request.ReadFromJsonAsync<HookEvent>(ct);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid event." });
                }
            }

            state.RequestCatalog(SyncState.HookDebounce);
            if (settings.AutoFixNotIndexed && string.Equals(hook?.Event, HookEvent.Import, StringComparison.OrdinalIgnoreCase))
            {
                await sync.RefreshImportAsync(hook!, ct);
            }

            return Results.Accepted();
        }).AllowAnonymous();
        return api;
    }

    public const string HookTokenHeader = "X-Linkarr-Token";

    // Both sides hashed first: a fixed-length, constant-time comparison whatever the presented length.
    public static bool TokenMatches(string expected, string? presented) =>
        !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(presented)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private static async Task<CatalogItem> FindAsync(LinkarrDbContext db, string key, CancellationToken ct) =>
        await db.Catalog.FirstOrDefaultAsync(c => c.Key == key, ct) ?? throw new KeyNotFoundException("That title is not in the catalog any more.");
}

// Adds one title to modularr by its id, with modularr's own defaults for its category (from the lookup) or its first
// quality profile and root folder when the lookup has none.
public static class TitleAdder
{
    public static async Task<string> AddAsync(ModularrClient modularr, TitleKind kind, int? id, bool search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modularr);
        if (id is not int known)
        {
            throw new ArgumentException("This title has no TMDB/TVDB id, modularr cannot add it.", nameof(id));
        }

        try
        {
            if (kind == TitleKind.Movie)
            {
                ArrMovie movie = await modularr.LookupMoviesAsync(FormattableString.Invariant($"tmdb:{known}"), ct) is [ArrMovie found, ..]
                    ? found
                    : throw new KeyNotFoundException($"modularr does not know TMDB id {known}.");
                if (movie.Id > 0)
                {
                    return movie.Title;
                }

                (int profile, string? root) = await DefaultsAsync(modularr, movie.QualityProfileId, movie.RootFolderPath, ct);
                await modularr.AddMovieAsync(movie with { QualityProfileId = profile, RootFolderPath = root }, search, ct);
                return movie.Title;
            }

            ArrSeries series = await modularr.LookupSeriesAsync(FormattableString.Invariant($"tvdb:{known}"), ct) is [ArrSeries foundSeries, ..]
                ? foundSeries
                : throw new KeyNotFoundException($"modularr does not know TVDB id {known}.");
            if (series.Id > 0)
            {
                return series.Title;
            }

            (int seriesProfile, string? seriesRoot) = await DefaultsAsync(modularr, series.QualityProfileId, series.RootFolderPath, ct);
            await modularr.AddSeriesAsync(series with { QualityProfileId = seriesProfile, RootFolderPath = seriesRoot }, search, ct);
            return series.Title;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Added meanwhile: that is what was asked.
            return string.Empty;
        }
    }

    private static async Task<(int Profile, string? Root)> DefaultsAsync(ModularrClient modularr, int profile, string? root, CancellationToken ct)
    {
        if (profile <= 0)
        {
            profile = await modularr.QualityProfilesAsync(ct) is [ArrQualityProfile first, ..] ? first.Id : throw new InvalidOperationException("modularr has no quality profile.");
        }

        if (string.IsNullOrEmpty(root))
        {
            root = await modularr.RootFoldersAsync(ct) is [ArrRootFolder first, ..] ? first.Path : null;
        }

        return (profile, root);
    }
}
