using System.Net;
using System.Text.Json.Serialization;
using Linkarr.Api;
using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Catalog;
using Linkarr.Api.Data;
using Linkarr.Api.Requests;
using Linkarr.Api.Sync;
using Linkarr.Api.Users;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The container sets LINKARR_CONFIG_DIR=/config (Dockerfile); dev keeps its config next to the API project.
string configDirectory = builder.Configuration["LINKARR_CONFIG_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "config");
Directory.CreateDirectory(configDirectory);
string databasePath = Path.Combine(configDirectory, "linkarr.db");
builder.Services.AddDbContext<LinkarrDbContext>(options => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()));
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddLinkarrAuth(configDirectory);
builder.Services.AddAppHttp();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<Connections>();
builder.Services.AddScoped<UserSync>();
builder.Services.AddScoped<CatalogSync>();
builder.Services.AddSingleton<SyncState>();
builder.Services.AddSingleton<OwnerProvisioning>();
if (builder.Configuration["LINKARR_DISABLE_WORKER"] != "true")
{
    builder.Services.AddHostedService<SyncWorker>();
}

// Served over https (LINKARR_PUBLIC_URL): the session cookie is Secure even when a TLS-terminating proxy talks http to linkarr.
if (builder.Configuration["LINKARR_PUBLIC_URL"]?.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
{
    builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
        options => options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always);
}

// Behind a reverse proxy the client's address and scheme come from X-Forwarded-*, trusted only from TrustedProxies (';'-separated
// IPs or CIDRs, e.g. TrustedProxies=172.18.0.0/16 for a proxy container on the compose network). Empty by default on purpose: a
// trusted range lets anything in it choose the client address the login rate limit and the session list see, so list only the proxy.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (string proxy in (builder.Configuration["TrustedProxies"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (proxy.Contains('/', StringComparison.Ordinal))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxy));
        }
        else
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    }
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

WebApplication app = builder.Build();

app.UseForwardedHeaders();
app.UseApiErrors();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use((context, next) =>
{
    context.Response.Headers["X-Frame-Options"] = "DENY";
    // The static export inlines its bootstrap scripts and styles ('unsafe-inline'); next/font serves the fonts from here; posters
    // come from https image hosts.
    context.Response.Headers["Content-Security-Policy"] = Csp;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    return next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The UI files stay public; the API needs a signed-in user unless an endpoint says otherwise, and an admin to change anything.
app.MapGroup("/api").RequireAuthorization().RequireAdminForWrites()
    .MapAuthApi().MapUserApi().MapRoleApi().MapCatalogApi().MapRequestApi().MapAppApi().MapStatsApi();
app.MapGoLinks();

// Unknown /api routes must 404, not fall through to the SPA.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();

// For WebApplicationFactory in the tests.
public partial class Program
{
    public const string Csp = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data: blob:; "
        + "connect-src 'self'; font-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
}
