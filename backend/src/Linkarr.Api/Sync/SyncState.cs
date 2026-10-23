using System.Collections.Concurrent;

namespace Linkarr.Api.Sync;

// An app user linkarr does not manage: created directly in visularr or modularr.
public sealed record UnmanagedUser(string App, string RemoteId, string Name, bool Admin, bool Linkable);

// Shared by the background worker and the endpoints: one user sync and one catalog sync at a time, and the wake-up signal.
public sealed class SyncState
{
    public SemaphoreSlim UserLock { get; } = new(1, 1);
    public SemaphoreSlim CatalogLock { get; } = new(1, 1);
    public SemaphoreSlim Wake { get; } = new(0, 1);

    public IReadOnlyList<UnmanagedUser> Unmanaged { get; set; } = [];

    // When a targeted refresh was last sent for a catalog key, so the auto-fix does not resend it on every sync.
    public ConcurrentDictionary<string, DateTime> Refreshed { get; } = new(StringComparer.Ordinal);

    // A burst of webhook events (a season pack imports many files) makes one catalog sync, this long after the last event.
    public static readonly TimeSpan HookDebounce = TimeSpan.FromSeconds(15);

    // When a requested catalog sync is due, in UTC ticks; long.MaxValue when none is requested.
    private long catalogDue = long.MaxValue;
    private int usersRequested;

    public DateTime? CatalogDue => Interlocked.Read(ref catalogDue) is long due && due != long.MaxValue ? new DateTime(due, DateTimeKind.Utc) : null;

    // The webhook's way in: the worker runs a catalog sync once no event came for the debounce delay.
    public void RequestCatalog(TimeSpan debounce)
    {
        Interlocked.Exchange(ref catalogDue, (DateTime.UtcNow + debounce).Ticks);
        WakeUp();
    }

    // After the connections change: the worker reconciles users (and tells both apps they are managed) right away.
    public void RequestUsers()
    {
        Interlocked.Exchange(ref usersRequested, 1);
        RequestCatalog(TimeSpan.Zero);
    }

    public bool TakeUsersRequest() => Interlocked.Exchange(ref usersRequested, 0) == 1;

    private void WakeUp()
    {
        try
        {
            Wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    // True once, when a requested sync is due; an event arriving meanwhile pushes it back instead.
    public bool TakeCatalogRequest()
    {
        long due = Interlocked.Read(ref catalogDue);
        return due <= DateTime.UtcNow.Ticks && Interlocked.CompareExchange(ref catalogDue, long.MaxValue, due) == due;
    }
}
