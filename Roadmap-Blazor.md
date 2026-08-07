# Castmill — Product Roadmap & Build Plan

**One source in. A full campaign out.**

Castmill turns a single piece of source media — a video, a podcast, a webinar, a raw transcript — into a complete, brand-voiced marketing campaign: a long-form blog post, platform-tuned social posts, an email sequence, a newsletter, a landing page, show notes, short video clips, and generated imagery — all reviewed, scheduled, and published from one calm, editorial workspace.

- **Client:** 100% Blazor, built on **Ignite UI for Blazor** (Infragistics). Prefer the **MIT open-source components** (50+ free components incl. Grid Lite — NuGet `IgniteUI.Blazor.GridLite`); premium components only where a free equivalent doesn't exist (charts, full data grid).
- **Shells:** .NET MAUI Blazor Hybrid (desktop) + Blazor WebAssembly (web) from one shared UI library
- **Backend:** ASP.NET Core Minimal APIs on Azure App Service, **Azure SQL**, Azure Blob Storage
- **AI:** 100% **Azure AI Foundry**, server-side, BYO per-user Foundry credentials
- **Identity:** ASP.NET Core Identity (email + password), app-issued JWTs — no external identity provider
- **Domain:** `castmill.ai` *(verified available July 28, 2026 — register before anything else)*
- **Design reference:** [Mill Floor handoff](docs/design_handoff_castmill_mill_floor/README.md) — mid-to-high-fidelity HTML prototypes of all ten screens. Layout, hierarchy, copy and interaction behaviour are intentional and should be matched; the fixed 1440 × 880 canvas and the single token sheet are **not** (see §1.2 and ADR-F10).

---

## Part 1 — Product & Brand

### 1.1 Vision

Marketing teams produce one great "master" asset a week and then spend days manually slicing it into channel content. Castmill is the mill between the master and the channels: drop the source in, and a fan-out of AI generators produces every downstream artifact — each one traceable back to the exact words in the source that produced it, each one auditable, editable, and schedulable before anything ships.

Three product principles:

1. **Provenance over magic.** Every generated claim traces to a transcript segment. Trust is the feature.
2. **Review is the workflow.** Nothing publishes without a human pass; the UX makes reviewing fast and pleasant, not a chore.
3. **Calm software.** No dashboards screaming metrics. An editorial desk, not a cockpit.

### 1.2 Brand & design language

**Two theme families ship together**, each in light and dark, with a runtime switcher (frontend ADR-F09). Feature CSS binds only to a **semantic token layer** (surface / on-surface / accent / status / rule / type roles / spacing / motion); the families supply the values. Adding a third family must require zero feature-CSS edits.

#### Family A — *Warm Editorial* (default)

Warm, editorial, book-like — closer to a well-set magazine than a SaaS dashboard.

| Token | Light | Dark | Notes |
|---|---|---|---|
| `--cm-paper` (background) | `#F5F0E8` warm ivory | `#1E1B18` warm charcoal | subtle paper grain overlay at 2–3% opacity |
| `--cm-ink` (foreground) | `#1A1815` | `#EDE7DD` | never pure black/white |
| `--cm-ember` (accent) | `#C15F3C` terracotta | `#D97757` lifted terracotta | actions, glows, links |
| `--cm-brass` (secondary) | `#8A7248` | `#A98E63` | metadata, timestamps |
| `--cm-sage` (success) | `#5F7355` | `#7E9471` | published/reviewed states |
| `--cm-panel` | `#FBF8F2` | `#26221E` | cards, docks |
| `--cm-rule` (borders) | `#DDD3C2` | `#3A342E` | hairline rules, like column rules in print |

- **Type:** serif display for headlines and artifact titles (Source Serif 4 or equivalent, licensed-safe), humanist sans for UI/body (Inter or Public Sans), monospace only for character counters and code.
- **Radius:** soft (4–7 px). **Status:** warm glow ring.

#### Family B — *Industry Blueprint* (from the design handoff)

Technical, drafting-table: square corners, hairline frames with `+` registration marks, duotone media plates.

| Role | Value | Notes |
|---|---|---|
| bg / surface | `#F2F2F3` / `#E9E9EA` | dark mode inverts to the `900`/`800` neutrals |
| text | `#1D1F20` | divider `rgba(29,31,32,.16)` |
| accent | `#5980A6` steel blue | ramp `100 #EEF6FF · 200 #D6EBFF · 300 #B5D9FD · 400 #94BCE3 · 500 #749DC4 · 600 #597EA3 · 700 #416180 · 800 #2C455D · 900 #1D2D3D` |
| neutral 400 | `#B7B7BA` | draft status |

- **Type:** Barlow Condensed 600 headings (42 / 32 / 25 / 20), Barlow 400 body 15 px/1.55, monospace for timecodes, counters and segment IDs; kickers 10 px monospace, 0.12–0.14 em tracking, uppercase.
- **Spacing scale:** 3.4 · 6.8 · 10.2 · 13.6 · 20.4 · 27.2 px. **Radius: 0.** **Shadows:** sm `0 1px 2px` · md `0 3px 10px` · lg `0 12px 32px`, ink at 14/16/22 %.
- **Status:** 3 px left bar — draft `#B7B7BA` · in review `#94BCE3` · queued `#416180` · published `#1D2D3D`.

#### Shared across both families

- **Motion:** 200 ms ease-out everywhere; no bounce, no parallax. The only "big" animation is the Press Run (§1.3.6). Honours `prefers-reduced-motion`.
- **Density:** generous margins; content column max ~72 ch in editors; whitespace is a feature. A comfortable/compact density toggle rides the spacing scale.
- **Status semantics:** the same four states in both families, encoded twice (bar + colour) so state never depends on colour alone (ADR-F12).
- **Dark mode:** first-class in both families, shipped from day one via CSS custom properties + Ignite UI theme mapping.
- **Layout is fluid, not fixed** (ADR-F10): the prototypes are drawn at 1440 × 880, but the app is fluid from 1024 px to ultrawide — rail collapses to icons below 1180 px and to an overlay below 900 px; wide content (the swimlane board, keyword tables) scrolls inside its own region so the page never scrolls sideways.

### 1.3 The UX — "The Mill"

Not a tree-and-panes app. Eight connected ideas.

**Navigation model (two scopes, kept separate — ADR-F11).** The left rail is *workspace* scope: **Front page · Campaigns · The Wire**, plus a card for the active campaign and the campaign switcher (5 most recent; past ~12 campaigns the rail defers to the Campaigns index and ⌘K searches all of them, transcript text included). A campaign's four views — **Mill Floor · Focus mode · Image studio · SEO analysis** — are a segmented tab strip on the persistent campaign header, never rail items. Switching campaign keeps the current view; every campaign-scoped surface re-renders from the selected campaign.

#### 1.3.1 The Front Page (home)
An editorial front page, not a project list. Serif-headlined blocks:
- **"Ready for review"** — artifacts awaiting a human pass, newest first, each with a one-line AI summary of what changed.
- **"On the wire this week"** — the next 7 days of scheduled posts as a compact strip.
- **"Drafts aging"** — gentle nudges on stale work ("The webinar campaign has 3 drafts untouched for 12 days").
- **"Image slots waiting"** — the count of empty image slots with model tags and a jump into the Image Studio. Empty slots were otherwise invisible until publish time; image state is surfaced in three places on purpose (front page, campaign header counter, Focus Mode slot list).
- **"Start a run"** — the single primary action: drop media / paste a transcript / paste a URL.

