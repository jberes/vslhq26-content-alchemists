# Session memory — changes, fixes and decisions

A record of everything built, fixed and learned in this working session (August 2026).
All of it is **uncommitted** on `main` unless noted. Companion docs:
[feature-delta.md](feature-delta.md), [post-foundry.md](post-foundry.md).

---

## 1. The "SESSION EXPIRED" desktop crash — root-caused and fixed

**Symptom:** the entire desktop app collapsed into "session expired" errors mid-operation,
repeatedly, with no pattern the user could see.

**Root cause (proven with database evidence, not guessed):** the Mac Catalyst
`Entitlements.plist` was empty. MAUI SecureStorage on Mac Catalyst requires the
`keychain-access-groups` entitlement — without it every keychain write throws. The desktop
token provider swallowed the exception silently, so the refresh token was never persisted.
Exactly 15 minutes after sign-in the access token expired with nothing to renew it; every
request from every panel 401'd at once. The audit tables confirmed it: zero server-side
revocations, zero reuse detections, dozens of fresh logins minutes apart — the server never
killed a session; the client was losing its own key.

**Fixes:**
- `TokenProviderBase` — the refresh token is now held in memory as the authoritative copy;
  storage exists only so a session survives an app restart. A live session can no longer
  die because persistence is broken. Pinned by
  `A_session_survives_storage_that_silently_loses_every_write`, which replays the outage
  verbatim (writes vanish, reads find nothing, refresh must still succeed).
- `Platforms/MacCatalyst/Entitlements.plist` — the keychain entitlement was tried and
  **reverted**: `keychain-access-groups` is a restricted entitlement, and an ad-hoc-signed
  Debug build carrying it is killed by launchd at spawn ("Launchd job spawn failed" —
  no codesigning identity exists on this machine, so no provisioning profile can validate
  it). Dev builds instead persist the refresh token to a user-only (0600) file in app data
  (`DesktopTokenProvider` fallback, az/gh-CLI posture); a properly signed pkg (E10.4) adds
  the entitlement back and SecureStorage takes over automatically.
- `ShellLayout.razor` — a session that ends mid-flight now produces ONE redirect to
  sign-in (preserving returnUrl) plus one toast, instead of app-wide error soup. Routed
  through `StoreEvents.GuardedAsync` (the dispatcher-fault guard caught the unguarded
  first version — the guard works).
- `DesktopTokenProvider` — corrected the false comment on the swallowed write failure.

**Earlier session-expired fixes in the same family (different failure modes, all real):**
- The app's `HttpClient` carried .NET's default 100-second timeout while two image
  variants take ~123s server-side; the abort could land inside the silent refresh, the
  server rotated the single-use token, the client never stored the replacement, and the
  retry looked like token theft (family revoked). Timeout raised to 10 minutes.
- Refresh-token **reuse grace window** (Auth0-style, `Jwt:RefreshReuseGraceSeconds`,
  default 60): a replay within seconds rotates again instead of revoking the family —
  covers crash-between-exchange-and-store, two windows racing, and network retries.
  Outside the window revocation is as brutal as ever (tested).
- Only a definitive 401 from the refresh endpoint clears the stored session; transient
  errors and cancellations never sign the user out.
- A client-side 401 now forces a real refresh even when the local clock thinks the access
  token is still valid — the server wins disagreements.

## 2. Press runs that survive anything

**Bug: "said 13 items created, it did not create 13 items."** The generation run executed
inside its HTTP request, so a client timeout / closed app / navigation cancelled the
remaining generators mid-run with no report.

- Runs are decoupled from `RequestAborted` — linked only to application shutdown, with a
  30-minute cap.
- `InterruptedRunSweeper` (IHostedService) marks runs orphaned by a dead process as
  `Interrupted` at next startup instead of `Running` forever.
- The client `ReattachAsync` polls the run row after a POST fault, so a severed connection
  reattaches to the live run instead of reporting failure.
- Per-item progress UI, green completion checks, roll-up animation and a "Done" state on
  the press run.

## 3. SEO research before generation

