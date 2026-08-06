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

    public RunProgress? Progress { get; private set; }

    public bool IsRunning { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>True when the given campaign has a run in flight or a just-finished one to reveal.</summary>
    public bool IsActiveFor(Guid campaignId) =>
        CampaignId == campaignId && (IsRunning || Progress is not null);

    public void Start(Guid campaignId, Guid transcriptArtifactId, string? brief, string[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        // One run at a time. Starting a second cancels the first poll loop; the server-side
        // generation of the first run finishes regardless (it is server work, not ours).
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        CampaignId = campaignId;
        Kinds = kinds;
        Progress = null;
        Error = null;
        IsRunning = true;
        Changed?.Invoke();

        _ = RunAsync(campaignId, transcriptArtifactId, brief, kinds, _cts.Token);
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
        Guid campaignId, Guid transcriptArtifactId, string? brief, string[] kinds, CancellationToken ct)
    {
        var startedAfter = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

        // The long POST. Held as a task; its completion ends the loop below.
        var generate = generation.GenerateAsync(campaignId, transcriptArtifactId, brief, kinds, ct);

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
                        if (latest.Completed > previouslyCompleted && campaign.CampaignId == campaignId)
                        {
                            await campaign.LoadAsync(campaignId, force: true);
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
            Error = "Couldn't reach the Castmill API while generating.";
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
                    try
                    {
                        await campaign.LoadAsync(campaignId, force: true);
                    }
                    catch (HttpRequestException)
                    {
                        // The board will catch up on its next natural load.
                    }
                }
            }
        }
    }
}
