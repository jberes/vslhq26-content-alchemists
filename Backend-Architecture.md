# Castmill Backend — Architecture

> **Doc conventions.** This document follows the Azure Architecture Center reference-architecture format: architecture & dataflow first, then components, decision records, and considerations mapped to the five Well-Architected Framework (WAF) pillars. It is docs-as-code: it lives in the repo, changes ship in the same PR as the code they describe, and decisions are appended as ADRs — never rewritten.
>
> Companion docs: [Frontend-Architecture.md](Frontend-Architecture.md) · [Roadmap-Blazor.md](Roadmap-Blazor.md) · design reference: [Mill Floor handoff](docs/design_handoff_castmill_mill_floor/README.md)

---

## 1. Overview

The Castmill backend is a **single stateless ASP.NET Core (net10.0) Minimal API** service that owns identity enforcement, tenancy, persistence, secret custody, AI orchestration against Azure AI Foundry, and brokered publishing. It is the **only** component that ever holds credentials: storage keys, per-user Foundry keys, and publishing-broker tokens never reach a client.

**Workload shape:** low request volume, high per-request value (AI generation calls costing seconds and cents), strict tenant isolation, bursty media processing. This drives the three core postures: *stateless API + delegated heavy work*, *server-custody of every secret*, and *per-user rate limiting over autoscale complexity*.

## 2. Goals & non-goals

### Goals
| # | Goal | Measure |
|---|---|---|
| G1 | **Tenant isolation is structural, not conventional** | EF global query filters + integration tests proving cross-tenant reads/writes fail |
| G2 | **Zero secrets client-side** | Clients receive only short-lived, single-blob, single-operation SAS URLs and JWTs; audit shows no key material in any response |
| G3 | **Stateless & horizontally scalable** | Any instance can serve any request; no server affinity, no local disk state |
| G4 | **All AI behind one seam** | Every model call goes through `/api/v1/ai/*` via `Microsoft.Extensions.AI` abstractions; a model swap is config, not code |
| G5 | **Provenance is a first-class contract** | Every generation response carries transcript-segment citations; schema-validated before persist |
| G6 | **Deployable from empty subscription in one command** | Bicep + pipeline provisions and deploys end-to-end |
| G7 | **Observable by default** | Correlation ID client→server→dependency in every trace; Server-Timing on hot endpoints |

### Non-goals (v1)
- No external identity provider (Entra, Google, etc.) — identity is **ASP.NET Core Identity with email + password**, wholly owned by the API (ADR-010). No email-based password reset until an email sender exists; password change requires a signed-in session.
- No multi-user sharing, campaign members, or invites — every campaign is owned by the user who created it (ADR-011).
- No streaming token transport (SignalR is a v2 seam, deliberately left open).
- No background job framework (Hangfire/Quartz) — scheduling is delegated to the publishing broker; long media jobs run as Container Apps jobs.
- No multi-region active/active; single region + zone redundancy.
- No app-managed AI billing — Foundry credentials are BYO per user (revisit as ADR when a managed tier is wanted).

## 3. Architecture

```mermaid
flowchart LR
    subgraph Clients
        D[Desktop shell<br/>MAUI Blazor Hybrid]
        W[Web app<br/>Blazor WASM on SWA]
    end

    subgraph Azure["Azure (single region, zone-redundant)"]
        subgraph API["App Service — Castmill.Api (stateless, N instances)"]
            MW[Middleware<br/>auth · tenancy · rate limits · correlation]
            EP[Route groups<br/>/auth /me /settings /campaigns /artifacts<br/>/assets /brands /blob /publish /seo /ai]
            IDP[Identity<br/>ASP.NET Core Identity<br/>JWT + refresh-token issuance]
            AI[AI orchestration<br/>Microsoft.Extensions.AI]
            SAS[SAS minting]
            SEC[Secret custody<br/>AES-256-GCM]
        end
        SQL[(Azure SQL<br/>EF Core + migrations)]
        BLOB[(Blob Storage<br/>private + public containers)]
        ACA[Container Apps job<br/>ffmpeg media processing]
        AInsights[Application Insights]
    end

    subgraph External
        FOUNDRY[Azure AI Foundry<br/>BYO per-user project]
        SPEECH[Azure AI Speech<br/>fast transcription]
        BROKER[Publishing broker<br/>social scheduling]
        SEO[SEO data provider]
    end

    D -->|JWT bearer /api/v1| MW
    W -->|JWT bearer /api/v1| MW
    D & W -.->|"SAS GET only (uploads proxied via /blob — frontend ADR-F26)"| BLOB
    D & W -->|"email/password → /auth login/refresh"| MW
    MW --> EP --> AI
    EP --> IDP --> SQL
    EP --> SAS --> BLOB
    EP --> SEC
    EP --> SQL
    AI -->|user's endpoint + key| FOUNDRY
    AI --> SPEECH
    EP --> BROKER
    EP --> SEO
    EP -->|enqueue clip job| ACA --> BLOB
    API --> AInsights
```

