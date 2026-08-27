using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

// ---- Campaigns -------------------------------------------------------------

public sealed record CampaignCreateRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: MaxLength(8000)] string? Brief,
    Guid? BrandId = null,
    IReadOnlyList<CampaignLink>? Links = null,
    [property: MaxLength(30)] string? ContentType = null,
    [property: MaxLength(30)] string? Intent = null,
    IReadOnlyList<string>? OutputRecipe = null,
    bool SkipSeoAnalysis = false);

public sealed record CampaignUpdateRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: MaxLength(8000)] string? Brief,
    Guid? BrandId = null,
    IReadOnlyList<CampaignLink>? Links = null,
    [property: MaxLength(20)] string Status = CampaignStatus.Draft,
    [property: MaxLength(30)] string? ContentType = null,
    [property: MaxLength(30)] string? Intent = null,
    IReadOnlyList<string>? OutputRecipe = null,
    bool SkipSeoAnalysis = false);

public sealed record CampaignResponse(
    Guid Id, Guid OwnerId, string Name, string? Brief,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? BrandId = null,
    IReadOnlyList<CampaignLink>? Links = null,
    string Status = CampaignStatus.Draft,
    string? ContentType = null,
    string? Intent = null,
    IReadOnlyList<string>? OutputRecipe = null,
    bool SkipSeoAnalysis = false);

/// <summary>One artifact row on the workspace dashboard (review queue / aging drafts).</summary>
public sealed record DashboardArtifact(
    Guid CampaignId, string CampaignName, Guid ArtifactId,
    string Kind, string Title, string Status, DateTimeOffset UpdatedAt);

/// <summary>Counts for the four editorial workflow bins shown on the Review desk.</summary>
public sealed record ReviewDeskCounts(
    int Draft,
    int InReview,
    int Reviewed,
    int Published);

/// <summary>One bounded page from a single editorial workflow bin.</summary>
public sealed record ReviewDeskResponse(
    string Status,
    int Total,
    IReadOnlyList<DashboardArtifact> Items);

/// <summary>Per-campaign counters for the campaigns index cards.</summary>
public sealed record CampaignCounts(
    Guid CampaignId, int Artifacts, int InReview, int ImagesFilled, int ImagesTotal,
    /// <summary>Most recently placed image, cache-busted — the card's media band. Null keeps the duotone placeholder.</summary>
    string? HeroImageUrl = null,
    int Draft = 0,
    int Reviewed = 0,
    int Published = 0);

/// <summary>
/// The workspace dashboard in ONE call. The front page and the campaigns index previously
/// fetched a full preview per campaign (N+1, with every artifact preview and slot in each
/// payload) to derive exactly this.
/// </summary>
public sealed record DashboardResponse(
    IReadOnlyList<DashboardArtifact> ReviewQueue,
    IReadOnlyList<DashboardArtifact> AgingDrafts,
    IReadOnlyList<CampaignCounts> Campaigns,
    int EmptySlots,
    int CampaignsWithEmptySlots,
    IReadOnlyList<string> EmptySlotModels,
    Guid? FirstEmptySlotCampaign,
    /// <summary>Reviewed artifacts waiting for a slot on the Wire (status Queued).</summary>
    IReadOnlyList<DashboardArtifact>? ReadyToSchedule = null,
    ReviewDeskCounts? ReviewCounts = null);

// ---- Artifacts -------------------------------------------------------------

public sealed record ArtifactCreateRequest(
    [property: Required, MinLength(1), MaxLength(50)] string Kind,
    [property: Required, MinLength(1), MaxLength(300)] string Title,
    [property: Required] string ContentJson,
    Guid? ParentArtifactId = null);

public sealed record ArtifactUpdateRequest(
    [property: Required, MinLength(1), MaxLength(300)] string Title,
    [property: Required] string ContentJson);