A global **⌘K omnibox** does everything: jump to any campaign/artifact, start generation, schedule, search transcripts.

**Starting a run is a 3-step flow** and the only way a campaign is created: (1) pick a source — drop zone plus paste-URL / paste-transcript / pick-from-assets; (2) transcription progress — bar + narrated log stepping through extract → transcribe by range → diarize → "*n* timed segments"; (3) the brief — title/audience/voice/angle prefilled by small-model suggestions, then a fan-out checklist (blog, social ×6, email sequence, newsletter, landing page, show notes, clips ×3, SEO report, **image plan ×6**) all checked by default → **Press Run**.

#### 1.3.2 The Mill Floor (campaign canvas)
The centerpiece. Each campaign is a **zoomable horizontal storyboard**:
- Leftmost: the **Source Master** card — media player + synchronized transcript with a scrubber.
- Fanning right: **channel swimlanes** — Blog, Social, Email, Clips, Images, Landing Page, SEO — each holding "printed" artifact cards.
- Lane set: **Blog · Social · Email · Clips · Images · Page/SEO** — 66 px uppercase lane labels, cards sized to their weight (120–220 px).
- Card state is encoded twice (ADR-F12): a **3 px left bar** *and* the family's status colour — warm glow ring in Warm Editorial, flat bar in Industry Blueprint. Four states: draft → in review → queued → published.
- Zoom is a segmented control (Overview 0.72 / 100 %) applying `transform: scale()` with `transform-origin: top left` — never a re-layout. Zoom out to see the whole campaign; zoom in and cards reveal excerpt previews.
- Canvas is custom Razor + CSS grid/transforms; Ignite UI supplies the structured widgets inside cards and dialogs.

#### 1.3.3 Provenance threads
Hover any artifact card (or any paragraph in Focus Mode) → a thin ink line traces across the canvas to the **transcript segments that sourced it**; click to **pin** the thread set (hovering elsewhere then restores the pinned one); the cited transcript rows highlight and each thread is labelled with its segment ID. Click through to the quoted source side-by-side. One cubic path per citation, drawn on an SVG overlay measured against the canvas region, recomputed on scroll / zoom / resize / theme change. Implemented by carrying segment-ID citations through every generation call. This is the demo moment and the trust story.

#### 1.3.4 Focus Mode
Click a card → the canvas collapses to a **filmstrip** along the bottom edge and the artifact opens full-bleed:
- **Manuscript editor** — the Notion-style markdown editor (§2.5), generous margins, outline rail on the right gutter for H1–H3 navigation.
- **The Producer** (right rail) — steering input ("make it more technical"), regenerate section/whole, two-pass generate→audit progress rendered as narrated steps naming the audit model ("Outlining… Drafting… Auditing against Field Manual…"), **the image slots used by this artifact** with EMPTY/DONE badges, a **version filmstrip** to compare and restore prior takes (server-persisted revisions — ADR-017/F14), and a footer validator line + "Mark reviewed & schedule".
- **Image stubs in the manuscript**: an unfilled slot renders as a dashed stub with a Generate action; a filled one renders as a figure with its caption. Stubs live in the markdown as real blockquote-style markers so they survive editor round-trips.
- Per-platform surfaces (social) show live **character-limit meters** against each target network.

#### 1.3.5 The Wire
A dockable **publish timeline** strip: a queue of reviewed-but-unscheduled artifacts on the left, the week ahead as day columns on the right. Drag a card onto a day → it schedules and a toast confirms the broker slot. Hover a scheduled item → platform-accurate post preview. Errors and "sent" states flow back into the same strip. The Wire renders from our own schedule mirror, so it loads instantly and survives reload (ADR-016).

#### 1.3.6 The Press Run
Generation is the animation. When a campaign generates, there are no spinners: artifact cards **print onto the canvas one at a time** as each generator completes, with a single calm narrated log line ("Printing: LinkedIn post · 3 of 14"). Progress *is* the fan-out becoming visible — driven by real per-artifact completions, never a timer.

#### 1.3.7 The Image Studio
The fourth campaign view, and the reason image work stopped being invisible. Left: the **image plan** — six typed slots reserved when the run starts, each with kind, target dimensions and an EMPTY/DONE badge:

| Slot | Size | Notes |
|---|---|---|
| YouTube thumbnail | 1280 × 720 | headline overlay + safe area |
| Blog header | 1600 × 840 | hero |
| Inline 1–3 | 1200 × 675 | figures inside the manuscript |
| Social card | 1200 × 1200 | |

Right: the studio — model choice (gpt-image-2 · MAI-Image-2.5 · optionally one non-Foundry provider, §3.1), a **provenance-labelled prompt** seeded from the transcript moment the image illustrates ("built from segment s02 · 04:18") with constraint chips, variant count 2/4/6, and a **reference frame** pulled from the source video for image-to-image edits with preservation. Thumbnail headlines (≤32 chars, safe-area guide) are **composited by Castmill after generation, never baked by the model** — models still mangle small text, and this way the headline can change without paying for a re-render. Generate → skeleton tiles → variant tiles → click one to place it: the slot flips DONE, the WebP publishes, and the manuscript's stub is replaced in place.

#### 1.3.8 The SEO / AEO desk
The fourth tab's companion: scorecard (score, one-line verdict, titles / JSON-LD / image-alt checks), keyword table with share-of-voice bars, and content angles with "Draft these as artifacts". Fed by the keyword plan — AI brief → DataForSEO metrics → opportunity ranking — including the 3 A/B-testable YouTube titles. **Honesty rule:** an unpublished campaign shows *projected* positions and says so on the surface.

### 1.4 What ships in v1 (scope fence)

**In:** media/transcript ingest, transcription (cloud + local desktop), campaign fan-out generation (blog, social ×6 platforms, email sequence, newsletter, landing page, show notes, clip suggestions, SEO brief + keyword plan, image prompts + generation), **typed image slots with the Image Studio** (variants, reference-frame edits, server-composited thumbnail headlines), the Mill Floor / Focus / Image Studio / SEO / Wire UX, **two theme families × light/dark with a switcher**, scheduling via a publishing broker, SEO/AEO report, brand-voice profiles, sharing within a tenant.
**Out (v2+):** direct platform posting APIs, real-time multi-user co-editing, analytics ingestion, mobile shells (the web app is responsive to 1024 px, not phone-shaped), marketplace/templates, streaming token-by-token generation (v1 is request/response with Press Run progress).

---

## Part 2 — Architecture

### 2.1 Solution layout

```
Castmill.sln
├─ src/Castmill.Api             ASP.NET Core (net10.0) Minimal APIs → Azure App Service
├─ src/Castmill.UI              Razor Class Library — ALL pages/components (Ignite UI for Blazor)
├─ src/Castmill.Desktop         .NET MAUI Blazor Hybrid shell (Win + macOS)
├─ src/Castmill.Web             Blazor WebAssembly host → Azure Static Web Apps
├─ src/Castmill.Core            Domain models, DTOs, validation (shared client+server)
├─ src/editor-interop           npm/esbuild project → one bundled castmill-editor.js RCL static asset
├─ tests/Castmill.Api.Tests     Integration tests (WebApplicationFactory + Testcontainers SQL)
├─ tests/Castmill.UI.Tests      bUnit component tests
├─ tests/editor-interop         vitest markdown round-trip suite
└─ tests/Castmill.E2E           Playwright (WASM app)
```