### Dataflow (canonical request: "generate campaign")
1. Client holds an app-issued access JWT (from `POST /api/v1/auth/login` with email + password, or a silent `POST /api/v1/auth/refresh`) and calls `POST /api/v1/ai/campaigns` with the campaign brief + transcript reference.
2. Middleware validates the JWT signature against the server-held signing key, resolves `(tenant, user)` (every user owns exactly one tenant, created at registration), stamps the correlation ID, and applies the `ai` rate-limit partition.
3. The AI orchestrator decrypts the caller's Foundry credentials, resolves each generator's model through the alias table, and fans out generation calls; each generator returns typed JSON **with transcript-segment citations**, schema-validated before acceptance.
4. Artifacts persist to Azure SQL as typed JSON content with a `Version` counter; the response carries ETags. Generated images are written to Blob (private), published derivatives (WebP) to the public container.
5. Every dependency call is traced in Application Insights under the request's correlation ID; `Server-Timing` reports the phase breakdown to the client for the Press Run UI.

## 4. Components

| Component | Responsibility | Key constraint |
|---|---|---|
| **Middleware pipeline** | JWT validation (app-issued tokens, server-held signing key), tenant resolution + guard, correlation ID, forwarded headers, HSTS, rate limiting (`ai` 30/min · `writes` 60/min · `searches` 60/min, partitioned by `sub`) | Order is load-bearing; covered by integration tests |
| **Identity** (`Services/Auth`) | ASP.NET Core Identity (email + password): register, login, change-password, lockout, PBKDF2 hashing; issues short-lived (~15 min) access JWTs + rotating refresh tokens; one tenant created per user at registration | No external IdP (ADR-010); Production refuses to start without a JWT signing key; refresh tokens hashed at rest and revocable |
| **Route groups** (Minimal APIs) | One file per group under `Endpoints/`; all require the `TenantAllowed` policy | No controller ceremony; endpoint filters for validation |
| **Persistence** (`Data/`) | EF Core `DbContext`, **real migrations from day one**, global tenant query filters, ETag optimistic concurrency on artifacts, lightweight `Preview` projection (strips heavy content for list views) | `EnsureCreated` is banned; migrations gate deploys |
| **Secret custody** (`Services/Secrets`) | AES-256-GCM encrypted `UserSetting` values; typed accessors per secret kind (Foundry, broker); Production refuses to start without an encryption key | Secrets never appear in logs, responses, or client payloads |
| **SAS service** | Mints single-blob, single-op SAS (10 min default / 1 h cap); public-container write SAS for published WebP with immutable cache headers | Storage account key exists only here |
| **AI orchestration** (`Services/Ai`) | `Microsoft.Extensions.AI` clients (chat, image, transcription), per-user credential resolution, model-alias remap table, fan-out generators, deterministic validators, prompt-transparency ring buffer | One seam (G4); citations required (G5) |
| **Image plan** (`Services/Images`) | Typed `ImageSlot` rows owned by a content artifact (kind, target dimensions, auto/manual prompt, selected reference assets, source segment, state, published URL); up to three brand `product` assets attach automatically; Foundry image edits receive the real reference bytes; slot-accurate resize/crop and server-side headline overlay | Slot state is the single source for every image counter in the UI (ADR-012, ADR-013, ADR-025) |
| **Media** | ≤25 MB path: server-side extract + Foundry transcription. Long media: Azure AI Speech fast transcription. Web clip export **and reference-frame extraction**: Container Apps ffmpeg job reading/writing Blob | API instances never run ffmpeg in-process (ADR-014) |
| **Schedule mirror** | `ScheduleEntry` rows for The Wire (artifact → channel → slot, with queued/sent/error state), reconciled against the broker's queue; the broker remains the scheduler of record | The Wire renders from our data, never blocking on a broker round-trip (ADR-016) |
| **Publishing** | Typed client for the broker (channels, create/cancel scheduled posts); per-channel fan-out with partial-failure reporting | Broker token via secret custody |
| **Infra** (`/infra`) | Bicep: App Service (Linux, zone-redundant plan), Azure SQL, Storage, Container Apps environment, App Insights, SWA | Reprovisionable from scratch (G6) |

### API surface (v1)
`/api/v1` route groups — all `TenantAllowed` except `/health` and `/auth`:
`/auth` (register, login, refresh, logout, change-password) · `/me` (profile, avatar) · `/settings` · `/campaigns` (incl. `/{id}/seo-targets` GET/PUT — ADR-023/026) · `/campaigns/{id}/artifacts` (ETag CRUD) · `/campaigns/{id}/artifacts/{id}/revisions` (list, get, restore — B9) · `/campaigns/{id}/image-slots` (list, create-for-artifact, patch, generate, clear — ADR-025) · `/assets` · `/brands` (incl. `/lookup` draft-from-URL with SSRF guard, brand assets + per-kind templates) · `/blob` (sas, test, list, `assets/{id}/content` upload proxy — frontend ADR-F26) · `/images/composite` (headline overlay — B9) · `/media` (clip-jobs, frames — B9) · `/publish` (channels, posts, test) · `/schedule` (week, create, move, cancel — B9) · `/seo` (analyze, keyword-plan, research, **deep-analysis** — ADR-026, report, share) · `/ai` (status probe, SEO-gated campaign fan-out, per-generator endpoints, render-images, transcription, `runs/latest` — ADR-022/026) · `/health`

