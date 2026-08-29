using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Auth;

public sealed class ExternalAuthAttemptCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExternalAuthOptions> options,
    TimeProvider clock,
    ILogger<ExternalAuthAttemptCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanAsync(stoppingToken);
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes),
            clock);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CleanAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            var removeBefore = clock.GetUtcNow().AddHours(-options.Value.RetentionHours);
            await db.ExternalAuthAttempts
                .Where(attempt => attempt.ExpiresAt < removeBefore)
                .ExecuteDeleteAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "External authentication attempt cleanup failed.");
        }
    }
}