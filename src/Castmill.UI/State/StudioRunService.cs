using Castmill.Core.Resources;
using Castmill.UI.Http;

namespace Castmill.UI.State;

/// <summary>
/// Owns an in-flight image generation so it survives navigation — the PressRunService
/// pattern applied to pixels. The long POST (generate or steer) is held un-awaited; a
/// poll of the image-kind run row surfaces per-variant completions for skeleton tiles.
/// One run at a time, per the studio's one-slot-at-a-time interaction.
/// </summary>
public sealed class StudioRunService(ImagesClient images, GenerationClient generation) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(800);

    private CancellationTokenSource? _cts;

    // Wall-clock of the run, for the "how long has this been going" label. A Stopwatch, not
    // dates: image renders run for minutes and the label must not jump if the clock adjusts.
    private readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>How long the current (or just-finished) run has been going.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public Guid? CampaignId { get; private set; }

    public Guid? SlotId { get; private set; }

    /// <summary>How many variants this run was asked for — one skeleton tile each.</summary>
    public int ExpectedVariants { get; private set; }

    public RunProgress? Progress { get; private set; }

    public bool IsRunning { get; private set; }

    public string? Error { get; private set; }

    /// <summary>The finished batch, for the view to fold into its gallery.</summary>
    public VariantBatchResponse? Result { get; private set; }

    public RunProgress? BatchProgress { get; private set; }

    public ImageBatchResponse? BatchResult { get; private set; }

    public bool IsBatchRunning { get; private set; }

    /// <summary>The content item a running batch was narrowed to; null = whole campaign.</summary>
    public Guid? BatchArtifactId { get; private set; }

    public string? BatchError { get; private set; }

    public event Action? Changed;

    public bool IsActiveFor(Guid slotId) => SlotId == slotId && IsRunning;

    public bool IsBatchActiveFor(Guid campaignId) =>
        CampaignId == campaignId && (IsBatchRunning || BatchProgress is not null || BatchResult is not null);

    /// <summary>
    /// Starts a fresh-generation run for the slot. <paramref name="modelAlias"/> renders this
    /// batch with a chosen model without changing the slot's saved default.
    /// </summary>
    public void StartGenerate(Guid campaignId, Guid slotId, int variants, string? modelAlias = null) =>
        Start(campaignId, slotId, variants,
            ct => images.GenerateAsync(campaignId, slotId, variants, modelAlias, ct));

    /// <summary>Starts a steered run from an existing take.</summary>
    public void StartSteer(
        Guid campaignId, Guid slotId, Guid sourceVariantId, string note, int variants = 1,
        string? modelAlias = null) =>
        Start(campaignId, slotId, variants,
            ct => images.SteerAsync(campaignId, slotId, sourceVariantId, note, variants, modelAlias, ct));

    public void StartBatch(Guid campaignId, int variantsPerSlot, Guid? artifactId = null)
    {
        if (IsRunning || IsBatchRunning)
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CampaignId = campaignId;
        SlotId = null;
        ExpectedVariants = 0;
        BatchProgress = null;
        BatchResult = null;
        BatchError = null;
        BatchArtifactId = artifactId;
        IsBatchRunning = true;
        _clock.Restart();
        Changed?.Invoke();

        _ = RunBatchAsync(campaignId, variantsPerSlot, artifactId, _cts.Token);
    }

    /// <summary>Adopts a running campaign batch after a reload or a lost POST response.</summary>
    public async Task TryAttachBatchAsync(Guid campaignId)
    {
        if (IsRunning || IsBatchRunning)
        {
            return;
        }

        RunProgress latest;
        try
        {
            latest = await generation.GetLatestRunAsync(campaignId, "image-batch");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            return;
        }

        if (latest.Status != "Running")
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CampaignId = campaignId;
        SlotId = null;
        BatchProgress = latest;
        BatchResult = null;
        BatchError = null;
        // An adopted run's original scope is unknown — treat it as campaign-wide.
        BatchArtifactId = null;
        IsBatchRunning = true;
        _clock.Restart();
        Changed?.Invoke();
        _ = TrackAttachedBatchAsync(campaignId, latest.Id, _cts.Token);
    }

    private void Start(Guid campaignId, Guid slotId, int variants, Func<CancellationToken, Task<VariantBatchResponse>> call)
    {
        if (IsBatchRunning)
        {
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        CampaignId = campaignId;
        SlotId = slotId;
        ExpectedVariants = variants;
        Progress = null;
        Result = null;
        Error = null;
        IsRunning = true;
        _clock.Restart();
        Changed?.Invoke();

        _ = RunAsync(campaignId, call, _cts.Token);
    }

    public void Dispose() => _cts?.Cancel();

    private async Task RunAsync(
        Guid campaignId, Func<CancellationToken, Task<VariantBatchResponse>> call, CancellationToken ct)
    {
        var startedAfter = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        var request = call(ct);

        try
        {
            while (!request.IsCompleted && !ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);

                try
                {
                    var latest = await generation.GetLatestRunAsync(campaignId, "image", ct);
                    if (latest.StartedAt >= startedAfter)
                    {
                        Progress = latest;
                    }
                }
                catch (ApiException)
                {
                    // 404 until the run row exists: keep polling.
                }

                // Every tick, not only on progress: the elapsed-time label advances with the
                // poll even while the run row has nothing new to say.
                Changed?.Invoke();
            }

            Result = await request;
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
            Error = "Couldn't reach the Castmill API while generating images.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _clock.Stop();
                IsRunning = false;
                Changed?.Invoke();
            }
        }
    }

    private async Task RunBatchAsync(
        Guid campaignId, int variantsPerSlot, Guid? artifactId, CancellationToken ct)
    {
        var requestStartedAt = DateTimeOffset.UtcNow;
        var startedAfter = requestStartedAt - TimeSpan.FromSeconds(10);
        var request = images.GeneratePendingAsync(campaignId, variantsPerSlot, artifactId, ct);

        try
        {
            while (!request.IsCompleted && !ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                try
                {
                    var latest = await generation.GetLatestRunAsync(campaignId, "image-batch", ct);
                    if (latest.StartedAt >= startedAfter)
                    {
                        BatchProgress = latest;
                    }
                }
                catch (ApiException)
                {
                    // 404 until the batch row exists.
                }
                Changed?.Invoke();
            }

            BatchResult = await request;
            try
            {
                BatchProgress = await generation.GetRunAsync(BatchResult.RunId, ct);
            }
            catch (ApiException)
            {
                // The typed result already carries the final summary.
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            if (!await ReattachBatchAsync(campaignId, ct, requestStartedAt))
            {
                BatchError = "Lost the connection before Castmill could find the image batch.";
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 409)
        {
            if (!await ReattachBatchAsync(campaignId, ct))
            {
                BatchError = ex.Message;
            }
        }
        catch (ApiException ex)
        {
            BatchError = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _clock.Stop();
                IsBatchRunning = false;
                Changed?.Invoke();
            }
        }
    }

    private async Task<bool> ReattachBatchAsync(
        Guid campaignId, CancellationToken ct, DateTimeOffset? startedAfter = null)
    {
        for (var attempt = 0; attempt < 8 && !ct.IsCancellationRequested; attempt++)
        {
            RunProgress latest;
            try
            {
                latest = await generation.GetLatestRunAsync(campaignId, "image-batch", ct);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                await Task.Delay(PollInterval, ct);
                continue;
            }

            if (startedAfter is not null && latest.StartedAt < startedAfter
                && latest.Status != "Running")
            {
                await Task.Delay(PollInterval, ct);
                continue;
            }

            BatchProgress = latest;
            Changed?.Invoke();
            if (latest.Status != "Running")
            {
                return true;
            }
            return await FollowBatchAsync(campaignId, latest.Id, ct);
        }
        return false;
    }

    private async Task<bool> FollowBatchAsync(Guid campaignId, Guid runId, CancellationToken ct)
    {
        var lastMovement = DateTimeOffset.UtcNow;
        var completed = BatchProgress?.Completed ?? 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);
            try
            {
                var latest = await generation.GetLatestRunAsync(campaignId, "image-batch", ct);
                if (latest.Id != runId)
                {
                    return false;
                }
                BatchProgress = latest;
                if (latest.Completed > completed)
                {
                    completed = latest.Completed;
                    lastMovement = DateTimeOffset.UtcNow;
                }
                Changed?.Invoke();
                if (latest.Status is "Completed" or "Interrupted")
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
            }

            if (DateTimeOffset.UtcNow - lastMovement > TimeSpan.FromMinutes(5))
            {
                BatchError = "The image batch stopped reporting progress. Successful takes remain in their slots.";
                return true;
            }
        }
        return true;
    }

    private async Task TrackAttachedBatchAsync(Guid campaignId, Guid runId, CancellationToken ct)
    {
        try
        {
            await FollowBatchAsync(campaignId, runId, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (CampaignId == campaignId && BatchProgress?.Id == runId)
            {
                _clock.Stop();
                IsBatchRunning = false;
                Changed?.Invoke();
            }
        }
    }
}