## 5. Design decisions (ADR log)

| ADR | Decision | Rationale | Revisit when |
|---|---|---|---|
| ADR-001 | Minimal APIs over MVC controllers | Small surface (~12 groups), endpoint filters cover validation, less ceremony | Surface exceeds ~25 groups or team grows past 3 |
| ADR-002 | Azure SQL + EF Core migrations (no `EnsureCreated`) | Deterministic schema evolution; migrations reviewed in PRs; SQL tooling ecosystem (incl. MSSQL MCP server for agent-assisted inspection) | — |
| ADR-003 | Typed-JSON artifact content column, not normalized tables | Artifact shapes evolve fast per channel; schema validation at the boundary; `Preview` projection solves list-view weight | A reporting workload needs relational queries over content |
| ADR-004 | BYO per-user Foundry credentials | User owns AI spend; no app-level billing/quota liability | Managed tier demanded → new ADR for app-owned Foundry + managed identity |
| ADR-005 | All AI server-side behind `Microsoft.Extensions.AI` | Model portability (Foundry catalog drift), secret custody, single audit point | — |
| ADR-006 | Request/response generation; no streaming in v1 | Fan-out progress (Press Run) needs per-artifact granularity, not per-token; halves transport complexity | Focus-Mode inline rewrite UX wants token streaming → SignalR ADR |
| ADR-007 | Scheduling delegated to publishing broker; no job framework | Broker owns retries/timezones/platform quirks; API stays stateless | Direct platform APIs (v2) force owned scheduling |
| ADR-008 | Heavy media in Container Apps jobs, not App Service | ffmpeg re-encodes starve web workers; jobs scale to zero | — |
| ADR-009 | Per-user fixed-window rate limits over autoscale-first | Cost control on AI endpoints; honest 429s beat surprise bills | — |
| ADR-010 | ASP.NET Core Identity (email + password) with app-issued JWTs; no external identity provider | No Azure app registration is available (rules out Entra/MSA); zero third-party setup or spend; Identity ships hashing/lockout/2FA hooks; the API issues its own JWTs so the bearer middleware and `IAuthTokenProvider` seam are unchanged; an external provider (Google, Entra) can be added later as an additive external login | Multi-team or SSO demand appears, or an email sender is added (enables password reset + external-login linking) |
| ADR-011 | One tenant per user; single-owner campaigns — no members, shares, or invites in v1 | Personal app; tenant-isolation machinery (G1) is retained structurally, so ownership *is* isolation and multi-user later is additive (an `Invitation` table + member rows), not a migration | A second user needs access to a campaign |
| ADR-012 | **Typed image slots**: an `ImageSlot` entity per campaign (kind, target dimensions, prompt, model alias, source segment, state, published URL), reserved when the run starts — not a bag of prompts inside one artifact | The design surfaces image state in three separate places (front-page "slots waiting", campaign-header `n/6` counter, Focus Mode slot list) and places a chosen variant *into* a named slot. A prompt list can't be counted, badged, addressed, or patched; rows can | Slots become user-definable per campaign (then they need a template table) |
| ADR-013 | Thumbnail headline text is **composited server-side** (SkiaSharp) after generation, never prompted into the image | Image models still mangle small text at thumbnail scale; a compositor gives exact safe-area control and lets the headline change without paying for a re-generation | A model renders reliable small text at 1280×720 |
| ADR-014 | Reference-frame extraction runs in the existing Container Apps ffmpeg job, not in-process | Same reason as ADR-008 — ffmpeg re-encodes starve web workers — and the clip job already reads source media from Blob and writes results back | — |
| ADR-015 | `IImageGenerator` is a provider seam; **non-Foundry image providers are admitted** behind a per-user credential slot, feature-flagged off by default | The Image Studio design offers a non-Foundry model (Google "nano banana"). §3.1's Foundry-only rule continues to hold for every *text* generator and for the default image path; admitting one optional image provider is additive, and the alternative is refusing a model the design calls for. Its credential rides the existing AES-256-GCM `UserSetting` custody — no new secret mechanism | A second non-Foundry provider appears (generalize the credential UX), or Foundry ships an equivalent model |
| ADR-016 | Schedule entries are **mirrored** in our database; the publishing broker remains the scheduler of record | The Wire is a persistent workspace surface: it must render on load without a broker round-trip, survive reload, and hold entries the broker hasn't accepted yet. Does not weaken ADR-007 — we never own retries or timezone logic; on reconcile, broker state wins | Direct platform APIs (v2) make us the scheduler of record |
| ADR-017 | Artifact revisions persisted server-side as a bounded ring (last 10), not client-local | Focus Mode's version filmstrip offers compare **and restore**; client-local takes evaporate on reload and never roam between the desktop and web shells. Supersedes the client-local filmstrip note in the frontend dataflow | Diff volume justifies content-addressed dedupe |
| ADR-018 | The whole repo targets the **.NET 10 GA package band** (`10.0.10`, SDK 10.0.302), replacing the rc.1 pins the backend shipped on. `global.json` pins the SDK with `rollForward: latestFeature` so a future major cannot be picked up silently | Forced at the start of frontend F0: every published version of Ignite UI for Blazor floors `Microsoft.AspNetCore.Components.Web` at `>= 10.0.0`, and bUnit 2.x at `>= 10.0.10`, so rc.1 could not restore the client at all. Doing it at F0 was far cheaper than after several client phases had been built on a dead band. Cost was one new GA analyzer (CA1873) and one latent test bug, both fixed; all 80 backend tests pass unchanged on GA | .NET 11 GA, which is a deliberate upgrade with the same re-verification |
| ADR-019 | **Artifacts carry a `Status` column** (Draft/InReview/Queued/Published) changed through a dedicated ETag-guarded `PATCH /artifacts/{id}/status` | The client's review queue, status encoding (frontend ADR-F12) and the Wire queue all read artifact state, and the entity had none — the whole review flow had nothing to read. Status is a distinct intent from a content save, so it is a distinct endpoint with its own guard; the review gate (E6.9) hangs off it. Stored as a string, not an enum, so the set can grow without a migration and the column reads plainly in the database | A fifth state appears, or transitions need role gating |
| ADR-020 | A **second, non-Foundry provider is admitted for text**, scoped to the second-pass **Tech Edit** (`Ai:TextProviders`, alias `chat-tech-edit`, credential `SecretKind.TechEditKey`). Pass 1 and the whole fan-out stay Foundry-only. An optional customer knowledge-base gateway (`KnowledgeBase:*` + `SecretKind.KnowledgeBaseToken`) can brief that pass | §3.1's Foundry-only text rule was written before there was a second pass worth crossing families for; a second opinion from the *same* family is worth much less than one from a different one, and the user's own key means their own billing and quota. ADR-015 named "a second non-Foundry provider appears" as its own revisit trigger — this is it, so the credential UX generalizes rather than being special-cased again. **ADR-005 is not weakened**: the Anthropic SDK's `AsIChatClient` returns a `Microsoft.Extensions.AI.IChatClient`, so no call site learns a second abstraction and an unconfigured provider falls back to Foundry rather than failing. The pass writes **in place behind a revision snapshot** (`reason = "tech-edit"`), and its output is run through the *same* validator pass 1 had to satisfy, so a second pass can never produce an artifact generation would have rejected | A third provider family appears, or Foundry's catalogue makes a direct key unnecessary |
| ADR-021 | **Optional git publishing writes through the Git Data API as one atomic commit**, authenticated by a per-user fine-grained PAT in existing secret custody (`SecretKind.GitHubToken`). Repo profiles are **brand-scoped**; branch names are deterministic (`castmill/{slug}`) and a re-publish updates that branch and reuses its open PR | The Contents API commits once *per file* and is non-atomic: a post is markdown plus several images, so a failure halfway leaves a branch holding the post without its hero image and a green PR the author merges into a broken site. Blobs → tree → commit → ref is one commit and one reviewable diff, and `base_tree` makes create-vs-update transparent, removing the whole stale-blob-sha 409 class. A typed `HttpClient` rather than Octokit because Octokit owns its own `HttpClient` and would sit outside the resilience/telemetry/timeout handling every other outbound call uses, for seven trivially shaped endpoints. A PAT reuses the shipped `BrokerToken` custody with zero new infrastructure, and token acquisition sits behind `IGitHubTokenProvider`-shaped resolution so a GitHub App can replace it without touching the publisher. Deliberately **not** requesting the Workflows permission, and path validation refuses `.github/` and any upward traversal. **Revisit when** an org customer's policy forbids long-lived PATs | A GitHub App becomes necessary, or two-way sync (repo → Castmill) is wanted |
| ADR-022 | **Generation runs are decoupled from their HTTP request**: the fan-out runs on a CTS linked to application shutdown (30-min cap), never to `RequestAborted`; an `InterruptedRunSweeper` hosted service marks runs orphaned by a dead process as `Interrupted` at startup; clients reattach to the live run row via `runs/latest` after a transport fault | A run used to execute inside its request, so a client timeout, closed app or dropped connection cancelled the remaining generators mid-run with the completed items' model spend already paid and nothing reporting the truncation ("said 13 items created, it did not create 13 items"). The one thing that genuinely kills a run — process death — now gets an honest terminal label instead of `Running` forever. Pinned by `RunSurvivalTests` | A queue/worker substrate arrives (then runs become jobs) |
| ADR-023 | **SEO research runs *before* generation** and its result is a campaign-level contract: `/seo/research` (seed keyword → volumes, suggestions, People-Also-Ask with a question-form fallback), the chosen targets persisted via `PUT /campaigns/{id}/seo-targets`, and a `SeoTargetBlock` injected into **every** text generator (primary keyword in title/first heading/first 100 words, secondaries woven, questions answered self-contained, never invent stats). A `{{LINKS}}` placeholder is substituted post-generation from the campaign's context links | Analysing content *after* it is generated means paying twice to fix what a prompt could have gotten right; writing to researched targets makes the first run the correct one. PAA needed live verification: noun-phrase queries rarely carry a PAA box, so the provider falls back to "what is {keyword}" only when the first pass is empty, and questions rank paa > knowledge-base > transcript. Pinned by `SeoTargetTests` (16) and `TargetsStepTests` | The provider adds a batched research endpoint, or targets need per-artifact overrides |
| ADR-024 | **Refresh-token reuse gets a grace window** (`Jwt:RefreshReuseGraceSeconds`, default 60): a replay of a just-consumed token within the window rotates again instead of revoking the family; outside it, reuse detection revokes exactly as before | Strict single-use read three innocent events as theft: the app dying between the exchange and storing its successor, two windows racing one stored token, and a network retry replaying an answered request — each turned into a forced sign-out of a healthy session. Auth0 ships the same mechanism as its "reuse interval". Revoked tokens get **no** grace, and family revocation on out-of-window reuse is unchanged — `RefreshReuseGraceTests` proves the teeth are still in | Refresh volume makes the grace a measurable replay surface |
| ADR-025 | **Every image card is owned by one content artifact**, carries `PromptMode` (`Auto`/`Manual`) and selected brand-asset ids, and is rendered through a reference-aware provider call. The resolver adds up to three brand assets of kind `product` automatically; explicit references and product screenshots are fetched server-side from private Blob and normalized before the Foundry image-edits call | Campaign-wide image controls made it impossible to tell which post an image belonged to and prompt-only “references” could not preserve a person, product or UI. Artifact ownership gives Focus, Image Studio, publishing and insertion one durable scope. Product screenshots are authoritative inputs by default, while manual references remain card-level choices | A provider exposes first-class subject/reference roles or product assets need per-card opt-out |
| ADR-026 | **A persisted deep SEO/AEO report is a hard precondition for even brief generation** (`POST /seo/deep-analysis` → upserted `seo-report`; then campaign targets are approved). Production and AI-brief generation return 409 until both the report and a non-empty approved target set exist. Approval updates the report payload/status as well as the campaign targets. The selected strategy, keyword gaps, existing rankings, answer-engine visibility, authority gap, competitors and report-grounded angles are injected into downstream text prompts. Blog head tags and JSON-LD are stored with the blog artifact, not on the campaign report | A post-hoc keyword report cannot influence titles, angles or copy already paid for. Making analysis a durable stage gives the producer a place to inspect and tune strategy, keeps all channels aligned to one intent, and prevents an alternate API caller from bypassing the wizard. Metadata belongs to the document it publishes with and versions with that document | The product supports non-search campaigns, which would require an explicit waived-analysis state rather than an implicit bypass |
| ADR-027 | **SEO has an explicit expensive report tier with honest partial failure and endpoint provenance.** Keyword research combines DataForSEO Labs Keyword Suggestions (phrase-match long tails), Keyword Ideas (category-adjacent opportunities), and Keyword Overview (exact metrics, difficulty and intent), then records the endpoint paths that completed. The deep report adds advanced organic SERP/PAA/AI Overview, ranked keywords, backlink summaries, domain position footprints, multi-keyword SERP Competitors with topical visibility/ETV, up to five enriched competitor profiles, and four AI-optimization engines. External sections soft-fail into typed `SeoSectionStatus` rows; failed answer engines are excluded from visibility denominators. Content angles are generated from the assembled report and fall back to deterministic keyword-grounded suggestions. Domain citations compare normalized URI hosts, never substrings | An “extreme deep dive” must use the provider dataset suited to each question rather than stretching one SERP into keyword, competitor and authority conclusions. Persisted provenance proves which metered lookups completed; typed availability prevents empty arrays masquerading as measured zeroes; exact host matching prevents `notexample.com` from counting as an `example.com` citation | DataForSEO changes the response contract or report cost/latency requires a queued background job with resumable progress |
| ADR-028 | **Pre-report AI is restricted to research-audience inference; brand voice is authoritative Brand data.** `POST /ai/campaigns/{id}/research-context` reads the transcript and returns only an audience, with no title, angle, keyword or publishable copy, and remains available before the SEO approval gate. Voice is read from the selected Brand's persisted style card; the post-approval brief model no longer infers a speaker voice from the transcript | The report needs a specific audience to shape research, but generating a content brief before research would violate ADR-026. A transcript's speaking style is also not necessarily the organization's approved voice; one persisted Brand must steer every channel consistently | Research audience becomes part of the Brand contract, or campaigns need several audience segments |