**Rule: every screen, component, and service lives in `Castmill.UI`/`Castmill.Core`.** The two shells contain only bootstrapping and platform service implementations. This is what makes desktop + web one codebase.

### 2.2 Platform abstraction (the seam between shells)

| Interface | Desktop (MAUI Hybrid) | Web (WASM) |
|---|---|---|
| `IAuthTokenProvider` | email/password sign-in → access JWT in memory, refresh token in MAUI `SecureStorage`; silent restore on cold start | same RCL sign-in screens; refresh token in browser storage; silent refresh on 401 |
| `IMediaPipeline` | bundled ffmpeg sidecar via `Process.Start` (audio extract, clip cut, WebP) | upload source to Blob via SAS → server ffmpeg endpoints |
| `ILocalTranscription` | **Whisper.net** (whisper.cpp binding), models cached in app data | not available — cloud transcription only (feature-flagged off) |
| `IFileExporter` | native save dialog, reveal-in-folder | browser download via one-line JS interop |
| `IExternalLinkOpener` | `Launcher.OpenAsync` | `target="_blank"` |
| `IDropZone` | OS file drop with real paths | `InputFile` drag-drop (streams) |

Small, honest JS-interop islands on web (each ≤ a few lines): clipboard write, file download, `localStorage` for per-device UI state. Everything else is .NET.

### 2.3 Backend

