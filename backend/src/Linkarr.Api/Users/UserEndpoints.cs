using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Users;

// Admin only: linkarr's users are the source of truth, every change is provisioned to visularr and modularr.
public static class UserEndpoints
{
    public sealed record CreateUserRequest(string Username, string? DisplayName, string Password, Rights? Rights);

    public sealed record UpdateUserRequest(string? DisplayName, Rights? Rights, bool? Disabled, string? Username = null);

    public sealed record PasswordRequest(string Password);

    public sealed record SyncRequest(string? Password);

    public sealed record RemoteUserRequest(string App, string RemoteId);

    public static RouteGroupBuilder MapUserApi(this RouteGroupBuilder api)
    {
        RouteGroupBuilder users = api.MapGroup("/users").RequireAuthorization(AuthEndpoints.AdminPolicy);
        users.MapGet("/", async (LinkarrDbContext db, CancellationToken ct) =>
            (await db.Accounts.AsNoTracking().OrderBy(a => a.Username).ToListAsync(ct)).Select(UserDtos.Full));

        users.MapPost("/", async (CreateUserRequest request, AccountService accounts, UserSync sync, OwnerProvisioning retry, CancellationToken ct) =>
        {
            Account account = await accounts.CreateAsync(request.Username, request.DisplayName, request.Password, request.Rights ?? new Rights(), ct);
            await sync.ProvisionAsync(account, request.Password, ct);
            RetryIfMissed(account, request.Password, retry);
            return UserDtos.Full(account);
        });

        users.MapPatch("/{id:int}", async (int id, UpdateUserRequest request, HttpContext context, AccountService accounts, UserSync sync, CancellationToken ct) =>
        {
            Account account = await accounts.GetAsync(id, ct);
            bool demoting = request.Rights is { Admin: false } && account.IsAdmin;
            bool disabling = request.Disabled == true && !account.Disabled;
            if (demoting || disabling)
            {
                if (id == AuthEndpoints.UserId(context.User))
                {
                    throw new ArgumentException(disabling ? "You cannot disable your own account." : "You cannot remove your own admin right.", nameof(request));
                }

                await accounts.EnsureNotLastAdminAsync(account, disabling ? "disabled" : "demoted", ct);
            }

            if (request.DisplayName is not null)
            {
                account.DisplayName = AccountService.CleanDisplayName(request.DisplayName);
            }

            if (request.Rights is Rights rights)
            {
                account.Rights = rights;
            }

            if (request.Disabled is bool disabled)
            {
                account.Disabled = disabled;
            }

            string previousName = account.Username;
            if (request.Username is not null)
            {
                await accounts.RenameAsync(account, request.Username.Trim(), ct);
            }

            // modularr renames by its externalId, visularr through its user object.
            await sync.ProvisionAsync(account, password: null, ct);
            if (account.Username != previousName && id == AuthEndpoints.UserId(context.User))
            {
                // The new name ended this admin's own cookie: sign this browser in again.
                await AuthEndpoints.SignInAsync(context, accounts, account, ct);
            }

            return UserDtos.Full(account);
        });

        // Every linkarr session of the user ends, and its visularr devices are signed out.
        users.MapPost("/{id:int}/signout", async (int id, AccountService accounts, UserSync sync, CancellationToken ct) =>
            AuthEndpoints.SignedOutEverywhere(await sync.SignOutEverywhereAsync(await accounts.GetAsync(id, ct), ct)));

        // Admin only: where the user is signed in to linkarr, and ending one of those sessions.
        users.MapGet("/{id:int}/sessions", async (int id, HttpContext context, AccountService accounts, CancellationToken ct) =>
            (await accounts.SessionsAsync((await accounts.GetAsync(id, ct)).Id, ct)).Select(s => SessionDtos.Dto(s, context.User.FindFirstValue(AuthEndpoints.SessionClaim))));
        users.MapPost("/{id:int}/sessions/signout", async (int id, AuthEndpoints.SessionRequest request, AccountService accounts, CancellationToken ct) =>
            await accounts.EndSessionByPublicIdAsync(id, request.Id, ct) ? Results.NoContent() : throw new KeyNotFoundException("That session has already ended."));

        // An admin reset: linkarr, visularr and modularr get the new password; the user's sessions end.
        users.MapPost("/{id:int}/password", async (int id, PasswordRequest request, AccountService accounts, UserSync sync, OwnerProvisioning retry, CancellationToken ct) =>
        {
            Account account = await accounts.GetAsync(id, ct);
            await accounts.SetPasswordAsync(account, request.Password, ct);
            await sync.ProvisionAsync(account, request.Password, ct);
            RetryIfMissed(account, request.Password, retry);
            return UserDtos.Full(account);
        });

        // Retry now; a password the apps missed is re-entered here (it must be the account's linkarr password).
        users.MapPost("/{id:int}/sync", async (int id, SyncRequest request, AccountService accounts, UserSync sync, CancellationToken ct) =>
        {
            Account account = await accounts.GetAsync(id, ct);
            if (request.Password is not null && !AccountService.VerifyPassword(account, request.Password))
            {
                throw new ArgumentException("That is not this user's linkarr password; reset it instead.", nameof(request));
            }

            await sync.ProvisionAsync(account, request.Password, ct);
            return UserDtos.Full(account);
        });

        // Removed from both apps first: if one is down the user stays, so nothing is left behind unmanaged.
        users.MapDelete("/{id:int}", async (int id, HttpContext context, AccountService accounts, UserSync sync, LinkarrDbContext db, CancellationToken ct) =>
        {
            if (id == AuthEndpoints.UserId(context.User))
            {
                throw new ArgumentException("You cannot delete your own account.", nameof(id));
            }

            Account account = await accounts.GetAsync(id, ct);
            await accounts.EnsureNotLastAdminAsync(account, "deleted", ct);
            if (await sync.RemoveAsync(account, ct) is string error)
            {
                return Results.Json(new { error = $"Not deleted, an app could not remove the user: {error}" }, statusCode: StatusCodes.Status502BadGateway);
            }

            db.Accounts.Remove(account);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        users.MapPost("/sync", async (UserSync sync, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            await sync.ReconcileAsync(ct);
            return new { users = (await db.Accounts.AsNoTracking().OrderBy(a => a.Username).ToListAsync(ct)).Select(UserDtos.Full), unmanaged = state.Unmanaged };
        });
        users.MapGet("/unmanaged", (SyncState state) => state.Unmanaged);
        users.MapPost("/unmanaged/adopt", async (RemoteUserRequest request, UserSync sync, CancellationToken ct) =>
            UserDtos.Full(await sync.AdoptAsync(request.App, request.RemoteId, ct)));
        users.MapPost("/unmanaged/delete", async (RemoteUserRequest request, UserSync sync, CancellationToken ct) =>
        {
            await sync.DeleteUnmanagedAsync(request.App, request.RemoteId, ct);
            return Results.NoContent();
        });

        // The visularr libraries a user can be given.
        api.MapGet("/libraries", async (Connections connections, CancellationToken ct) =>
            await connections.VisularrAsync(ct) is VisularrClient visularr ? await visularr.LibrariesAsync(ct) : [])
            .RequireAuthorization(AuthEndpoints.AdminPolicy);
        return api;
    }

    // An app still starting (the stack comes up together) missed the password: retried for a short while, in memory only.
    private static void RetryIfMissed(Account account, string password, OwnerProvisioning retry)
    {
        if (account.SyncStatus != SyncStatus.Synced || account.PasswordPending)
        {
            retry.Retry(account.Id, password);
        }
    }
}