## 6. Considerations — Well-Architected pillars

- **Security.** ASP.NET Core Identity with PBKDF2 password hashing and lockout on repeated failures; short-lived (~15 min) app-signed access JWTs + rotating refresh tokens (hashed at rest, revocable on logout/password change); tenant guard on every route; AES-256-GCM secret custody with startup guards (no encryption key or JWT signing key → refuse to start; localhost CORS in Production → refuse to start); SAS least-privilege (single blob, single op, minutes-scale expiry); `appsettings.Local.json` excluded from publish; audit events on security-relevant actions (sign-in, password change, publish).
- **Reliability.** Stateless instances behind zone-redundant plan; ETag concurrency prevents lost updates; idempotent PUTs; transient-fault retry policies on SQL/Blob/HTTP via standard resilience handlers; health endpoint wired to platform probes.
- **Performance efficiency.** `Preview` projections keep campaign-open payloads small; `Server-Timing` exposes phase costs; AI fan-out parallelized per campaign; media off the request path (ADR-008).
- **Cost optimization.** BYO AI spend (ADR-004); Container Apps jobs scale to zero; rate limits cap runaway generation; single-region posture until usage justifies more.
- **Operational excellence.** Bicep-first provisioning; migrations as deploy gate; correlation IDs end-to-end; prompt-transparency log for AI support cases; this doc + ADR log maintained in-PR.

