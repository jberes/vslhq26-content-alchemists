namespace Castmill.Core;

/// <summary>
/// Every user owns exactly one tenant, created at registration (ADR-011).
/// Tenant isolation is structural: all tenant-scoped entities carry TenantId
/// and are covered by EF global query filters (G1).
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public sealed class Campaign : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Single-owner model (ADR-011): the Identity user who created the campaign.</summary>
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Brief { get; set; }

    /// <summary>The brand steering this campaign's generation — null means "None".
    /// No FK constraint; brand delete detaches campaigns explicitly (house style).</summary>
    public Guid? BrandId { get; set; }

    /// <summary>JSON array of <c>CampaignLink</c> — home page, GitHub pages, docs — that
    /// inform generation. Validated at the boundary (ADR-003 typed-JSON precedent).</summary>
    public string? ContextJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Artifact : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    /// <summary>Typed JSON content (ADR-003); schema-validated at the boundary before persist.</summary>
    public required string ContentJson { get; set; }
    /// <summary>Optimistic-concurrency counter surfaced to clients as an ETag.</summary>
    public long Version { get; set; }

    /// <summary>
    /// Review state: Draft, InReview, Queued or Published (<see cref="ArtifactStatus"/>).
    /// Stored as a string so the set can grow without a migration and so the column reads
    /// plainly in the database. Every client surface encodes it twice — a 3 px bar AND the
    /// status colour — so state never depends on colour alone (frontend ADR-F12).
    /// </summary>
    public string Status { get; set; } = ArtifactStatus.Draft;

    /// <summary>
    /// The transcript segment ids this artifact cites, as a JSON array — extracted from
    /// ContentJson BY THE DATABASE (computed column, JSON_QUERY). Provenance threads must
    /// draw from the list projection without a per-card fetch, and ADR-003 forbids loading
    /// ContentJson for list views; a computed column satisfies both. Null when the content
    /// has no citations (transcripts, clip lists).
    /// </summary>
    public string? CitationsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The four artifact states, in the order work moves through them. Kept as constants
/// rather than an enum so the persisted value is readable and adding a state does not
/// renumber anything.
/// </summary>
public static class ArtifactStatus
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Queued = "Queued";
    public const string Published = "Published";

    public static readonly string[] All = [Draft, InReview, Queued, Published];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// Immutable snapshot of an artifact taken before a mutation (ADR-017). Bounded
