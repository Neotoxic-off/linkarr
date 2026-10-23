using System.Security.Cryptography;
using Linkarr.Api.Apps;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Data;

// Migrates in StartingAsync, which runs before any hosted service (the web server included) starts.
public sealed class DatabaseMigrator(IServiceScopeFactory scopes) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        LinkarrDbContext db = scope.ServiceProvider.GetRequiredService<LinkarrDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        // Persistent in the file: readers no longer block the writer.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        if (!await db.Settings.AnyAsync(cancellationToken))
        {
            db.Settings.Add(new AppSettings { Id = 1, WebhookToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) });
            await db.SaveChangesAsync(cancellationToken);
        }

        AppSettings settings = await db.Settings.FirstAsync(s => s.Id == 1, cancellationToken);
        DeploymentSettings.Apply(settings, scope.ServiceProvider.GetRequiredService<IConfiguration>(), scope.ServiceProvider.GetRequiredService<Connections>());
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