## 7. Phased backlog

**Contract for every phase:** ends in a state that is *committed, CI-green, deployed to the dev environment, and demonstrable*. No phase leaves broken scaffolding for the next. Each phase lists its **check-in gate** — the proof required before the phase's PR merges.

**Status legend:** ✅ complete · 🔶 partially complete (remaining work noted) · ⬜ not started. *(Last updated 2026-07-28.)*

### Phase B0 — Repo & walking skeleton *(size S)* — ✅ complete 2026-07-28
- Solution scaffold: `Castmill.Api`, `Castmill.Core`, test projects; editorconfig, analyzers, nullable enabled.
- `/health` endpoint; CI workflow (build + test on PR).
- **Check-in gate:** CI green; `curl /health` returns 200 locally.

### Phase B1 — Infrastructure as code *(size M)* — 🔶 code complete 2026-07-28 *(full Bicep under `/infra` — App Service + managed identity RBAC, Entra-only SQL, Entra-only Storage w/ containers+queue, ACA env + queue-scaled clip job, App Insights, 5xx alert — plus one-command `deploy.sh` that runs under `az login` alone (no app registration). Template compiles; the G6 gate — first live run into an empty resource group — hasn't been executed yet. Current dev runs on the hand-made SQL/storage.)*
- Bicep under `/infra`: App Service plan + app, Azure SQL (serverless tier to start), Storage, App Insights; deploy workflow.
- No external identity setup — auth is self-contained in the API (ADR-010, lands in B2).
- **Check-in gate:** empty subscription → `deploy` pipeline → `/health` live on App Service (G6 proven at skeleton scale).

