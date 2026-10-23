using System.Globalization;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api;

// The links between the apps: modularr and visularr point at /go/…, linkarr redirects to the matching page of the other app.
// Anonymous on purpose: they only ever redirect to the public URLs the admin configured (ids are digits only, never a URL from
// the request), and the target app signs the user in itself. Only a signed-in linkarr user gets the catalog's resolved target.
public static class GoEndpoints
{
    private const string Tmdb = "tmdb";
    private const string Tvdb = "tvdb";

    public static IEndpointRouteBuilder MapGoLinks(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder go = app.MapGroup("/go").AllowAnonymous();

        // The title in visularr: its item page when the catalog knows it, else visularr's own lookup by id.
        // Without a linkarr session the answer never depends on the catalog, so it reveals nothing about what is in it.
        go.MapGet("/watch", async (string? tmdb, string? tvdb, HttpContext context, LinkarrDbContext db, CancellationToken ct) =>
        {
            if (ParseId(tmdb, tvdb) is not (string provider, int id))
            {
                return BadLink();
            }

            if ((await db.Settings.AsNoTracking().FirstAsync(ct)).VisularrPublicUrl is not string visularr)
            {
                return NotConfigured(UserSync.Visularr);
            }

            string? itemId = !SignedIn(context) ? null
                : await Titles(db, provider, id).Where(c => c.VisularrId != null).Select(c => c.VisularrId).FirstOrDefaultAsync(ct);
            return Results.Redirect(itemId is null
                ? $"{visularr}/web/open/?{provider}={id.ToString(CultureInfo.InvariantCulture)}"
                : $"{visularr}/web/item/?id={Uri.EscapeDataString(itemId)}");
        });

        // The title in modularr: its page when modularr has it, else modularr's explore search for it.
        go.MapGet("/manage", async (string? tmdb, string? tvdb, HttpContext context, LinkarrDbContext db, CancellationToken ct) =>
        {
            if (ParseId(tmdb, tvdb) is not (string provider, int id))
            {
                return BadLink();
            }

            if ((await db.Settings.AsNoTracking().FirstAsync(ct)).ModularrPublicUrl is not string modularr)
            {
                return NotConfigured(UserSync.Modularr);
            }

            List<CatalogItem> items = SignedIn(context) ? await Titles(db, provider, id).ToListAsync(ct) : [];
            if (items.FirstOrDefault(c => c.ModularrId is not null) is CatalogItem known)
            {
                string type = known.Kind == TitleKind.Movie ? "movie" : "series";
                return Results.Redirect($"{modularr}/title/?type={type}&id={known.ModularrId!.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            string term = items.FirstOrDefault()?.Title ?? $"{provider}:{id.ToString(CultureInfo.InvariantCulture)}";
            return Results.Redirect($"{modularr}/explore/?q={Uri.EscapeDataString(term)}");
        });

        go.MapGet("/app/{name}", async (string name, LinkarrDbContext db, CancellationToken ct) =>
        {
            AppSettings settings = await db.Settings.AsNoTracking().FirstAsync(ct);
            (bool known, string? url) = name switch
            {
                UserSync.Modularr => (true, settings.ModularrPublicUrl),
                UserSync.Visularr => (true, settings.VisularrPublicUrl),
                _ => (false, null),
            };
            return !known ? Results.NotFound() : url is null ? NotConfigured(name) : Results.Redirect(url + "/");
        });
        return app;
    }

    // Exactly one of tmdb/tvdb, ASCII digits only (no sign, space or other numerals), within int.
    public static (string Provider, int Id)? ParseId(string? tmdb, string? tvdb) => (tmdb, tvdb) switch
    {
        (string value, null) when Digits(value) is int id => (Tmdb, id),
        (null, string value) when Digits(value) is int id => (Tvdb, id),
        _ => null,
    };

    private static int? Digits(string value) =>
        value.Length is > 0 and <= 9 && value.All(char.IsAsciiDigit) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0
            ? id
            : null;

    private static IQueryable<CatalogItem> Titles(LinkarrDbContext db, string provider, int id) =>
        provider == Tmdb ? db.Catalog.AsNoTracking().Where(c => c.TmdbId == id) : db.Catalog.AsNoTracking().Where(c => c.TvdbId == id);

    private static bool SignedIn(HttpContext context) => context.User.Identity?.IsAuthenticated == true;

    private static IResult BadLink() => Results.BadRequest(new { error = "The link needs one numeric tmdb or tvdb id." });

    // Fixed text only: nothing from the request is written into the page.
    private static IResult NotConfigured(string app) => Results.Content(
        $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>linkarr</title></head>
        <body style="font-family:system-ui,sans-serif;background:#0b0b0c;color:#ececec;display:grid;place-items:center;min-height:100vh;margin:0;padding:16px">
        <main style="max-width:28rem"><h1 style="font-size:1.25rem">{app} has no public URL yet</h1>
        <p>linkarr does not know the address browsers use to reach {app}. An administrator can set it in linkarr's <a href="/settings/" style="color:inherit">Settings</a>.</p></main>
        </body></html>
        """,
        "text/html; charset=utf-8",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
