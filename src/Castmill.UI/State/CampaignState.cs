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
public sealed class CampaignState(CampaignsClient campaigns, EvidenceClient evidence)
{
    /// <summary>Aging threshold for the front page's "drafts aging" block.</summary>
    private static readonly TimeSpan DraftIsAging = TimeSpan.FromDays(7);

    private Task? _inFlight;
    private Guid? _inFlightId;
    private Task? _detailsTask;
    private int _detailsVersion;

    /// <summary>The campaign whose last load failed — see the guard in <see cref="LoadAsync"/>.</summary>
    private Guid? _failedId;

    public Guid? CampaignId { get; private set; }

    public CampaignResponse? Campaign { get; private set; }

    /// <summary>The brand steering this campaign, from the preview payload; null = None.</summary>
    public BrandSummaryResponse? Brand { get; private set; }

    public IReadOnlyList<ArtifactPreviewResponse> Artifacts { get; private set; } = [];

    public IReadOnlyList<SourceAssetResponse> Sources { get; private set; } = [];

    public IReadOnlyDictionary<Guid, EvidenceRevisionResponse> Evidence { get; private set; } =
        new Dictionary<Guid, EvidenceRevisionResponse>();

    public IReadOnlyDictionary<Guid, EvidenceRevisionResponse> ApprovedEvidence { get; private set; } =
        new Dictionary<Guid, EvidenceRevisionResponse>();

    public IReadOnlyDictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse>
        HistoricalEvidence { get; private set; } =
            new Dictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse>();

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

    public Guid? TranscriptSourceAssetId { get; private set; }

    public bool IsLoading { get; private set; }

    public bool IsLoadingEvidence { get; private set; }

    public bool IsLoadingTranscript { get; private set; }

    public bool IsLoadingDetails => IsLoadingEvidence || IsLoadingTranscript;

    public string? DetailsLoadError { get; private set; }

