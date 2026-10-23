using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Linkarr.Api.Data;
using Group = Linkarr.Api.Data.Group;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Auth;

// linkarr's own accounts: the first one is created by setup as admin, the others by an admin. Only password hashes are stored.
public sealed partial class AccountService(LinkarrDbContext db)
{
    public const int MinPasswordLength = 8;
    public const int MaxAvatarBytes = 512 * 1024;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private static readonly PasswordHasher<Account> Hasher = new();

    // Setup is check-then-insert: one at a time, or concurrent anonymous requests would each create an admin.
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    // Verified against for an unknown username, so a wrong name costs the same time as a wrong password.
    private static readonly string DummyHash = Hasher.HashPassword(new Account { Username = "-", PasswordHash = "" }, "not a password");

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct) => !await db.Accounts.AnyAsync(ct);

    public async Task<Account?> FindAsync(int id, CancellationToken ct) => await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<Account> GetAsync(int id, CancellationToken ct) =>
        await FindAsync(id, ct) ?? throw new KeyNotFoundException($"User {id} does not exist.");

    // Null once any account exists: setup only ever creates the first admin.
    public async Task<Account?> SetupAsync(string username, string password, CancellationToken ct)
    {
        await SetupLock.WaitAsync(ct);
        try
        {
            return await IsSetupRequiredAsync(ct)
                ? await CreateAsync(username, displayName: null, password, Role.AdminId, groupIds: null, ct)
                : null;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    // Saved before provisioning, which then records the remote ids and the sync status on it.
    // No role given: the default role of the first group that has one, else Member.
    public async Task<Account> CreateAsync(string username, string? displayName, string password, int? roleId, IReadOnlyCollection<int>? groupIds, CancellationToken ct)
    {
        await ValidateUsernameAsync(username, ct);
        ValidatePassword(password);
        List<Group> groups = await GroupsAsync(groupIds, ct);
        Role role = await RoleAsync(roleId ?? groups.Select(g => g.DefaultRoleId).FirstOrDefault(id => id is not null) ?? Role.MemberId, ct);
        if (roleId is null && role.Admin)
        {
            // A group never makes an admin, even if its default role was turned into one since.
            role = await RoleAsync(Role.MemberId, ct);
        }

        Account account = new() { Username = username, DisplayName = CleanDisplayName(displayName), PasswordHash = "", Created = DateTime.UtcNow, Role = role, Groups = groups };
        account.PasswordHash = Hasher.HashPassword(account, password);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Role> RoleAsync(int id, CancellationToken ct) =>
        await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new ArgumentException($"Role {id} does not exist.", nameof(id));

    // The first role (built-ins first) with exactly these permissions, or a new "Custom: <username>" one: an adopted user keeps
    // what it had in its app without any existing role changing.
    public async Task<Role> RoleForAsync(Rights rights, string username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rights);
        List<Role> roles = await db.Roles.OrderBy(r => r.Id).ToListAsync(ct);
        if (roles.FirstOrDefault(r => SamePermissions(r.Rights, rights)) is Role existing)
        {
            return existing;
        }

        string name = $"Custom: {username}";
        for (int n = 2; roles.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
        {
            name = FormattableString.Invariant($"Custom: {username} ({n})");
        }

        Role role = new() { Name = name, Rights = rights };
        db.Roles.Add(role);
        return role;
    }

    public static bool SamePermissions(Rights a, Rights b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a with { Libraries = null } == b with { Libraries = null }
            && Role.CleanLibraries(a.Libraries).Order(StringComparer.Ordinal).SequenceEqual(Role.CleanLibraries(b.Libraries).Order(StringComparer.Ordinal));
    }

    // Every id must exist: a typo never silently drops a group (and its libraries).
    public async Task<List<Group>> GroupsAsync(IReadOnlyCollection<int>? ids, CancellationToken ct)
    {
        List<Group> groups = ids is null or { Count: 0 } ? [] : await db.Groups.Where(g => ids.Contains(g.Id)).ToListAsync(ct);
        if (ids is not null && groups.Count != ids.Distinct().Count())
        {
            throw new ArgumentException("A group does not exist.", nameof(ids));
        }

        return groups;
    }

    // Null for an unknown username, a wrong password or a disabled account.
    public async Task<Account?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Username == username, ct);
        if (account is null || password is null)
        {
            Hasher.VerifyHashedPassword(new Account { Username = "-", PasswordHash = DummyHash }, DummyHash, password ?? "");
            return null;
        }

        PasswordVerificationResult result = Hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed || account.Disabled)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = Hasher.HashPassword(account, password);
            await db.SaveChangesAsync(ct);
        }

        return account;
    }

    public static bool VerifyPassword(Account account, string? password) =>
        password is not null && Hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;

    // A new password ends every session of the account; the caller signs the current browser in again.
    public async Task SetPasswordAsync(Account account, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidatePassword(password);
        account.PasswordHash = Hasher.HashPassword(account, password);
        await db.Sessions.Where(s => s.AccountId == account.Id).ExecuteDeleteAsync(ct);
    }

    // Saved by the caller with the provisioning that renames the app users. The new name ends the account's cookies (in its stamp).
    public async Task RenameAsync(Account account, string username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (username != account.Username)
        {
            await ValidateUsernameAsync(username, ct);
            account.Username = username;
        }
    }

    // The last enabled admin can be neither demoted, disabled nor deleted: linkarr would have nobody to manage it.
    public async Task EnsureNotLastAdminAsync(Account account, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsAdmin && !account.Disabled && await db.Accounts.CountAsync(a => a.Role.Admin && !a.Disabled, ct) == 1)
        {
            throw new ArgumentException($"The last admin cannot be {action}.", nameof(account));
        }
    }

    public static string? CleanDisplayName(string? displayName)
    {
        string? trimmed = displayName?.Trim();
        if (trimmed is { Length: > 64 })
        {
            throw new ArgumentException("The display name must be at most 64 characters.", nameof(displayName));
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // Only raster formats recognised by their first bytes: never SVG or HTML, which the browser would run as this origin.
    public static string? AvatarType(ReadOnlySpan<byte> image) =>
        image.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : image.StartsWith("GIF87a"u8) || image.StartsWith("GIF89a"u8) ? "image/gif"
        : image.Length >= 12 && image.StartsWith("RIFF"u8) && image[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public async Task<Account> SetAvatarAsync(int id, byte[] image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length > MaxAvatarBytes)
        {
            throw new ArgumentException($"The picture must be at most {MaxAvatarBytes / 1024} KB.", nameof(image));
        }

        string type = AvatarType(image) ?? throw new ArgumentException("The picture must be a PNG, JPEG, GIF or WebP image.", nameof(image));
        Account account = await GetAsync(id, ct);
        AccountAvatar? avatar = await db.Avatars.FirstOrDefaultAsync(a => a.AccountId == id, ct);
        if (avatar is null)
        {
            db.Avatars.Add(new AccountAvatar { AccountId = id, Content = image, ContentType = type });
        }
        else
        {
            avatar.Content = image;
            avatar.ContentType = type;
        }

        account.AvatarUpdated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> RemoveAvatarAsync(int id, CancellationToken ct)
    {
        Account account = await GetAsync(id, ct);
        await db.Avatars.Where(a => a.AccountId == id).ExecuteDeleteAsync(ct);
        account.AvatarUpdated = null;
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<AccountAvatar?> FindAvatarAsync(int id, CancellationToken ct) =>
        await db.Avatars.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == id, ct);

    // A new signed-in browser, with its address and user agent for the sessions lists; sessions idle for SessionLifetime are dropped on the way.
    public async Task<string> StartSessionAsync(Account account, string? ip, string? userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        DateTime now = DateTime.UtcNow;
        await db.Sessions.Where(s => s.LastSeen < now - SessionLifetime).ExecuteDeleteAsync(ct);
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.Sessions.Add(new Session { Id = id, AccountId = account.Id, Created = now, LastSeen = now, IpAddress = ip, LastIp = ip, UserAgent = Clip(userAgent, MaxUserAgentLength) });
        account.LastSignIn = now;
        await db.SaveChangesAsync(ct);
        return id;
    }

    // Written at most once a minute per session, or when its address changes, so a page full of requests costs no writes.
    public async Task<bool> IsSessionOpenAsync(string sessionId, int accountId, string? ip, CancellationToken ct)
    {
        Session? session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.AccountId == accountId, ct);
        if (session is null)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (session.LastSeen < now.AddMinutes(-1) || (ip is not null && ip != session.LastIp))
        {
            session.LastSeen = now;
            session.LastIp = ip ?? session.LastIp;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<List<Session>> SessionsAsync(int accountId, CancellationToken ct) =>
        await db.Sessions.AsNoTracking().Where(s => s.AccountId == accountId).OrderByDescending(s => s.LastSeen).ToListAsync(ct);

    // By the id the UI shows (SessionDtos.PublicId), never the secret one; false when the account has no such session.
    public async Task<bool> EndSessionByPublicIdAsync(int accountId, string publicId, CancellationToken ct)
    {
        Session? session = (await SessionsAsync(accountId, ct)).FirstOrDefault(s => SessionDtos.PublicId(s.Id) == publicId);
        if (session is null)
        {
            return false;
        }

        await EndSessionAsync(session.Id, ct);
        return true;
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken ct) =>
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);

    public async Task EndSessionsAsync(int accountId, CancellationToken ct) =>
        await db.Sessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(ct);

    private async Task ValidateUsernameAsync(string username, CancellationToken ct)
    {
        if (username is null || !UsernamePattern().IsMatch(username))
        {
            throw new ArgumentException("Username must be 3 to 32 characters among letters, digits, '.', '_' and '-'.", nameof(username));
        }

        // visularr's bootstrap admin goes by that name: a linkarr user of the same name would be linked to it.
        if (string.Equals(username, Users.UserProvisioner.VisularrBootstrap, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Username '{username}' is reserved.", nameof(username));
        }

        if (await db.Accounts.AnyAsync(a => a.Username == username, ct))
        {
            throw new ArgumentException($"Username '{username}' is already taken.", nameof(username));
        }
    }

    private const int MaxUserAgentLength = 400;

    private static string? Clip(string? text, int length) => string.IsNullOrWhiteSpace(text) ? null : text.Length > length ? text[..length] : text;

    public static void ValidatePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.", nameof(password));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UsernamePattern();
}