### Phase B2 — Identity, tenancy & data core *(size L)* — ✅ complete 2026-07-28 *(412-stale-ETag gate closed by B4's artifact tests; the App Insights trace check still lands with B1's Azure resources)*
- ASP.NET Core Identity (email + password): `/auth` group (register, login, refresh, logout, change-password), app-issued access JWT + rotating refresh token, JWT bearer validation, JWT-signing-key startup guard.
- `TenantAllowed` policy; one tenant created per user at registration (permanent binding); `/me`.
- EF Core model (Tenant, User + Identity tables, Campaign, Artifact, Asset, BrandProfile, UserSetting, AuditEvent) + baseline migration; global query filters; ETag concurrency. Campaigns carry `OwnerId` (ADR-011).
- Correlation-ID middleware; rate-limit policies; integration-test harness (Testcontainers SQL).
- **Check-in gate:** integration tests prove register → login → authorized call → refresh → logout-revocation, G1 (cross-tenant access fails), and 412-on-stale-ETag; a trace in App Insights shows client-supplied correlation ID.

### Phase B3 — Secrets & storage *(size M)* — ✅ complete 2026-07-28 *(SAS is user-delegation via Entra RBAC — no storage account key exists anywhere; shared-key connection string supported as fallback; public-container publish path deferred to B7 where it's consumed)*
- AES-256-GCM `UserSetting` store + typed secret accessors; Production startup guards.
- SAS service + `/blob` group (mint, test, list); public-container publish path with immutable cache headers.
- **Check-in gate:** G2 audit — grep + integration tests show no key material in any response; SAS expiry and op-scoping tested.