    public string? LoadError { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Notifies subscribers without letting one of them break the store.
    ///
    /// Changed is raised SYNCHRONOUSLY from inside LoadAsync, outside any try — so a
    /// subscriber that throws used to propagate out of the caller's lifecycle method,
    /// surface as Blazor's generic "An unhandled error has occurred", and leave IsLoading
    /// stuck true with the view showing "Loading campaign…" forever. A broken subscriber is
    /// now recorded and named rather than taking the whole screen down.
    /// </summary>
    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoadError ??= $"A view failed to update ({ex.GetType().Name}: {ex.Message}).";
            }
        }
    }

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
        Sources = [];
        Evidence = new Dictionary<Guid, EvidenceRevisionResponse>();
        ApprovedEvidence = new Dictionary<Guid, EvidenceRevisionResponse>();
        HistoricalEvidence = new Dictionary<(Guid, int), EvidenceRevisionResponse>();
        ImageSlots = [];
        ImagesFilled = 0;
        ImagesTotal = 0;
        Transcript = null;
        TranscriptArtifactId = null;
        TranscriptSourceAssetId = null;
        LoadError = null;
        DetailsLoadError = null;
        IsLoading = true;
        IsLoadingEvidence = false;
        IsLoadingTranscript = false;
        _detailsVersion++;
        RaiseChanged();

        _inFlight = LoadCoreAsync(campaignId);
        return _inFlight;
    }

    /// <summary>
    /// Re-reads the SAME campaign in place: fetch first, then swap. Nothing is blanked and
    /// IsLoading is never raised, so no view flickers through its loading state.
    ///
    /// This exists because <see cref="LoadAsync"/> deliberately clears the store before
    /// fetching — right when SWITCHING campaigns, where showing the previous campaign's
    /// artifacts under the new name would be a lie, and wrong when refreshing the one already
    /// on screen. A press run refreshed after every completed artifact, so a 13-item run tore
    /// the whole board down and rebuilt it 13 times: that is the flashing.
    ///
    /// Silent by design — a refresh that fails leaves the last good board up, because the
    /// press run's final reconciliation will correct it anyway.
    /// </summary>
    public async Task RefreshAsync(Guid campaignId)
    {
        if (CampaignId != campaignId)
        {
            return;
        }

        try
        {
            var preview = await campaigns.GetPreviewAsync(campaignId);
            var sources = preview.Sources ?? [];
            var loadedEvidence = await FetchEvidenceAsync(campaignId, sources);

            // The user may have switched campaigns while this was in flight.
            if (CampaignId != campaignId)
            {
                return;
            }

            Campaign = preview.Campaign;
            Brand = preview.Brand;
            Artifacts = preview.Artifacts;
            Sources = sources;
            Evidence = loadedEvidence.Current;
            ApprovedEvidence = loadedEvidence.Approved;
            HistoricalEvidence = MergeHistorical(
                HistoricalEvidence, loadedEvidence.Historical);
            TranscriptSourceAssetId = TranscriptArtifactId is { } transcriptArtifactId
                ? Sources.SingleOrDefault(
                    source => source.LegacyArtifactId == transcriptArtifactId)?.Id
                : null;
            ImageSlots = preview.ImageSlots;
            ImagesFilled = preview.ImagesFilled;
            ImagesTotal = preview.ImagesTotal;

            // The transcript does not change during a run, so it is not re-fetched here —
            // that request was pure cost on every single completion.
            RaiseChanged();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
        }
    }

    private async Task LoadCoreAsync(Guid campaignId)
    {
        var corePublished = false;
        try
        {
            var preview = await campaigns.GetPreviewAsync(campaignId);
            var sources = preview.Sources ?? [];
            var transcriptPreview = preview.Artifacts.FirstOrDefault(a => a.Kind == "transcript");

            // Guard against an out-of-order response: if the user switched again while this
            // was in flight, the newer load owns the store.
            if (CampaignId != campaignId)
            {
                return;
            }

            Campaign = preview.Campaign;
            Brand = preview.Brand;
            Artifacts = preview.Artifacts;
            Sources = sources;
            ImageSlots = preview.ImageSlots;
            ImagesFilled = preview.ImagesFilled;
            ImagesTotal = preview.ImagesTotal;

            TranscriptArtifactId = transcriptPreview?.Id;
            TranscriptSourceAssetId = transcriptPreview is null
                ? null
                : Sources.SingleOrDefault(source => source.LegacyArtifactId == transcriptPreview.Id)?.Id;
            IsLoading = false;
            _failedId = null;
            corePublished = true;
            StartDetailsLoad(campaignId, sources, transcriptPreview);
            RaiseChanged();
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

            if (CampaignId == campaignId && !corePublished)
            {
                // Recorded before notifying: the notification re-renders, and the re-render
                // calls back into LoadAsync, which needs to already know this one failed.
                _failedId = LoadError is null ? null : campaignId;
                IsLoading = false;
                RaiseChanged();
            }
        }
    }

    /// <summary>
    /// The preview is the first meaningful paint. Full evidence and transcript content are
    /// independent follow-up requests, so they hydrate concurrently without holding the
    /// campaign header, artifact list, image counts, or Focus navigation behind them.
    /// </summary>
    private void StartDetailsLoad(
        Guid campaignId,
        IReadOnlyList<SourceAssetResponse> sources,
        ArtifactPreviewResponse? transcriptPreview)
    {
        var version = ++_detailsVersion;
        DetailsLoadError = null;
        IsLoadingEvidence = sources.Count > 0;
        IsLoadingTranscript = transcriptPreview is not null;

        var evidenceTask = IsLoadingEvidence
            ? LoadEvidenceDetailsAsync(campaignId, sources, version)
            : Task.CompletedTask;
        var transcriptTask = transcriptPreview is not null
            ? LoadTranscriptDetailsAsync(campaignId, transcriptPreview.Id, version)
            : Task.CompletedTask;
        _detailsTask = Task.WhenAll(evidenceTask, transcriptTask);
    }

    public Task WhenDetailsLoadedAsync() => _detailsTask ?? Task.CompletedTask;

    private async Task LoadEvidenceDetailsAsync(
        Guid campaignId,
        IReadOnlyList<SourceAssetResponse> sources,
        int version)
    {
        try
        {
            var loaded = await FetchEvidenceAsync(campaignId, sources);
            if (CampaignId != campaignId || _detailsVersion != version)
            {
                return;
            }

            Evidence = loaded.Current;
            ApprovedEvidence = loaded.Approved;
            HistoricalEvidence = loaded.Historical;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (CampaignId == campaignId && _detailsVersion == version)
            {
                DetailsLoadError = "Some source evidence couldn't be loaded.";
            }
        }
        finally
        {
            if (CampaignId == campaignId && _detailsVersion == version)
            {
                IsLoadingEvidence = false;
                RaiseChanged();
            }
        }
    }

    private async Task LoadTranscriptDetailsAsync(Guid campaignId, Guid artifactId, int version)
    {
        try
        {
            var (artifact, _) = await campaigns.GetArtifactAsync(campaignId, artifactId);
            if (CampaignId != campaignId || _detailsVersion != version)
            {
                return;
            }

            Transcript = ParseTranscript(artifact.ContentJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (CampaignId == campaignId && _detailsVersion == version)
            {
                DetailsLoadError = "The source transcript couldn't be loaded.";
            }
        }
        finally
        {
            if (CampaignId == campaignId && _detailsVersion == version)
            {
                IsLoadingTranscript = false;
                RaiseChanged();
            }
        }
    }

    public void Clear()
    {
        _inFlight = null;
        _inFlightId = null;
        _failedId = null;
        _detailsTask = null;
        _detailsVersion++;
        CampaignId = null;
        Campaign = null;
        Brand = null;
        Artifacts = [];
        Sources = [];
        Evidence = new Dictionary<Guid, EvidenceRevisionResponse>();
        ApprovedEvidence = new Dictionary<Guid, EvidenceRevisionResponse>();
        HistoricalEvidence = new Dictionary<(Guid, int), EvidenceRevisionResponse>();
        ImageSlots = [];
        ImagesFilled = 0;
        ImagesTotal = 0;
        Transcript = null;
        TranscriptArtifactId = null;
        TranscriptSourceAssetId = null;
        IsLoading = false;
        IsLoadingEvidence = false;
        IsLoadingTranscript = false;
        LoadError = null;
        DetailsLoadError = null;
        RaiseChanged();
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

    public async Task RefreshEvidenceAsync(Guid campaignId, Guid sourceAssetId)
    {
        if (CampaignId != campaignId)
        {
            return;
        }
        var revision = await evidence.GetEvidenceAsync(campaignId, sourceAssetId);
        var approved = revision.Source.ApprovedEvidence is null
            ? null
            : await evidence.GetEvidenceAsync(campaignId, sourceAssetId, approved: true);
        if (CampaignId != campaignId)
        {
            return;
        }
        Evidence = new Dictionary<Guid, EvidenceRevisionResponse>(Evidence)
        {
            [sourceAssetId] = revision,
        };
        var approvedEvidence = new Dictionary<Guid, EvidenceRevisionResponse>(ApprovedEvidence);
        if (approved is not null)
        {
            approvedEvidence[sourceAssetId] = approved;
        }
        else
        {
            approvedEvidence.Remove(sourceAssetId);
        }
        ApprovedEvidence = approvedEvidence;
        if (approved is not null)
        {
            HistoricalEvidence = new Dictionary<(Guid, int), EvidenceRevisionResponse>(HistoricalEvidence)
            {
                [(sourceAssetId, approved.Revision)] = approved,
            };
        }
        Sources = Sources
            .Select(source => source.Id == sourceAssetId ? revision.Source : source)
            .ToList();
        RaiseChanged();
    }

    private async Task<(
        IReadOnlyDictionary<Guid, EvidenceRevisionResponse> Current,
        IReadOnlyDictionary<Guid, EvidenceRevisionResponse> Approved,
        IReadOnlyDictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse> Historical)>
        FetchEvidenceAsync(
        Guid campaignId,
        IReadOnlyList<SourceAssetResponse> sources)
    {
        if (sources.Count == 0)
        {
            return (
                new Dictionary<Guid, EvidenceRevisionResponse>(),
                new Dictionary<Guid, EvidenceRevisionResponse>(),
                new Dictionary<(Guid, int), EvidenceRevisionResponse>());
        }
        var current = new System.Collections.Concurrent.ConcurrentDictionary<Guid, EvidenceRevisionResponse>();
        var approved = new System.Collections.Concurrent.ConcurrentDictionary<Guid, EvidenceRevisionResponse>();
        var historical = new System.Collections.Concurrent.ConcurrentDictionary<
            (Guid, int), EvidenceRevisionResponse>();
        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (source, ct) =>
            {
                current[source.Id] = await evidence.GetEvidenceAsync(campaignId, source.Id, ct: ct);
                if (source.ApprovedEvidence is not null)
                {
                    var latest = await evidence.GetEvidenceAsync(
                        campaignId, source.Id, approved: true, ct: ct);
                    approved[source.Id] = latest;
                    historical[(source.Id, latest.Revision)] = latest;
                }
            });
        return (
            new Dictionary<Guid, EvidenceRevisionResponse>(current),
            new Dictionary<Guid, EvidenceRevisionResponse>(approved),
            new Dictionary<(Guid, int), EvidenceRevisionResponse>(historical));
    }

    public async Task EnsureEvidenceRevisionAsync(
        Guid campaignId, Guid sourceAssetId, int revision)
    {
        var key = (sourceAssetId, revision);
        if (CampaignId != campaignId || HistoricalEvidence.ContainsKey(key))
        {
            return;
        }
        var loaded = await evidence.GetEvidenceAsync(
            campaignId, sourceAssetId, revision: revision);
        if (CampaignId != campaignId)
        {
            return;
        }
        HistoricalEvidence = new Dictionary<(Guid, int), EvidenceRevisionResponse>(HistoricalEvidence)
        {
            [key] = loaded,
        };
        RaiseChanged();
    }

    private static Dictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse>
        MergeHistorical(
            IReadOnlyDictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse> existing,
            IReadOnlyDictionary<(Guid SourceAssetId, int Revision), EvidenceRevisionResponse> loaded)
    {
        var merged = new Dictionary<(Guid, int), EvidenceRevisionResponse>(existing);
        foreach (var item in loaded)
        {
            merged[item.Key] = item.Value;
        }
        return merged;
    }
}
