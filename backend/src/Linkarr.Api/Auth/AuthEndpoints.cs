using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Linkarr.Api.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace Linkarr.Api.Auth;

// Cookie sign-in against linkarr's accounts (modularr's model): session rows, a security stamp, a login rate limit per IP
// (/64 for IPv6), a same-origin check on writes and admin-only writes. Every /api route needs a signed-in user unless marked anonymous.
public static class AuthEndpoints
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";
    public const string UserRole = "User";
    public const string LoginRateLimit = "login";

    // The only writes a member may make: their own session, password, profile and picture, and a title request. The modularr
    // webhook is anonymous and checks its own token.
    private static readonly HashSet<string> MemberWrites = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/setup", "/api/auth/login", "/api/auth/logout", "/api/auth/signout-all", "/api/auth/password", "/api/auth/profile", "/api/auth/avatar", "/api/requests",
        "/api/auth/sessions/signout",
        "/api/hooks/modularr",
    };

    private const string StampClaim = "linkarr:stamp";
    internal const string SessionClaim = "linkarr:session";

    public sealed record Credentials(string Username, string Password);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record ProfileRequest(string? DisplayName);

    public sealed record SessionRequest(string Id);

    public static IServiceCollection AddLinkarrAuth(this IServiceCollection services, string configDirectory)
    {
        // Persisted keys keep cookies valid across restarts and decrypt the stored API keys: readable by this account only.
        string keys = Path.Combine(configDirectory, "keys");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(keys);
        }
        else
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(keys, OwnerOnly);
            File.SetUnixFileMode(keys, OwnerOnly);
        }

        services.AddDataProtection().SetApplicationName("linkarr").PersistKeysToFileSystem(new DirectoryInfo(keys));

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "linkarr";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = AccountService.SessionLifetime;
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => WriteError(context.Response, StatusCodes.Status401Unauthorized, "Not signed in.");
            options.Events.OnRedirectToAccessDenied = context => WriteError(context.Response, StatusCodes.Status403Forbidden, "Admin only.");
            options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
        });
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    public static RouteGroupBuilder MapAuthApi(this RouteGroupBuilder api)
    {
        api.MapGet("/auth/state", StateAsync).AllowAnonymous();
        api.MapPost("/auth/setup", SetupAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        api.MapPost("/auth/logout", async (HttpContext context, AccountService accounts, CancellationToken ct) =>
        {
            if (context.User.FindFirstValue(SessionClaim) is string session)
            {
                await accounts.EndSessionAsync(session, ct);
            }

            await context.SignOutAsync();
            return Results.NoContent();
        });

        // Every session of this account ends, this browser's too (see UserSync.SignOutEverywhereAsync for the apps).
        api.MapPost("/auth/signout-all", async (HttpContext context, AccountService accounts, UserSync sync, CancellationToken ct) =>
        {
            string? error = await sync.SignOutEverywhereAsync(await accounts.GetAsync(UserId(context.User), ct), ct);
            await context.SignOutAsync();
            return SignedOutEverywhere(error);
        });

        // The new password goes to both apps too; when one is down the account says so and asks for it again later.
        api.MapPost("/auth/password", async (ChangePasswordRequest request, HttpContext context, AccountService accounts, UserSync sync, CancellationToken ct) =>
        {
            Account account = await accounts.GetAsync(UserId(context.User), ct);
            if (!AccountService.VerifyPassword(account, request.CurrentPassword))
            {
                throw new ArgumentException("The current password is wrong.", nameof(request));
            }

            await accounts.SetPasswordAsync(account, request.NewPassword, ct);
            await sync.ProvisionAsync(account, request.NewPassword, ct);
            await SignInAsync(context, accounts, account, ct);
            return UserDtos.Me(account);
        }).RequireRateLimiting(LoginRateLimit);

        // The signed-in user's own browsers; ending the current one signs this browser out too.
        api.MapGet("/auth/sessions", async (HttpContext context, AccountService accounts, CancellationToken ct) =>
            (await accounts.SessionsAsync(UserId(context.User), ct)).Select(s => SessionDtos.Dto(s, context.User.FindFirstValue(SessionClaim))));
        api.MapPost("/auth/sessions/signout", async (SessionRequest request, HttpContext context, AccountService accounts, CancellationToken ct) =>
        {
            string? current = context.User.FindFirstValue(SessionClaim);
            if (!await accounts.EndSessionByPublicIdAsync(UserId(context.User), request.Id, ct))
            {
                throw new KeyNotFoundException("That session has already ended.");
            }

            if (current is not null && SessionDtos.PublicId(current) == request.Id)
            {
                await context.SignOutAsync();
            }

            return Results.NoContent();
        });

        api.MapPatch("/auth/profile", async (ProfileRequest request, HttpContext context, AccountService accounts, LinkarrDbContext db, CancellationToken ct) =>
        {
            Account account = await accounts.GetAsync(UserId(context.User), ct);
            account.DisplayName = AccountService.CleanDisplayName(request.DisplayName);
            await db.SaveChangesAsync(ct);
            return UserDtos.Me(account);
        });
        api.MapPut("/auth/avatar", async (HttpContext context, AccountService accounts, CancellationToken ct) =>
        {
            if (context.Request.ContentLength > AccountService.MaxAvatarBytes)
            {
                return Results.BadRequest(new { error = $"The picture must be at most {AccountService.MaxAvatarBytes / 1024} KB." });
            }

            // Read at most one byte past the limit, whatever the declared length, so a chunked upload cannot grow memory.
            using MemoryStream image = new();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0 && image.Length <= AccountService.MaxAvatarBytes)
            {
                image.Write(buffer, 0, read);
            }

            return Results.Ok(UserDtos.Me(await accounts.SetAvatarAsync(UserId(context.User), image.ToArray(), ct)));
        });
        api.MapDelete("/auth/avatar", async (HttpContext context, AccountService accounts, CancellationToken ct) =>
            UserDtos.Me(await accounts.RemoveAvatarAsync(UserId(context.User), ct)));
        api.MapGet("/avatars/{id:int}", async (int id, HttpContext context, AccountService accounts, CancellationToken ct) =>
        {
            if (await accounts.FindAvatarAsync(id, ct) is not AccountAvatar avatar)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.File(avatar.Content, avatar.ContentType);
        });
        return api;
    }

    // Members only read: every write needs an admin, except the few in MemberWrites. Browser writes must come from this origin.
    public static RouteGroupBuilder RequireAdminForWrites(this RouteGroupBuilder api)
    {
        api.AddEndpointFilter(async (context, next) =>
        {
            HttpRequest request = context.HttpContext.Request;
            bool safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
            if (!safe && !IsSameOrigin(request))
            {
                return Results.Json(new { error = "Cross-origin request refused." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // Exact paths, not prefixes: a route added later under /api/auth must not become member-writable by accident.
            bool memberMay = safe || MemberWrites.Contains(request.Path.Value?.TrimEnd('/') ?? "");
            return memberMay || context.HttpContext.User.IsInRole(AdminRole)
                ? await next(context)
                : Results.Json(new { error = "Admin only." }, statusCode: StatusCodes.Status403Forbidden);
        });
        return api;
    }

    // CSRF: SameSite=Strict still lets another origin of the same site send the cookie, and bodyless POSTs need no preflight.
    internal static bool IsSameOrigin(HttpRequest request)
    {
        string? fetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(fetchSite))
        {
            return fetchSite is "same-origin" or "none";
        }

        string? origin = request.Headers.Origin;
        return string.IsNullOrEmpty(origin)
            || (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    // DNS rebinding: before any account exists a rebinding page could create the first admin, so setup only answers when the
    // server is addressed by IP, localhost, or a host listed in AllowedHosts.
    private static bool IsTrustedSetupHost(HttpRequest request, IConfiguration configuration)
    {
        string host = request.Host.Host;
        if (IPAddress.TryParse(host, out _) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] allowed = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a => a != "*" && (a.Equals(host, StringComparison.OrdinalIgnoreCase)
            || (a.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(a[1..], StringComparison.OrdinalIgnoreCase))));
    }

    private static async Task<object> StateAsync(HttpContext context, AccountService accounts, IConfiguration configuration, CancellationToken ct)
    {
        Account? account = context.User.Identity?.IsAuthenticated == true ? await accounts.FindAsync(UserId(context.User), ct) : null;
        bool setupRequired = await accounts.IsSetupRequiredAsync(ct);
        return new
        {
            setupRequired,
            setupTokenRequired = setupRequired && SetupToken(configuration) is not null,
            user = account is null ? null : UserDtos.Me(account),
        };
    }

    public sealed record SetupRequest(string Username, string Password, string? SetupToken = null);

    public const string SetupTokenVariable = "LINKARR_SETUP_TOKEN";

    private static string? SetupToken(IConfiguration configuration) => configuration[SetupTokenVariable]?.Trim() is { Length: > 0 } token ? token : null;

    private static async Task<IResult> SetupAsync(
        SetupRequest request, HttpContext context, AccountService accounts, UserSync sync, OwnerProvisioning owner, IConfiguration configuration, CancellationToken ct)
    {
        if (!IsTrustedSetupHost(context.Request, configuration))
        {
            return Results.BadRequest(new { error = "Open linkarr by its IP address or localhost to create the first account, or list this host name in AllowedHosts." });
        }

        // With LINKARR_SETUP_TOKEN set, only whoever has the deployment's token can claim the first admin account.
        if (SetupToken(configuration) is string expected && !Catalog.CatalogEndpoints.TokenMatches(expected, request.SetupToken?.Trim()))
        {
            return Results.Json(new { error = "Wrong setup token: it is the LINKARR_SETUP_TOKEN value of this deployment." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (await accounts.SetupAsync(request.Username, request.Password, ct) is not Account account)
        {
            return Results.Conflict(new { error = "Setup is already done." });
        }

        // The password is known only now: the owner becomes admin in both apps (configured by the deployment), retried for a
        // short while in the background when an app is still starting.
        if (!await sync.ProvisionOwnerAsync(account, request.Password, ct))
        {
            owner.Retry(account.Id, request.Password);
        }

        await SignInAsync(context, accounts, account, ct);
        return Results.Ok(UserDtos.Me(account));
    }

    private static async Task<IResult> LoginAsync(Credentials request, HttpContext context, AccountService accounts, CancellationToken ct)
    {
        if (await accounts.AuthenticateAsync(request.Username, request.Password, ct) is not Account account)
        {
            return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        await SignInAsync(context, accounts, account, ct);
        return Results.Ok(UserDtos.Me(account));
    }

    internal static IResult SignedOutEverywhere(string? error) => error is null
        ? Results.NoContent()
        : Results.Json(new { error = $"Signed out of linkarr, but not of {error}" }, statusCode: StatusCodes.Status502BadGateway);

    // A new session (recording where it signs in from) and its cookie.
    internal static async Task SignInAsync(HttpContext context, AccountService accounts, Account account, CancellationToken ct)
    {
        string session = await accounts.StartSessionAsync(account, ClientIp(context), context.Request.Headers.UserAgent.ToString(), ct);
        await SignInAsync(context, account, session);
    }

    // After UseForwardedHeaders, so behind a trusted proxy this is the browser's address, not the proxy's.
    internal static string? ClientIp(HttpContext context) => context.Connection.RemoteIpAddress is IPAddress address
        ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
        : null;

    private static Task SignInAsync(HttpContext context, Account account, string session) => context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(SessionClaim, session),
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, account.IsAdmin ? AdminRole : UserRole),
            new Claim(StampClaim, Stamp(account)),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme)));

    // A cookie dies with its account, a password change, an admin change, disabling, or its own logout.
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        AccountService accounts = context.HttpContext.RequestServices.GetRequiredService<AccountService>();
        Account? account = context.Principal is ClaimsPrincipal principal ? await accounts.FindAsync(UserId(principal), context.HttpContext.RequestAborted) : null;
        string? session = context.Principal?.FindFirstValue(SessionClaim);
        if (account is null || account.Disabled || context.Principal?.FindFirstValue(StampClaim) != Stamp(account) || session is null
            || !await accounts.IsSessionOpenAsync(session, account.Id, ClientIp(context.HttpContext), context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    }

    internal static string Stamp(Account account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.Username}|{account.IsAdmin}|{account.Disabled}|{account.PasswordHash}")))[..32];

    // One bucket per IPv4 address but per IPv6 /64: a single host owns a whole /64 and would otherwise get unlimited buckets.
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    internal static int UserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out int id) ? id : 0;

    private static Task WriteError(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { error = message });
    }
}
