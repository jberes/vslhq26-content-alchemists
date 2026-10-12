using Castmill.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Moves a running content run's <c>UpdatedAt</c> while a generator is mid-call (ADR-086). At
/// "max" reasoning effort one piece can take longer than the client's five-minute stall guard,
/// and completions alone could not tell a slow model from a server that died; a heartbeat
/// that stops only when the process does can.
/// </summary>
public sealed class RunHeartbeat : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop;
    private readonly Task _loop;

    public RunHeartbeat(
        IServiceScopeFactory scopes, Guid runId, TimeProvider clock, CancellationToken ct, TimeSpan? interval = null)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = BeatAsync(scopes, runId, clock, interval ?? Interval, _stop.Token);
    }

    private static async Task BeatAsync(
        IServiceScopeFactory scopes, Guid runId, TimeProvider clock, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    // Its own scope: the orchestrator's DbContext is busy on the run, and a
                    // DbContext is not thread-safe. By id — the run is this request's own row.
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
                    await db.GenerationRuns.IgnoreQueryFilters()
                        .Where(run => run.Id == runId && run.Status == "Running")
                        .ExecuteUpdateAsync(set => set.SetProperty(run => run.UpdatedAt, clock.GetUtcNow()), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missed beat only makes the client wait longer; it must never fail the run.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