/// ring per artifact — the oldest row is trimmed once the cap is reached — so the
/// Focus Mode version filmstrip can compare and restore without unbounded growth.
/// </summary>
public sealed class ArtifactRevision : ITenantScoped
{
    /// <summary>How many revisions are retained per artifact.</summary>
    public const int RingSize = 10;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ArtifactId { get; set; }
    /// <summary>The artifact's Version at the time of the snapshot.</summary>
    public long Version { get; set; }
    public required string Title { get; set; }
    public required string ContentJson { get; set; }
    /// <summary>What caused the snapshot: manual-save | image-placed | restore.</summary>
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One typed image slot in a campaign's image plan (ADR-012). Slots are reserved
/// when the run starts — the UI counts and badges rows, which a bag of prompts
/// inside an artifact could never support.
/// </summary>
public sealed class ImageSlot : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    /// <summary>
    /// The artifact this slot belongs to, for kinds that are per-artifact rather than
    /// per-campaign — a campaign can hold several blogs and each needs its own header and
    /// inline images. Null means campaign-wide (the youtube-thumbnail and social-card slots,
    /// and every slot reserved before this column existed).
    /// </summary>
    public Guid? ArtifactId { get; set; }
    /// <summary>youtube-thumbnail · blog-header · blog-inline-1..3 · social-card.</summary>
    public required string Kind { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public string? Prompt { get; set; }
    /// <summary>Model alias or provider name to render with; null uses the "image" alias.</summary>
    public string? ModelAlias { get; set; }
    /// <summary>Transcript segment this image illustrates — prompts stay provenance-labelled.</summary>
    public string? SourceSegmentId { get; set; }
    /// <summary>Composited headline (thumbnails only); applied server-side, never prompted (ADR-013).</summary>
    public string? HeadlineText { get; set; }
    public bool SafeArea { get; set; }
    /// <summary>Empty | Filled.</summary>
    public required string State { get; set; }
    /// <summary>The image as published — composited when the slot carries a headline.</summary>
    public string? PublishedUrl { get; set; }
    /// <summary>
    /// The un-composited image kept alongside it, so a headline edit re-composites
    /// instead of paying for another generation (ADR-013).
    /// </summary>
    public string? BaseImagePath { get; set; }
    public string? BaseImageUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The Wire's mirror of schedule state (ADR-016). The broker remains the
/// scheduler of record — this row exists so the strip renders on load, survives
/// reload, and can hold entries the broker has not accepted yet.
/// </summary>
public sealed class ScheduleEntry : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid? ArtifactId { get; set; }
    public required string ChannelId { get; set; }
    /// <summary>Broker's id once accepted; null while the entry is local-only.</summary>
    public string? BrokerPostId { get; set; }
    public required string Text { get; set; }
    public string? MediaUrl { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    /// <summary>Draft | Queued | Sent | Error.</summary>
    public required string Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One fan-out run. Persisted (not in-memory) because the API is stateless (G3):
/// the Press Run polls progress from any instance while the generate call is
/// still in flight on another.
/// </summary>
public sealed class GenerationRun : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    /// <summary>Running | Completed.</summary>
    public required string Status { get; set; }

    /// <summary>content | image. The Press Run polls content runs; the Image Studio
    /// polls image runs — the discriminator keeps the two reveals from crossing.</summary>
    public string Kind { get; set; } = "content";

    /// <summary>The slot an image run is generating into; null for content runs.</summary>
    public Guid? SlotId { get; set; }

    public int TotalKinds { get; set; }
    /// <summary>Per-kind outcomes as they complete: [{ kind, success, artifactId, error, durationMs }].</summary>
    public required string ItemsJson { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One generated take for an image slot. Variants persist (they used to evaporate with
/// the component that requested them) so the studio can show every take, keep/discard
/// them, and steer new takes from old ones.
/// </summary>
public sealed class ImageVariant : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid SlotId { get; set; }

    /// <summary>Full-size public WebP.</summary>
    public required string Url { get; set; }
    public required string BlobPath { get; set; }

    /// <summary>Gallery thumbnail (480 px longest edge), public WebP.</summary>
    public required string ThumbUrl { get; set; }
    public required string ThumbBlobPath { get; set; }

    public required string Model { get; set; }

    /// <summary>The EXACT prompt sent — post brand-injection, post steering.</summary>
    public required string Prompt { get; set; }

    /// <summary>The user's adjustment note when this take was steered from another.</summary>
    public string? SteeringNote { get; set; }

    /// <summary>The variant this one was steered from — the take's lineage.</summary>
    public Guid? SourceVariantId { get; set; }

    /// <summary>Candidate | Kept | Discarded. Blobs are never deleted (immutable cache).</summary>
    public required string State { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Asset : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string BlobPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An image in a brand's kit: a logo, a reusable background, a face that may appear in
/// generated imagery. The bytes live on an ordinary <see cref="Asset"/> (private blob,
/// SAS-read); this row adds brand scoping, a kind and a prompt-usable label.
/// </summary>
public sealed class BrandAsset : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BrandId { get; set; }
    public Guid AssetId { get; set; }

    /// <summary>logo | background | face | other.</summary>
    public required string Kind { get; set; }

    /// <summary>Display name, doubling as prompt text ("the host, short dark hair").</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Per-brand steering for one generator kind — "our newsletter template", "our blog
/// voice". The default template for a kind is appended to that generator's instructions
/// on every run for campaigns carrying the brand.
/// </summary>
public sealed class BrandTemplate : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BrandId { get; set; }

    /// <summary>A generator kind (blog, newsletter, social-x, …) — validated at the boundary.</summary>
    public required string Kind { get; set; }

    public required string Name { get; set; }

    /// <summary>The steering prompt appended to the generator's instructions.</summary>
    public required string SteeringPrompt { get; set; }

    /// <summary>The template auto-applied for this kind; at most one per (brand, kind).</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BrandProfile : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? StyleCardJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-user setting. Values of secret kinds (Foundry key, broker token) are stored
/// AES-256-GCM encrypted (Phase B3); plaintext secret values must never reach this row.
/// </summary>
public sealed class UserSetting : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool IsEncrypted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Web clip-export job (ADR-008): ffmpeg runs in a Container Apps job fed by a
/// storage queue — API instances never transcode in-process. The worker reports
/// completion through a token-authenticated callback; only the token's SHA-256
/// hash is stored.
/// </summary>
public sealed class ClipJob : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    /// <summary>clip | frame — a frame job extracts one still at InSeconds (ADR-014).</summary>
    public required string Mode { get; set; }
    public double InSeconds { get; set; }
    public double OutSeconds { get; set; }
    /// <summary>Crop to 9:16 vertical for short-form platforms.</summary>
    public bool CropVertical { get; set; }
    /// <summary>Burn captions into the video (worker expects SRT).</summary>
    public bool BurnCaptions { get; set; }
    public string? CaptionsSrt { get; set; }
    /// <summary>Queued | Processing | Succeeded | Failed.</summary>
    public required string Status { get; set; }
    public string? OutputBlobPath { get; set; }
    public string? Error { get; set; }
    /// <summary>SHA-256 of the worker callback token; plaintext exists only in the queue message.</summary>
    public required string CallbackTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Security-relevant events: sign-in, password change, token revocation, publish.</summary>
public sealed class AuditEvent : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
