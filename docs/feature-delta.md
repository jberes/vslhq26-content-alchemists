# Feature delta: PostFoundry → Castmill

> **Canonical backlog:** This file is Castmill's source of truth for feature backlog
> scope and completion status. Update it in the same change that completes or materially
> changes a backlog item.

The original backlog was derived from [post-foundry.md](post-foundry.md), the "as built"
spec of the predecessor product. It now also includes Castmill-specific requirements that
were discovered while implementing the delta.

Status legend:

- [x] ✅ **Completed** — implemented end to end and covered by automated tests.
- [ ] **Backlog** — not yet complete. A note may describe a partial implementation.
- 🆕 **ADDED TO BACKLOG** — a Castmill requirement added after the original delta review.

Current delivery snapshot (updated 2026-08-20):

- [x] ✅ **Completed:** 1–16, 22–28, 32–34, 47, and additions N1–N4 and N6–N30.
- [ ] **Still open:** 17–21, 29–31, 35–46, and additions N5 and N31–N48. Partially
  implemented items remain open until their complete acceptance criteria are delivered.

Deliberately excluded: everything Castmill already matches or beats — two-pass blog audit +
Tech Edit, clip export with captions/9:16, brand-from-URL, starter templates, schedule
mirror + broker cancel, press-run narration, diarization, blog pre-publish validators,
image stubs + WebP publish-for-web, prompt-contract rules, optimistic concurrency, light
previews, soft-fail sections, keyboard shortcuts, breadcrumbs, readiness-with-reason.

---

## Group 1 — Image generation & product fidelity *(§7.7, §7.9, §7.14)*

- [x] ✅ **1. Reference-image generation** — Azure Foundry image generation accepts and
  transmits the selected reference-image bytes; providers without reference-image support
  are rejected instead of silently degrading to prompt-only generation.
- [x] ✅ **2. The product-fidelity rule** — a `product` asset category whose screenshots
  (up to 3) auto-attach to image generation with reproduce-the-real-UI instructions.
- [x] ✅ **3. Auto vs manual prompt per image card** — Auto rebuilds from the current
  artifact, campaign context, concept, and references each run; Manual is used verbatim.
- [x] ✅ **4. Thumbnail concepts before pixels** — concept descriptions are generated as
  a cheap artifact and the selected concept drives pixel generation.
- [x] ✅ **5. Generate-all-pending images** — one Image Studio action discovers every pending
  campaign slot (or an explicit content-item scope), deterministically skips filled, rendering,
  ineligible and already-satisfied slots, then generates each missing take through its saved
  prompt, model and reference path. A durable, re-attachable campaign run narrates real
  slot/variant progress and typed failures; partial success persists, retry targets only missing
  takes, and generation never places or publishes a take into content. Preflight shows the
  eligible/skipped count and estimated take cost before confirmation; campaign locking prevents
  competing batch/single-slot work, and provider readiness/reference-capability failures remain
  per-slot results rather than aborting successful siblings. Covered by `ImageVariantTests`,
  `ImageStudioGalleryTests` and the CSS token-reference gate.

## Group 2 — SEO/AEO depth *(§7.6, §7.7, §9)*

*Castmill already has research-before-run, PAA, the keyword plan, targets-into-prompts and
the cluster map; these go deeper.*

- [x] ✅ **6. AEO scorecard** — query 4 answer engines (ChatGPT/Gemini/Claude/Perplexity, web-search
   on) with the audience-shaped question; per-engine cites/does-not-cite cards, citation
   chips, visibility %; failed engines excluded from the denominator.