### Phase B4 — Core resource APIs *(size L)* — ✅ complete 2026-07-28 *(assets are metadata-only until B3 wires blob SAS; settings refuse the reserved `secret.` prefix until B3's encrypted store)*
- `/campaigns`, `/artifacts` (typed-JSON content + `Preview` projection), `/assets`, `/brands`, `/settings`.
- Endpoint-filter validation.
- **Check-in gate:** full CRUD demonstrable via OpenAPI UI against dev; `Preview` payload for a 50-artifact campaign under 100 KB.

### Phase B5 — AI orchestration on Foundry *(size XL — the critical path)* — ✅ complete 2026-07-28 *(M.E.AI client layer + per-user credential resolution + model-alias table, /ai/status probe, transcript ingest w/ segment IDs, blog outline→draft→cross-model-audit, full fan-out ×13 kinds, deterministic validators incl. hard char caps, prompt-transparency ring buffer, and B5.4 image rendering: gpt-image-2/MAI → WebP (SkiaSharp) → public container → blog `![stub:slot]()` replacement. All proven via fake-model integration tests; the live-model run awaits Ai:Foundry credentials in appsettings.Development.json — verify with `GET /ai/status?probe=true`.)*
- `Microsoft.Extensions.AI` client layer; per-user Foundry credential resolution; model-alias remap table; `/ai/status` deployment probe.
- Timed-transcript ingest (segment IDs); blog generator (outline → draft → cross-model audit) **with citations**; then the fan-out set (social ×6 with per-platform rules and hard char caps, email sequence, newsletter, landing page, show notes, clip suggestions, image prompts).
- Deterministic validators (word bands, char caps, banned phrases) as a review gate; prompt-transparency ring buffer.
- **Check-in gate:** seeded transcript → full campaign fan-out in dev, every artifact schema-valid with ≥1 citation (G4, G5); model swap demonstrated by config change only.
- *Sub-checkpoints (each its own PR):* B5.1 client layer + probe · B5.2 transcript + blog · B5.3 social/email/landing/notes · B5.4 images · B5.5 validators + log.

### Phase B6 — Media pipeline *(size L)* — ✅ complete 2026-07-28 *(≤25 MB Foundry transcription + Azure AI Speech fast-transcription w/ diarization, blob-fed, auto-routed by size; clip export: `/media/clip-jobs` enqueue/status, storage-queue dispatch, hash-stored single-use callback tokens (constant-time compared, burned at terminal status), ffmpeg worker container under `/infra/clipjob` with ACA job + KEDA queue scaler in the Bicep. Lifecycle integration-tested with a captured queue; the live ffmpeg run needs the worker image pushed + infra deployed.)*
- Server transcription: ≤25 MB extract+transcribe path; Azure AI Speech fast-transcription path for long/diarized media.
- Resumable block-blob upload contract for web clients.
- Container Apps ffmpeg job: clip export (stream-copy + re-encode, 9:16 crop, burned captions) Blob-to-Blob; job enqueue/status endpoints.
- **Check-in gate:** browser-only flow ingests a 1 GB video → transcript → exported clip, API instances never exceeding baseline CPU (ADR-008 proven).

### Phase B7 — Publishing & SEO *(size M)* — ✅ complete 2026-07-28 *(typed broker client — token via secret custody — with `/publish` channels/queue/posts/cancel/test and per-channel partial-failure fan-out + publish audit events; SEO provider client with `/seo` analyze (typed report persisted as artifact) / report / share (HTML-encoded public snapshot, `noindex`). SEO provider is **DataForSEO v3** — live-verified 2026-07-28 (real volume/difficulty/CPC through /seo/analyze), plus `/seo/keyword-plan`: AI seo-brief (summary, focus keywords, 3 A/B YouTube titles) → DataForSEO metrics → opportunity-ranked plan artifact. Broker still TBD: fill Publish:BrokerBaseUrl when chosen; client paths may need adjusting to that vendor's shape.)*
- Broker client (channels, schedule, cancel, queue read) with partial-failure fan-out reporting; `/publish` group.
- SEO provider client; `/seo` analyze/report/share (public HTML snapshot, ~90-day SAS).
- **Check-in gate:** post scheduled and cancelled on a sandbox channel from OpenAPI UI; shared report opens unauthenticated.

### Phase B8 — Production hardening *(size M)* — ✅ complete 2026-07-28 *(standard resilience handlers on every outbound HTTP dependency, EF `EnableRetryOnFailure` for Azure SQL, App Insights wiring (activates when the connection string is set), 5xx metric alert in the Bicep, [security review checklist](docs/SECURITY-REVIEW.md) with two live-deploy items open (prod CORS allowlist, alert action group), and [key-rotation runbook](docs/KEY-ROTATION.md). Load pass + game-day drill deferred until a live production deployment exists.)*
- Load pass on hot endpoints; resilience-handler tuning; App Insights dashboards + alerts (5xx rate, AI latency, rate-limit saturation).
- Security review against §6 checklist; key-rotation runbook; penetration checklist (SAS scope, CORS, headers).
- **Check-in gate:** WAF review recorded in this doc; alerts fire in a game-day drill; G7 trace walkthrough documented.