- **Research phase in New Campaign** (Targets step): DataForSEO keyword volume/suggestions,
  People-Also-Ask via advanced SERP (with a "what is X" fallback — noun phrases rarely get
  a PAA box; verified live), top-3 keywords pre-selected, free-form keyword entry.
- Targets persist on the campaign (`SeoTargetsJson`) and are injected into every generator
  prompt (`SeoTargetBlock`, placed last before the transcript): primary keyword in
  title/first heading/first 100 words, secondaries woven in, questions answered
  self-contained (AEO), never invent stats.
- `{{LINKS}}` placeholder substituted post-generation with the workspace's social URLs.
- **Content cluster map** (`ContentCluster` + `ClusterMap.razor`): radial SVG — pillar blog
  centred, supporting pieces on a ring, missing channels as dashed "+ add" nodes that
  draft that kind against the same targets. Deterministic geometry (angle = f(index)).

## 4. YouTube as a first-class citizen

The original point of the product: a perfect SEO YouTube description with 3 alternate
titles. Now: its own artifact kind, first in the fan-out and on the board, exactly 3
title variants for A/B testing, chapters (0:00 rule), 125-char hook discipline, AEO
self-contained-answer rules, social URLs from Settings injected via `{{LINKS}}`.

## 5. Brand editor overhaul

- Four tabs (identity / assets / templates / danger zone) replacing the one long page.
- Multiple faces/backgrounds/accents per brand with per-kind selection; square asset cards
  with editable titles (PATCH rename endpoint).
- Per-kind default templates: the editor swaps with the kind select, auto-creates a
  starter, saves on blur.
- "New brand from URL" with AI fill (SSRF-guarded lookup endpoint) and an "Add context"
  paste panel the AI mines.
- Brand delete behind a strong confirmation.
- Bigger colour picker with hex input (validated server-side).

## 6. Uploads that actually work on desktop

Browser→blob SAS PUT failed twice over (account CORS, then `NSUrlSessionHandler` chunked
bodies on Catalyst). Replaced with an API proxy upload —
`POST /api/v1/blob/assets/{id}/content` — using `ByteArrayContent`, which works in both
shells. Lesson enforced since: test on the shell the user actually runs.

## 7. AI-driven campaign creation

Step 3 of New Campaign auto-fills: transcript ingested → AI suggests the brief and a
transcript summary on both ingest paths. Fan-out choices derive from the artifact
registry (YouTube first and pre-selected).

## 8. App-wide density pass

`--cm-space-unit` 3.4px → 4px, body text to 1rem, meta to 0.8125rem; compact mode keeps
3.4px. Toolbar/button/contrast fixes on Mill Floor cards and Image Studio.

## 9. Hard-won platform lessons (now enforced by tests)

- **A Razor `@* *@` comment inside an attribute list** is emitted as an attribute name —
  fatal `InvalidCharacterError` in a real browser, invisible to bUnit (AngleSharp is
  tolerant). Source-scan test: `RazorMarkupSanityTests`.
