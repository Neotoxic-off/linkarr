using Linkarr.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Sync;

// The catalog every 5 min (or shortly after modularr's webhook events), the users every 10 min.
public sealed partial class SyncWorker(IServiceScopeFactory scopes, SyncState state, ILogger<SyncWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CatalogEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UsersEvery = TimeSpan.FromMinutes(10);

    // An app still starting (the compose stack comes up together) must not leave its error on screen for a whole period.
    public static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime nextCatalog = DateTime.UtcNow;
        DateTime nextUsers = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool requested = state.TakeCatalogRequest();
            if (requested || DateTime.UtcNow >= nextCatalog)
            {
                await RunAsync<CatalogSync>("catalog", (sync, ct) => sync.RunAsync(ct), stoppingToken);
                nextCatalog = DateTime.UtcNow + (await FailedAsync(s => s.CatalogSyncError, stoppingToken) ? RetryAfterError : CatalogEvery);
            }

            if (state.TakeUsersRequest() || DateTime.UtcNow >= nextUsers)
            {
                await RunAsync<UserSync>("users", (sync, ct) => sync.ReconcileAsync(ct), stoppingToken);
                nextUsers = DateTime.UtcNow + (await FailedAsync(s => s.UserSyncError, stoppingToken) ? RetryAfterError : UsersEvery);
            }

            DateTime next = new[] { nextCatalog, nextUsers, state.CatalogDue ?? DateTime.MaxValue, state.UsersDue ?? DateTime.MaxValue }.Min();
            TimeSpan wait = next - DateTime.UtcNow;
            await state.Wake.WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
        }
    }

    // A failing sync is logged and retried at its next turn; it never stops the worker.
    private async Task RunAsync<T>(string name, Func<T, CancellationToken, Task> run, CancellationToken stoppingToken) where T : notnull
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await run(scope.ServiceProvider.GetRequiredService<T>(), stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            LogSyncFailed(logger, name, ex);
        }
    }

    private async Task<bool> FailedAsync(Func<AppSettings, string?> error, CancellationToken stoppingToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AppSettings? settings = await scope.ServiceProvider.GetRequiredService<LinkarrDbContext>().Settings.AsNoTracking().FirstOrDefaultAsync(stoppingToken);
        return settings is not null && error(settings) is not null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Name} sync failed")]
    private static partial void LogSyncFailed(ILogger logger, string name, Exception exception);
}