/// <summary>List-view projection (ADR-003): everything except the heavy content.</summary>
public sealed record ArtifactPreviewResponse(
    Guid Id, Guid CampaignId, string Kind, string Title, string Status, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    /// <summary>Evidence citation ids this artifact cites — legacy segment ids remain readable.</summary>
    IReadOnlyList<string>? Citations = null,
    Guid? ParentArtifactId = null,
    bool IsPlaceholder = false,
    IReadOnlyList<ApprovedEvidenceRevision>? Evidence = null);

public sealed record ArtifactResponse(
    Guid Id, Guid CampaignId, string Kind, string Title, string ContentJson, string Status,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? ParentArtifactId = null);

/// <summary>
/// Status transitions are their own action, not part of a content save: "mark reviewed" and
/// "edit the copy" are different intents with different guards (roadmap E6.9's review gate).
/// </summary>
public sealed record ArtifactStatusRequest(
    [property: Required, MinLength(1), MaxLength(20)] string Status);

// ---- Artifact revisions (B9.7 / ADR-017) -----------------------------------

public sealed record ArtifactRevisionResponse(
    Guid Id, Guid ArtifactId, long Version, string Title, string Reason, DateTimeOffset CreatedAt);

public sealed record ArtifactRevisionDetailResponse(
    Guid Id, Guid ArtifactId, long Version, string Title, string Reason,
    string ContentJson, DateTimeOffset CreatedAt);

// ---- Image plan (B9.1 / ADR-012) -------------------------------------------

public sealed record ImageSlotResponse(
    Guid Id, Guid CampaignId, string Kind, int TargetWidth, int TargetHeight,
    string? Prompt, string? ModelAlias, string? SourceSegmentId,
    string? HeadlineText, bool SafeArea, string State, string? PublishedUrl, string? BaseImageUrl,
    DateTimeOffset UpdatedAt,
    /// <summary>Solid band behind the headline, "#RRGGBB"; null = none.</summary>
    string? HeadlineBackground = null,
    /// <summary>
    /// The artifact this slot belongs to, for per-artifact kinds (a specific blog's header
    /// and inline images). Null for campaign-wide slots. Placing an image rewrites THIS
    /// artifact's stub markers.
    /// </summary>
    Guid? ArtifactId = null,
    /// <summary>Auto | Manual.</summary>
    string PromptMode = "Auto",
    /// <summary>Explicitly selected brand-kit reference rows. Product references attach automatically.</summary>
    IReadOnlyList<Guid>? ReferenceAssetIds = null,
    /// <summary>Thumb of the best un-discarded take (kept first, then newest) — lets the sheet
    /// preview a slot that has generated work but nothing placed yet. Null when no takes.</summary>
    string? LatestTakeThumbUrl = null,
    /// <summary>Candidate + kept takes. Discarded takes do not satisfy a future batch target.</summary>
    int ActiveTakeCount = 0,
    /// <summary>The take explicitly marked Kept, even when it has not been placed yet.</summary>
    string? KeeperThumbUrl = null,
    /// <summary>Keeper id used by authenticated full-resolution download actions.</summary>
    Guid? KeeperVariantId = null);

public sealed record ImageSlotPatchRequest(
    [property: MaxLength(4000)] string? Prompt,
    [property: MaxLength(100)] string? ModelAlias,
    [property: MaxLength(50)] string? SourceSegmentId,
    /// <summary>Composited after generation (ADR-013) — never sent to the model.</summary>
    [property: MaxLength(32)] string? HeadlineText,
    bool? SafeArea,
    [property: MaxLength(10)] string? PromptMode = null,
    IReadOnlyList<Guid>? ReferenceAssetIds = null,
    /// <summary>Clears a card override so it inherits the workspace image-model default.</summary>
    bool? UseDefaultModel = null);

/// <summary>Adds an image card to a specific content artifact. The server chooses a
/// platform-correct shape from the artifact kind unless explicit dimensions are supplied.</summary>
public sealed record ImageSlotCreateRequest(
    [property: Required] Guid ArtifactId,
    [property: MaxLength(4000)] string? Prompt = null,
    [property: MaxLength(10)] string PromptMode = "Auto",
    [property: Range(256, 4096)] int? TargetWidth = null,
    [property: Range(256, 4096)] int? TargetHeight = null);