- **Blazor dispatcher faults bypass ErrorBoundary.** Store `Changed` handlers must route
  through `StoreEvents.GuardedAsync`; `StoreEventGuardTests` fails the build on the next
  unguarded handler (it caught this session's own ShellLayout edit).
- **Same-selector CSS rules merge across files** via the import cascade —
  `CssSelectorCollisionTests` guards it after `.cm-brand` and `.cm-app__main` collisions
  broke layout and trapped a modal under the rail.
- **`ExecuteUpdateAsync` bypasses the EF change tracker** — read back through a fresh
  context in tests.
- **`<text>` is reserved in Razor** — use `<svg:text>` inside SVG markup.
- Azure SQL serverless auto-pauses: 60s connect timeout + retry-on-failure; the seeder
  must not crash startup.

## 10. feature-delta.md

Two full passes over the 955-line PostFoundry spec produced
[feature-delta.md](feature-delta.md): 47 candidate features in 8 groups, honest
partial-overlap notes, exclusions listed, group 3 flagged as an ADR-010/011 strategic
reversal. Uncertain overlaps were verified read-only in code before claiming a delta.

---

## Test state at time of writing

| Suite | Result |
| --- | --- |
| Castmill.UI.Tests | 167 / 167 |
| Castmill.Media.Tests | 13 / 13 |
| Castmill.Api.Tests | 223 / 223 |
| editor-interop (npm) | 38 / 38 (unchanged this session) |

Nothing committed since `e842f10`; the working tree holds all of the above awaiting an
explicit commit instruction.

---

## 11. N26 generalized source evidence — completed 2026-08-20

This session closed N26 end to end rather than stopping at the transcript-compatible backend
foundation.

### Source adapters and immutable snapshots

- Added one tenant-authorized `SourceImportService` for:
  - public server-rendered HTML pages;
  - uploaded TXT, Markdown, HTML, PDF, DOCX and PPTX documents;
  - current or historical Castmill artifact revisions.
- Every adapter produces the same immutable `SourceAsset` + approved `EvidenceBlock` revision.
  Locator kinds cover webpage heading/element/ordinal, document section/page/paragraph, slide and
  artifact JSON field/version/revision.
- `SourceOriginIdentity` changes source uniqueness from content hash alone to source kind + immutable
  origin + content. Same-origin retries reuse one snapshot; identical bytes from two URLs/assets or
  artifact revisions remain separate provenance.
- Import and SEO-input staleness commit in one SQL transaction. Idempotent retries do not advance
  report versions.

### Security and resource boundaries

- Extracted the shared `PublicUrlGuard`; Brand lookup and source import use the same private/reserved
  address policy.
- Source import manually follows at most five redirects and validates every hop. `SocketsHttpHandler`
  uses a connect callback that re-resolves and pins the socket to validated public IPs, closing the
  DNS-rebinding gap between validation and connection. Cookies and proxies are disabled.
- Document transport is capped while streaming, even if asset metadata lies. Parser work is globally
  limited to two jobs; timed-out/cancelled synchronous jobs retain their slot until they actually end.
- Office packages cap compressed bytes, entry count, expanded bytes and compression ratio. PDFs cap
  pages and accumulate character limits while pages are enumerated. Every source caps blocks,
  per-block text, total extracted text and generation prompt context.
- Added AngleSharp and PdfPig. PdfPig resolves a vulnerable SSH.NET transitive, so `SSH.NET 2026.0.0`
  is an explicit patched security pin; NuGet audit remains an error.

### Legacy migration and provenance integrity

- Proactive `LegacyEvidenceBackfillWorker` scans untouched transcript artifacts in bounded 50-row
  pages and commits one source at a time. It uses the same runtime UTF-8 hashing, canonical approved
  hash, label clamp and duplicate/overlong stable-ID normalizer as lazy generation-time compatibility.
  Unique-key losers clear tracking and continue, so parallel app instances converge safely.
- Generation, replacement, YouTube-title regeneration and Tech Edit now commit artifact/revision
  writes and dependency snapshots in one execution-strategy transaction.
- Approved evidence blocks and immutable marker tuples load in one SQL query filtered to the exact
  approved revisions; accumulated historical corrections are not materialized.
- Approval is conditional on the still-current revision. Concurrent duplicate approvals return the
  winner's unchanged tuple. New approvals stale SEO inputs transactionally; approval responses and
  GET projections omit excluded blocks. A valid all-excluded approval stays readable as an empty
  projection and cannot fall back to raw transcript text.
- Artifact previews carry the current dependency marker set. Historical evidence reads accept an
  explicit revision so an old artifact can show exactly what it consumed.

### Evidence-only generation

- Initial generation, YouTube title regeneration, Tech Edit and N4 impact regeneration accept a
  campaign with approved webpage/document/artifact evidence and no transcript artifact.
- Clip suggestions remain intentionally transcript-gated because local segment IDs compute media
  timing. The API returns that readiness reason rather than masking it as an exception type.
- Multi-source prompts expose canonical `evidence:{sourceAssetId:N}:{stableBlockId}` citation IDs;
  ambiguous local aliases fail validation.

### Shared Source Master

- Added `EvidenceClient`, source/evidence state and `SourceEvidenceReview` in `Castmill.UI`, so web
  and MAUI use one implementation.
- Source tabs switch among media, webpage, document and Castmill-artifact snapshots. Media keeps the
  timed transcript rail; generalized blocks display locator-aware labels, origin links and stable
  anchors.
- The compact Source Master renders approved evidence or the historical revision captured by the
  active artifact. View renders the current review revision and supports correction, exclusion and
  explicit approval. Draft corrections never rewrite old provenance visually.
- Deep links include `source`, `revision` and `evidence`; the shared router reopens and highlights the
  exact block. Current/approved projections load with bounded parallelism; historical revisions load
  only when cited or linked and survive same-campaign refreshes.
- Campaign state assembles async results locally and commits only after rechecking campaign ownership,
  preventing delayed responses from a previous campaign from contaminating the active one.
- Mobile browser verification found two intrinsic-width bugs: `.cm-app__main` lacked
  `min-inline-size: 0`, and `.cm-app__content` relied on an implicit min-content column. Explicit
  `minmax(0, 1fr)` tracks now keep the full campaign frame at exactly 390 px. Source tabs are
  non-shrinking and horizontally scroll inside the Source Master.

### Browser and test evidence

- Live authenticated browser flow imported an existing blog as an artifact snapshot, opened
  `/floor?source=...&revision=1&evidence=artifact-0001`, selected the correct source tab and
  highlighted the historical block.
- Playwright checks passed at 1440 × 880 and 390 × 844 with visible tabs, correct origin links,
  exact viewport containment and no console/page errors.
- Migration SQL drops the old content-hash unique index and creates the origin-identity unique index;
  EF reports no pending model changes.

## Test state after N26 completion

| Suite | Result |
| --- | --- |
| Castmill.Api.Tests | 328 / 328 |
| Castmill.UI.Tests | 236 / 236 |
| Castmill.Media.Tests | 13 / 13 |
| editor-interop (npm) | 40 / 40 |
| `dotnet build Castmill.NoDesktop.slnf` | 0 warnings / 0 errors |
| `git diff --check` | clean |

N26 is checked complete in [feature-delta.md](feature-delta.md). N27–N31 remain separate product
workflow items: they own the polished starter chooser, richer webpage extraction UX, resumable media
upload, voice capture and re-mill workflow—not the generalized evidence contract itself.

---

## 12. N27 Start a Run and N29 webpage starter — completed 2026-08-20

### Durable, truthful Start a Run

- Replaced promised source choices with capabilities that work now: paste text, webpage and document
  in both shells, plus local media only when the host supports it.
- Added durable `Campaign.Intent` and `OutputRecipeJson`, API validation, a real migration and an
  idempotent corrective migration for a development database whose history had advanced without the
  columns. Omitted fields on Rename/status updates preserve the existing run plan.
- The six-step route is Source → Reading → Intent → Context → SEO/AEO → Output recipe. The campaign
  id enters `?campaign=` immediately; reload restores source modality, intent, context, approved
  report and recipe. Recipe writes are serialized after live browser testing proved rapid toggles
  could otherwise complete out of order.
- Nullable transcript contracts route webpage/document evidence through the same audience, deep
  analysis, approval, brief and Press Run APIs. Clip generation remains honestly media-only.
- Mobile validation exposed a shared topbar overflow. At 390 px, desktop-only phase/style/account
  metadata now hides while navigation and Sign out remain visible; document width stays exactly the
  viewport.

### Webpage extraction and review

- Static AngleSharp extraction now prefers `article`, then `main`, removes navigation/header/footer/
  aside/forms/hidden and executable surfaces, and captures title, canonical URL, author,
  publish/update dates, allowlisted JSON-LD article/product facts, eligible image references,
  heading context and readable body.
- Metadata and images are ordinary revisioned evidence blocks, not a mutable sidecar. They can be
  inspected, corrected, excluded, approved, cited and reopened through historical deep links.
- Start a Run shows the shared review before intent. Editing/excluding creates a draft and disables
  intent until approval. Web imports offer exactly **Repurpose this page** and
  **Promote or expand this page**.
- JavaScript-only shells never execute page code and return a specific recovery message. The client
  now preserves non-validation HTTP 400 `ProblemDetails.detail` instead of replacing it with a
  generic sentence.
- The existing SSRF boundary remains: validate initial URL and every redirect, re-resolve and pin the
  socket to public addresses, disable redirects/cookies/proxies in the handler, and cap redirect
  count, response type/bytes/time, parser concurrency/time and extracted output.
- Every transcript-shaped research prompt and final generator prompt marks source material as
  untrusted data and rejects commands embedded in imported prose as instructions.

### Validation after N27/N29

| Suite | Result |
| --- | --- |
| Castmill.Api.Tests | 336 / 336 |
| Castmill.UI.Tests | 244 / 244 |
| Castmill.Media.Tests | 13 / 13 |
| Total .NET | 593 / 593 |
| editor-interop | 40 / 40; editor gzip 210,759 bytes (< 250 KB) |
| always-on Playwright | 3 passed; metered deep SEO scenario intentionally skipped |

Live browser runs completed the N27 paste path and the N29 real-public-page path through deep
SEO/AEO approval and Press Run. The N27 newsletter attempt reached orchestration but its live model
response failed the existing literal-placeholder validator; thumbnail concepts and image prompts
completed. The N29 `https://example.com/` run completed all 3 selected jobs. No console/page errors
occurred in the final always-on Playwright gate.

---

## 13. N28 resumable media and N30 voice notes — completed 2026-08-21

### Resumable private media

- Added tenant-scoped `MediaUpload` rows and the `ResumableMediaUploads` migration. Sessions own one
  campaign and private Asset, record contiguous bytes/block manifest/status/error/expiry and retain
  the completed transcript artifact id.
- The client sends 4 MiB blocks through the authenticated API with SHA-256. The API enforces the
  2 GiB operator cap, media MIME, exact block order and length, fixed-time checksum comparison and
  idempotent duplicate retries before committing the private block Blob.
- Pause keeps the active session; reload restores its offset and asks the browser user to reselect
  the same name/size because browser file handles do not survive navigation. Cancel removes Blob
  data. Provider failure returns to Committed with an actionable message, so retry does not upload
  again.
- Short media uses the configured Foundry transcribe deployment. Long media uses Azure Speech fast
  transcription and diarization. Both call the existing transcript writer, producing one artifact,
  approved `SourceAsset` and media-time-range evidence blocks.
- Production Bicep now provisions Speech, grants the App Service managed identity Cognitive
  Services User and passes only the endpoint. Local Region+Key configuration remains available.

### Focused voice recorder

- Added shared `IVoiceCaptureService`, `BrowserVoiceCaptureService`, `VoiceRecorder.razor` and the
  `castmill-recorder.js` island. It works in WASM and the MAUI WebView; Mac and Windows manifests
  declare microphone usage.
- No microphone opens before Record. The service detects insecure/unsupported contexts, reports
  permission denial, negotiates WebM/Opus then MP4, exposes elapsed time and a throttled input level,
  supports pause/resume/stop/playback/discard and auto-stops at ten minutes.
- Stop/discard/dispose release tracks, timers, audio context and object URLs. Use recording returns
  one bounded in-memory voice note and enters the resumable media path; no recorder-specific
  transcription or evidence model exists.

### Validation after N28/N30

| Suite | Result |
| --- | --- |
| Castmill.Api.Tests | 340 / 340 |
| Castmill.UI.Tests | 252 / 252 |
| Castmill.Media.Tests | 13 / 13 |
| Total .NET | 605 / 605 |
| editor-interop | 45 / 45, including 5 recorder tests |
| Mac Catalyst build | 0 warnings / 0 errors |
| always-on Playwright | 4 passed; metered deep SEO intentionally skipped |
| Bicep | Compiles; Speech resource, endpoint and managed-identity role present |

The combined browser journey selected uploaded WAV media and recorded a fake-microphone voice note,
then took each through timed evidence review, intent, mocked non-metered analysis approval and Press
Run. The complete browser suite reported no console/page errors. SQL tests prove interruption/resume,
idempotency, provider retry, short/long routing, cancellation and cross-tenant isolation.
