using Castmill.UI.Http;

namespace Castmill.UI.State;

/// <summary>
/// Owns an in-flight generation run so it survives navigation: the new-campaign flow starts
/// the run and immediately navigates to the Mill Floor, which watches the progress here.
///
/// Two channels, deliberately: the long-running POST (held, not awaited by any component —
/// a component awaiting it would cancel the request when it disposed on navigation) and a
/// poll of <c>runs/latest</c> that surfaces per-artifact completions while the POST is still
/// buffering. The reveal is driven by real completion events, never a timer (ADR-F13).
/// </summary>
public sealed class PressRunService(GenerationClient generation, CampaignState campaign) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(800);

    private CancellationTokenSource? _cts;

    public Guid? CampaignId { get; private set; }

    /// <summary>The kinds this run was asked for, in request order — the press panel's rows.</summary>
    public IReadOnlyList<string> Kinds { get; private set; } = [];

    /// <summary>How many of each kind this run prints — the board draws one ghost per copy.</summary>
    public int Copies { get; private set; } = 1;

    public RunProgress? Progress { get; private set; }

    public bool IsRunning { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>True when the given campaign has a run in flight or a just-finished one to reveal.</summary>
    public bool IsActiveFor(Guid campaignId) =>
        CampaignId == campaignId && (IsRunning || Progress is not null);

    /// <param name="copies">How many of each kind to print — "3 more LinkedIn posts".</param>
    public void Start(Guid campaignId, Guid transcriptArtifactId, string? brief, string[] kinds, int copies = 1)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        // One run at a time. Starting a second cancels the first poll loop; the server-side
        // generation of the first run finishes regardless (it is server work, not ours).
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        CampaignId = campaignId;
        Kinds = kinds;
        Copies = copies;
        Progress = null;
        Error = null;
        IsRunning = true;
        Changed?.Invoke();

        _ = RunAsync(campaignId, transcriptArtifactId, brief, kinds, copies, _cts.Token);
    }

    /// <summary>Clears the finished run once the canvas has finished revealing it.</summary>
    public void Acknowledge()
    {
        if (!IsRunning)
        {
            CampaignId = null;
            Progress = null;
            Error = null;
            Changed?.Invoke();
        }
    }

    public void Dispose() => _cts?.Cancel();

    private async Task RunAsync(
        Guid campaignId, Guid transcriptArtifactId, string? brief, string[] kinds, int copies, CancellationToken ct)
    {
        var startedAfter = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

        // The long POST. Held as a task; its completion ends the loop below.
        var generate = generation.GenerateAsync(campaignId, transcriptArtifactId, brief, kinds, copies, ct);

        try
        {
            while (!generate.IsCompleted && !ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);

                try
                {
                    var latest = await generation.GetLatestRunAsync(campaignId, "content", ct);

                    // Only adopt a run that started for THIS press: a stale run from an
                    // earlier session would replay its reveal.
                    if (latest.StartedAt >= startedAfter)
                    {
                        var previouslyCompleted = Progress?.Completed ?? 0;
                        Progress = latest;
                        Changed?.Invoke();

                        // The service owns the board refresh, so completions land even when
                        // no view is mounted (the user may be on another tab). Guarded on
                        // the store still holding THIS campaign — a forced load of the
                        // run's campaign would otherwise hijack a user who switched away.
                        // RefreshAsync, not LoadAsync(force): a forced load blanks the store
                        // and raises IsLoading, so the board flashed through its empty state
                        // once per completed artifact.
                        if (latest.Completed > previouslyCompleted && campaign.CampaignId == campaignId)
                        {
                            await campaign.RefreshAsync(campaignId);
                        }
                    }
                }
                catch (ApiException)
                {
                    // 404 until the orchestrator has created the run row: keep polling.
                }
            }

            var finished = await generate;

            Progress = new RunProgress(
                finished.RunId, campaignId, "Completed",
                finished.Results.Count, finished.Results.Count, finished.Results,
                Progress?.StartedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (HttpRequestException)
        {
            // The POST died — but since the server no longer ties the run to the request, the
            // run itself is very likely still printing. Re-attach through the run row instead
            // of declaring everything lost: this is exactly the failure that used to truncate
            // a 13-item run to whatever had landed when the connection blinked.
            if (!await ReattachAsync(campaignId, ct))
            {
                Error = "Lost the connection while generating — the board will show whatever printed.";
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsRunning = false;
                Changed?.Invoke();

                // Reconciliation: whatever the poll saw or missed, the run is over — one
                // final reload guarantees the board matches the server. Same hijack guard.
                if (campaign.CampaignId == campaignId)
                {
                    // Also a refresh: the run is over, but tearing the board down at the very
                    // moment the user starts reading it is the worst possible time to flash.
                    await campaign.RefreshAsync(campaignId);
                }
            }
        }
    }

    /// <summary>
    /// Follows a run the request lost by polling its row until it reaches a terminal state.
    /// A stall guard (no new completions for 5 minutes) stops this from polling a row that
    /// died with the server process — the startup sweep marks those "Interrupted".
    /// </summary>
    private async Task<bool> ReattachAsync(Guid campaignId, CancellationToken ct)
    {
        if (Progress is null)
        {
            return false; // the POST died before the run row was ever seen — nothing to follow
        }

        var lastCompleted = Progress.Completed;
        var lastMovement = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);

            RunProgress latest;
            try
            {
                latest = await generation.GetLatestRunAsync(campaignId, "content", ct);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                if (DateTimeOffset.UtcNow - lastMovement > TimeSpan.FromMinutes(5))
                {
                    return false;
                }
                continue;
            }

            if (latest.Id != Progress.Id)
            {
                return false; // a newer run superseded this one; let it own the panel
            }

            Progress = latest;
            Changed?.Invoke();

            if (latest.Completed > lastCompleted)
            {
                lastCompleted = latest.Completed;
                lastMovement = DateTimeOffset.UtcNow;
                if (campaign.CampaignId == campaignId)
                {
                    await campaign.RefreshAsync(campaignId);
                }
            }

            if (latest.Status is "Completed" or "Interrupted")
            {
                return true;
            }

            if (DateTimeOffset.UtcNow - lastMovement > TimeSpan.FromMinutes(5))
            {
                Error = "The run stopped reporting progress — the board shows what printed.";
                return true;
            }
        }

        return true;
    }

}
