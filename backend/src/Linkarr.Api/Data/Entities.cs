namespace Linkarr.Api.Data;

// What a linkarr user may do; linkarr is the source of truth and provisions it to visularr and modularr.
// Libraries are visularr library ids, used only when AllLibraries is off.
public sealed record Rights(
    bool Admin = false,
    bool Watch = true,
    bool AllLibraries = true,
    IReadOnlyList<string>? Libraries = null,
    bool Request = true,
    bool ManageDownloads = false,
    bool ModularrAccess = false);

public enum SyncStatus
{
    Pending,
    Synced,
    OutOfSync,
}

public sealed class Account
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public string? DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public bool Disabled { get; set; }
    public DateTime Created { get; set; }
    public DateTime? AvatarUpdated { get; set; }
    public DateTime? LastSignIn { get; set; }

    public bool IsAdmin { get; set; }
    public bool Watch { get; set; }
    public bool AllLibraries { get; set; } = true;
    public List<string> Libraries { get; set; } = [];
    public bool Request { get; set; }
    public bool ManageDownloads { get; set; }
    public bool ModularrAccess { get; set; }

    // The id modularr links its user to: stable whatever happens to the username.
    public string ExternalId { get; set; } = Guid.NewGuid().ToString("N");
    public string? VisularrId { get; set; }
    public int? ModularrId { get; set; }
    public SyncStatus SyncStatus { get; set; }
    public string? SyncError { get; set; }
    public DateTime? LastSynced { get; set; }

    // An app could not receive the password linkarr last knew (down, or the account was created without one): it must be entered again.
    public bool PasswordPending { get; set; }

    public Rights Rights
    {
        get => new(IsAdmin, Watch, AllLibraries, [.. Libraries], Request, ManageDownloads, ModularrAccess);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            IsAdmin = value.Admin;
            Watch = value.Watch;
            AllLibraries = value.AllLibraries;
            Libraries = [.. (value.Libraries ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal)];
            Request = value.Request;
            ManageDownloads = value.ManageDownloads;
            ModularrAccess = value.ModularrAccess;
        }
    }
}

// Id is the secret the cookie carries: it never leaves the server (see SessionDtos for the id shown in the UI).
public sealed class Session
{
    public required string Id { get; set; }
    public int AccountId { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastSeen { get; set; }

    // Where the browser signed in from, and the last address seen since (they differ when a phone changes networks).
    public string? IpAddress { get; set; }
    public string? LastIp { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class AccountAvatar
{
    public int AccountId { get; set; }
    public required byte[] Content { get; set; }
    public required string ContentType { get; set; }
}

public enum TitleKind
{
    Movie,
    Series,
}

public enum CatalogState
{
    Available,
    Downloading,
    NotIndexed,
    Untracked,
    Missing,
    Unmonitored,
}

// One title of the merged catalog, rebuilt by every catalog sync. Key: "movie:tmdb:603", "series:tvdb:81189" or "visularr:<id>".
public sealed class CatalogItem
{
    public required string Key { get; set; }
    public TitleKind Kind { get; set; }
    public required string Title { get; set; }
    public int Year { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public CatalogState State { get; set; }
    public bool Monitored { get; set; }
    public bool HasFile { get; set; }
    public int? ModularrId { get; set; }
    public string? ModularrPath { get; set; }
    public string? VisularrId { get; set; }
    public double? Progress { get; set; }
    public string? Poster { get; set; }

    // When the title first became playable, carried over from sync to sync; null for what was already there before tracking began.
    public DateTime? AvailableSince { get; set; }
}

// One row per day, written by each catalog sync (the day's last one wins) and kept for SnapshotDays: the trends on the Stats page.
public sealed class CatalogSnapshot
{
    public const int SnapshotDays = 90;

    public DateOnly Date { get; set; }
    public int Movies { get; set; }
    public int Series { get; set; }
    public int Available { get; set; }
    public int Downloading { get; set; }
    public int NotIndexed { get; set; }
    public int Untracked { get; set; }
    public int Missing { get; set; }
    public int Unmonitored { get; set; }
}

public enum Approval
{
    Pending,
    Approved,
    Denied,
}

public sealed class TitleRequest
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public required string Username { get; set; }
    public TitleKind Kind { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public required string Title { get; set; }
    public int Year { get; set; }
    public string? Poster { get; set; }
    public Approval Approval { get; set; }
    // Why adding it to modularr failed; cleared by a successful retry.
    public string? Error { get; set; }
    public DateTime Created { get; set; }
}

// The single settings row (Id 1). API keys are stored encrypted with ASP.NET Data Protection (see Connections).
public sealed class AppSettings
{
    public int Id { get; set; }
    public string? ModularrUrl { get; set; }
    public string? ModularrKey { get; set; }
    public string? ModularrPublicUrl { get; set; }
    public string? VisularrUrl { get; set; }
    public string? VisularrKey { get; set; }
    public string? VisularrPublicUrl { get; set; }
    public string? LinkarrPublicUrl { get; set; }
    public bool RequestsNeedApproval { get; set; }
    public bool AutoFixNotIndexed { get; set; }

    // Requests a non-admin may make per rolling 24 hours; 0 means no limit.
    public int RequestsPerDay { get; set; } = 10;
    public required string WebhookToken { get; set; }
    public DateTime? LastUserSync { get; set; }
    public string? UserSyncError { get; set; }
    public DateTime? LastCatalogSync { get; set; }
    public string? CatalogSyncError { get; set; }
}
