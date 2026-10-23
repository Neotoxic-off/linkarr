using System.Security.Cryptography;
using Linkarr.Api.Apps;
using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Users;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Sync;

// Provisions accounts to both apps, reconciles all of them (every 10 min and on demand) and lists the app users nobody manages.
public sealed class UserSync(LinkarrDbContext db, Connections connections, AccountService accounts, SyncState state, IConfiguration configuration)
{
    public const string Visularr = "visularr";
    public const string Modularr = "modularr";

    public async Task ProvisionAsync(Account account, string? password, CancellationToken ct)
    {
        await UserProvisioner.SyncAsync(account, password, await connections.ModularrAsync(ct), await connections.VisularrAsync(ct), ct);
        await db.SaveChangesAsync(ct);
    }

    // The owner right after setup: both apps get it as admin with its password, then visularr's bootstrap admin goes.
    // True when nothing is left to retry with this password.
    public async Task<bool> ProvisionOwnerAsync(Account account, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        await state.UserLock.WaitAsync(ct);
        try
        {
            await ProvisionAsync(account, password, ct);
            if (account.VisularrId is not null && await connections.VisularrAsync(ct) is VisularrClient visularr)
            {
                try
                {
                    await UserProvisioner.RemoveVisularrBootstrapAsync(await visularr.UsersAsync(ct), [account], visularr, ct);
                }
                catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
                {
                    // The next reconcile removes it.
                }
            }

            return account.SyncStatus == SyncStatus.Synced && !account.PasswordPending;
        }
        finally
        {
            state.UserLock.Release();
        }
    }

    // Null when both apps removed the account (or are not configured); otherwise why not, and the account stays.
    public async Task<string?> RemoveAsync(Account account, CancellationToken ct)
    {
        string? error = await UserProvisioner.RemoveAsync(account, await connections.ModularrAsync(ct), await connections.VisularrAsync(ct), ct);
        await db.SaveChangesAsync(ct);
        return error;
    }

