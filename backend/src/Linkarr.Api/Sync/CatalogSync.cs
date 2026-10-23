using Linkarr.Api.Apps;
using Linkarr.Api.Catalog;
using Linkarr.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Sync;

// Pulls both libraries and modularr's queue, and replaces the stored catalog with the merge. A failed pull keeps the last catalog.
public sealed partial class CatalogSync(LinkarrDbContext db, Connections connections, SyncState state, ILogger<CatalogSync> logger)
{
    // The auto-fix sends a title's targeted refresh at most this often.
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken ct)
    {
        await state.CatalogLock.WaitAsync(ct);
        try
        {
            AppSettings settings = await connections.SettingsAsync(ct);
            ModularrClient? modularr = await connections.ModularrAsync(ct);
            VisularrClient? visularr = await connections.VisularrAsync(ct);
            if (modularr is null && visularr is null)
            {
                return;
            }

            IReadOnlyList<CatalogItem> catalog;
            try
            {
                catalog = CatalogBuilder.Build(
                    modularr is null ? [] : await modularr.MoviesAsync(ct),
                    modularr is null ? [] : await modularr.SeriesAsync(ct),
                    modularr is null ? [] : await modularr.QueueAsync(ct),
                    visularr is null ? [] : await visularr.ItemsAsync(ct));
            }
            catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
            {
                settings.CatalogSyncError = AppHttp.Describe(ex);
                await db.SaveChangesAsync(ct);
                return;
            }

            await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct))
            {
                DateTime now = DateTime.UtcNow;
                Dictionary<string, CatalogItem> previous = await db.Catalog.AsNoTracking().ToDictionaryAsync(c => c.Key, ct);
                CarryAvailability(catalog, previous, now);
                await db.Catalog.ExecuteDeleteAsync(ct);
                db.Catalog.AddRange(catalog);
                await SnapshotAsync(catalog, DateOnly.FromDateTime(now), ct);
                settings.LastCatalogSync = now;
                settings.CatalogSyncError = null;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            db.ChangeTracker.Clear();
            if (settings.AutoFixNotIndexed && visularr is not null)
            {
                await AutoFixAsync(catalog, visularr, ct);
            }
        }
        finally
        {
            state.CatalogLock.Release();
        }
    }

    // A title playable before keeps its date; a newly playable one gets now. The very first sync dates nothing: it found a library, not imports.
    public static void CarryAvailability(IReadOnlyList<CatalogItem> catalog, IReadOnlyDictionary<string, CatalogItem> previous, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(previous);
        foreach (CatalogItem item in catalog.Where(c => c.State == CatalogState.Available))
        {
            item.AvailableSince = previous.GetValueOrDefault(item.Key) is { State: CatalogState.Available } before ? before.AvailableSince
                : previous.Count == 0 ? null
                : now;
        }
    }

    // Today's counts (replacing an earlier sync's of the same day); days past the window are dropped.
    private async Task SnapshotAsync(IReadOnlyList<CatalogItem> catalog, DateOnly today, CancellationToken ct)
    {
        CatalogSnapshot snapshot = await db.Snapshots.FirstOrDefaultAsync(s => s.Date == today, ct) ?? db.Snapshots.Add(new CatalogSnapshot { Date = today }).Entity;
        int Count(CatalogState state) => catalog.Count(c => c.State == state);
        snapshot.Movies = catalog.Count(c => c.Kind == TitleKind.Movie);
        snapshot.Series = catalog.Count(c => c.Kind == TitleKind.Series);
        snapshot.Available = Count(CatalogState.Available);
        snapshot.Downloading = Count(CatalogState.Downloading);
        snapshot.NotIndexed = Count(CatalogState.NotIndexed);
        snapshot.Untracked = Count(CatalogState.Untracked);
        snapshot.Missing = Count(CatalogState.Missing);
        snapshot.Unmonitored = Count(CatalogState.Unmonitored);
        DateOnly oldest = today.AddDays(-CatalogSnapshot.SnapshotDays);
        await db.Snapshots.Where(s => s.Date < oldest).ExecuteDeleteAsync(ct);
    }

    // Asks visularr to scan the folder of one title modularr has a file for.
    public async Task RefreshAsync(CatalogItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        VisularrClient visularr = await connections.VisularrAsync(ct) ?? throw new InvalidOperationException("visularr is not configured.");
        string path = item.ModularrPath ?? throw new ArgumentException("modularr has no folder for this title.", nameof(item));
        await visularr.RefreshPathAsync(path, ct);
        state.Refreshed[item.Key] = DateTime.UtcNow;
    }

    // An import from modularr's webhook: visularr scans the title's folder now rather than at its next library scan.
    // Only a folder the catalog knows is scanned: the event's Path counts only when it is a catalog title's folder, so a
    // forged event cannot point visularr anywhere else. A failure is only logged (the auto-fix retries).
    public async Task RefreshImportAsync(HookEvent hook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hook);
        (TitleKind kind, int? modularrId) = hook.MovieId is int movie ? (TitleKind.Movie, movie) : (TitleKind.Series, hook.SeriesId);
        CatalogItem? item = modularrId is null ? null
            : await db.Catalog.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind && c.ModularrId == modularrId, ct);
        string? folder = hook.Path?.TrimEnd('/', '\\');
        item ??= string.IsNullOrEmpty(folder) ? null : await db.Catalog.AsNoTracking().FirstOrDefaultAsync(c => c.ModularrPath == folder, ct);
        if (item?.ModularrPath is not string path || await connections.VisularrAsync(ct) is not VisularrClient visularr)
        {
            return;
        }

        try
        {
            await visularr.RefreshPathAsync(path, ct);
            if (item is not null)
            {
                state.Refreshed[item.Key] = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
        {
            LogAutoFixFailed(logger, hook.Title ?? path, AppHttp.Describe(ex));
        }
    }

    private async Task AutoFixAsync(IReadOnlyList<CatalogItem> catalog, VisularrClient visularr, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        foreach (CatalogItem item in catalog.Where(c => c.State == CatalogState.NotIndexed && c.ModularrPath is not null))
        {
            if (state.Refreshed.TryGetValue(item.Key, out DateTime last) && now - last < RefreshEvery)
            {
                continue;
            }

            try
            {
                await visularr.RefreshPathAsync(item.ModularrPath!, ct);
                state.Refreshed[item.Key] = now;
            }
            catch (Exception ex) when (AppHttp.IsAppFailure(ex, ct))
            {
                LogAutoFixFailed(logger, item.Title, AppHttp.Describe(ex));
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-fix refresh of {Title} failed: {Error}")]
    private static partial void LogAutoFixFailed(ILogger logger, string title, string error);
}