- [x] ✅ **7. Competitor deep-dive** — multi-keyword SERP Competitors discovers topical
  competitors and measures visibility, overlap, average position and ETV; backlink summary
  and domain rank overview then enrich each competitor with authority and position footprint
  (#1 / #2–3 / #4–10), with your row pinned.
- [x] ✅ **8. "Keywords you already rank for"** — ranked-keywords endpoint; frames angles as
   extend/defend vs duplicate.
- [x] ✅ **9. Authority-aware angle steering** — backlink gap vs best competitor quantitatively
   steers generation toward achievable (long-tail / low-KD) targets.
- [x] ✅ **10. Staleness badges** — derived `inputsStale` / `anglesStale` / `shareStale` signals
    ("Inputs changed — re-run analysis", "New SEO data since these angles").
- [x] ✅ **11. Content-angle regeneration loop** — angles regenerated *from the report* (gaps, PAA,
    uncited engines), each one-click seeding a blog. Castmill's cluster map + Scout are
    adjacent; the report→angles→seed loop with staleness is the delta.
- [x] ✅ **12. AI-Overview & featured-snippet surfacing** — the persisted deep analysis
  captures and displays DataForSEO AI-overview and featured-snippet answer surfaces and
  carries the resulting strategy into downstream generation prompts.
- [x] ✅ **13. Angle-labeled, scored YouTube titles** with per-slot regenerate (A/B/C slots; angle
    taxonomy: seo / curiosity / how-to / problem-solution / thought-leadership).
- [x] ✅ **14. Suggested pinned comment generator** (references a concrete moment, ends with a
    question).
- [x] ✅ **15. Two-pass (outline → draft → self-audit) for the YouTube description**, audited
    against platform rules (125-char hook, hashtag hoist, chapter keywords). Castmill's
    two-pass is blog-only.
- [x] ✅ **16. Per-artifact grounding panel** — "Real search data informing this content" on the
    YouTube/blog screens (keyword badges, SERP list, AI-Overview badge, PAA), with an
    honest empty state when no provider is configured.

## Group 3 — Collaboration & sharing *(§5, §7.14)*

*Castmill is single-owner (ADR-011); this whole group is a strategic reversal, not
incremental work.*

- [ ] **17. Project → org sharing** (tenant-wide read) with a Private/Public badge;
    corporate-domain classifier.
- [ ] **18. Invite-by-name project members** — type-ahead user search, cross-tenant, removable.
- [ ] **19. Asset-level sharing** (AssetShare) alongside project and settings shares.
- [ ] **20. Settings/secret sharing** with explicit enumeration of which secrets become visible.
- [ ] **21. M365/Entra sign-in + Graph avatars/identity chrome** — *listed for completeness
    only*: conflicts with ADR-010 (email+password, no external IdP) and the standing
    no-Azure-app-registration constraint.

## Group 4 — Structure & content model *(§2, §6.3, §7.3–7.5)*

- [x] ✅ **22. Artifact parent/child hierarchy** — a blog owns its own social posts and images.
    Castmill scoped image slots per-blog already; socials/email are campaign-level only.
- [x] ✅ **23. Per-blog social sets** + blog accordion tree with availability dots / approval
    checks / count badges.
- [x] ✅ **24. Multi-file sources** — several recordings combine into one campaign transcript with
    per-source labels.
- [x] ✅ **25. Placeholder blog seeded from the strongest angle** (renders the Generate panel and
    seeds its first generation with that angle).
- [x] ✅ **26. Summary screen/artifact** — persisted, editable executive summary + numbered key
    takeaways + keyword opportunities. Castmill's AI brief summary (run flow) is transient
    and never stored; no summary surface exists after the run.
- [x] ✅ **27. Campaign status lifecycle** (draft → ready) with a status badge, plus inline
    campaign rename in the header.
- [x] ✅ **28. Content-type field on the brief** (tutorial / product demo / webinar / thought
    leadership) steering every generator.

## Group 5 — Publishing & distribution *(§7.10–7.13, §7.8)*

- [ ] **29. Live vendor publish queue** — the vendor-neutral workflow is delivered: The Wire
  renders immediately from Castmill's schedule mirror, reconciles after render through the
  typed broker client, groups the reviewed/unscheduled queue and week by status, and supports
  move, cancel and atomic retry without losing reload persistence. Platform previews use the
  shared limits and broker/channel readiness is explicit. **Still open:** choose a concrete
  broker, adapt its exact API paths/channel mapping, configure credentials and prove a live
  schedule/reconcile/cancel round trip against its sandbox. Until then this is not a live vendor
  queue and remains unchecked.
- [ ] **30. Full composer** — multi-channel composition, per-channel editable variants,
  Unicode-aware hard-limit warnings, platform previews, campaign-owned published-image
  attachment, local staging and scheduled broker submission are delivered. Partial-failure retry
  excludes channels that already succeeded. **Still open:** provider-backed next-slot and
  send-now semantics are intentionally disabled until the selected broker can represent them;
  short-lived image links are never staged as durable media. Covered locally by `WirePageTests`.
- [ ] **31. Send a clip to the scheduler** — the safety and UI contract is delivered: only an
  exported clip with a durable remote media URL can enter composition; a local path, expiring
  worker SAS URL or the `clip-suggestions` instruction artifact is rejected with a designed
  reason. **Still open:** deploy the cloud clip worker/publication handoff so an exported web
  clip receives that durable URL, then prove broker media upload/scheduling end to end.
- [x] ✅ **32. Per-blog Schema & metadata panel** — Focus persists canonical site URL + slug,
  title/description, author/site/organization/logo identity and optional video metadata with the
  owning blog while preserving unknown content JSON. It derives FAQ schema only from visible
  question/answer prose and emits structured `Article` plus optional `FAQPage`/`VideoObject`
  JSON-LD. Combined, HTML-head-only and JSON-LD-only outputs are encoded/serialized with
  structured APIs and copy through the cross-shell clipboard service. Covered by
  `BlogPublishingMetadataTests`, `ExportButtonTests` and API export tests.
- [x] ✅ **33. Export ZIP includes generated images** — campaign ZIP export now downloads placed
  and recoverable non-discarded generated images, deduplicates shared blobs, assigns deterministic
  traversal-safe collision-resistant paths, rewrites matching Markdown references and includes
  a manifest that records included and unavailable images honestly. Count, byte and timeout
  bounds prevent an export from becoming an unbounded remote fetch; Markdown and document
  exports remain compatible. Covered by `ExportTests` and `ExportEndpointTests`.
- [x] ✅ **34. Email `[YOUTUBE_VIDEO_URL]` placeholder convention** — email-sequence and
  newsletter prompt contracts require the literal ESP-replacement token wherever the future
  video URL belongs; deterministic validation rejects a missing token, and post-generation
  workspace-link substitution preserves it rather than inventing or erasing a URL. Focus labels
  the token without turning it into generated copy. Covered by `AiValidatorTests` and generation,
  Brand-template and SEO-target regression tests.

## Group 6 — Models & steering *(§7.15, §7.14, §7.11, §8.4)*

- [ ] **35. User-facing per-generator model overrides** (blog, audit, YouTube description,
    socials), provider inferred from the model name, OpenAI/Anthropic mixable,
    "(use default)" inheritance. Castmill's aliases are server-config only.
- [ ] **36. Brand-voice distillation** — exemplar texts → a distilled style card
    (`distill_brand_voice`).
- [ ] **37. Richer Brand "AI Context" document** — positioning, ICP, personas, competitors,
    differentiators, campaign priorities — injected into every text generator. Castmill's
    style card (voice/audience/tagline) is a thinner slice.
- [ ] **38. Clip discovery instruction + review-state persistence** — user scouting directive;
    re-suggest replaces only un-reviewed candidates; lossless stream-copy fast path when
    neither captions nor reframe is on.
- [ ] **39. Whisper model-size picker** (base … large-v3) at ingest, with first-use download
    progress.

## Group 7 — UX & platform patterns *(§6.2, §7.1, §10)*

- [ ] **40. Prompt transparency in the chrome** — a header control showing the exact prompt
    behind every generation this session (Castmill has `/ai/log`; no UI surface).
- [ ] **41. Prefetch-on-intent** — hover/focus on a campaign card prefetches its artifacts.
- [ ] **42. Server-Timing instrumentation** (`auth` / `db` / `serialize`) on hot endpoints.
- [ ] **43. Home pipeline card** — the 11-step visual of everything the product produces.
- [ ] **44. In-place report-inputs panel** — edit the brief's SEO fields on the report page
    itself, with a re-run nudge (a second edit surface over the same data).

## Group 8 — Leapfrog opportunities *(§11 — PostFoundry's own admitted gaps)*

*Castmill can skip past PostFoundry here, not just catch up.*

- [ ] **45. AEO trend over time** — snapshot reports per run instead of one root artifact;
    visibility trend charts.
- [ ] **46. AEO prompt *sets*** — several question phrasings per keyword, with repetition for
    variance, instead of one generic question asked once.
- [x] ✅ **47. Exact-domain citation matching** (host-part comparison, not substring `Contains`).

## Castmill additions discovered during implementation

- [x] ✅ 🆕 **N1. Item-owned Focus Producer and Image Studio** — the Producer operates on
  the selected content item rather than the campaign. Each artifact owns its image cards,
  prompts, references, concepts, and generated variants; Image Studio is a content-first
  workspace for moving item by item through image creation.
- [x] ✅ 🆕 **N2. SEO/AEO analysis-first production gate** — the visible flow is Source →
  Transcript → research Context → full SEO/AEO Report → SEO-informed Production brief →
  Press Run. Brief and production endpoints reject generation until an analysis and approved
  targets exist; approval is persisted on the report, and all downstream prompts receive the
  report strategy, search evidence, authority constraints, and content opportunities.
- [x] ✅ 🆕 **N3. Blog-owned publishing intelligence** — blog metadata and JSON-LD live on
  and persist with the blog artifact in Focus mode instead of being presented as SEO-report
  output.
- [x] ✅ 🆕 **N4. Re-analysis impact review** — when an approved SEO/AEO analysis changes,
  identify downstream artifacts generated from the prior version and let the user keep or
  regenerate each one. This should build on item 10's staleness signals. **Implementation slice
  delivered 2026-08-19:** ADR-043 and migration `ContentDependencySnapshots` add tenant-scoped,
  append-preserving dependency observations with normalized edges for every complete approved
  evidence marker `(SourceAssetId, Revision, RevisionId, Hash, ApprovedAt)` plus semantic hashes
  of the approved report and target strategy. Deep analysis, target approval, brief, campaign
  fan-out, individual generation, Tech Edit, title regeneration and impact regeneration capture
  their dependencies. One impact service classifies user content as Fresh, Evidence changed,
  Strategy changed, Both changed or Unknown; operational artifacts are structurally excluded.
  The SEO desk is read-only on entry and offers explicit per-artifact Keep (acknowledge current
  inputs without changing copy) or Regenerate (existing generator path, same artifact identity,
  revision preserved) actions with readiness reasons. ADR-044 and migration
  `ArtifactRevisionDependencySnapshots` bind each new artifact revision to the dependency
  observation current when its content was captured; restore writes the old content and clones
  that historical observation plus evidence edges as one new current `restored` snapshot, while
  legacy unlinked revisions remain Unknown. The complete SQL-backed flow proves dependency
  capture, all five staleness states, excluded/unapproved evidence stability, read-only viewing,
  Keep, selected Regenerate with history, atomic Restore, tenant isolation, operational exclusion
  and legacy Unknown; classifier/UI tests and the full build are also green.
- [ ] 🆕 **N5. Existing-campaign analysis transition** — provide a guided recovery state for
  campaigns created before the analysis-first gate, taking the user directly to analysis
  review instead of leaving a generation action at a raw conflict response. The N4 impact API and
  SEO desk now classify pre-foundation content as **Unknown / Needs transition** rather than
  assuming it is fresh, and regeneration remains unavailable until approved evidence and strategy
  exist. **Still open:** the guided recovery route that assembles/approves those inputs and returns
  the user to the intended generation action.
- [x] ✅ 🆕 **N6. Purpose-built report visualization grammar** — all quantitative SEO/AEO
  report charts use ApexCharts, while the campaign node structure uses ApexTree with the
  pillar as hierarchy root, supporting artifacts and missing-channel actions as leaves,
  stable IDs, accessible navigation, pan/zoom, and typed open/draft interactions.
- [x] ✅ 🆕 **N7. Verifiable DataForSEO coverage and live production E2E** — research
  combines Keyword Suggestions, Keyword Ideas and Keyword Overview and persists exact
  successful endpoint provenance; the report adds advanced SERP, ranked keywords, backlinks,
  domain footprints, multi-keyword SERP competitors, and four answer engines. Each answer
  engine resolves an account-supported model through DataForSEO's live model catalog and
  only requests web search when that model supports it. An opt-in Playwright test drives the
  real analysis-first flow with metered provider calls, requires all four engines to succeed,
  verifies production gating plus Focus ownership and ApexCharts/ApexTree rendering, and
  cleans up its test campaign and Brand.
- [x] ✅ 🆕 **N8. AI-derived research audience with authoritative Brand voice** — immediately
  after transcript ingest, a dedicated pre-report AI pass infers the specific research
  audience without generating titles, angles or content. The Context step keeps that audience
  editable, while brand voice is read-only and comes only from the selected Brand's persisted
  style card; changing or clearing the Brand synchronizes the voice deterministically.
- [x] ✅ 🆕 **N9. In-place Brand asset reclassification** — every Asset Kit image exposes a
  hover/focus type switcher (product, face, background, accent, logo or other). Changing the
  type persists through a tenant-scoped endpoint and immediately moves the card into the
  correct group without re-uploading the source image.
- [x] ✅ 🆕 **N10. Stateful Image Studio controls and composition-safe generation** — prompt
  mode updates its selected slot without reloading the campaign, chips are large toggle
  controls with explicit applied state, and the content-first left rail has a dedicated,
  properly spaced add-image action. Every generate and steer prompt now ends with the slot's
  exact dimensions/aspect ratio, a central safe composition zone and strict instructions
  against clipped typography, invented UI panels or off-canvas content.
- [x] ✅ 🆕 **N11. Full-width Markdown AEO response workspace** — answer engines are full-width
  accessible tabs with one focused response panel; engine answers render as sanitized
  Markdown, and citations remain attached to the active engine.
- [x] ✅ 🆕 **N12. Mill-Floor-parity Focus navigation and operational-artifact routing** —
  Focus uses the Mill Floor's lane names and ordering as clean, alternate-colour vertical
  category bands; only content rows are interactive, each selection updates the manuscript,
  and row deletion uses the shared trash-can affordance. SEO reports, transcripts and image
  planning artifacts are excluded from dashboard edit work and cannot render as raw Focus
  manuscripts, including through stale deep links.
- [x] ✅ 🆕 **N13. Unified campaign rail** — the workspace rail has one Campaigns section and
  lists every campaign instead of duplicating the active campaign or reducing larger
  workspaces to a recent subset. Rows show name, date added and update recency, preserve the
  active state and view when switching, and use the standard hover/focus trash-can delete
  affordance behind the existing destructive confirmation.
- [x] ✅ 🆕 **N14. Authoritative full-height Brand content templates** — YouTube is a first-class
  Brand Template with a strategy-focused starter. A saved template is injected as the primary
  content brief into every applicable generation pass and takes precedence over generic writing
  guidance while Castmill retains its JSON, grounding, provenance and safety contracts. The
  complete 20,000-character prompt persists without truncation, and its responsive editor fills
  the remaining screen height with a narrow/short-screen scrolling fallback.
- [x] ✅ 🆕 **N15. Canonical content-type surface contract** — all thirteen user-generatable
  artifact kinds are derived into campaign creation, Mill Floor on-demand generation and Brand
  Templates from one tested inventory; Clip Suggestions is no longer missing and system-only
  Campaign Summary is no longer offered as a generator. Publishable content is explicitly
  separated from strategy and operational artifacts, preventing internal summaries, SEO plans,
  clip instructions and deep-report machinery from leaking into Image Studio, the outward-facing
  ApexTree cluster or The Wire. Campaign format remains visible after creation in the header,
  campaign index and workspace switcher. The durable matrix is documented in
  [content-type-surfaces.md](content-type-surfaces.md).
- [x] ✅ 🆕 **N16. Cross-shell reliable copy actions** — transcript Copy and View → Copy all
  use one clipboard service rather than invoking `navigator.clipboard` from Razor. The browser
  path uses the asynchronous Clipboard API and automatically falls back to a synchronous hidden
  selection for desktop WebViews or restricted browser contexts; SEO share-link copying uses the
  same contract. Component tests verify both transcript actions receive the complete readable
  text, and Playwright verifies both the real clipboard and forced fallback paths.
- [x] ✅ 🆕 **N17. SEO Analysis is a dedicated artifact role, not Mill content** — SEO brief
  has been removed from creation checkboxes, Print more, Mill Floor, Focus, Brand Templates,
  dashboards and public generation endpoints. Keyword plans and deep SEO/AEO reports are also
  excluded from all production/editing lists and surface only in the SEO Analysis tab. The
  legacy AI `seo-brief` schema remains an internal research pass for the legacy keyword-plan
  endpoint, which now consumes and deletes its temporary row rather than persisting a fake
  content item. Existing legacy rows remain safely hidden.
- [x] ✅ 🆕 **N18. Deterministic Focus entry selection** — entering Focus from a campaign or
  the campaign tab opens and highlights the first visible content row in canonical lane order
  (YouTube first when present). Valid artifact deep links still win, stale operational links
  fall back to that first row, and switching campaigns cannot retain the previous selection.
- [x] ✅ 🆕 **N19. Page-only production lane** — the obsolete `Page/SEO` Mill Floor and Focus
  label is now `Page`. Landing pages remain production content; SEO/AEO reports and plans keep
  their separate SEO Analysis role and never consume board space.
- [x] ✅ 🆕 **N20. Race-safe Brand asset reclassification** — type changes return the server's
  canonical updated asset, and keyed group/card rendering prevents a card moved between Face,
  Background, Accent and other sections from inheriting a neighbouring select's DOM value.
  The visible selector, group heading and persisted kind now stay in agreement.
- [x] ✅ 🆕 **N21. Source-copy context for image decisions** — Image Studio and the full-size
  take dialog show the actual artifact text the selected image supports, alongside—not replaced
  by—the AI visual description. Inline blog images prefer prose surrounding their image marker;
  selection-race guards prevent a slower previous artifact fetch from showing stale context.
- [x] ✅ 🆕 **N22. Contact-sheet Image Studio** (ADR-F43) — the studio canvas is the image plan
  itself: each content piece renders its slots as tiles at their true aspect ratios (published
  image when filled, dashed hole when empty, `Rendering` while a run is live), with a per-piece
  fill count and a ghost "add image" tile in the piece's own row. Selecting a tile opens the
  slot editor as a closable drawer beside the sheet (close button and Escape dismiss it; the
  take lightbox stacks above it unchanged), and the open slot is mirrored to `?slot=` so deep
  links and refreshes restore it. Nothing auto-opens on entry — the default view is the whole
  campaign's coverage, which was the point: state reads from the tiles, not from text badges.
  Iteration: wider drawer / narrower tiles; brand-kit references moved out of the drawer into
  a master–detail picker dialog (grouped list left, preview + select right, chips for current
  picks); campaign cards on the Campaigns index show the most recently placed image in the
  media band via `CampaignCounts.HeroImageUrl` (duotone placeholder when none). Slots with
  generated-but-unplaced work preview their best take (kept first, then newest) on the sheet
  tile with an "In takes" state and on the card band, via `ImageSlotResponse.LatestTakeThumbUrl`;
  the studio page adopted the fill pattern (sheet and drawer each own their scrollbar), and the
  brand kit loads on studio entry rather than only on a state-change event.
- [x] ✅ 🆕 **N23. Immediate campaign rename synchronization** — a successful header Rename
  reconciles the authoritative server response into both campaign-local state and the persistent
  workspace Campaigns list. The header, active rail row, tooltip/initial and updated timestamp
  now change in the same interaction without a page reload or redundant full-list request.
- [x] ✅ 🆕 **N24. Honest image-take state controls** — Mark as Keeper immediately gives the
  exact generated-image card a prominent Keeper badge and selected border treatment. Image
  Studio loads the slot's recoverable take inventory once, hides discarded rows by default,
  and renders Show discarded takes only when at least one persisted discarded take actually
  exists; restoring the last one removes the control again.
- [x] ✅ 🆕 **N25. Default-first compact image-model selection** — Settings → Models persists
  one workspace default from the same readiness-aware generator catalog used by Image Studio.
  Cards without an override inherit later default changes; the drawer shows only the current
  model and its Default/This image scope, while Change opens a focused selector dialog. Choosing
  Workspace default clears a prior per-card override instead of copying a transient alias.

### Content starter expansion

The product direction remains **one authoritative source in, a source-grounded campaign out**.
New starters broaden what counts as a source without weakening provenance, review, or the
analysis-first production gate. Source modality, campaign intent and output recipe are separate
concepts: adding an input must not create a parallel campaign pipeline or a new campaign format.

#### Foundation and first starters

- [x] ✅ 🆕 **N26. Generalized source evidence contract** — replace the transcript-only grounding
  assumption with durable `SourceAsset` and `EvidenceBlock` concepts. A block identifies its
  source and a stable locator appropriate to that modality: media time range, webpage
  heading/paragraph, document page/section, slide number, research URL/passage, or an existing
  Castmill artifact/revision. Generators cite evidence-block IDs through one schema; media
  evidence preserves the current transcript-segment behavior and provenance threads. The Source
  Master evolves into a multi-source evidence view without changing artifact ownership.
  **Depends on:** a new backend and frontend ADR defining source immutability, locator shapes,
  source snapshots and migration compatibility. **Complete when:** existing campaigns migrate
  without losing citations; every generator accepts the generalized citation contract; media,
  text and URL evidence render and deep-link in both shells; tenant-isolation tests cover every
  new entity. **Backend foundation delivered 2026-08-19:** ADR-042 defines immutable source
  snapshots, append-only evidence revisions and legacy segment compatibility. Migration
  `GeneralizedEvidenceFoundation` adds tenant-scoped source/evidence tables with global query
  filters, uniqueness/check constraints and cascades. Transcript ingest now creates one
  idempotent transcript source whose ordered media-time evidence keeps the normalized segment IDs,
  source labels and timestamps already used by artifact citations and provenance. Tenant-authorized
  APIs list sources, project current/approved evidence, correct or exclude blocks, approve a
  revision and resolve legacy string citations. Approval records an immutable
  `(SourceAssetId, Revision, RevisionId, Hash, ApprovedAt)` marker and its hash/projection
  structurally omit excluded blocks. All four SQL/HTTP evidence-foundation cases pass against the
  Testcontainers database, including idempotent transcript ingest, approved projection,
  cross-tenant invisibility and legacy citation resolution; the hash/citation regressions and
  solution build also pass. **Generator integration delivered 2026-08-20:** ADR-045 routes fan-out,
  blog, YouTube, title regeneration and Tech Edit through one complete approved-evidence projection.
  Prompts expose canonical `evidence:{sourceAssetId:N}:{stableBlockId}` IDs; validators normalize a
  uniquely resolvable legacy ID before persistence and reject ambiguous same-ID sources. Artifact
  JSON remains `citations: string[]`, preview projection remains lightweight, qualified IDs resolve
  through the evidence API, and the current transcript overlay maps back only citations belonging
  to that transcript source. Clip timing is likewise scoped to the explicitly selected transcript.
  Prompt blocks and dependency snapshots share one captured approval tuple, conditional evidence
  approval cannot approve a revision that advanced before commit, and migration
  `EnforceEvidenceConcurrency` creates a filtered unique current-snapshot index after deterministic
  duplicate cleanup. A valid pre-foundation transcript artifact is lazily and idempotently
  backfilled into one approved source/evidence revision on first generation load. Multi-source SQL
  coverage proves a generator can cite a second approved source even when local block IDs overlap.
  **Completion delivered 2026-08-20:** tenant-authorized adapters now snapshot public HTML pages,
  uploaded TXT/Markdown/HTML/PDF/DOCX/PPTX documents and current or historical Castmill artifact
  revisions through one bounded `SourceImportService`. URL fetches manually validate every redirect,
  pin sockets to revalidated public addresses and disable pooled cookies/proxies; document parsing
  caps transport bytes, archive expansion/ratio/entries, PDF pages, blocks, per-block and total text,
  parser duration and concurrent parser jobs. Snapshot identity includes immutable origin plus
  content so same-origin retries are idempotent while identical bytes from different origins remain
  distinct. `SourceOriginIdentity` migrates the unique index accordingly, and proactive startup
  backfill uses the same runtime UTF-8/canonical evidence hashing and stable-ID normalization as the
  lazy compatibility path in bounded, race-tolerant batches. Initial generation, title regeneration,
  Tech Edit and N4 regeneration accept evidence-only campaigns; clip generation remains honestly
  transcript-gated. Generated content and its dependency observation commit atomically. The shared
  RCL Source Master now provides source tabs, current review controls, approved and historical
  provenance projections, origin links and revision-stable `source/revision/evidence` deep links in
  both shells. Campaign state loads source projections with bounded parallelism, lazy-loads historical
  revisions, survives campaign-switch/refresh races and never substitutes draft text for an
  artifact's captured evidence. Live Playwright verification passed at 1440×880 and 390×844 with
  visible source tabs, exact viewport containment and no console/page errors. Final repository gates:
  328 API + 236 UI + 13 Media = 577 .NET tests, 40 editor tests, zero failures/skips/warnings,
  valid migration SQL, no pending EF model changes and a clean `git diff --check`.
- [x] ✅ 🆕 **N27. Truthful, recipe-aware Start a Run surface** — replace the source screen's
  promised-but-unimplemented URL/asset options with capability-backed starter families:
  Record or upload, Paste text, Import a page, Re-mill Castmill content, Research an opportunity,
  and later connected sources. Starter selection records the source modality separately from
  campaign format and intent (`repurpose`, `promote`, `launch`, `build authority`, `refresh`,
  `capture idea`). The selected intent proposes an editable output recipe rather than creating a
  second generation path. Unavailable capabilities state why per G3; no dead controls.
  **Depends on:** N26 for non-transcript starters; N28–N31 deliver the first enabled choices.
  **Complete when:** every visible starter succeeds or has an explicit unavailable state in each
  shell; source, intent and recipe survive reload; creation remains one analysis-gated flow.
  **Completed 2026-08-20:** the shared RCL now shows only paste, webpage and document in web, plus
  local media when the host reports that capability. Source, six canonical intents and a validated
  output recipe are distinct persisted decisions; `?campaign=` resumes the furthest durable step.
  Immediate campaign saves are serialized so rapid recipe edits cannot overwrite newer choices,
  and unrelated campaign updates preserve omitted run-plan fields. Transcript and evidence-only
  starters use the same context, deep-analysis, approval and Press Run APIs. Live browser proof
  completed paste → intent → reload → SEO/AEO → approval → edited recipe → Press Run; SQL/API and
  shared-RCL regressions cover persistence, capability filtering and transcript-free generation.
- [x] ✅ 🆕 **N28. Cross-shell media upload and cloud transcription** — finish the existing F7 web
  gap for audio and video files while retaining desktop-local Whisper. Use a resumable,
  bounded-memory upload path suitable for source media, persist the private source asset, submit
  short media to the configured transcription provider and long media to Azure AI Speech, then
  produce timed evidence blocks with source labels. Upload, extract, transcribe and diarize use
  determinate progress and a narrated log; cancellation and retry are resumable and honest.
  **Depends on:** source-media storage modelling from N26 and the production storage/CORS
  decision; does not depend on N29–N31. **Complete when:** a web-only user and a desktop user each
  complete upload → transcript review → analysis → Press Run; large-file interruption resumes;
  cross-tenant source access fails in API tests.
  **Completed 2026-08-21:** both shells expose a private cloud-media starter while desktop retains
  local ffmpeg/Whisper. Tenant-scoped SQL upload sessions stream exact 4 MiB blocks into private
  Blob with a 2 GiB hard cap, SHA-256, deterministic idempotent block ids, contiguous progress,
  commit/cancel/expiry metadata and resumable retry after transport or transcription failure.
  Reload restores the server offset and honestly requires browser file reselection. ≤25 MiB routes
  to Foundry transcription; long media routes to Azure Speech with diarization. Bicep provisions
  Speech and grants App Service managed-identity access. Timed transcript evidence opens for review,
  then uses the existing intent → analysis → Press Run gates. SQL tests cover resume, duplicate
  blocks, checksum/order/size/type bounds, cancel, provider retry, short/long routing and cross-tenant
  404s; shared-RCL and Playwright flows complete the user journey.
- [x] ✅ 🆕 **N29. Webpage and blog starter** — import a public article, report, case study, landing
  page or product page into a versioned source snapshot. Extract canonical URL, title, author,
  publish/update dates, headings, readable body, structured-data facts and eligible images while
  excluding navigation, cookie chrome and repeated boilerplate. Offer two explicit intents:
  **Repurpose this page** and **Promote or expand this page**. Evidence citations resolve to the
  captured heading/paragraph and retain the original URL. Fetching must apply the existing Brand
  lookup's SSRF posture to the initial URL and every redirect, reject non-public resolved
  addresses, cap redirects/time/bytes/types and never execute page script.
  **Depends on:** N26. **Complete when:** representative article, product and JavaScript-shell
  pages have honest preview/failure states; the user can inspect and exclude extracted evidence;
  generated claims deep-link to the saved snapshot rather than mutable live HTML; SSRF and
  prompt-injection boundary tests are green.
  **Completed 2026-08-20:** the immutable static-HTML adapter prefers article/main content, strips
  chrome and hidden/script surfaces, and revisions title, canonical URL, author, publish/update
  dates, allowlisted JSON-LD article/product facts, eligible image references, headings and body as
  ordinary inspectable evidence. Start a Run renders that review before the two page-specific
  intents; correction/exclusion locks intent until the new revision is approved. JavaScript-only
  shells return an actionable paste/server-rendered recovery message. Initial and redirect URLs are
  public-address validated and socket-pinned; redirects, time, bytes, media types, parser work and
  extracted output remain capped. All research/generation source projections are marked untrusted
  data. Representative article/product/JS-shell, SSRF, prompt-injection, approval-gate, citation,
  shared-RCL and always-on Playwright tests pass. A real `https://example.com/` run completed
  import → review → Repurpose → live SEO/AEO → approval → recipe → Press Run with 3/3 jobs.
- [x] ✅ 🆕 **N30. Voice-note starter** — let a user capture an idea directly in Castmill through a
  focused audio recorder, not a general recording studio. Provide microphone permission and
  unsupported states, visible elapsed time and input level, pause/resume, playback, discard and
  explicit Use recording. Web uses secure-context media capture; desktop implements the same
  capability behind a platform seam. The recording then follows N28's source upload,
  transcription and transcript-review path.
  **Depends on:** N28's cloud path for web; desktop capture may proceed against the existing
  local transcription seam. **Complete when:** denied permission, interrupted capture, MIME
  negotiation and maximum duration are tested; no microphone opens before a user gesture; the
  resulting timed evidence behaves exactly like uploaded audio.
  **Completed 2026-08-21:** `IVoiceCaptureService` and the shared recorder island provide explicit
  unsupported, permission-denied, error, recording, paused and stopped states; elapsed time, input
  level, pause/resume, playback, discard and Use recording; WebM/Opus → MP4 negotiation; ten-minute
  auto-stop; user-gesture and secure-context guards; and deterministic track/object-URL cleanup.
  Unsupported shells hide the starter and show the reason. macOS/Windows declare microphone use.
  Accepted bytes go through N28 unchanged. Vitest, bUnit and fake-microphone Playwright checks cover
  permission, gesture, MIME, duration and the complete record → review → analysis → Press Run path.
- [ ] 🆕 **N31. Re-mill existing Castmill content** — start a new run from one or more owned
  artifacts, a prior campaign summary or an approved artifact revision. Preserve a link to the
  originating campaign/artifact/version and distinguish quoted source text from newly generated
  descendants. Initial intents are **Promote this**, **Refresh this**, **Adapt for another
  audience** and **Fill missing channels**; the last option proposes only absent distribution
  kinds from the content-type registry.
  **Depends on:** N26. **Complete when:** source deletion/revision does not silently mutate the
  new run's snapshot; lineage is visible from Source Master; recursive self-sourcing and
  cross-tenant references are rejected; duplicate requested outputs are explicit choices.
- [ ] 🆕 **N32. Starter and ingest telemetry** — measure starter impression/selection, ingest
  start/success/failure, source-ready latency, transcript/evidence-review edits, analysis entry,
  Press Run start and abandonment. Events use source kind, shell, size/duration bands and typed
  failure reason only; raw source text, URLs with query strings, filenames and transcript content
  never enter telemetry. Provide a development diagnostics view and production funnel query.
  **Depends on:** the event contract can be implemented alongside N27; individual starter events
  land with N28–N31. **Complete when:** each starter has a traceable funnel, correlation IDs join
  client/API/dependency spans, and privacy tests prove source content is excluded.

#### Source expansion and reusable runs

- [ ] 🆕 **N33. Authorized YouTube source import** — connect a user's YouTube channel and import
  videos the user is authorized to manage, including metadata and an available caption track;
  otherwise offer paste-transcript or upload-original-media fallbacks. Do not depend on an
  unofficial public-video downloader: the official captions download API requires authorization
  and permission to edit the video. Preserve video URL, channel/title metadata and caption
  timecodes as evidence; imported media rights and intended use require explicit confirmation.
  **Depends on:** N26 and an explicit reversal/extension of ADR-010's no-external-identity rule
  for a narrowly scoped YouTube OAuth connection; N28 if original-media fallback is uploaded.
  **Complete when:** connect, token custody, revoke, import, unavailable captions and deleted
  video states are covered; OAuth credentials never reach application logs or artifact content.
- [ ] 🆕 **N34. Document and slide sources** — accept PDF, DOCX and PPTX as private source
  assets; extract text, headings, tables, speaker notes and page/slide boundaries with a reviewable
  evidence preview. Preserve page/slide citations and identify scanned pages that require OCR
  rather than pretending they are empty. Embedded images can become reference candidates only
  after the user confirms rights and relevance.
  **Depends on:** N26 and the N28 resumable upload substrate; may ship PDF first behind the same
  contract. **Complete when:** extraction is deterministic, encrypted/private at rest, size- and
  complexity-capped, protected from archive bombs and malicious relationships, and citations
  navigate to the correct page or slide in both shells.
- [ ] 🆕 **N35. Podcast and RSS episode starter** — import an RSS/Atom podcast feed, show an
  episode picker and ingest the selected enclosure plus feed metadata. Existing transcripts may
  be used only when their timing and provenance are trustworthy; otherwise transcribe the audio
  through N28. Feed refresh is explicit and cannot mutate an existing run's source snapshot.
  **Depends on:** N26, N28 and the URL-fetch security boundary from N29. **Complete when:** feeds
  with redirects, relative URLs, duplicate GUIDs, large enclosures and missing transcripts have
  designed outcomes; episode title, show, date and source labels survive through generation.
- [ ] 🆕 **N36. Goal-based run recipes** — persist reusable combinations of intent, output kinds,
  copy counts, image-slot defaults and per-kind Brand templates. Ship curated recipes for Webinar
  follow-up, Product launch, Thought-leadership distribution, Existing-page promotion, Voice note
  to campaign and SEO content refresh. Recipes project from `ArtifactKinds` roles and cannot
  expose operational artifacts as outputs. They are suggestions: the final brief remains editable
  and every paid operation exposes its scope before Press Run.
  **Depends on:** N27 and the canonical content-type registry; independent of N33–N35.
  **Complete when:** save, duplicate, edit, delete and apply are cross-shell; stale recipes handle
  removed kinds safely; one recipe cannot bypass analysis approval, validators or Brand rules.
- [ ] 🆕 **N37. Start from an opportunity** — accept a seed keyword, audience question or topic
  together with a Brand/site and intended audience, then run research before any production.
  Castmill assembles a reviewable evidence pack from DataForSEO and cited public sources, proposes
  reachable angles and approved targets, and only then creates the production brief. This is not
  a bare keyword-to-copy shortcut: unsupported claims must cite the research pack, owned facts
  must come from Brand/source evidence, and predicted opportunities stay labelled as predicted.
  **Depends on:** N26, N29's safe fetch/snapshot boundary and the existing analysis-first gate.
  **Complete when:** the run has no fabricated transcript, every generated factual claim resolves
  to owned or research evidence, source diversity/recency are visible, and the user can reject
  sources or cancel before generation spend.
- [ ] 🆕 **N38. Mixed source packs** — allow up to six heterogeneous starting sources, such as a
  webinar recording, product page and research report. Users can designate each source as factual
  authority, audience context, style exemplar or visual reference; roles are explicit and cannot
  override Brand voice, prompt safety or machine-readable output contracts. Duplicate evidence is
  collapsed without losing source attribution, and conflicting claims are surfaced before the
  brief rather than silently reconciled by a model.
  **Depends on:** N26 plus at least two non-media source adapters. **Complete when:** source roles,
  ordering and exclusions survive reload; generators can cite several source kinds in one
  artifact; contradiction and source-removal tests prove deterministic downstream staleness.
- [ ] 🆕 **N39. Connected source adapters** — add Google Drive, Dropbox, OneDrive and approved
  meeting/recording providers only after URL, document and media ingestion share one stable source
  contract. Connectors browse/select and import a private snapshot; they do not become an alternate
  artifact store or leak provider tokens to clients. Each connector has readiness, token-expiry,
  revoke and source-deleted states behind one adapter interface.
  **Depends on:** N26, N28, N34 and provider-specific authorization ADRs. **Complete when:** the
  first connector proves the interface without provider branching in campaign creation, and a
  second connector can be added without changing source, evidence or generation contracts.

#### Source review and additional outputs

- [ ] 🆕 **N40. Source-quality review and rights checkpoint** — before research, every starter
  presents what Castmill will treat as evidence: transcript segments, extracted page sections,
  document pages or imported artifacts. Users can correct transcript text without destroying
  timing, exclude irrelevant blocks, name speakers/sources and confirm that they have permission
  to transform the material. Low-confidence transcription/OCR and sources containing likely
  prompt-injection instructions are called out explicitly. Edits create a new evidence revision
  and feed N4's downstream impact model once content has been generated.
  **Depends on:** N26; modality-specific controls land with their source adapter. **Complete when:**
  generation always records the approved evidence revision, excluded blocks cannot be cited, and
  changing approved evidence marks dependent research/artifacts stale rather than mutating them.
  **Revision foundation delivered 2026-08-19:** correcting or excluding one block clones the full
  evidence set into a new draft revision, leaving the previously approved projection immutable;
  approving the draft advances the source's approved marker, and citation resolution cannot return
  an excluded block from that projection. This is the durable contract N4 consumes, and N4's
  SQL-backed Keep, Regenerate and atomic Restore workflow is delivered. ADR-045 also ensures every
  AI content generator records the complete approved evidence marker set through its dependency
  snapshot. **Still open:**
  the cross-shell review surface, speaker/source editing, rights confirmation, transcription/OCR
  confidence, prompt-injection warnings, and generalized impact coverage beyond the delivered
  SEO/AEO flow.
- [ ] 🆕 **N41. Video script content type** — add long-form explainer/tutorial and short-form
  vertical video script variants with scene, narration, on-screen text, B-roll and evidence fields.
  **Depends on:** ArtifactKinds role/surface updates, validators and Brand templates.
  **Complete when:** scripts are editable in Focus, source-grounded, image-reference aware and
  exportable without pretending Castmill rendered final video.
- [ ] 🆕 **N42. Carousel and deck-outline content type** — generate a slide-by-slide narrative
  with title, body, visual direction, citation and CTA per card for LinkedIn/Instagram carousels
  or presentation handoff. First scope is structured outline plus Markdown/PPT-friendly export,
  not a full slide-layout editor. **Depends on:** content-type surface contract and export work.
  **Complete when:** slide counts/character bands validate, each factual slide is grounded, and
  visual directions can seed typed image slots without creating nested cards in the UI.
- [ ] 🆕 **N43. Case study and testimonial content type** — transform customer interviews,
  transcripts and approved outcome evidence into problem/approach/result narratives, quote cards
  and channel variants. Metrics and quotations require exact evidence; missing proof renders an
  explicit placeholder/check rather than generated numbers. **Depends on:** N26 and validators.
  **Complete when:** quote/metric fidelity tests block unsupported publication and the artifact can
  own social/email/image children through the existing hierarchy.
- [ ] 🆕 **N44. FAQ and knowledge-base content type** — generate answer-first FAQs, support
  articles and optional FAQ JSON-LD from approved product/source evidence. This extends item 32's
  metadata work but keeps the editable article in Focus and schema with the owning document.
  **Depends on:** content-type surfaces and richer metadata output. **Complete when:** answers are
  self-contained, cited and validated for invented product behavior; schema mirrors visible copy.
- [ ] 🆕 **N45. Paid-ad variant set** — generate grouped search and paid-social copy variants with
  platform fields, hard limits, audience/offer labels and test hypotheses. Ads are distribution
  content but must not enter The Wire until the chosen broker/channel can represent them honestly.
  **Depends on:** per-platform validators, Brand AI context (#37) and publishing capability rules.
  **Complete when:** every field has a hard validator, claims are grounded and export preserves
  campaign/variant grouping for external ad tools.
- [ ] 🆕 **N46. Event and webinar promotion kit** — add a recipe-backed package for registration
  landing page, invite/reminder/follow-up emails, social promotion, speaker copy, replay promotion
  and post-event nurture. It composes existing artifact kinds plus explicit event context rather
  than introducing one opaque mega-artifact. **Depends on:** N36 and The Wire/composer (#29–31).
  **Complete when:** pre-event and post-event recipes share one source/brief, timing-sensitive copy
  validates against event dates, and generated children remain independently reviewable.
- [ ] 🆕 **N47. Podcast episode package** — extend show notes into a first-class recipe containing
  episode title options, description, chapters, guest/host details, key links, quote cards, promo
  copy and newsletter/email choices. This does not replace `show-notes`; it coordinates existing
  and new artifact kinds around an audio/RSS source. **Depends on:** N35, N36 and link-placeholder
  conventions from item 34. **Complete when:** chapters use real timecodes, names/links come from
  approved context, and every package item remains editable, validatable and exportable alone.

#### Production deployment follow-through

- [ ] 🆕 **N48. Close production preflight blockers** — a read-only preflight completed on
  2026-08-19: `infra/main.bicep` compiled locally with one BCP037 warning, `worker.py` passed Python
  syntax validation, startup/CORS/secret guards were confirmed and the clip-worker image was found
  build-ready without embedded credentials. It also proved that the first empty-resource-group
  deployment is **not** ready: the Container Apps clip job and queue scaler lack Storage Blob/Queue
  data-role assignments; the KEDA `identity` property is outside the current Bicep type; the API
  managed identity's SQL user/roles are a manual post-deploy step; the deployer firewall rule is
  never removed; `db_owner` and `AllowAzureServices` are broader than the runtime needs; B1 cannot
  satisfy the documented zone-redundancy claim; and the production SWA CORS origin plus alert action
  group remain unset. The repository also lacks the approved `.azure/deployment-plan.md` required
  by the formal `azure-validate` workflow. **Complete when:** these static blockers are remediated,
  the worker image builds and is pushed, validation proof and a no-surprise group `what-if` are
  recorded, an empty-RG deployment succeeds, managed-identity SQL/Storage access is smoke-tested,
  the real SWA origin and alert action group are wired, and temporary firewall access is removed.

### Delivery goals and order

1. **Any owned idea becomes evidence-ready in under five minutes.** Deliver N26–N32 in the
   order N26 → {N28, N29, N31} → N27/N30, targeting at least 90% ingest success and no visible
   dead starter controls.
2. **Trust survives every source type.** Deliver N40 with N26 and require every later adapter to
   prove immutable snapshots, reviewable evidence and resolvable citations before it is enabled.
3. **Campaigns reach a destination.** In parallel with starter work, finish open items 29–34,
   item 5 and N4 so publishing, rich export, image completion and re-analysis do not remain a
   weaker end of the journey.
4. **Broaden only on the shared contract.** After the foundation, sequence N33–N39 by measured
   starter demand; add N41–N47 through the existing content-type surface contract, validators and
   Brand templates rather than one-off generation surfaces.

---

Groups 1, 2 and 4 carry the most product weight. Group 3 reverses ADR-011/ADR-010 and
needs a deliberate strategic decision before any of it is built. N26 and N40 are the
architectural gates for starter expansion; no source adapter may bypass them. New
Castmill-specific requirements belong in the additions section with an `N` identifier and
the 🆕 marker.