    // Ends the account's linkarr sessions and signs it out of visularr (Jellyfin's devices). modularr's provisioning API has no
    // call that ends sessions short of a new password or disabling, so modularr sessions stay. Null, or why visularr failed.
    public async Task<string?> SignOutEverywhereAsync(Account account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        await accounts.EndSessionsAsync(account.Id, ct);
        try
        {
            if (account.VisularrId is string visularrId && await connections.VisularrAsync(ct) is VisularrClient visularr)
            {
                await visularr.SignOutUserAsync(visularrId, ct);
            }

            return null;
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            return $"{Visularr}: {AppHttp.Describe(ex)}";
        }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        await state.UserLock.WaitAsync(ct);
        try
        {
            AppSettings settings = await connections.SettingsAsync(ct);
            ModularrClient? modularr = await connections.ModularrAsync(ct);
            VisularrClient? visularr = await connections.VisularrAsync(ct);
            List<string> errors = [];
            List<UnmanagedUser> unmanaged = [];

            // Idempotent: both apps hide their own user management and link here; modularr also gets the webhook token.
            if (settings.LinkarrPublicUrl is string url)
            {
                await TryAsync(Modularr, errors, async () => { if (modularr is not null) { await modularr.SetManagedAsync(url, settings.WebhookToken, configuration["Linkarr:InternalUrl"], ct); } }, ct);
                await TryAsync(Visularr, errors, async () => { if (visularr is not null) { await visularr.SetManagedAsync(url, ct); } }, ct);
            }

            List<Account> all = await db.Accounts.ToListAsync(ct);
            foreach (Account account in all)
            {
                await UserProvisioner.SyncAsync(account, password: null, modularr, visularr, ct);
            }

            HashSet<string> visularrIds = [.. all.Select(a => a.VisularrId).OfType<string>()];
            HashSet<string> externalIds = [.. all.Select(a => a.ExternalId)];
            await TryAsync(Visularr, errors, async () =>
            {
                if (visularr is not null)
                {
                    IReadOnlyList<JellyfinUser> users = await visularr.UsersAsync(ct);
                    await UserProvisioner.RemoveVisularrBootstrapAsync(users, all, visularr, ct);
                    unmanaged.AddRange(users.Where(u => !visularrIds.Contains(u.Id) && !UserProvisioner.IsVisularrBootstrap(u))
                        .Select(u => new UnmanagedUser(Visularr, u.Id, u.Name, u.Policy?.IsAdministrator == true, Linkable: true)));
                }
            }, ct);
            await TryAsync(Modularr, errors, async () =>
            {
                if (modularr is not null)
                {
                    unmanaged.AddRange((await modularr.UsersAsync(ct)).Where(u => u.ExternalId is null || !externalIds.Contains(u.ExternalId))
                        .Select(u => new UnmanagedUser(Modularr, u.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), u.Username,
                            u.Role == RightsMapping.ModularrAdmin, Linkable: u.ExternalId is not null)));
                }
            }, ct);

            settings.LastUserSync = DateTime.UtcNow;
            settings.UserSyncError = errors.Count == 0 ? null : string.Join(" · ", errors);
            await db.SaveChangesAsync(ct);
            state.Unmanaged = unmanaged;
        }
        finally
        {
            state.UserLock.Release();
        }
    }

    // Adopting makes (or reuses) the linkarr account of that name with access to the app and links exactly this app user (the
    // only way an existing app user is ever linked). Its password is unknown until an admin resets it.
    public async Task<Account> AdoptAsync(string app, string remoteId, CancellationToken ct)
    {
        UnmanagedUser user = Find(app, remoteId);
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Username == user.Name, ct);
        // An app user is linked to one account only, and an account to one user per app.
        int? modularrId = app == Visularr ? null : int.Parse(remoteId, System.Globalization.CultureInfo.InvariantCulture);
        if (app == Visularr ? await db.Accounts.AnyAsync(a => a.VisularrId == remoteId, ct) : await db.Accounts.AnyAsync(a => a.ModularrId == modularrId, ct))
        {
            throw new ArgumentException($"That {app} user is already linked to a linkarr account.", nameof(remoteId));
        }

        if (account is not null && (app == Visularr ? account.VisularrId is not null : account.ModularrId is not null))
        {
            throw new ArgumentException($"{account.Username} is already linked to another {app} user.", nameof(remoteId));
        }

        if (account is null)
        {
            Rights rights = app == Visularr
                ? new Rights(Admin: user.Admin, Watch: true)
                : new Rights(Watch: false, ManageDownloads: user.Admin, ModularrAccess: true);
            account = await accounts.CreateAsync(user.Name, displayName: null, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), rights, ct);
            account.PasswordPending = true;
        }
        else if (app == Visularr)
        {
            account.Watch = true;
        }
        else
        {
            account.ModularrAccess = true;
        }

        // Linked by the adopted user's id (visularr) or by modularr's explicit adopt flag, never by provisioning matching a name.
        // Its password stays the app's own until an admin sets one.
        account.PasswordPending = true;
        if (app == Visularr)
        {
            account.VisularrId = remoteId;
        }
        else
        {
            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            account.ModularrId = await modularr.UpsertUserAsync(account.ExternalId,
                new ProvisionUserBody(account.Username, RightsMapping.ModularrRole(account), Password: null, account.Disabled, Adopt: true), ct);
        }

        await ProvisionAsync(account, password: null, ct);
        state.Unmanaged = [.. state.Unmanaged.Where(u => u != user)];
        return account;
    }

    // visularr users by id; modularr only those linked to an account linkarr no longer has (the provisioning API deletes by externalId).
    public async Task DeleteUnmanagedAsync(string app, string remoteId, CancellationToken ct)
    {
        UnmanagedUser user = Find(app, remoteId);
        if (app == Visularr)
        {
            VisularrClient visularr = await connections.VisularrAsync(ct) ?? throw new InvalidOperationException("visularr is not configured.");
            await visularr.DeleteUserAsync(remoteId, ct);
        }
        else
        {
            ModularrClient modularr = await connections.ModularrAsync(ct) ?? throw new InvalidOperationException("modularr is not configured.");
            string externalId = (await modularr.UsersAsync(ct)).FirstOrDefault(u => u.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == remoteId)?.ExternalId
                ?? throw new ArgumentException("This modularr user was not created by linkarr: adopt it, or delete it in modularr.", nameof(remoteId));
            await modularr.DeleteUserAsync(externalId, ct);
        }

        state.Unmanaged = [.. state.Unmanaged.Where(u => u != user)];
    }

    private UnmanagedUser Find(string app, string remoteId) =>
        state.Unmanaged.FirstOrDefault(u => u.App == app && u.RemoteId == remoteId)
        ?? throw new KeyNotFoundException("That app user is not in the last sync; run Sync now.");

    private static async Task TryAsync(string app, List<string> errors, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            errors.Add($"{app}: {AppHttp.Describe(ex)}");
        }
    }
}