public sealed record GenerateVariantsRequest(
    [property: Range(1, 6)] int Variants = 2,
    /// <summary>
    /// Render THIS batch with a specific provider or Foundry alias, without changing the
    /// slot's saved default. Comparing two models on the same prompt is the normal way to
    /// work; making that comparison require a persisted settings change was not.
    /// </summary>
    [property: MaxLength(100)] string? ModelAlias = null);

/// <summary>A persisted take for a slot. State: Candidate | Kept | Discarded.</summary>
public sealed record ImageVariantResponse(
    Guid Id, Guid SlotId, string Url, string ThumbUrl, string Model, string State,
    string? SteeringNote, Guid? SourceVariantId, int Width, int Height, DateTimeOffset CreatedAt);

public sealed record VariantStateRequest(
    [property: Required, MaxLength(20)] string State);

/// <summary>Steer a new take from an existing one: original prompt + the adjustment.</summary>
public sealed record SteerVariantRequest(
    /// <summary>Optional: references are real image inputs (ADR-025), so a new take steered
    /// only by a selected face or background needs no typed adjustment.</summary>
    [property: MaxLength(1000)] string? Note,
    [property: Range(1, 3)] int Variants = 1,
    /// <summary>Per-batch model override, exactly as on generate. Null keeps the slot's default.</summary>
    [property: MaxLength(100)] string? ModelAlias = null);

/// <summary>Result envelope for generate/steer: the run id (pollable) + persisted takes.</summary>
public sealed record VariantBatchResponse(
    Guid RunId, Guid SlotId, string Kind,
    IReadOnlyList<ImageVariantResponse> Variants,
    IReadOnlyList<string>? Failures);

public sealed record ImageBatchGenerateRequest(
    [property: Range(1, 6)] int VariantsPerSlot = 1,
    /// <summary>Optional explicit content-item scope; null generates across the campaign.</summary>
    Guid? ArtifactId = null);

public sealed record ImageBatchSlotResult(
    Guid SlotId,
    string Kind,
    string Outcome,
    int RequestedVariants,
    int SucceededVariants,
    int FailedVariants,
    string? ErrorCode = null,
    string? Error = null);

public sealed record ImageBatchResponse(
    Guid RunId,
    int EligibleSlots,
    int SucceededSlots,
    int FailedSlots,
    int SkippedSlots,
    int SucceededVariants,
    int FailedVariants,
    IReadOnlyList<ImageBatchSlotResult> Slots);

public sealed record PlaceVariantRequest(
    /// <summary>The persisted variant to place. Preferred over Url.</summary>
    Guid? VariantId,
    /// <summary>Legacy: a variant URL from this slot's generate response.</summary>
    [property: MaxLength(2000), Url] string? Url,
    /// <summary>Blog artifact whose ![stub:kind]() marker gets replaced; optional.</summary>
    Guid? BlogArtifactId);

public sealed record CompositeHeadlineRequest(
    [property: Required] Guid CampaignId,
    [property: Required] Guid SlotId,
    [property: Required, MinLength(1), MaxLength(32)] string Headline,
    bool SafeArea = true,
    /// <summary>Solid band behind the text, "#RRGGBB". Null or unparseable = no band.</summary>
    [property: MaxLength(9)] string? HeadlineBackground = null);

// ---- Schedule mirror (B9.6 / ADR-016) --------------------------------------

public sealed record ScheduleEntryCreateRequest(
    [property: Required] Guid CampaignId,
    Guid? ArtifactId,
    [property: Required, MinLength(1), MaxLength(200)] string ChannelId,
    [property: Required, MinLength(1)] string Text,
    [property: Required] DateTimeOffset ScheduledAt,
    [property: MaxLength(2000)] string? MediaUrl,
    /// <summary>Push to the broker immediately; false keeps the entry local (Draft).</summary>
    bool PushToBroker = true);

public sealed record ScheduleEntryMoveRequest(
    [property: Required] DateTimeOffset ScheduledAt);

