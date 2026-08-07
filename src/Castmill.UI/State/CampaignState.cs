using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;

namespace Castmill.UI.State;

/// <summary>
/// Everything the four campaign views read: the campaign, its artifacts and its image-slot
/// state, all from the single <c>Preview</c> payload (G9).
///
/// The prototype's own bug log was almost entirely "header changed, content didn't", so this
/// store has one job beyond caching: when <see cref="LoadAsync"/> is called with a different
/// campaign id, EVERYTHING here is replaced and <see cref="Changed"/> fires once. No surface
/// gets to keep a stale copy. <c>CampaignSwitchTests</c> is the regression test.
/// </summary>
public sealed class CampaignState(CampaignsClient campaigns)
{
    /// <summary>Aging threshold for the front page's "drafts aging" block.</summary>
    private static readonly TimeSpan DraftIsAging = TimeSpan.FromDays(7);

    private Task? _inFlight;
    private Guid? _inFlightId;

    /// <summary>The campaign whose last load failed — see the guard in <see cref="LoadAsync"/>.</summary>
    private Guid? _failedId;

    public Guid? CampaignId { get; private set; }

    public CampaignResponse? Campaign { get; private set; }

    /// <summary>The brand steering this campaign, from the preview payload; null = None.</summary>
    public BrandSummaryResponse? Brand { get; private set; }

    public IReadOnlyList<ArtifactPreviewResponse> Artifacts { get; private set; } = [];

    public IReadOnlyList<ImageSlotResponse> ImageSlots { get; private set; } = [];

    public int ImagesFilled { get; private set; }

    public int ImagesTotal { get; private set; }

    /// <summary>
    /// The campaign's timed transcript — the provenance backbone. Loaded with the preview
    /// (it is one more small fetch) so the Source Master card and the threads never need a
    /// per-hover round-trip (§3.5).
    /// </summary>
    public TranscriptContent? Transcript { get; private set; }

    /// <summary>The transcript artifact's id, for regenerate calls.</summary>
    public Guid? TranscriptArtifactId { get; private set; }

    public bool IsLoading { get; private set; }

    public string? LoadError { get; private set; }

    public event Action? Changed;

    // ---- Derived views the surfaces read ------------------------------------

    /// <summary>Artifacts waiting on a human — the front page's primary column.</summary>
    public IEnumerable<ArtifactPreviewResponse> ReadyForReview =>
        Artifacts.Where(a => a.Status == ArtifactStatus.InReview)
                 .OrderByDescending(a => a.UpdatedAt);

    /// <summary>Drafts that have sat untouched long enough to be worth a nudge.</summary>
    public IEnumerable<ArtifactPreviewResponse> AgingDrafts(DateTimeOffset now) =>
        Artifacts.Where(a => a.Status == ArtifactStatus.Draft && now - a.UpdatedAt > DraftIsAging)
                 .OrderBy(a => a.UpdatedAt);

    public IEnumerable<ImageSlotResponse> EmptySlots =>
        ImageSlots.Where(s => !string.Equals(s.State, "Filled", StringComparison.Ordinal));

