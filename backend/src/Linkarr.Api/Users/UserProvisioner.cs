using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Linkarr.Api.Apps;
using Linkarr.Api.Data;

namespace Linkarr.Api.Users;

// How linkarr rights become each app's own model.
public static class RightsMapping
{
    public const string ModularrAdmin = "Admin";
    public const string ModularrUser = "User";

    // An admin has everything, so an account in both apps.
    public static bool HasVisularrAccount(Account account) => account.IsAdmin || account.Watch;

    public static bool HasModularrAccount(Account account) => account.IsAdmin || account.ModularrAccess;

    public static string ModularrRole(Account account) => account.IsAdmin || account.ManageDownloads ? ModularrAdmin : ModularrUser;

    // Only the fields linkarr owns change; the rest of Jellyfin's policy is kept as it is. A disabled account is never a
    // Jellyfin administrator (Jellyfin refuses to disable one).
    public static JsonObject ApplyPolicy(JsonObject policy, Account account)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(account);
        bool allLibraries = account.IsAdmin || account.AllLibraries;
        policy["IsAdministrator"] = account.IsAdmin && !account.Disabled;
        policy["IsDisabled"] = account.Disabled;
        policy["EnableMediaPlayback"] = account.IsAdmin || account.Watch;
        policy["EnableAllFolders"] = allLibraries;
        policy["EnabledFolders"] = new JsonArray([.. (allLibraries ? [] : account.Libraries).Select(l => JsonValue.Create(l))]);
        return policy;
    }
}

// Brings visularr and modularr in line with one account. The password is given only when the user just chose it: linkarr
// never stores it. An unreachable app never throws here: the account records the error and the next reconcile retries.
public static class UserProvisioner
{
    public static async Task SyncAsync(Account account, string? password, ModularrClient? modularr, VisularrClient? visularr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        List<string> errors = [];
        bool passwordMissed = false;
        foreach ((string app, Func<Task>? sync) in new (string, Func<Task>?)[]
        {
            ("visularr", visularr is null ? null : () => SyncVisularrAsync(account, password, visularr, ct)),
            ("modularr", modularr is null ? null : () => SyncModularrAsync(account, password, modularr, ct)),
        })
        {
            if (sync is null)
            {
                continue;
            }

            try
            {
                await sync();
            }
            catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
            {
                errors.Add($"{app}: {AppHttp.Describe(ex)}");
                passwordMissed |= password is not null;
            }
        }

        account.SyncError = errors.Count == 0 ? null : string.Join(" · ", errors);
        account.SyncStatus = errors.Count == 0 ? SyncStatus.Synced : SyncStatus.OutOfSync;
        account.LastSynced = DateTime.UtcNow;
        if (password is not null)
        {
            account.PasswordPending = passwordMissed;
        }
    }