public sealed record ScheduleEntryResponse(
    Guid Id, Guid CampaignId, Guid? ArtifactId, string ChannelId, string? BrokerPostId,
    string Text, string? MediaUrl, DateTimeOffset ScheduledAt, string Status, string? Error,
    DateTimeOffset UpdatedAt);

public sealed record PublishReadinessResponse(
    bool BrokerConfigured,
    bool CredentialStored,
    bool Ready,
    string Detail,
    bool CanStageLocally = true,
    bool CanSchedule = false,
    bool CanSendNow = false,
    bool CanUseNextSlot = false);

// ---- Assets (metadata only until B3 wires blob SAS) ------------------------

public sealed record AssetCreateRequest(
    [property: Required, MinLength(1), MaxLength(400)] string FileName,
    [property: Required, MinLength(1), MaxLength(200)] string ContentType,
    [property: Range(0, 10_737_418_240)] long SizeBytes);

public sealed record AssetResponse(
    Guid Id, string FileName, string ContentType, long SizeBytes,
    string BlobPath, DateTimeOffset CreatedAt);

// ---- Brand profiles: see Resources/BrandDtos.cs ----------------------------

// ---- Settings (plaintext kinds only; encrypted kinds arrive in B3) ---------

public sealed record SettingWriteRequest(
    [property: Required, MaxLength(4000)] string Value);

public sealed record SettingResponse(string Key, string Value, DateTimeOffset UpdatedAt);

// ---- SEO/AEO targets: research BEFORE generation, then steering for every generator ----

/// <summary>
/// One keyword under consideration. Metrics are nullable because the research step must work
/// with no DataForSEO credential at all — a model-proposed keyword with no volume is still a
/// usable target, and pretending it has a volume of 0 would be a lie.
/// </summary>
public sealed record SeoTarget(
    [property: Required, MinLength(1), MaxLength(200)] string Term,
    long? Volume = null,
    double? Difficulty = null,
    /// <summary>volume / (difficulty + 10) — the existing ranking heuristic.</summary>
    double? Opportunity = null,
    /// <summary>"provider" when DataForSEO returned it, "model" when only the model proposed it.</summary>
    string Source = "model",
    double? Competition = null,
    double? Cpc = null,
    string? Intent = null);

/// <summary>What the research step proposes. Nothing here is persisted until the user picks.</summary>
public sealed record SeoResearchResponse(
    IReadOnlyList<SeoTarget> Keywords,
    /// <summary>Real questions to answer — People-Also-Ask, the knowledge gateway, the transcript.</summary>
    IReadOnlyList<SeoQuestion> Questions,
    /// <summary>False when no SEO provider is configured: the keywords are the model's alone.</summary>
    bool HasProviderMetrics,
    /// <summary>Anything the user should know about how thin the data is.</summary>
    IReadOnlyList<string> Notes,
    /// <summary>Exact provider endpoint paths that completed for this research run.</summary>
    IReadOnlyList<string>? ProviderLookups = null);

public sealed record SeoQuestion(
    [property: Required, MinLength(1), MaxLength(300)] string Question,
    /// <summary>"paa" (Google People also ask), "knowledge-base", or "transcript".</summary>
    string Source = "transcript");

public sealed record SeoResearchRequest(
    [property: Required] Guid CampaignId,
    Guid? TranscriptArtifactId = null);

/// <summary>The required, persisted analysis that precedes content generation.</summary>
public sealed record SeoDeepAnalysisRequest(
    [property: Required] Guid CampaignId,
    Guid? TranscriptArtifactId = null,
    [property: MaxLength(2000), Url] string? SiteUrl = null);

public sealed record SeoSerpResult(
    int Rank, string Title, string Url, string Domain, string? Description = null);

public sealed record SeoSerpSnapshot(
    string Keyword,
    string? AiOverview,
    string? FeaturedSnippet,
    IReadOnlyList<SeoSerpResult> OrganicResults);

public sealed record SeoCitation(string Title, string Url, string Domain, bool IsOwnDomain = false);

