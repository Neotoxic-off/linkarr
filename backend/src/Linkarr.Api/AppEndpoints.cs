using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api;

// Connections settings (admin), the dashboard and the public app links.
public static class AppEndpoints
{
    // An API key left null keeps the saved one, "" clears it.
    public sealed record SettingsRequest(
        string? ModularrUrl, string? ModularrApiKey, string? ModularrPublicUrl,
        string? VisularrUrl, string? VisularrApiKey, string? VisularrPublicUrl,
        string? LinkarrPublicUrl, bool RequestsNeedApproval, bool AutoFixNotIndexed, int? RequestsPerDay = null);

    public sealed record TestRequest(string App, string? Url, string? ApiKey);

    public static RouteGroupBuilder MapAppApi(this RouteGroupBuilder api)
    {
        RouteGroupBuilder settings = api.MapGroup("/settings").RequireAuthorization(AuthEndpoints.AdminPolicy);
        settings.MapGet("/", async (Connections connections, IConfiguration configuration, CancellationToken ct) =>
            ToDto(await connections.SettingsAsync(ct), connections, configuration));
        settings.MapPut("/", async (SettingsRequest request, HttpContext context, Connections connections, LinkarrDbContext db, SyncState state,
            IConfiguration configuration, CancellationToken ct) =>
        {
            AppSettings saved = await connections.SettingsAsync(ct);
            (string? oldModularrUrl, string? oldVisularrUrl) = (saved.ModularrUrl, saved.VisularrUrl);
            saved.ModularrUrl = Connections.ValidUrl(request.ModularrUrl, "modularr URL");
            saved.ModularrPublicUrl = Connections.ValidUrl(request.ModularrPublicUrl, "modularr public URL");
            saved.VisularrUrl = Connections.ValidUrl(request.VisularrUrl, "visularr URL");
            saved.VisularrPublicUrl = Connections.ValidUrl(request.VisularrPublicUrl, "visularr public URL");
            // The address both apps link to for user management; this page's own address until the admin sets one.
            saved.LinkarrPublicUrl = Connections.ValidUrl(request.LinkarrPublicUrl, "linkarr public URL")
                ?? $"{context.Request.Scheme}://{context.Request.Host}";
            saved.ModularrKey = NewKey(request.ModularrApiKey, saved.ModularrKey, connections);
            saved.VisularrKey = NewKey(request.VisularrApiKey, saved.VisularrKey, connections);
            saved.RequestsNeedApproval = request.RequestsNeedApproval;
            saved.AutoFixNotIndexed = request.AutoFixNotIndexed;
            saved.RequestsPerDay = request.RequestsPerDay is int perDay ? Math.Max(0, perDay) : saved.RequestsPerDay;
            // The deployment's values win over what was sent.
            DeploymentSettings.Apply(saved, configuration, connections);
            // A saved key only goes to the address it was entered for: a new address without a new key drops it (a key set by
            // the deployment is applied again just below).
            if (request.ModularrApiKey is null && saved.ModularrUrl != oldModularrUrl)
            {
                saved.ModularrKey = null;
            }

            if (request.VisularrApiKey is null && saved.VisularrUrl != oldVisularrUrl)
            {
                saved.VisularrKey = null;
            }

            DeploymentSettings.Apply(saved, configuration, connections);
            await db.SaveChangesAsync(ct);
            state.RequestUsers();
            return ToDto(saved, connections, configuration);
        });

        // A new webhook token, pushed to modularr right away (if that fails, the next user sync pushes it; events are refused meanwhile).
        settings.MapPost("/webhook-token", async (Connections connections, LinkarrDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            AppSettings saved = await connections.SettingsAsync(ct);
            saved.WebhookToken = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await db.SaveChangesAsync(ct);
            if (saved.LinkarrPublicUrl is string url && await connections.ModularrAsync(ct) is ModularrClient modularr)
            {
                await modularr.SetManagedAsync(url, saved.WebhookToken, configuration["Linkarr:InternalUrl"], ct);
            }

            return Results.NoContent();
        });

        // Tests the address and key as typed; an empty key means the saved one (for the saved address). The key never comes back.
        settings.MapPost("/test", async (TestRequest request, Connections connections, CancellationToken ct) =>
        {
            AppSettings saved = await connections.SettingsAsync(ct);
            bool modularr = request.App == UserSync.Modularr;
            // No address typed: test the saved connection as it is.
            string? url = Connections.ValidUrl(request.Url, "URL") ?? (modularr ? saved.ModularrUrl : saved.VisularrUrl);
            // The saved key only ever goes to the saved address.
            string? key = !string.IsNullOrEmpty(request.ApiKey) ? request.ApiKey
                : url == (modularr ? saved.ModularrUrl : saved.VisularrUrl) ? connections.Unprotect(modularr ? saved.ModularrKey : saved.VisularrKey)
                : null;
            try
            {
                string message = modularr
                    ? await (connections.Modularr(url, key) ?? throw new ArgumentException("Enter the URL and the API key.", nameof(request))).PingAsync(ct)
                    : await (connections.Visularr(url, key) ?? throw new ArgumentException("Enter the URL and the API key.", nameof(request))).PingAsync(ct);
                return new { ok = true, message };
            }
            catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
            {
                return new { ok = false, message = AppHttp.Describe(ex) };
            }
        });

        api.MapGet("/links", async (Connections connections, CancellationToken ct) =>
        {
            AppSettings saved = await connections.SettingsAsync(ct);
            return new { modularr = saved.ModularrPublicUrl, visularr = saved.VisularrPublicUrl };
        });

        api.MapGet("/dashboard", async (HttpContext context, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            bool admin = context.User.IsInRole(AuthEndpoints.AdminRole);
            AppSettings saved = await db.Settings.AsNoTracking().FirstAsync(ct);
            Dictionary<CatalogState, int> counts = await db.Catalog.GroupBy(c => c.State).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
            List<CatalogItem> downloading = await db.Catalog.AsNoTracking().Where(c => c.Progress != null).OrderByDescending(c => c.Progress).Take(8).ToListAsync(ct);
            return new
            {
                counts = Enum.GetValues<CatalogState>().ToDictionary(s => s.ToString(), s => counts.GetValueOrDefault(s)),
                downloads = downloading.Select(c => new { c.Key, c.Kind, c.Title, c.Year, c.TmdbId, c.TvdbId, c.Progress, c.Poster }),
                users = admin ? new
                {
                    total = await db.Accounts.CountAsync(ct),
                    outOfSync = await db.Accounts.CountAsync(a => a.SyncStatus == SyncStatus.OutOfSync || a.PasswordPending, ct),
                    unmanaged = state.Unmanaged.Count,
                } : null,
                pendingRequests = admin ? await db.Requests.CountAsync(r => r.Approval == Approval.Pending, ct) : 0,
                saved.LastCatalogSync,
                catalogError = admin ? saved.CatalogSyncError : null,
                saved.LastUserSync,
                userError = admin ? saved.UserSyncError : null,
                links = new { modularr = saved.ModularrPublicUrl, visularr = saved.VisularrPublicUrl },
            };
        });
        return api;
    }