    /// <summary>
    /// Loads a campaign, at most once per id at a time.
    ///
    /// SINGLE-FLIGHT IS LOAD-BEARING, not an optimization. Callers are components whose
    /// OnParametersSetAsync runs on every re-render, and this store's Changed event *causes*
    /// re-renders. Without collapsing concurrent loads for the same id, the first load —
    /// which necessarily has Campaign == null while in flight — re-enters here on every
    /// notification and spins forever. That is an unresponsive tab, not a slow one, and no
    /// bUnit test catches it because a stubbed transport completes before the re-render.
    /// </summary>
    public Task LoadAsync(Guid campaignId, bool force = false)
    {
        if (!force)
        {
            // In flight for this campaign. The test is _inFlightId alone, NOT
            // "_inFlight is not null": an async method runs synchronously up to its first
            // await, so the clear-and-notify below fires while the field assignment
            // "_inFlight = LoadCoreAsync(...)" has not happened yet. A guard that also
            // required _inFlight to be set was therefore bypassable by exactly the
            // re-entrant notification it existed to stop, and recursed.
            if (_inFlightId == campaignId)
            {
                return _inFlight ?? Task.CompletedTask;
            }

            // Already loaded.
            if (CampaignId == campaignId && Campaign is not null)
            {
                return Task.CompletedTask;
            }

            // Already FAILED. Without this the store retries forever: a failure fires
            // Changed, the view re-renders, the shell's OnParametersSetAsync runs again and
            // calls straight back in — and because a failed load leaves Campaign null, the
            // "already loaded" check above never catches it. That loop hammers the API and
            // makes the page flash between "Loading campaign…" and the error, which is
            // exactly what a pending migration produced. Retrying is an explicit force.
            if (_failedId == campaignId)
            {
                return Task.CompletedTask;
            }
        }

        // An explicit reload gets a clean slate, including a second chance after a failure.
        _failedId = null;

        _inFlightId = campaignId;

        // Clear and notify HERE, synchronously, before the async part starts — so any
        // re-entrant caller triggered by this notification hits the guard above.
        //
        // Clearing first is also what stops a half-swapped store: anything rendering during
        // the load sees an empty, loading campaign rather than the previous campaign's
        // artifacts under the new one's name.
        CampaignId = campaignId;
        Campaign = null;
        Brand = null;
        Artifacts = [];
        ImageSlots = [];
        ImagesFilled = 0;
        ImagesTotal = 0;
        Transcript = null;
        TranscriptArtifactId = null;
        LoadError = null;
        IsLoading = true;
        Changed?.Invoke();

        _inFlight = LoadCoreAsync(campaignId);
        return _inFlight;
    }

    private async Task LoadCoreAsync(Guid campaignId)
    {
        try
        {
            var preview = await campaigns.GetPreviewAsync(campaignId);

            // Guard against an out-of-order response: if the user switched again while this
            // was in flight, the newer load owns the store.
            if (CampaignId != campaignId)
            {
                return;
            }

            Campaign = preview.Campaign;
            Brand = preview.Brand;
            Artifacts = preview.Artifacts;
            ImageSlots = preview.ImageSlots;
            ImagesFilled = preview.ImagesFilled;
            ImagesTotal = preview.ImagesTotal;

            // The transcript is an artifact like any other; its full content is the one
            // exception to "list views never load content", because every campaign surface
            // reads segments. One fetch per campaign switch.
            var transcriptPreview = preview.Artifacts.FirstOrDefault(a => a.Kind == "transcript");
            TranscriptArtifactId = transcriptPreview?.Id;
            Transcript = null;
            if (transcriptPreview is not null)
            {
                var (full, _) = await campaigns.GetArtifactAsync(campaignId, transcriptPreview.Id);
                if (CampaignId != campaignId)
                {
                    return;
                }

                Transcript = ParseTranscript(full.ContentJson);
            }
        }
        catch (ApiException ex)
        {
            LoadError = ex.Message;
        }
        catch (HttpRequestException)
        {
            LoadError = "Couldn't reach the Castmill API.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad. This task is awaited from a component lifecycle method, so
            // anything that escapes here surfaces as an unhandled renderer exception and the
            // page dies rather than showing a message — which is what a pending migration
            // did: the API returned a 500 whose body was an HTML error page, and interpreting
            // that threw something neither of the clauses above names. A campaign that
            // cannot load is a message, never a dead screen.
            LoadError = $"This campaign couldn't be loaded ({ex.GetType().Name}). "
                        + "The API log will say why.";
        }
        finally
        {
            if (_inFlightId == campaignId)
            {
                _inFlight = null;
                _inFlightId = null;
            }

            if (CampaignId == campaignId)
            {
                // Recorded before notifying: the notification re-renders, and the re-render
                // calls back into LoadAsync, which needs to already know this one failed.
                _failedId = LoadError is null ? null : campaignId;
                IsLoading = false;
                Changed?.Invoke();
            }
        }
    }

    public void Clear()
    {
        _inFlight = null;
        _inFlightId = null;
        _failedId = null;
        CampaignId = null;
        Campaign = null;
        Brand = null;
        Artifacts = [];
        ImageSlots = [];
        ImagesFilled = 0;
        ImagesTotal = 0;
        Transcript = null;
        TranscriptArtifactId = null;
        IsLoading = false;
        LoadError = null;
        Changed?.Invoke();
    }

    private static readonly System.Text.Json.JsonSerializerOptions TranscriptJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static TranscriptContent? ParseTranscript(string contentJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<TranscriptContent>(contentJson, TranscriptJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