public sealed record SeoAeoEngineResult(
    string Provider,
    string Label,
    bool Succeeded,
    bool DomainCited,
    string? Answer,
    IReadOnlyList<SeoCitation> Citations,
    string? Error = null);

public sealed record SeoAeoScorecard(
    double? VisibilityPercent,
    int EnginesSucceeded,
    int EnginesCitingDomain,
    IReadOnlyList<SeoAeoEngineResult> Engines);

public sealed record SeoRankedKeyword(
    string Term,
    int Position,
    long? Volume,
    double? Difficulty,
    double? EstimatedTraffic,
    string Url,
    string? Intent = null);

public sealed record SeoAuthoritySnapshot(
    string Domain,
    double? Rank,
    long? Backlinks,
    long? ReferringDomains,
    long? ReferringMainDomains,
    long? BrokenBacklinks,
    double? SpamScore);

public sealed record SeoPositionFootprint(
    long Position1,
    long Positions2To3,
    long Positions4To10,
    long TotalOrganic,
    double? EstimatedTraffic);

public sealed record SeoCompetitorSnapshot(
    string Domain,
    int BestSerpPosition,
    SeoAuthoritySnapshot? Authority,
    SeoPositionFootprint? Footprint,
    bool IsOwnDomain = false,
    int? TopicKeywordCount = null,
    double? TopicVisibility = null,
    double? TopicEstimatedTraffic = null,
    double? TopicAveragePosition = null);

public sealed record SeoContentAngle(
    string Angle,
    string AudienceNeed,
    string SuggestedAsset,
    string TargetKeyword,
    string Rationale);

public sealed record SeoSectionStatus(string Section, bool Available, string Detail);

/// <summary>The expensive report-only datasets. Kept behind one optional property so older
/// persisted reports remain readable while new reports can grow without another artifact kind.</summary>
public sealed record SeoDeepInsights(
    SeoAeoScorecard Aeo,
    IReadOnlyList<SeoTarget> KeywordGaps,
    IReadOnlyList<SeoRankedKeyword> RankedKeywords,
    SeoAuthoritySnapshot? SiteAuthority,
    IReadOnlyList<SeoCompetitorSnapshot>? Competitors,
    IReadOnlyList<SeoContentAngle> ContentAngles,
    IReadOnlyList<SeoSectionStatus> Sections,
    DateTimeOffset AnglesGeneratedAt);

public sealed record SeoAnalysisReportResponse(
    Guid ReportArtifactId,
    DateTimeOffset GeneratedAt,
    SeoResearchResponse Research,
    SeoSerpSnapshot Serp,
    IReadOnlyList<string> Recommendations,
    string Status = "Draft",
    string? SiteUrl = null,
    string? CampaignBrief = null,
    SeoDeepInsights? Insights = null,
    bool InputsStale = false,
    bool AnglesStale = false,
    bool ShareStale = false,
    DateTimeOffset? SharedAt = null);

public sealed record SeoAngleRegenerationResponse(
    Guid ReportArtifactId,
    IReadOnlyList<SeoContentAngle> Angles,
    DateTimeOffset GeneratedAt);

public sealed record YoutubeTitleRegenerationRequest(string? Steering = null);
public sealed record YoutubeTitleOptionResponse(
    string Slot, string Title, string Angle, double Score, string Rationale);
public sealed record YoutubeTitleRegenerationResponse(
    Guid ArtifactId, long Version, YoutubeTitleOptionResponse Option);

/// <summary>
/// The chosen targets, saved on the CAMPAIGN so every later run — including content added
/// weeks afterwards — is written against the same keywords.
/// </summary>
public sealed record SeoTargetsRequest(
    [property: MaxLength(200)] string? PrimaryKeyword,
    IReadOnlyList<SeoTarget>? Keywords = null,
    IReadOnlyList<SeoQuestion>? Questions = null);

public sealed record SeoTargetsResponse(
    string? PrimaryKeyword,
    IReadOnlyList<SeoTarget> Keywords,
    IReadOnlyList<SeoQuestion> Questions);