    // Errors as { error } with a status the UI can show; never a stack trace.
    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) => app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        Exception? error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        (int status, string message) = error switch
        {
            ArgumentException argument => (StatusCodes.Status400BadRequest, argument.ParamName is null ? argument.Message : argument.Message.Replace($" (Parameter '{argument.ParamName}')", "", StringComparison.Ordinal)),
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request."),
            KeyNotFoundException => (StatusCodes.Status404NotFound, error.Message),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, error.Message),
            OperationCanceledException => (StatusCodes.Status504GatewayTimeout, AppHttp.Describe(error)),
            // What an app answered (it can carry an excerpt of its response body) is for admins only.
            HttpRequestException or InvalidOperationException or System.Text.Json.JsonException => (StatusCodes.Status502BadGateway,
                context.User.IsInRole(AuthEndpoints.AdminRole) ? error.Message : "An app linkarr relies on did not answer as expected; an admin can see why."),
            _ => (StatusCodes.Status500InternalServerError, "Internal error"),
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }));

    private static string? NewKey(string? entered, string? saved, Connections connections) => entered switch
    {
        null => saved,
        "" => null,
        _ => connections.Protect(entered.Trim()),
    };

    private static object ToDto(AppSettings settings, Connections connections, IConfiguration configuration) => new
    {
        deploymentFields = DeploymentSettings.Fields(configuration),
        settings.ModularrUrl,
        modularrKeySet = connections.Unprotect(settings.ModularrKey) is not null,
        settings.ModularrPublicUrl,
        settings.VisularrUrl,
        visularrKeySet = connections.Unprotect(settings.VisularrKey) is not null,
        settings.VisularrPublicUrl,
        settings.LinkarrPublicUrl,
        settings.RequestsNeedApproval,
        settings.AutoFixNotIndexed,
        settings.RequestsPerDay,
    };
}
