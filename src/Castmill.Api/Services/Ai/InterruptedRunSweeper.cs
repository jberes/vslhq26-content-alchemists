using Castmill.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Marks runs the previous process left "Running" as "Interrupted" at startup.
///
/// A run executes inside the API process, so a crash, a redeploy or a Ctrl-C mid-run leaves
/// its row saying Running forever — the press panel then shows a run that will never finish
/// and never fail, which is the least honest state a progress display can be in. Sweeping at
/// startup is sound precisely BECAUSE the run lives in-process: if this process is starting,
/// no run from before it can still be executing.
///
/// Single-instance assumption, stated: with multiple API instances this would interrupt a
/// sibling's live runs and must become a heartbeat check instead.
/// </summary>
public sealed class InterruptedRunSweeper(
    IServiceScopeFactory scopes,
    ILogger<InterruptedRunSweeper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();

        try
        {
            var swept = await SweepAsync(db, cancellationToken);
            if (swept > 0)
            {
                logger.LogWarning(
                    "Marked {Count} orphaned generation run(s) as Interrupted — the previous "
                    + "process exited while they were printing.", swept);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            // A sleeping database must not stop the API from starting (the DemoUserSeeder
            // rule, applied here). The rows stay stale until the next start.
            logger.LogWarning(ex, "Could not sweep orphaned runs; the database did not answer.");
        }
    }

    /// <summary>Cross-tenant on purpose: startup housekeeping owns every orphan, and no
    /// request context exists to scope by.</summary>
    internal static Task<int> SweepAsync(CastmillDbContext db, CancellationToken ct) =>
        db.GenerationRuns
            .IgnoreQueryFilters()
            .Where(r => r.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Interrupted"), ct);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
