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

Current delivery snapshot (verified 2026-08-11):

- [x] ✅ **Completed:** 1–4, 6–16, 22–28, 47, and additions N1–N3 and N6–N25.
- [ ] **Still open:** 5, 17–21, 29–46 (except 47), and additions N4–N5. Partially
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
- [ ] **5. Generate-all-pending images** across every slot in one narrated pass (Castmill
   batches variants per slot only).

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

- [ ] **29. Live vendor publish queue** — the Wire reads Castmill's mirror; PostFoundry reads the
    Buffer account live (status tabs, cancel-for-good, channel resolution).
- [ ] **30. Full composer** — multi-channel picker, per-channel truncation warnings + live
    preview, attach an image via short-lived link, next-slot/schedule/send-now, plus a
    local "Add to Queue" staging step per social post.
- [ ] **31. Send a clip to the scheduler** (upload → hand the link to the broker).
- [ ] **32. Per-blog Schema & metadata panel** — canonical URL builder, author/org identity,
    `VideoObject` schema, Combined / HTML-head / JSON-LD-only outputs with one-click copy.
    Partial: canonical/title/description plus `Article` and optional `VideoObject` JSON-LD
    are persisted on the blog; the richer output modes and FAQ schema remain open.
- [ ] **33. Export ZIP includes generated images** (Castmill's ZIP is markdown-only).
- [ ] **34. Email `[YOUTUBE_VIDEO_URL]` placeholder convention** for ESP replacement.

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
- [ ] 🆕 **N4. Re-analysis impact review** — when an approved SEO/AEO analysis changes,
  identify downstream artifacts generated from the prior version and let the user keep or
  regenerate each one. This should build on item 10's staleness signals.
- [ ] 🆕 **N5. Existing-campaign analysis transition** — provide a guided recovery state for
  campaigns created before the analysis-first gate, taking the user directly to analysis
  review instead of leaving a generation action at a raw conflict response.
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

---

Groups 1, 2 and 4 carry the most product weight. Group 3 reverses ADR-011/ADR-010 and
needs a deliberate strategic decision before any of it is built. New Castmill-specific
requirements belong in the additions section with an `N` identifier and the 🆕 marker.
