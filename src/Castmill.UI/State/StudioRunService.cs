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

    public event Action? Changed;

    public bool IsActiveFor(Guid slotId) => SlotId == slotId && IsRunning;

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

    private void Start(Guid campaignId, Guid slotId, int variants, Func<CancellationToken, Task<VariantBatchResponse>> call)
    {
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
}
