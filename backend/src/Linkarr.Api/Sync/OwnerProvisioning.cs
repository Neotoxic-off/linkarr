using Linkarr.Api.Auth;
using Linkarr.Api.Data;

namespace Linkarr.Api.Sync;

// Right after setup, or when a user is created or reset while an app is still starting: the password is retried for a short
// window, kept in memory only (never persisted). After that the account shows "password needed".
public sealed partial class OwnerProvisioning(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<OwnerProvisioning> logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    // The retry in progress (the tests wait for it).
    public Task Retrying { get; private set; } = Task.CompletedTask;

    public void Retry(int accountId, string password) => Retrying = Task.Run(() => RetryAsync(accountId, password, lifetime.ApplicationStopping));

    private async Task RetryAsync(int accountId, string password, CancellationToken ct)
    {
        DateTime until = DateTime.UtcNow + Window;
        TimeSpan delay = FirstDelay;
        try
        {
            while (DateTime.UtcNow + delay <= until)
            {
                await Task.Delay(delay, ct);
                delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                Account? account = await scope.ServiceProvider.GetRequiredService<AccountService>().FindAsync(accountId, ct);
                // Gone, or its password changed since: this one must not be sent any more.
                if (account is null || !AccountService.VerifyPassword(account, password)
                    || await scope.ServiceProvider.GetRequiredService<UserSync>().ProvisionOwnerAsync(account, password, ct))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            LogRetryFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Provisioning the owner to the apps failed")]
    private static partial void LogRetryFailed(ILogger logger, Exception exception);
}