### Phase B9 — Design-driven additions *(size L)* — ✅ complete 2026-07-28 *(all eight stories shipped: `ImageSlot`/`ArtifactRevision`/`ScheduleEntry`/`GenerationRun` entities + `DesignAdditions` migration applied to Azure SQL; `/image-slots` (list·reserve·patch·generate·place·clear), `/images/composite`, `/media/frames`, `/schedule` (list·create·move·cancel·reconcile), `/artifacts/{id}/revisions` (list·get·restore), `/ai/runs/{id}`, `/campaigns/{id}/preview`; `IImageProvider` seam with the Foundry adapter plus a config-gated non-Foundry adapter and per-provider readiness in `/ai/status`. 80 tests green (was 62). **Live-verified 2026-07-28:** reserve 6 slots → gpt-image-2 → cropped to exactly 1280×720 → WebP → headline composited → publicly served (HTTP 200, both base and composited measured at 1280×720), preview counter 1/6. One open item: the overlay uses the platform fallback face — set `Castmill:OverlayFontPath` to a licence-clean condensed face before prod (the API reports `fontFallback: true` until then).)*

Everything here is **additive**: it comes from the [Mill Floor design handoff](docs/design_handoff_castmill_mill_floor/README.md), which surfaces server capabilities B0–B8 don't have. No existing feature is removed or narrowed.

- **B9.1 Image slots** *(size M)* — `ImageSlot` entity + migration; slots reserved by the run: YouTube thumbnail 1280×720, blog header 1600×840, inline ×3 1200×675, social card 1200×1200. `/campaigns/{id}/image-slots` (list · patch prompt/model/aspect · clear), slot counts folded into the campaign `Preview` projection so one call feeds the header counter and the front-page block. Each slot carries the transcript segment that motivated it, so prompts stay provenance-labelled. (ADR-012)
- **B9.2 Slot-accurate output** *(size S)* — models emit only 1536×1024 / 1024×1536 / 1024×1024; add a SkiaSharp resize + aspect-preserving centre-crop pass to the slot's exact dimensions before WebP encode, with the crop rule documented and unit-tested per slot kind.
- **B9.3 Overlay compositor** *(size M)* — `POST /images/composite`: headline ≤32 chars, 8 % dashed-safe-area geometry honoured, condensed face scaled to output height, drop shadow; composites onto the stored WebP without re-generating, and re-composites on headline edit. Font must be a licence-clean embedded face — no system-font assumption on Linux App Service. (ADR-013)
- **B9.4 Reference frames** *(size M)* — `POST /media/frames` extracts a frame at a timestamp through the ACA ffmpeg job, stores it private, returns a short-lived SAS; feeds image-to-image "control with preservation" edits. (ADR-014)
- **B9.5 Second image provider** *(size M)* — `IImageGenerator` seam with the Foundry adapter as default and one non-Foundry adapter behind `Ai:Providers:*`, its key in the encrypted `UserSetting` store; `/ai/status` reports readiness per provider so the client can grey out a model instead of failing a generate. (ADR-015)
- **B9.6 Schedule mirror** *(size M)* — `ScheduleEntry` entity + `/schedule` group (week query · create · move · cancel) with queued/sent/error state; reconcile job pulls broker queue state and lets the broker win. The Wire loads from here. (ADR-016)
- **B9.7 Artifact revisions** *(size M)* — `ArtifactRevision` bounded ring (10) written on every AI regenerate and manual save; `/artifacts/{id}/revisions` list · get · restore, restore being an ordinary ETag-guarded write so concurrency rules don't fork. (ADR-017)
- **B9.8 Press Run granularity** *(size S)* — per-artifact fan-out endpoints already exist; add a run-scoped `runId` + `GET /ai/runs/{runId}` progress projection so the client's card-by-card reveal is driven by real completion events rather than a timer (ADR-006 stands — still no token streaming).
- **Check-in gate:** a run reserves 6 slots → generate into the thumbnail slot → composite a headline → the slot reads DONE with a WebP that is exactly 1280×720; a reference frame extracts from the seeded video; a schedule entry survives an API restart and reconciles against a stubbed broker; a revision restores byte-identical markdown; `/ai/runs/{id}` reports 14 completions in order.

**Dependency order:** B0 → B1 → B2 → B3 → B4 → B5 → {B6, B7 in parallel} → B8 → B9. B9.1/B9.2 unblock the client's Image Studio (F10) and should land first; B9.6 pairs with F8, B9.7 with F5. The frontend consumes each phase as it lands (see [Frontend-Architecture.md](Frontend-Architecture.md) §7 for the interleave).