- **API:** Minimal APIs, versioned under `/api/v1`, OpenAPI in dev. Route groups: `/auth` (register, login, refresh, logout, change-password), `/me`, `/settings`, `/campaigns`, `/campaigns/{id}/artifacts`, `/assets`, `/brands`, `/blob` (SAS minting), `/publish` (broker), `/seo`, `/ai/*` (all generation), `/health`.
- **Data:** Azure SQL via EF Core with **real migrations from day one** (`dotnet ef migrations`). Entities: `Tenant`, `User` (+ ASP.NET Core Identity tables), `Campaign` (with `OwnerId` — single-owner, no members/shares in v1), `Artifact` (typed JSON content column + `Version` for ETag optimistic concurrency + lightweight `Preview` projection), `ArtifactRevision` (bounded ring of 10, powers the version filmstrip — ADR-017), `Asset`, `ImageSlot` (typed per-campaign image plan: kind, dimensions, prompt, model alias, source segment, state, published URL — ADR-012), `ScheduleEntry` (The Wire's mirror of broker schedule state — ADR-016), `BrandProfile`, `UserSetting` (AES-256-GCM encrypted value), `AuditEvent`, `ClipJob`. Tenant ID denormalized on children + EF global query filters.
- **Blob:** per-tenant private container + companion public container (immutable-cache WebP for embedding in published content). **Storage keys never leave the server** — clients receive short-lived (10 min) single-blob, single-operation SAS URLs minted by the API.
- **Auth:** ASP.NET Core Identity, email + password — no external identity provider (no Azure app registration needed). The API issues short-lived (~15 min) access JWTs + rotating refresh tokens (hashed at rest, revoked on logout/password change) and validates its own bearer tokens. One tenant per user, created at registration, permanent binding. Rate-limit policies partitioned per user: `ai` 30/min, `writes` 60/min, `searches` 60/min.
- **Secrets:** per-user encrypted settings for Foundry endpoint+key and the publishing-broker token; startup guards refuse to boot Production with missing encryption key or localhost CORS.
- **No server state beyond SQL+Blob:** stateless, horizontally scalable; scheduling delegated to the publishing broker; long media jobs (web clip export) run as Azure Container Apps jobs writing results to Blob.

### 2.4 AI on Azure AI Foundry — see Part 3.

### 2.5 The editor (the one JS-interop component that matters)

Ignite UI for Blazor has no rich-text editor, so Castmill ships **one** custom interop component:

- **Contract: markdown string in → markdown string out.** No HTML persisted, no editor-native JSON persisted. This keeps every export path (`.md`, `.docx`, ZIP, API) trivial and makes the editor swappable forever.
- `castmill-editor.js`: an esbuild bundle of TipTap core (`@tiptap/core`, starter-kit, markdown, image, placeholder, task-list/item, suggestion, bubble-menu + floating-ui). Vanilla JS/DOM only — no React/Vue runtime.
- `RichEditor.razor`: hosts the bundle via `IJSObjectReference`; surface is tiny — `init(el, markdown, opts)`, `setMarkdown()`, `getMarkdown()`, `onChange`/`onBlur` callbacks through `DotNetObjectReference`. Persistence commits on blur.
- Features: `/` slash menu (12 block types), selection bubble menu (bold/italic/strike/code/link), task lists, image + YouTube-as-thumbnail insertion via Blazor dialogs, outline rail as a pure Blazor sibling component fed by heading-change events.
- **Ordered-list tokenizer guard:** the markdown bridge must disable non-CommonMark "lettered list" tokenization (prose like "Yes. Both…" must never parse as a list). A **vitest round-trip regression suite** in `tests/editor-interop` is a required deliverable of the editor epic, run in CI: parse→serialize→parse must be byte-stable on a corpus including FAQ prose and numbered steps.
- Secondary markdown surfaces (brand guidelines, AI context) are **pure .NET**: textarea + **Markdig**-rendered preview with a tag-whitelist sanitizer (AI text can contain pseudo-tags like `<colors>` — unwrap unknown elements).
- `.docx` export via **DocumentFormat.OpenXml** server-side (styled: serif headings, code in monospace); `.md` and campaign ZIP via `IFileExporter`.

### 2.6 Ignite UI for Blazor usage map

| Surface | Ignite UI component |
|---|---|
| SEO/AEO report charts (bar, stacked bar) | **Blazor-ApexCharts** (free for Blazor — frontend ADR-F24); replaces `IgbCategoryChart`, removing the last commercial-Ignite dependency |
| Queue & report lists | `IgbList` / `IgbGrid` where sorting matters |
| Dialogs (new campaign, settings, steering, image insert) | `IgbDialog` |
| Tabs (metadata: combined / head / JSON-LD) | `IgbTabs` |
| Inputs, selects, checkboxes, date-time pickers (Wire slots) | `IgbInput`, `IgbSelect`, `IgbCheckbox`, `IgbDatePicker` |
| Toasts/snackbars | `IgbToast` / `IgbSnackbar` behind a DI `INotifier` |
| Avatars, badges, chips (platform tags, status) | `IgbAvatar`, `IgbBadge`, `IgbChip` |
| Trees (asset browser) | `IgbTree` |
| Image-plan slot list, model/variant pickers | `IgbList` + `IgbRadioGroup` / custom segmented control |
| Stepper for the 3-step new-campaign flow | `IgbStepper` (MIT) |

The Mill Floor canvas and its provenance threads, the campaign header + tab strip, Front Page, Focus Mode chrome, the Image Studio variant grid, the Wire, and the Press Run animation are **custom Razor/CSS** — that's the differentiated UX and shouldn't fight a component library.

---

## Part 3 — AI on Azure AI Foundry

### 3.1 Principles

- **Everything server-side** under `/api/v1/ai/*`. No model keys or provider calls in any client shell.
- **BYO per-user credentials:** each user stores their Azure AI Foundry project endpoint + API key in encrypted settings (validated by a probe endpoint that lists deployments). A guided setup screen walks through creating a Foundry project and the required model deployments.
- **Provider abstraction:** `Microsoft.Extensions.AI` (`IChatClient` / `IImageGenerator` equivalents) so model swaps are configuration; a model-alias remap table handles catalog drift without breaking persisted user settings. Aliases may name their resource (`"eastus2:gpt-image-2"`) so deployments can live across several Foundry resources.
- **Foundry-only, with one documented exception.** Every *text* generator and the default image path run on Azure AI Foundry. The Image Studio may additionally offer **one non-Foundry image provider** (e.g. Google's "nano banana") behind the `IImageGenerator` seam, feature-flagged off by default, with its key in the same encrypted per-user store and its readiness reported by `/ai/status` (backend ADR-015). No other provider category is admitted without a new ADR.
- **Request/response first.** v1 generation is blocking per artifact; the Press Run gives per-artifact progress. SignalR token streaming is a v2 epic.
- **Prompt transparency:** every call's full prompt + model + timing recorded to an in-memory ring buffer, viewable in a "Prompt log" dialog. Never persisted.

### 3.2 Model matrix (Foundry catalog, July 2026 — re-verify at build time)

| Job | Primary | Alternatives in Foundry |
|---|---|---|
| Long-form drafting (blog, landing page) | GPT-5.x flagship (reasoning effort high) | Anthropic Claude (Opus/Sonnet class) via Foundry catalog |
| Audit pass (two-pass generate→audit) | different family than the drafter (cross-model audit) | configurable per generator |
| Social/email/short-form | GPT-5.x mid-tier | Claude Sonnet class |
| Suggestions (metadata, clips, image ideas) | small/fast tier | MAI or mini models |
| **Image generation** | **gpt-image-2** (hero images, thumbnails, reference-image edits) | **MAI-Image-2.5 / 2.5-Flash** (Microsoft first-party; image-to-image editing, control-with-preservation), gpt-image-1.5/1/1-mini, Stable Diffusion 3.5, Stable Image Ultra/Core |
| Transcription (cloud) | `gpt-4o-transcribe` (diarized variant where available) | Whisper on Azure OpenAI (≤25 MB), **Azure AI Speech fast transcription** for long media + diarization |
| Transcription (desktop offline) | Whisper.net (local whisper.cpp models) | — |

Every generator has a per-user model override with a "default" sentinel; defaults live in one config file.

### 3.3 Generation pipeline (the fan-out)

1. **Ingest** → source media to Blob (web) or local path (desktop); audio extracted (mono 16 kHz) and transcribed to a **timed transcript** (segment IDs + timestamps) — the provenance backbone.
2. **Brief** → user confirms title, audience, angle, brand profile; small-model metadata suggestions prefill everything.
3. **Fan-out** → parallel generators produce: blog (outline → draft → audit), 6-platform social set (per-platform rules: length bands, hashtag policy, URL policy, hard char caps), email sequence, newsletter, landing page copy, show notes, clip suggestions (in/out timestamps + platform fit), image prompts.
4. **Every generator returns citations** — the transcript segment IDs it drew from — persisted with the artifact to power provenance threads.
5. **Audit** → deterministic validators run before "reviewed" is allowed: word-count bands, keyword occurrence targets, banned-phrase list, per-platform char limits.
6. **Image plan** → the run **reserves six typed slots** (§1.3.7) with brand-aware, provenance-labelled prompts, rather than emitting a loose bag of prompts. Each slot records the transcript segment it illustrates.
7. **Images** → slot prompt (+ optional reference frame extracted from the source video) → gpt-image-2/MAI generation → resize/crop to the slot's exact dimensions → WebP publish to the public container → optional server-side headline compositing for the thumbnail → inline replacement of visible image-stub markers in the blog markdown (stubs are real markdown blockquote markers so they survive editing round-trips).

Platform character limits (enforced in composer meters and validators): X 280 · Bluesky 300 · Mastodon/Threads/Pinterest 500 · Instagram/TikTok 2,200 · LinkedIn 3,000 · YouTube 5,000 · Facebook 63,206.

---

## Part 4 — Backlog

Sizing: S ≈ ≤2 days · M ≈ ≤1 week · L ≈ 2–3 weeks · XL ≈ 4+ weeks (one engineer + Claude Code).

**Status:** ✅ complete · 🔶 partial (gap noted) · unmarked = not started. *(Last updated 2026-07-28 — backend **B0–B9** shipped, 80 tests green; this revision folds in the [Mill Floor design handoff](docs/design_handoff_castmill_mill_floor/README.md): new stories 3.5, 3.6, 4.8–4.10, 5.7, 6.10–6.14, 8.4, 10.6 and the new epic E11, plus a rewritten 4.1/4.3/4.4/9.2. **Nothing was removed** — the design's additions are additive to the original scope.)*

### E1 — Foundations (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 1.1 🔶 | Register `castmill.ai`; create GitHub repo, solution scaffold (all projects in §2.1), CI build on PR | S | `dotnet build` + all test projects green in Actions — *server solution + CI done 2026-07-28; **all §2.1 client projects scaffolded 2026-07-29** (Castmill.UI, Castmill.Web, Castmill.Desktop, editor-interop, Castmill.UI.Tests, tests/editor-interop) in frontend phase F0, repo moved to the .NET 10 GA band (ADR-018); domain registration + GitHub remote still pending. Castmill.E2E (Playwright) is the one §2.1 project still absent — it arrives with F9/E10.3* |
| 1.2 🔶 | Provision Azure: App Service (Linux), Azure SQL, Storage account, Static Web App, Application Insights — as Bicep in `/infra` | M | `azd up`/pipeline provisions from scratch; `/health` returns 200 — *Azure SQL being created manually; Bicep still owed for G6* |
| 1.3 ✅ | ASP.NET Core Identity: Identity tables in the baseline migration, `/auth` group (register, login, refresh, logout, change-password), JWT issuance + bearer validation, signing-key startup guard | M | register → login → authorized `/me` → refresh → logout-revocation proven in integration tests — *done 2026-07-28, incl. refresh-reuse family revocation* |
| 1.4 ✅ | EF Core baseline migration (all §2.3 entities), tenant query filters, ETag concurrency on artifacts | M | integration tests prove cross-tenant isolation + 412 on stale ETag — *done 2026-07-28: isolation proven at both DbContext and HTTP level; 428/412 If-Match contract tested* |
| 1.5 🔶 | Observability: correlation-ID middleware, Server-Timing, structured logs → App Insights | S | correlation ID visible client→server in one trace — *correlation-ID middleware done (validated, injection-safe); Server-Timing + App Insights pending Azure* |

### E2 — Auth, tenancy & secrets (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 2.1 ✅ | Desktop sign-in/register/change-password screens (RCL), refresh token in MAUI `SecureStorage`, silent restore on cold start, sign-out revokes | M | cold-start silent restore works; tokens never written unencrypted |
| 2.2 ✅ | Web sign-in (same RCL screens), refresh token in browser storage, token attach via `IAuthTokenProvider` in one HTTP chokepoint, silent refresh on 401 | S | same UI code signs in on web |
| 2.3 ✅ | Tenant guard: one tenant per user created at registration, permanent binding, `/me` endpoint | S | cross-tenant access rejected in integration tests — *done 2026-07-28* |
| 2.4 ✅ | Encrypted `UserSetting` store (AES-256-GCM) + settings sync; per-device UI state stays local | M | settings roam across machines; key rotation documented — *done 2026-07-28: authenticated encryption w/ tamper tests; secret values never returned by any endpoint* |
| 2.5 | BYO Foundry credentials: settings UI, encrypted storage, `/ai/status` probe listing deployments, guided setup doc | M | invalid endpoint/key surfaces actionable error before first generation |

### E3 — Design system (M→L)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 3.1 ✅ | **Semantic token layer** + both families (§1.2) as CSS custom properties, each light/dark; paper-grain treatment (Warm Editorial) and registration-mark/duotone treatments (Industry Blueprint) | M | all four family × mode combinations pass WCAG AA on text; no feature CSS references a family token directly |
| 3.2 ✅ | Map tokens onto Ignite UI theming **per family** | M | dialog/tabs/inputs visually indistinguishable from custom chrome in both families |
| 3.3 ✅ | Typography & motion spec page (living style guide route, dev-only) with family × mode × breakpoint switchers | S | reviewed sign-off on the style guide |
| 3.4 ✅ | `INotifier` toast service, confirm-dialog service, empty-state components | S | used by ≥3 features without bespoke styling |
| 3.5 ✅ | **Theme switcher**: `ThemeService` (family × mode), persisted per device, `prefers-color-scheme` on first run, class-swap application | S | switch is instant with no flash; provenance overlay re-measures; choice survives restart on both shells |
| 3.6 ✅ | **Responsive layout system** (ADR-F10): fluid app grid, breakpoints `sm 900 / md 1180 / lg 1440 / xl 1920`, rail collapse (icons < 1180, overlay < 900), content max-widths, per-region scroll containers, comfortable/compact density toggle | M | every screen renders without page-level horizontal scroll from 1024 px to 2560 px and at 200 % zoom; nothing clipped at 700 px height |

### E4 — The Mill UX (L)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 4.1 ✅ | Front Page: review queue (with one-line "what changed" summaries), wire-this-week strip, aging drafts, **image slots waiting**, "Start a run" | M | loads <1 s against seeded data; empty-slot count matches the campaign header counter |
| 4.2 ✅ | ⌘K omnibox: navigation, actions, transcript search | M | keyboard-only operation of the whole app |
| 4.3 ✅ | Mill Floor canvas: Source Master card (player + timed transcript + scrubber), swimlane storyboard, Overview/100 % zoom, status bar + glow ring, card previews | L | 60 fps pan/zoom with 100 cards (virtualized) on WASM; board scrolls in-region, page never scrolls sideways |
| 4.4 ✅ | Provenance threads: citation storage → hover trace → **click to pin** → side-by-side quoted source; segment-ID labels + cited-row highlight | M | every generated artifact shows ≥1 traceable segment; threads stay correct after resize, zoom and theme switch |
| 4.5 ✅ | Focus Mode: filmstrip collapse, manuscript layout, inline image stubs, Producer rail (steer/regenerate/narrated two-pass/slot badges/versions) | L | version compare + restore round-trips markdown byte-stable |
| 4.6 | The Wire: dock, queue column, drag-to-schedule, platform preview on hover, sent/error states | M | drag from canvas schedules via broker in one gesture; the chip survives a reload |
| 4.7 ✅ | Press Run: per-artifact print-in animation + narrated log line driven by real completion events | S | no spinners anywhere in generation UX; reveal order matches completion order |
| 4.8 ✅ | **App shell & navigation model** (ADR-F11): rail with WORKSPACE / IN THIS CAMPAIGN grouping, active-campaign card, campaign switcher + index-past-12 rule, shortcut hints, "Start a run" | M | rail never lists campaign views; switching campaign re-renders header **and** content (regression-tested) |
| 4.9 ✅ | **Campaign header + view tabs**: breadcrumb, name, `n/6` image counter, four tabs as real buttons | S | tabs keyboard-reachable with visible focus rings; switching campaign keeps the active view |
| 4.10 ✅ | **New-campaign 3-step flow**: source picker, transcription progress with narrated log, brief + fan-out checklist → Press Run | M | the only path that creates a campaign; no indeterminate spinner in any step |

### E5 — Editor (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 5.1 ✅ | `editor-interop` bundle: TipTap core + markdown + slash menu + bubble menu, esbuild → RCL static asset | M | bundle <250 KB gzip; zero framework runtime inside |
| 5.2 ✅ | `RichEditor.razor` interop component + blur-commit persistence + outline rail | M | works identically in both shells |
| 5.3 ✅ | **Round-trip regression suite** (vitest, CI-gated): FAQ prose, numbered steps, tasks, images | S | parse→serialize→parse byte-stable on full corpus |
| 5.4 | Image & YouTube insert dialogs (YouTube = thumbnail + link, pure markdown) | S | inserted content survives round-trip + export |
| 5.5 ✅ | Markdig preview surfaces (brand guidelines/AI context) with tag-whitelist sanitizer | S | pseudo-tags render as text, never as DOM |
| 5.6 | Exports: `.md`, styled `.docx` (OpenXml), campaign ZIP | M | Word opens the .docx with correct heading styles |
| 5.7 ✅ | **Artifact revisions API + filmstrip binding** (ADR-017/F14): bounded ring of 10 written on every regenerate/save; list, get, restore as an ETag-guarded write | M | restore returns byte-identical markdown and survives reload on the other shell — *server side done 2026-07-28 (bounded ring of 10, ETag-guarded restore, snapshots on manual save / image-placed / restore); filmstrip binding is a client story. Note: AI regenerate still inserts a new artifact row rather than revising in place* |

### E6 — AI pipeline (XL)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 6.1 ✅ | Foundry client layer on `Microsoft.Extensions.AI`: chat, image, transcription; per-user credential resolution; model-alias remap table | M | one config swap retargets any generator's model — *done 2026-07-28: chat, image, and transcription clients behind the alias table; per-user secrets → config fallback; /ai/status probe* |
| 6.2 ✅ | Timed-transcript ingest: segment IDs, timestamps, speaker labels (when diarized) | M | transcript renders synced to media scrubber — *server side done 2026-07-28 (paste + both transcription paths); scrubber UI is a client story* |
| 6.3 ✅ | Blog generator: outline → draft → cross-model audit; citations; image-stub markers | L | 1,500–2,500-word draft with valid stubs + citations — *done 2026-07-28; word-band + citation validators enforced; live-model run pending Foundry creds* |
| 6.4 ✅ | Social fan-out: 6 platforms, per-platform rules + hard caps, hashtags policy | M | validators reject over-limit posts before review — *done 2026-07-28; caps from shared PlatformLimits table* |
| 6.5 ✅ | Email sequence, newsletter, landing page, show notes generators | M | each returns citations + passes validators — *done 2026-07-28* |
| 6.6 ✅ | Clip suggester: in/out timestamps + platform fit + hook text | M | timestamps land within source duration; ranked list — *done 2026-07-28; in/out range validator enforced* |
| 6.7 ✅ | Image pipeline: prompt builder (brand-aware), gpt-image-2/MAI generation, WebP publish, stub replacement | L | published blog renders images from public container — *done 2026-07-28: /ai/campaigns/{id}/render-images generates per slot (blog-hero, youtube-thumbnail, inline), WebP-encodes, publishes to the public container, and replaces blog `![stub:slot]()` markers; live render awaits Foundry creds* |
| 6.8 | Brand-voice: exemplar ingestion → distilled style card; injected into every generator | M | A/B: generated copy matches style card on rubric |
| 6.9 🔶 | Deterministic validators + review gate; prompt-transparency log dialog | S | "Mark reviewed" blocked until validators pass — *server validators + /ai/log ring buffer done 2026-07-28; review-gate UI is a client story* |
| 6.10 ✅ | **Typed image slots** (ADR-012): `ImageSlot` entity + migration, slots reserved by the run with provenance-labelled prompts, `/campaigns/{id}/image-slots` (list/patch/clear), slot counts in the `Preview` projection | M | one `Preview` call feeds the header counter, front-page block and Focus Mode list — no per-surface polling — *done 2026-07-28: `GET /campaigns/{id}/preview` returns artifacts + slots + imagesFilled/imagesTotal in one payload; reservation is idempotent and happens inside the run* |
| 6.11 ✅ | **Slot-accurate output + overlay compositor** (ADR-013): resize/crop from the model's native size to exact slot dimensions; `POST /images/composite` for ≤32-char headlines with 8 % safe area, licence-clean embedded font | M | thumbnail is exactly 1280 × 720; editing the headline re-composites without a new generation — *done 2026-07-28, live-verified: gpt-image-2 output cropped to exactly 1280×720 and re-composite made zero model calls. Open: set `Castmill:OverlayFontPath` to a licence-clean condensed face — the API reports `fontFallback: true` until then* |
| 6.12 ✅ | **Reference-frame extraction** (ADR-014): `POST /media/frames` at a timestamp via the ACA ffmpeg job → private blob + short SAS | M | a frame feeds an image-to-image edit; API CPU unchanged during extraction — *done 2026-07-28: `POST /media/frames` enqueues a `frame`-mode job on the existing queue/worker/status path; live ffmpeg run still needs the worker image pushed* |
| 6.13 ✅ | **Second image provider** (ADR-015): `IImageGenerator` seam + one non-Foundry adapter, feature-flagged, credential in the encrypted store, readiness in `/ai/status` | M | an unconfigured provider is disabled in the UI with a reason — never a failed generate — *done 2026-07-28: `/ai/status` returns per-provider readiness; a provider that isn't explicitly enabled never reaches the registry* |
| 6.15 ✅ | **Tech Edit — second pass on a different provider** (ADR-020): `IChatProvider` seam + Anthropic adapter behind `Ai:TextProviders`, optional knowledge-base gateway, `POST /ai/campaigns/{id}/artifacts/{artifactId}/tech-edit`, Producer-rail button | L | revises in place behind a `tech-edit` revision, output held to the kind's own pass-1 validator, unconfigured provider falls back to Foundry with the reason on the control — *done 2026-08-06. Note this is the **first AI path that revises in place** rather than printing a new row, so 🔶5.7 no longer applies to it. Default model `claude-opus-5`; sampling parameters are deliberately never sent (they are a 400 from Opus 5 onward) and thinking is left adaptive* |
| 6.14 ✅ | **Run progress projection**: `runId` + `GET /ai/runs/{id}` so the Press Run reveals on real completions | S | 14 artifacts report in completion order; no client-side timer — *done 2026-07-28: `GenerationRun` row + `GET /ai/runs/{id}`, run id also returned in the response and the `Castmill-Run-Id` header* |

### E7 — Media (L)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 7.1 🔶 | Ingest UX: drop/upload media, paste transcript, paste URL (article fetch) | M | 2 GB video uploads resumably on web (block blob) |
| 7.2 🔶 | Desktop media services: ffmpeg sidecar fetch script (pinned SHA-256), audio extraction, WebP | M | works offline; binaries verified at fetch |
| 7.3 ✅ | Desktop local transcription: Whisper.net + model download manager with progress | M | 1-hour audio transcribes locally without cloud |
| 7.4 🔶 | Cloud transcription endpoint: extraction server-side, ≤25 MB path + Speech fast-transcription path | M | web-only user can transcribe end-to-end — *both API paths done 2026-07-28 (audio files; auto-routes >25 MB to Speech w/ diarization); server-side a/v extraction not included — video needs the Speech path or desktop extraction* |
| 7.5 ✅ | Clip export — desktop: stream-copy + re-encode modes, 9:16 crop, burned ASS captions, faststart | L | frame-accurate re-encode; captions clear platform UI margins |
| 7.6 🔶 | Clip export — web: Container Apps ffmpeg job against Blob source, result to Blob + download | M | same output as 7.5 from the browser — *server side done 2026-07-28 (enqueue/status endpoints, queue dispatch, token-authenticated worker callback, ffmpeg worker + ACA job in /infra); live run needs the worker image pushed + infra deployed* |

### E8 — Publishing (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 8.1 🔶 | Broker integration (Buffer-class API): token settings, channel list, create/delete scheduled posts | M | schedule + cancel round-trip from The Wire — *API side done 2026-07-28 (typed client, /publish group, partial-failure fan-out, audit events); pick the concrete broker, fill Publish:BrokerBaseUrl, and adjust client paths to its API shape* |
| 8.2 | Composer: per-channel text variants, char meters, media attach, schedule/now | M | over-limit channels warn with exact truncation counts |
| 8.3 | Queue view on The Wire: queued/sent/error tabs fed live from broker | S | error states actionable (retry/edit) |
| 8.4 ✅ | **Schedule mirror** (ADR-016): `ScheduleEntry` entity + `/schedule` group (week/create/move/cancel) reconciled against broker state, broker wins on conflict | M | The Wire renders before any broker call and a scheduled chip survives an API restart — *done 2026-07-28: `/schedule` group with broker push, move-as-cancel-and-reschedule, and a reconcile that lets the broker win; a broker rejection leaves an Error row rather than losing the gesture* |

### E9 — SEO/AEO reports (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 9.1 ✅ | Analysis endpoint (SERP/keyword/AI-overview provider), typed report model | M | report persists + reloads — *done 2026-07-28: provider is **DataForSEO v3** (Basic auth, search-volume + keyword-suggestions endpoints), live-verified — real volume/difficulty/CPC persisted and reloaded* |
| 9.2 | **SEO / AEO desk** (fourth campaign view): scorecard column, keyword table + share-of-voice bars on `IgbCategoryChart`, content angles with "Draft these as artifacts", the 3 A/B YouTube titles | M | charts (Blazor-ApexCharts, ADR-F24) theme correctly in all four family × mode combinations; an unpublished campaign labels its positions **projected** on the surface, not in a tooltip |
| 9.3 ✅ | Shareable public report link (public-container HTML snapshot, ~90-day SAS) | S | link opens with no auth — *done 2026-07-28: HTML-encoded snapshot published to the public container (blob-public URL instead of a 90-day SAS — user-delegation SAS caps at 7 days, so the container grants blob-level public read)* |
| 9.4 | Blog metadata builder: head tags + JSON-LD (Article/FAQ/Video) with copy tabs | S | validates in Google Rich Results test |
| 9.5 ✅ | **Keyword plan** — AI SEO brief from the transcript (summary + focus keywords, optional user "focus" steer) → DataForSEO metrics + related suggestions → opportunity-ranked plan artifact; plus **3 SEO-friendly YouTube titles for A/B testing** (≤100 chars, validator-enforced) | M | plan artifact persists with ranked keywords + 3 titles — *done 2026-07-28: `POST /seo/keyword-plan`; `seo-brief` joined the generation fan-out; DataForSEO leg live-verified, AI leg fake-verified (live once a chat deployment exists on the Foundry resource)* |

### E10 — Web parity & launch (M)
| # | Story | Size | Acceptance |
|---|---|---|---|
| 10.1 | SWA config: routing fallback, CSP, 401→login redirect; API CORS lockdown | S | prod origins only; CSP has no `unsafe-*` except wasm-eval |
| 10.2 | Feature flags for desktop-only capabilities (local whisper, local clip export) with graceful web fallbacks | S | web never shows a dead button |
| 10.3 | Playwright e2e: sign-in, ingest→generate→review→schedule happy path on WASM | M | runs in CI against a seeded environment |
| 10.4 | Desktop packaging: MAUI Win (MSIX) + macOS (pkg/notarized) | M | installable builds from CI |
| 10.5 | castmill.ai landing page + docs (guided Foundry setup, user guide) | M | a new user reaches first campaign in <15 min |
| 10.6 | **Responsive + theme matrix in CI**: Playwright screenshots of every screen at 1024/1280/1440/1920/2560 across both families × light/dark | S | no page-level horizontal scroll, no clipped chrome; diffs reviewed as artifacts |

### E11 — Image Studio, client (L) — *new, from the design handoff*
| # | Story | Size | Acceptance |
|---|---|---|---|
| 11.1 | **Image plan column**: typed slots with kind, dimensions, EMPTY/DONE badges, bound to the same `Preview` payload as the header counter | S | every empty-slot surface agrees without a refetch |
| 11.2 | **Studio pane**: model radio group gated on `/ai/status` readiness, provenance-labelled prompt seeded per slot, constraint chips, variant count 2/4/6, live cost estimate | M | an unconfigured provider is disabled with a reason; chips append without clobbering user text |
| 11.3 | **Reference-frame picker**: choose a timestamp from the source, extract via `/media/frames`, use as the edit reference | M | image-to-image edit visibly preserves the reference framing |
| 11.4 | **Thumbnail headline + safe area**: ≤32-char field, safe-area toggle, preview matching the server compositor's geometry exactly | M | client preview and server output are pixel-comparable; headline edit re-composites without re-generating |
| 11.5 | **Generate → variants → place**: skeleton tiles, `v1 · <model>` tiles, click to place → slot DONE, WebP published, manuscript stub replaced in place | M | Focus Mode shows the figure and the header reads *n+1*/6 with no reload |

### Sequencing

```
E1 ──► E2 ──► E5 ──► E6 ──► E7 ──► E8/E9 ──► E10
  └──► E3 ──► E4 ──► E11 (canvas needs design system; provenance needs E6 citations;
                          Image Studio needs E6.10–6.13 + E4.5's manuscript stubs)
```
Critical path: **E5 (editor) → E6 (AI pipeline)**. Desktop shell reaches usable alpha after E6; web follows from the same RCL with E7.4/7.6 + E10. E3.5/E3.6 (theme switcher + responsive system) are prerequisites for every E4/E11 surface — do them inside E3, not as a retrofit.

---

## Part 5 — Risks & verification

| Risk | Mitigation |
|---|---|
| Foundry model catalog drift (ids appear/retire) | `Microsoft.Extensions.AI` abstraction + model-alias remap table; `/ai/status` probe validates deployments before use |
| BYO-credential setup friction | guided setup with screenshots + probe endpoint with specific, actionable errors; consider an app-managed tier later |
| Editor round-trip fidelity | markdown-only contract; CI-gated vitest corpus (FAQ prose, numbered lists, tasks) — the corpus grows with every bug found |
| Canvas performance on WASM | card virtualization, CSS-transform pan/zoom (no re-layout), AOT publish, measure with 100-card seed campaign |
| Long-media limits on cloud transcription | Azure AI Speech fast-transcription path for >25 MB; desktop local Whisper as escape hatch |
| Two shells drift apart | hard rule: no UI outside the RCL; e2e suite runs against WASM; desktop smoke checklist per release |
| `.ai` domain availability is point-in-time | register castmill.ai immediately (Session 0) |
| Two theme families double the styling surface | one **semantic** token layer; feature CSS may never name a family token; the style-guide route and the CI screenshot matrix (E10.6) cover all four combinations, so drift is caught by diff rather than by eye |
| Fixed-canvas design values leak into code as magic pixels | ADR-F10: handoff pixel values are treated as ratios; the layout system owns breakpoints and max-widths, and only the provenance overlay is allowed to measure pixels at runtime |
| Non-Foundry image provider breaks the "100 % Foundry" story | ADR-015 fences it: image-only, feature-flagged off, same encrypted credential custody, readiness surfaced in `/ai/status`; every text generator stays on Foundry |
| Overlay compositor font licensing / Linux font availability | embed a licence-clean condensed face as a resource in the API — never rely on a system font on App Service; the client preview uses the same face via a web font |
| Slot dimensions don't match model output sizes | models emit 1536×1024 / 1024×1536 / 1024×1024 only; E6.11 adds a documented, unit-tested resize + centre-crop per slot kind — never a CSS-stretched "close enough" |
| Provenance threads mis-draw on a fluid layout | overlay re-measures on `ResizeObserver`, scroll, zoom and theme change; a Playwright case asserts thread endpoints after a resize |

**Test strategy per layer:** vitest (editor bundle) · bUnit (components) · integration tests with Testcontainers SQL (API, tenancy, ETag) · Playwright (WASM e2e) · manual desktop smoke: sign-in → ingest local video → local + cloud transcribe → generate campaign → edit in Focus Mode → export .md/.docx → clip export → schedule on The Wire.

---

## Part 6 — Claude Code build playbook

Each session below is one focused Claude Code run. Paste the prompt, review the diff, keep the acceptance check green before moving on.

**Session 0 — Workspace (½ day).** Register `castmill.ai`. `git init` the repo. Author `CLAUDE.md`: stack conventions (Minimal APIs, RCL-only UI rule, token sheet, test commands), Ignite UI for Blazor usage notes. Add the **Microsoft MSSQL MCP server** to Claude Code (`claude mcp add` → Azure SQL; SQL auth or your Azure sign-in — this is dev tooling for the database, unrelated to app identity) for schema/data inspection in later sessions.
> *Prompt:* "Initialize the Castmill repo per Roadmap-Blazor.md §2.1: create the solution skeleton, CLAUDE.md with our conventions, .gitignore, and an Actions workflow that builds and runs all test projects."

**Session 1 — Scaffold (E1.1, 1 day).**
> *Prompt:* "Create the projects in §2.1 with `dotnet new`, wire project references, add Ignite UI for Blazor to Castmill.UI, add the editor-interop npm project with an esbuild config emitting to the RCL's wwwroot. Everything must build."
> *Done when:* CI green; blank Blazor page renders one Ignite UI component in both shells.

**Session 1b — Ignite UI AI tooling (when installing Ignite UI, ½ day).** Install Infragistics' agent skills + MCP servers so later UI sessions are component-aware:
> - Agent Skills (4 skills: components, grids, theming, generate-from-image): `gh skill install IgniteUI/igniteui-blazor` (installs to `.claude/skills/`), or the all-in-one `npx igniteui-cli ai-config --assistants generic --agents claude`.
> - Theming MCP server (design tokens, palettes, WCAG AA contrast validation — feeds E3): `claude mcp add igniteui-theming -- npx -y igniteui-theming igniteui-theming-mcp` (Node 18+, stdio).
> - Optional: Ignite UI CLI MCP (`igniteui-cli` scaffolding/docs tools).
> - Open-source components: 50+ Ignite UI Blazor components are MIT (Dialog, Tabs, Inputs, Tree, Combo, Stepper, Toast, Card, …). The free grid is **Grid Lite** — `dotnet add package IgniteUI.Blazor.GridLite` (MIT, .NET 8/9/10; upgrade path to the premium full Data Grid is API-compatible by design). Premium-only: full Data/Tree/Pivot/Hierarchical grids, charts (needed for `IgbCategoryChart` in E9), maps/gauges, Dock Manager, Excel.
> Docs: infragistics.com/products/ignite-ui-blazor/blazor/components/ai/ (skills, theming-mcp, ai-assisted-development-overview).

**Session 2 — Azure + CI/CD (E1.2–1.3, 1–2 days).**
> *Prompt:* "Write Bicep under /infra for App Service (Linux), Azure SQL, Storage, SWA, App Insights per §2.3. Add deploy workflows."
> *Done when:* `/health` live on App Service; WASM shell served from SWA.

**Session 3 — Auth & secrets (E2, 2–3 days).**
> *Prompt:* "Implement ASP.NET Core Identity per §2.3: the /auth group (register, login, refresh, logout, change-password) with JWT issuance + rotating refresh tokens, bearer validation + tenant guard + /me on the API, IAuthTokenProvider for both shells per §2.2 (SecureStorage on desktop, browser storage on web), the encrypted UserSetting store, and the BYO Foundry credentials screen with the /ai/status probe."
> *Done when:* both shells sign in; probe lists Foundry deployments.

**Session 4 — Design system (E3, 3–4 days).**
> *Prompt:* "Implement the §1.2 semantic token layer plus both theme families (Warm Editorial, Industry Blueprint) as CSS custom properties with light/dark, the ThemeService switcher, the responsive layout system (breakpoints, rail collapse, density toggle) per E3.6, Ignite UI theme mapping per family, the style-guide route with family × mode × breakpoint switchers, INotifier, confirm dialogs, empty states."
> *Done when:* style guide reviewed; AA contrast verified in all four combinations; no page-level horizontal scroll from 1024 px to 2560 px.

**Session 5 — Editor (E5, 1 week).**
> *Prompt:* "Build castmill-editor.js per §2.5 (TipTap core, markdown bridge with the ordered-list tokenizer disabled, slash menu, bubble menu), RichEditor.razor with blur-commit, the outline rail, insert dialogs, Markdig preview surfaces, and the vitest round-trip suite as a CI gate."
> *Done when:* round-trip corpus byte-stable; editor identical in both shells.

**Sessions 6–7 — AI pipeline (E6, 2–3 weeks).**
> *Prompt (6):* "Implement the Foundry client layer (§3) on Microsoft.Extensions.AI with per-user credentials and the model-alias table; then the timed-transcript ingest and the blog generator (outline→draft→audit) with citations and image stubs."
> *Prompt (7):* "Add the remaining generators (§3.3 fan-out), deterministic validators, the review gate, and the prompt log."
> *Done when:* a seeded transcript fans out into a full campaign with citations on every artifact.

**Sessions 8–9 — The Mill UX (E4, 2–3 weeks).** Work against the [Mill Floor handoff](docs/design_handoff_castmill_mill_floor/README.md) — recreate the designs as Razor components; do not port the prototype's imperative DOM writes (they're a prototype shortcut for what is ordinary data binding here).
> *Prompt (8):* "Build the app shell and navigation model per E4.8/4.9 (rail scopes, campaign header + view tabs), the Front Page including the image-slots block, the ⌘K omnibox, the 3-step new-campaign flow, and the Mill Floor canvas (Source Master + virtualized swimlanes, zoom, status bar + glow ring) per §1.3 and the handoff."
> *Prompt (9):* "Add provenance threads with hover/pin behaviour, Focus Mode with the Producer rail and the server-revision version filmstrip, The Wire dock reading the schedule mirror, and the Press Run animation driven by /ai/runs/{id}."
> *Done when:* 4.3's 60 fps target met; end-to-end review flow works keyboard-only; switching campaign re-renders header and content.

**Session 9b — Image Studio (E11 + backend E6.10–6.13, 1–1.5 weeks).**
> *Prompt:* "Implement typed image slots server-side (ImageSlot entity, /image-slots endpoints, Preview counts), slot-accurate resize/crop, the headline overlay compositor, reference-frame extraction through the ACA job, and the Image Studio view per §1.3.7 — slot column, model gating on /ai/status, provenance-labelled prompts with chips, variants, reference frame, headline + safe-area preview, place-into-slot with stub replacement."
> *Done when:* an empty campaign reserves 6 slots, a composited 1280×720 thumbnail lands in the manuscript, and every image counter agrees without a reload.

**Session 10 — Media (E7, 1–2 weeks).**
> *Prompt:* "Implement IMediaPipeline for both shells: desktop ffmpeg sidecar (pinned-hash fetch script), Whisper.net local transcription with model manager, server extraction/transcription endpoints, resumable web upload, and both clip-export paths."
> *Done when:* the desktop smoke checklist (Part 5) passes; web user completes ingest→transcribe.

**Session 11 — Publishing + reports (E8–E9, 1 week).**
> *Prompt:* "Integrate the publishing broker (channels, schedule, cancel), the composer with char meters, the Wire queue states, the SEO analysis endpoint + report UI on IgbCategoryChart, and the public share link."
> *Done when:* drag-to-schedule ships a real post to a sandbox channel.

**Session 12 — Launch (E10, 1 week).**
> *Prompt:* "SWA CSP/routing lockdown, desktop-only feature flags with web fallbacks, the Playwright e2e happy path in CI, MAUI packaging for Win/macOS, and the castmill.ai landing page + setup docs."
> *Done when:* a fresh user on a fresh machine reaches a scheduled campaign in under 15 minutes.

---

*Document version 1.1 — July 28, 2026 (v1.1 merges the Mill Floor design handoff: two theme families + switcher, fluid responsive layout, the navigation model, the Image Studio, and the schedule/revision/slot server additions). Re-verify the Foundry model catalog (§3.2) and `castmill.ai` availability before Session 0.*