    // Removes the account from both apps; the error of the first app that failed, null when both are done.
    public static async Task<string?> RemoveAsync(Account account, ModularrClient? modularr, VisularrClient? visularr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            if (visularr is not null && account.VisularrId is string visularrId)
            {
                await visularr.DeleteUserAsync(visularrId, ct);
                account.VisularrId = null;
            }

            // Always, by externalId (idempotent): an upsert modularr committed but whose answer was lost left no id here.
            if (modularr is not null)
            {
                await modularr.DeleteUserAsync(account.ExternalId, ct);
                account.ModularrId = null;
            }

            return null;
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            return AppHttp.Describe(ex);
        }
    }

    private static async Task SyncVisularrAsync(Account account, string? password, VisularrClient visularr, CancellationToken ct)
    {
        if (!RightsMapping.HasVisularrAccount(account))
        {
            if (account.VisularrId is string id)
            {
                await visularr.DeleteUserAsync(id, ct);
                account.VisularrId = null;
            }

            return;
        }

        JsonObject? user = account.VisularrId is string known ? await visularr.UserAsync(known, ct) : null;
        bool created = false;
        if (user is null)
        {
            // Not linked yet, or deleted in visularr since: create one. Never linked by name (a name proves nothing about who
            // owns that user): one of the same name is left alone until an admin adopts it (UserSync.AdoptAsync, by its id).
            account.VisularrId = null;
            if ((await visularr.UsersAsync(ct)).Any(u => string.Equals(u.Name, account.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"a visularr user named {account.Username} already exists: adopt or delete it in Unmanaged users.");
            }

            account.VisularrId = await visularr.CreateUserAsync(account.Username, password ?? RandomPassword(), ct);
            created = true;
            user = await visularr.UserAsync(account.VisularrId, ct) ?? throw new HttpRequestException("visularr lost the user it just returned.");
            account.PasswordPending |= password is null;
        }

        string userId = account.VisularrId!;
        // A username renamed in linkarr.
        if (user["Name"]?.GetValue<string>() is string name && !string.Equals(name, account.Username, StringComparison.Ordinal))
        {
            await visularr.RenameUserAsync(userId, user, account.Username, ct);
        }

        JsonObject policy = VisularrClient.Policy(user);
        bool wasAdmin = policy["IsAdministrator"]?.GetValue<bool>() == true;
        RightsMapping.ApplyPolicy(policy, account);
        if (wasAdmin && account.Disabled)
        {
            // Jellyfin refuses to disable an administrator: demote first, then disable.
            JsonObject demoted = (JsonObject)policy.DeepClone();
            demoted["IsDisabled"] = false;
            await visularr.SetPolicyAsync(userId, demoted, ct);
        }

        await visularr.SetPolicyAsync(userId, policy, ct);
        if (password is not null && !created)
        {
            await visularr.SetPasswordAsync(userId, password, ct);
        }
    }

    private static async Task SyncModularrAsync(Account account, string? password, ModularrClient modularr, CancellationToken ct)
    {
        if (!RightsMapping.HasModularrAccount(account))
        {
            // No id recorded may still mean a user: an upsert modularr committed but whose answer was lost. Ask by externalId.
            account.ModularrId ??= (await modularr.UsersAsync(ct)).FirstOrDefault(u => u.ExternalId == account.ExternalId)?.Id;
            if (account.ModularrId is not null)
            {
                await modularr.DeleteUserAsync(account.ExternalId, ct);
                account.ModularrId = null;
            }

            return;
        }

        // Keyed by externalId on modularr's side: a committed upsert whose answer was lost is found again here, never duplicated.
        bool creating = account.ModularrId is null;
        account.ModularrId = await modularr.UpsertUserAsync(
            account.ExternalId, new ProvisionUserBody(account.Username, RightsMapping.ModularrRole(account), password, account.Disabled), ct);
        account.PasswordPending |= creating && password is null;
    }

    // visularr's headless wizard leaves this administrator, with a password nobody knows. linkarr neither manages nor lists it.
    public const string VisularrBootstrap = "visularr-bootstrap";

    public static bool IsVisularrBootstrap(JellyfinUser user) =>
        string.Equals(user?.Name, VisularrBootstrap, StringComparison.OrdinalIgnoreCase);

    // Deletes the bootstrap admin once an enabled linkarr admin is a visularr administrator (Jellyfin refuses to delete its
    // last admin). users: visularr's list as just read. True when it was deleted.
    public static async Task<bool> RemoveVisularrBootstrapAsync(IReadOnlyList<JellyfinUser> users, IEnumerable<Account> accounts, VisularrClient visularr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(visularr);
        HashSet<string> visularrAdmins = [.. users.Where(u => u.Policy?.IsAdministrator == true && !IsVisularrBootstrap(u)).Select(u => u.Id)];
        if (users.FirstOrDefault(IsVisularrBootstrap) is not JellyfinUser bootstrap
            || !accounts.Any(a => a.IsAdmin && !a.Disabled && a.VisularrId is string id && visularrAdmins.Contains(id)))
        {
            return false;
        }

        await visularr.DeleteUserAsync(bootstrap.Id, ct);
        return true;
    }

    // For an app account created before linkarr knows a password: nobody can sign in with it until one is set.
    private static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
