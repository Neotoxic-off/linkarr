using Linkarr.Api.Data;

namespace Linkarr.Api.Users;

public static class UserDtos
{
    // The signed-in user's own view.
    public static object Me(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new
        {
            account.Id,
            account.Username,
            account.DisplayName,
            admin = account.IsAdmin,
            rights = account.Rights,
            account.PasswordPending,
            account.Created,
            avatar = Avatar(account),
        };
    }

    // An admin's view: rights and the provisioning state in both apps.
    public static object Full(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new
        {
            account.Id,
            account.Username,
            account.DisplayName,
            account.Disabled,
            rights = account.Rights,
            account.Created,
            avatar = Avatar(account),
            account.VisularrId,
            account.ModularrId,
            account.SyncStatus,
            account.SyncError,
            account.LastSynced,
            account.PasswordPending,
            account.LastSignIn,
        };
    }

    private static string? Avatar(Account account) =>
        account.AvatarUpdated is DateTime updated ? FormattableString.Invariant($"/api/avatars/{account.Id}?v={updated.Ticks}") : null;
}
