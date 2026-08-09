# PostFoundry — Architecture, Features & UX Specification

*Living document · August 2026 · describes the system as built*

Companion documents: [user-guide.md](./user-guide.md) (task-oriented, for end users),
[roadmap-blog-hierarchy-and-seo.md](./roadmap-blog-hierarchy-and-seo.md),
[analysis/feature-roadmap-2026.md](./analysis/feature-roadmap-2026.md).
`Client/PostFoundry-Spec.md` is the **original 2025 build brief** and is now historical —
where it disagrees with this document, this document is correct.

---

## Table of contents

1. [What PostFoundry is](#1-what-postfoundry-is)
2. [Core concepts](#2-core-concepts)
3. [Architecture](#3-architecture)
4. [Data model](#4-data-model)
5. [Security, sharing & tenancy](#5-security-sharing--tenancy)
6. [UX flow — the shell](#6-ux-flow--the-shell)
7. [Primary screens, feature by feature](#7-primary-screens-feature-by-feature)
8. [The generation pipeline](#8-the-generation-pipeline)
9. [The SEO / AEO subsystem](#9-the-seo--aeo-subsystem)
10. [Performance & reliability patterns](#10-performance--reliability-patterns)
11. [Known gaps and observations](#11-known-gaps-and-observations)
12. [Repository map](#12-repository-map)

---

## 1. What PostFoundry is

One recording in, a whole campaign out.

Give PostFoundry a webinar, product demo, video, audio file, or a pasted transcript. It
produces a campaign brief, YouTube titles + description + thumbnail, blog posts, per-platform
social copy, on-brand generated images, distribution assets (email sequence, newsletter,
landing-page copy, podcast show notes), captioned vertical video clips, and a live SEO/AEO
report grounded in real search data.

Everything it produces is an editable, versioned **artifact** that a human reviews and
approves. **Nothing publishes itself.**

---

## 2. Core concepts

Three ideas explain nearly every design decision in the app.

### Projects

One source recording and everything cast from it. Private by default; shared deliberately.
A project carries a `sourceType`, a `status` (`draft` → `ready`), an optional linked Brand
Product, and a collection of artifacts.

### Artifacts

Every output is an artifact: typed, versioned, status-bearing (`draft` / `needs_review` /
`approved`), stored as JSON. They form a **parent/child hierarchy** — a blog post is a
container that owns its own social posts and images.

The 21 artifact types (`Client/src/types/artifact.ts`):

| Group | Types |
|---|---|
| Source | `transcript`, `transcript_timed`, `project_brief` |
| Strategy | `summary`, `seo_report` |
| YouTube | `youtube_titles`, `youtube_description`, `thumbnail_concepts`, `thumbnail_images` |
| Blog | `blog_post`, `blog_header_images` |
| Visual | `images` |
| Social | `social_posts`, `publish_queue` |
| Video | `video_clips` |
| Distribution | `email_sequence`, `newsletter_email`, `landing_page_copy`, `podcast_show_notes` |

### Grounding

Nothing is generated cold. Before any prompt runs, the app assembles:

- the **transcript** (the source of truth for every factual claim),
- the **Brand Product AI Context** (positioning, ICP, competitors, differentiators),
- real **DataForSEO** keyword and SERP data,
- for images, your **actual product screenshots**,
- and three layers of **steering** (house template < project brief < inline directive).

---

## 3. Architecture

### 3.1 Topology

```text
┌──────────────────────── Desktop client (Tauri 2) ───────────────────────────┐
│  React 18 · TypeScript · Vite · Tailwind                                    │
│  zustand (UI + session state)   ·   TanStack Query (server cache)           │
│  3-pane shell: Sidebar │ Project tree │ Content pane                        │
│                                                                             │
│  Rust core — Client/src-tauri/src (~5,430 LOC)                              │
│   commands.rs      all LLM prompt contracts + provider HTTP                 │
│   distribution.rs  email / newsletter / landing page / show notes / voice   │
│   audio.rs         ffmpeg sidecar: extract, compress to the 25 MB cap       │
│   whisper.rs       local whisper.cpp sidecar + model download               │
│   msauth.rs        Entra ID PKCE via loopback redirect                      │
│   azure_blob.rs    direct blob PUT/DELETE with a server-minted SAS          │
│   clip_export.rs   ffmpeg cut · burn captions · reframe 9:16                │
└──────────────────────────────┬──────────────────────────────────────────────┘
                               │  HTTPS + Entra ID bearer token
                               │  (api://{clientId}/access_as_user)
┌──────────────────────────────▼──────────────────────────────────────────────┐
│  PostFoundryServer — ASP.NET Core 9 minimal APIs (Azure App Service)        │
│   System of record ......... EF Core → Azure SQL                            │
│   Vendor facade ............ DataForSEO · Buffer GraphQL · Azure Blob SAS   │
│   Cross-cutting ............ encrypted secrets · audit log · tenant guard   │
│                              rate limits · ProblemDetails · correlation IDs │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 The load-bearing decision: LLM calls run on the desktop

The server **never proxies model calls**. `Endpoints/AiEndpoints.cs` is a single
`GET /status` probe answering "does this user have a key configured?" — nothing more. Its
own comment states the rationale: a server-side LLM proxy used to exist, but client
orchestration is canonical and two parallel pipelines is a maintenance trap.

Consequences worth internalizing:

- **`commands.rs` is where the product lives.** Every prompt contract is a Rust `format!`
  template with explicit rules, banned-word lists, and JSON output schemas.
- The server stores API keys encrypted and returns them to the desktop at generation time.
- The browser preview can read and edit everything but cannot generate — hence the
  recurring UI string *"Run the desktop app to generate…"*.
- Rate limiting on `/api/v1/ai` protects latency and vendor quota, not spend (spend is on
  the user's own key).

### 3.3 Client stack

| Concern | Choice |
|---|---|
| UI | React 18 + TypeScript, Tailwind 3, Radix primitives, lucide icons |
| Server cache | TanStack Query — projects, artifacts, Buffer channels/posts, assets |
| Local state | zustand — `projectStore`, `settingsStore`, `uiStore`, `assistantStore` |
| Charts | ApexCharts (`react-apexcharts`) |
| Editors | TipTap rich editor (slash commands, image + YouTube insert, outline rail) and `@uiw/react-md-editor` for markdown |
| Validation | zod schemas in `types/schemas` |
| Notifications | sonner toasts |
| Tests | vitest (unit), Playwright (e2e smoke) |
| Desktop | Tauri 2 with dialog / opener / shell plugins; ffmpeg + whisper-cli fetched as sidecars via `npm run fetch:sidecars` |

### 3.4 Server stack

| Concern | Implementation |
|---|---|
| Framework | ASP.NET Core 9, minimal APIs, endpoint groups under `/api/v1` |
| Auth | JWT bearer against Entra ID v2.0. Accepts both v1 (`sts.windows.net`) and v2 issuers and both `aud` forms (`{clientId}` and `api://{clientId}`) so an app-manifest setting can't lock everyone out. Requires `access_as_user` in `scp`, then a `TenantAllowed` authorization requirement |
| Persistence | EF Core → Azure SQL |
| Secrets | `SettingsCipher` (AES). Production **refuses to start** without `SettingsEncryption:Key` |
| CORS | Explicit origin list, credentialed. Production **refuses to start** on an empty list or on any `localhost` / `127.0.0.1` origin. `tauri://localhost` is retained — it is the packaged app's hardened origin, not a webview-reachable HTTP origin |
| Rate limits | `ai` 30/min · `writes` 60/min · `searches` 60/min, partitioned per user `sub`; anonymous callers share one bucket so an unauthenticated flood can't exhaust the limiter before authz runs |
| Errors | Global exception handler → RFC 7807 `ProblemDetails`; stack traces never leave the server |
| Tracing | `X-Correlation-Id` per request, exposed via CORS and surfaced in client toasts for bug reports |
| Proxy | `UseForwardedHeaders` runs **first** so HSTS, HTTPS redirection, audit-log IPs, and absolute-URL construction see the real scheme and client IP behind App Service |
| Migrations | `EnsureCreated()` plus a list of idempotent best-effort SQL steps guarded by `information_schema` pre-checks. Production wraps them in one transaction and fails startup on any error; Development is tolerant per-step so a weird local DB doesn't block iteration |
| API docs | OpenAPI + Swagger UI, **Development only** |

### 3.5 Endpoint surface

```text
GET  /health

/api/v1/me                                    GET
/api/v1/settings                              GET · GET/PUT/DELETE {key}
                                              PUT {key}/visibility
                                              GET/POST {key}/shares · DELETE {key}/shares/{userId}
/api/v1/users/search                          GET      (min 2 chars, Take(20), rate-limited)
/api/v1/users/{userId}/avatar                 GET      (Graph photo proxy)
/api/v1/projects                              GET · POST · GET/PUT/DELETE {id}
                                              PUT {id}/visibility
/api/v1/projects/{id}/members                 GET · POST · PUT/DELETE {userId}
/api/v1/projects/{id}/artifacts               GET (?light=true) · POST
                                              GET/PUT/DELETE {artifactId}
/api/v1/assets                                GET · POST · PUT/DELETE {id}
                                              PUT {id}/visibility · GET/POST {id}/shares
/api/v1/brand-products                        GET · POST · PUT/DELETE {id}
/api/v1/azure-blob/sas                        POST     (mint a scoped, short-lived SAS)
/api/v1/azure-blob/test · /list               POST · GET
/api/v1/buffer/test · /channels · /posts      POST · GET · GET/POST · DELETE {postId}
/api/v1/seo/test · /analyze · /report         POST
/api/v1/seo/report/share                      POST · DELETE
/api/v1/ai/status                             GET
```

---

## 4. Data model

```text
Tenant ──< User ──< Project ──< Artifact (self-referencing via ParentId)
                 │           └─< ProjectMember
                 ├─< Asset ──< AssetShare
                 ├─< BrandProduct
                 ├─< UserSetting ──< UserSettingShare
                 └─< AuditEvent
```

Fields that carry design weight:

| Entity.Field | Why it matters |
|---|---|
| `Artifact.ParentId` | The hierarchy. A `blog_post` is a container; its `social_posts` and `images` reference it. A `null` parent means project-scope ("loose") |
| `Artifact.PreviewJson` | Precomputed lightweight projection. `GET /artifacts?light=true` selects this instead of `ContentJson` for image types, so opening a project never pulls base64 payloads out of SQL |
| `Artifact.Version` | Optimistic concurrency. Surfaces to the user as *"The project was modified by someone else."* |
| `Artifact.Status` | `draft` / `needs_review` / `approved`; drives the green check in the tree and the approved count on Overview |
| `Project.IsSharedWithTenant` | Org-wide read access — the Private / Public-to-org badge |
| `BrandProduct.AiContext` | Injected into **every text generator** |
| `BrandProduct.VoiceExamplesJson` / `VoiceStyleCard` | Exemplar texts and the distilled style card produced by `distill_brand_voice` |
| `Asset.Category` | `face` / `background` / `logo` / `product` / `other` — drives which generator can use the asset |
| `UserSetting.ValueJson` | Encrypted at rest; secrets round-trip to the client as `hasX: boolean`, never as values |
| `AuditEvent` | Every share, visibility change, create, and update |

---

## 5. Security, sharing & tenancy

**Private by default.** Three sharing axes:

1. **Project → org** — everyone in your Microsoft tenant gets read access. Corporate
   domains only (`EmailDomainClassifier` rejects consumer domains).
2. **Project → named user** — type-ahead search across everyone who has signed into
   PostFoundry, and it works cross-tenant. Invitees get read access; removable any time.
3. **Settings → org or user** — including spend-capable secrets. The dialog enumerates
   exactly which secrets become visible before you confirm.

Brand Products are org-shared by design: one person maintains them, every campaign uses them.

**Secret handling is uniform.** The DataForSEO basic-auth credential and the Azure Blob
account key never leave the server. The client receives only short-lived, operation-scoped
SAS URLs. Provider API keys are stored encrypted and returned to the desktop only at
generation time; they are never displayed after saving.

**Defense-in-depth details worth preserving:**

- `appsettings.Local.json` is never loaded in Production — it sits *after* environment
  variables in the config chain, so a stray copy next to the deployed binary would silently
  override real App Service settings.
- The SEO share-revocation endpoint is path-guarded to `seo-reports/` with no `..`, so it
  can never delete arbitrary blobs in a user's container.
- Write permission on a project is checked **before** any DataForSEO credit is spent.
- The tenant directory has a global EF query filter; every legitimate cross-tenant path
  (share-by-email lookups) calls `IgnoreQueryFilters()` explicitly.

---

## 6. UX flow — the shell

### 6.1 Layout

```text
┌───────────────────────────────────────────────────────────────────────┐
│ TopBar  ● PostFoundry                                 [M365 avatar ▾] │
├──────────┬──────────────────┬─────────────────────────────────────────┤
│ Pane 1   │ Pane 2           │ Pane 3                                  │
│ Sidebar  │ Project tree     │ ┌─────────────────────────────────────┐ │
│ (w-56)   │ (w-64)           │ │ ContentHeaderBar                    │ │
│          │                  │ │ ⇤ ⛶ │ Project › Section › Leaf   📜 │ │
│ New Proj │ Overview         │ ├─────────────────────────────────────┤ │
│ Home     │ Summary          │ │                                     │ │
│ Assets   │ AEO/SEO Report   │ │   The work surface                  │ │
│ Queue    │ ▸ YouTube        │ │                                     │ │
│ ───────  │ ▸ Blogs          │ │                                     │ │
│ Projects │ ▸ Distribution   │ │                                     │ │
│ ───────  │                  │ │                                     │ │
│ ☾ ⚙  ⇤   │                  │ └─────────────────────────────────────┘ │
└──────────┴──────────────────┴─────────────────────────────────────────┘
```

Pane 2 exists **only inside a project workspace**. On Home, Asset Library, Publish Queue,
and Processing the shell is a clean two-pane layout.

### 6.2 Chrome elements

**TopBar** — deliberately minimal: brand mark left, M365 avatar right (Graph photo with
initials fallback) with a sign-out dropdown. Project name and status live in the
breadcrumb; model/runtime status lives in Settings. Repeating them here was noise.

**ContentHeaderBar** — Evernote-style strip at the top of pane 3:

- collapse/expand the project tree,
- toggle focus mode (both nav panes hidden),
- a clickable breadcrumb (`Project › Blogs › {blog title} › Blog Content`) — the only
  "where am I" signal left in focus mode; long titles cap at 16rem with ellipsis,
- **prompt transparency** (📜): the exact prompt behind every generation this session.

**Keyboard shortcuts** (ignored while typing in an input or contenteditable):

| Shortcut | Action |
|---|---|
| `⌘\` | Toggle sidebar (pane 1) |
| `⌘⇧\` | Toggle project tree (pane 2) |
| `⌘⇧F` | Focus mode — hide both nav panes |

`⌘B` is deliberately *not* used: it's bold in the markdown editor.

When the sidebar is collapsed, a small reopen handle appears at the bottom-left edge.

### 6.3 Project tree behavior

- **Availability dots and approval checks.** Each leaf renders whether content exists and
  whether the artifact is `approved`.
- **Counts as badges.** Social platform leaves show post counts; a blog's Images leaf shows
  its *suggested-but-not-generated* backlog count.
- **Gating.** YouTube, Blogs, and Distribution are disabled until a campaign is generated
  (`summary` or `youtube_titles` exists), with the tooltip *"Generate a campaign from
  Overview first."* The AEO/SEO Report is deliberately **never** disabled — it carries its
  own gating and is usable pre-campaign.
- **Blog accordion.** Opening one blog's subtree collapses the others. Expansion state is
  persisted per project.
- **Auto-reveal.** A programmatic selection (e.g. a freshly generated blog) expands every
  ancestor group so the selection is visible.

### 6.4 The canonical happy path

```text
New Project ─ Step 1 Source ─→ ffmpeg extract ─→ transcribe ─→ suggest metadata
                                      │
                                      ▼
                            Step 2 Review (name · keywords · brief)
                                      │
                                      ▼
                            Project Overview → complete the Brief
                                      │
                            [Generate Campaign]
                                      │
        ┌──────────────┬──────────────┼──────────────┬────────────────┐
        ▼              ▼              ▼              ▼                ▼
     Summary       YouTube         Social       Thumbnail       Placeholder
   + angles     titles/desc      per platform    concepts          blog
        │
        ▼
  AEO/SEO Report ── Run analysis ──→ angles regenerate from real data
        │
        ▼
   Pick an angle → Blog (single or two-pass) → Images → Social → Buffer
                                                          Distribution assets
                                                          Video clips
```

---

## 7. Primary screens, feature by feature

### 7.1 Home

The landing surface when no project is open.

- **Hero + primary actions** — *New Content Project*, *Asset Library*, *Settings*.
- **Stats strip** — project count, brand-product count, reference-asset count.
- **The pipeline card** — an 11-step visual of what the app produces, color-coded by
  stage: transcript → summary/angles → AEO/SEO report → YouTube → thumbnails → blogs →
  social (6 platforms) → clips → email/newsletter/landing → show notes → Buffer.
- **Recent projects grid** — status badge, source type, last-updated date. Hovering or
  focusing a card **prefetches its artifacts**, so the click opens instantly.

### 7.2 Sidebar (pane 1)

`New Project` button · `Home` · `Asset Library` · `Publish Queue` · a refreshable project
list with per-project delete (guarded by a confirm dialog naming what gets destroyed) ·
footer with theme toggle, Settings, and the collapse handle.

Loading a project list shows skeleton rows rather than flashing the "No projects yet" empty
state. Errors render inline with a Retry link.

### 7.3 New Project dialog

**Step 1 — Source**

- Pick or drag media: `.mp4 .mov .m4v .mp3 .m4a .wav .webm`. Multiple files combine into
  one project; the transcript labels each source.
- Or paste a transcript and skip transcription entirely.
- **Process locally** toggle — on-device Whisper; nothing leaves the machine. Whisper model
  size (`base` … `large-v3`) is chosen here, and the model file downloads on first use with
  a progress bar.
- **Analyze** runs a visible stepper: audio extraction (ffmpeg) → transcription (cloud
  upload or local Whisper) → metadata suggestion.

**Step 2 — Review**

Suggested project name, keyword targets, and brief — all editable. *Create Project* lands
you on Overview with the transcript and brief saved, project status `draft`.

> Engines that expose timing (local Whisper always; `whisper-1` and
> `gpt-4o-transcribe-diarize` in the cloud) also write a `transcript_timed` artifact — the
> cut sheet that makes video clips possible. `gpt-4o-transcribe` and pasted transcripts do not.

### 7.4 Project Overview

The project's home node and the launcher for campaign generation.

**Header** — inline-editable project name; a meta line (source type, updated timestamp,
`N/M approved`); a **Private / Public to org** badge that opens the share dialog when
clicked; a status badge; and three actions:

| Action | Behavior |
|---|---|
| **Share** | Opens the share dialog (org toggle + invite by name/email) |
| **Export** | Zips the whole campaign — markdown files + generated images |
| **Generate Campaign** / **Re-generate Campaign** | The main event |

**Guidance line** — states the single blocking reason while generation is disabled: no
desktop runtime, no transcript, or no Brand Product choice. Otherwise it nudges you to
review the brief first, warning that a re-run replaces existing campaign assets.

**Two tabs:**

- **Brief** — Brand product (or explicit *None*), Audience, Content type (tutorial /
  product demo / webinar / thought leadership), Primary keyword cluster, Secondary
  keywords (comma-separated, rendered as badges), GitHub repo URL, Reference link,
  Brief/steering instructions, Additional context. Every field commits on blur with a
  *Saved* flash.
- **Transcript** — the transcript artifact with its status bar.

**Generation overlay** — a live four-phase checklist so the wait is legible:

1. Analyzing keywords with live SEO data — *volume · difficulty · SERP & AI-Overview presence*
2. Generating campaign content — *summary · YouTube titles & description · social posts · thumbnail concepts*
3. Assembling artifacts — *project brief · content angles · seeding the first blog draft*
4. Saving campaign to the server

A re-generate deliberately **carries forward** artifacts the campaign builder never
produces: `transcript_timed`, `video_clips`, and all four distribution artifacts.

### 7.5 Summary

- **Executive summary** — editable, commits on blur.
- **Key takeaways** — numbered list.
- **Content-angle pointer** — a card showing the angle count with an *Open AEO/SEO Report*
  button. Angles themselves live on the report page because they are derived from it; this
  keeps the summary → angle → blog workflow discoverable from here.
- **Keyword opportunities** — exploratory keywords surfaced from the transcript, badged,
  with a note that the real SEO scoring lives under YouTube and the report.

### 7.6 AEO/SEO Report

Covered in full in [§9](#9-the-seo--aeo-subsystem). As a screen:

**Analysis inputs panel** (editable in place — a second edit surface over the same
`project_brief` artifact): Audience · Primary keyword cluster · Secondary keywords · Site
domain, plus *Run analysis* / *Re-run analysis*, a last-run timestamp, and an
**"Inputs changed — re-run analysis"** badge when the report no longer matches the brief.

**Sections, in order:**

1. **AEO — AI answer visibility.** Four stat tiles (AI visibility %, AI Overview
   present/absent, People-Also-Ask count, engines queried), one card per engine
   (cites / does not cite / unavailable + citation count), and collapsible answers with
   citation chips — your own domain's chips highlighted.
2. **Target keywords.** Horizontal volume chart + a table of volume, difficulty meter, CPC,
   intent.
3. **Keyword ideas & gaps.** Related queries you do *not* target yet, each badged `gap`.
4. **Keywords you rank for.** Position, volume, difficulty, estimated traffic, ranking page.
5. **Who ranks around you.** Referring-domains comparison (you highlighted, competitors
   muted), a stacked #1 / #2–3 / #4–10 position footprint, and a comparison table with your
   row pinned at the top.
6. **SERP snapshot.** Top results with your rows highlighted, plus People Also Ask.
7. **Content angles** (derived section) — the angle list with a *Regenerate angles from
   this report* button and a **"New SEO data since these angles were generated"** badge.

**Share report** publishes a standalone dark-themed HTML page to your Azure storage behind
a long-lived read link (default 90 days); *Unpublish* deletes the blob, which is the real
revocation.

### 7.7 YouTube

One YouTube node per project — the video is the campaign driver. Sections:

| Section | Features |
|---|---|
| **Top 3 SEO/AEO titles** | Slots A / B / C, each showing its angle (seo / curiosity / how-to / problem-solution / thought-leadership) and the keywords used as badges. Inline edit (commits on blur), copy, and per-slot **Regenerate** |
| **YouTube description** | Large monospace editor, live character count, auto-save on blur. **Copy** · **Save** · **Regenerate** (single pass) · **Two-pass (deep)** — outline → draft → self-audit against platform rules (125-char hook, hashtag hoist, chapter keywords) |
| **Suggested pinned comment** | 1–3 sentences referencing a concrete moment and ending with a question. Copy / Save. Regenerated alongside the description |
| **YouTube thumbnail** | Face picker + Background picker (both optional, both with inline upload), AI-suggested **overlay text** (editable; auto-refreshes when you regenerate the description), a per-generation **steering** line, then **Generate** → auto-cropped to 1280×720 → **Download**. A shimmer overlay narrates *"Following YouTube best practices · cropping to 1280×720"* |
| **Real search data informing this content** | The DataForSEO snapshot this campaign was grounded in: keyword scoring badges (Vol / KD color-coded by difficulty / intent / CPC / competition), the SERP list, AI-Overview badge, featured snippet, and a collapsible People-Also-Ask list. When DataForSEO isn't configured, it explains that grounding is optional and everything else still works |

Sub-nodes: **Images** (project-level image cards), **Clips**, and **Social Media** (six
platforms).

> PostFoundry never connects to your YouTube channel. Titles, description, tags, pinned
> comment, and thumbnail are copy/paste + download.

### 7.8 Blogs

Projects hold many blogs; each blog is a container with `Blog Content`, `Images`, and
`Social Media` children. New blogs come from a content angle on the report page.

**Blog Content view:**

- **Generate / Regenerate** and **Two-pass (deep)**, both gated by a **steering dialog**
  that appends a one-off direction to the project brief and carries a *has video* toggle.
  Two-pass runs outline → draft → audit, with the audit on a separately configurable model.
- A campaign-seeded **placeholder** blog (titled from an angle, never generated) renders
  the Generate panel instead of an empty editor, and seeds its first generation with that
  angle.
- **Editor** — markdown with live preview, plus the TipTap rich editor with slash commands,
  an outline rail, and image/YouTube insert dialogs. Blog bodies may contain
  `> 🖼️ IMAGE [id]:` stubs that pair with the blog's image backlog.
- **Pre-publish checks** — structure, keyword placement, length, metadata
  (`services/blog/validateBlog.ts`).
- **Mark reviewed** — sets the artifact to `approved`.
- **Schema & metadata (AEO)** — Site URL, Blog URL (canonical = blog URL + `/{slug}`),
  Site name, Author, Organization logo URL, and an optional YouTube video URL that adds
  `VideoObject` schema. Output toggles between **Combined**, **HTML head only**, and
  **JSON-LD only**, with one-click copy. *(Note: these publishing-identity fields are
  edited here, not in Settings.)*

Blog generation targets 1,500–2,500 words, primary keyword in the H1, a TL;DR written for
AI Overviews, and an FAQ section aimed at People-Also-Ask.

### 7.9 Images

Each blog has an Images node; the project has a loose set under YouTube.

An image is a **card**: purpose (YouTube thumbnail / blog header / blog figure / custom),
prompt, size, reference assets, and the generated result.

- **Suggest images** (blog-scoped) analyzes the post and proposes a backlog.
- **Sync stubs from post** reconciles cards with the `> 🖼️ IMAGE` markers in the markdown.
- **Generate all (N)** loops through every pending card.
- **Auto vs manual prompt** — Auto rebuilds the prompt from transcript, brief, and *current*
  references on every generation, so removing a face really removes the face. Manual uses
  your text verbatim.
- **References** — Face and/or Background per card. Product screenshots from the linked
  Brand Product attach automatically (up to 3), with instructions to reproduce the real UI.
  Brand image guidelines are injected as authoritative color/composition rules.
- **Publish for web** — converts the generated PNG to WebP and uploads it to the public
  `-public` container, copying the URL. Available per card and as a publish-all loop with
  progress.
- Per card: Generate / Regenerate / Download.

### 7.10 Social Media

One node per platform (LinkedIn, X, Facebook, YouTube Community, Instagram, Bluesky), under
YouTube and under each blog.

- A context strip explains the scope: *"…posts promoting **this blog**"* vs *"Project-level
  posts. Regenerating rewrites all platforms using your Social posts model from Settings."*
- **Generate / Regenerate social posts** opens the steering dialog; regeneration rewrites
  every platform in that scope.
- Post cards show the text, hashtags, CTA, and a **character-limit badge** that turns amber
  past the platform limit, plus a `Queued` badge.
- Per post: **Copy** · **Add to Queue** (project-level only) · **Send to Buffer**.

### 7.11 Video Clips

Under YouTube. Requires a `transcript_timed` artifact; when absent, the view explains
exactly which transcription engines produce timestamps and what to do about it.

- **Discovery instruction** (optional) — e.g. *"find every App Builder demo moment"*.
- **Suggest clips / Re-suggest clips** — AI scouts candidates from the timed transcript,
  aligning boundaries to spoken segments. Re-running replaces un-reviewed suggestions and
  keeps approved/rejected ones.
- Each candidate carries a hook (the first spoken sentence), a 3–7 word overlay title, a
  rationale, a 0–100 score, platform fit, hashtags, and the excerpt.
- **Export options** — *Burn captions* and *Vertical 9:16 (1080×1920)*. Both off gives an
  instant lossless stream copy starting on the nearest keyframe; either on triggers a
  frame-accurate re-render.
- Approve → **Export** an MP4, or send it to Buffer (uploaded to Azure, then handed to
  Buffer as a readable link).

### 7.12 Distribution

Three nodes, each generating from the same brief, product voice, and SEO context:

- **Email** — a nurture/announcement **sequence** (each email with purpose, timing,
  subject, preheader, body, CTA + URL) and a one-off **newsletter digest**. Both have an
  optional steering line and per-email copy. Video links use a `[YOUTUBE_VIDEO_URL]`
  placeholder to replace in your ESP.
- **Landing Page** — headline, subhead, benefits, CTA blocks, FAQ.
- **Show Notes** — podcast episode title, summary, chapters, key quotes, and resource
  links. When the project has a timed transcript, chapter timestamps come from **real
  segment starts** rather than being inferred; the overlay says so while generating.

### 7.13 Publish Queue (sidebar)

A live view of your **actual Buffer account**, not a local mirror.

- Status tabs (drafts, queued, sent, …) with a refresh control.
- Post cards resolved against your channel list.
- **Cancel** a scheduled post (confirm-gated; it leaves Buffer's queue for good).
- **New Buffer post** opens the composer from scratch.

**Buffer composer:** pick one or more channels (loaded live), see per-channel truncation
warnings, optionally attach an image from the Asset Library (a 24-hour link is minted for
Buffer to fetch), choose *Next queue slot* / *Schedule for…* / *Send now*, and check the
per-channel live preview before submitting.

### 7.14 Asset Library (sidebar)

Two things live here.

**Brand Products** — a named product with:

- **Brand image guidelines** (markdown) — colors, composition rules, negative prompts.
  Injected into **image generation only**.
- **AI Context** (markdown) — positioning, ICP, personas, competitors, differentiators,
  campaign priorities. Injected into **every text generator**. *Generate from URL* drafts it
  from your product page.
- **Brand voice** — exemplar texts plus a **distilled style card** produced by
  `distill_brand_voice`.

Brand Products are org-shared automatically.

**Reference assets** — uploaded images categorized as Face, Background, Logo, **Product**,
or Other, each assignable to a Brand Product with a short description passed to the image
model as guidance.

> **The product-fidelity rule.** If a project's Brand Product has Product-category
> screenshots, up to 3 attach automatically to every image generation with instructions to
> reproduce the real interface. Upload real screenshots once; every thumbnail and blog
> header shows your actual product.

### 7.15 Settings dialog

Ten cards, each with its own Save; the dialog warns about unsaved changes on close.

| Card | Contents |
|---|---|
| **API Keys** | OpenAI and Anthropic keys. Write-only — the field shows configured / not configured |
| **Models** | Text provider + text model, transcription model, image model |
| **Per-generator model overrides** | `blogPostModel`, `blogAuditModel`, `youtubeDescriptionModel`, `youtubeAuditModel`, `socialPostsModel`. "(use default)" inherits the global text model; provider is inferred from the model name, so mixing OpenAI and Anthropic is fine |
| **YouTube Description Template** | House structure/tone/self-check rules, with reset |
| **Blog Post Template** | Same, for blogs |
| **Social Posts Template** | Same, for social |
| **SEO Analysis (DataForSEO)** | base64 `login:password`, location code (2840 = US), language code, **Test connection** |
| **Image Storage** | Azure Blob protocol, account name, key (server-side), endpoint suffix, container, **Test Connection** |
| **Buffer (social publishing)** | Personal Access Token, **Test Connection** |
| **Sharing** | Share these settings tenant-wide or with a specific teammate — the dialog lists exactly which secrets become visible first |

*Local Whisper model size is chosen in the New Project dialog; blog publishing identity is
edited in the blog's Schema & metadata panel.*

---

## 8. The generation pipeline

### 8.1 Campaign generation

`ProjectOverview.handleGenerate` →

1. `fetchProjectSeoInsights` — live DataForSEO snapshot for the brief's keywords, stored
   onto the brief as `seoInsights` (the distribution generators later read this snapshot
   rather than re-fetching).
2. `generate_campaign_json` (Rust) — one grounded call returning the whole campaign as JSON.
3. `buildCampaignFromInferredContent` — normalizes and defaults every field, mints artifact
   IDs, stamps `anglesGeneratedAt`, and seeds a placeholder blog from the strongest angle.
4. Persist project + artifacts.

Produced: executive summary + content angles + key takeaways + keyword opportunities;
5 scored YouTube titles; description with chapters, tags, thumbnail overlay, pinned comment;
5 thumbnail concepts; per-platform social posts; empty image sets; and the placeholder blog.

### 8.2 Prompt layer

Every prompt lives in Rust. Recurring contract elements:

- **Explicit grounding blocks** — `AI CONTEXT`, `SEO SNAPSHOT`, transcript, each with an
  explicit "not provided" fallback string so the template never emits a dangling section.
- **Strict JSON output shape** in the prompt, with `extract_json_value` on the way back
  handling the three real-world failure modes: markdown fences, leading prose, trailing
  commentary.
- **Banned hype words** — "seamless", "powerful", "revolutionary", "robust", "easily",
  "effortless", "next-level", "game-changing" — unless the transcript uses them first.
- **No-invention rules** — no URLs, repos, package names, API names, or keyboard shortcuts
  that aren't in the inputs; demoed behavior phrased as "in this demo".
- **Internal quality rubrics** the model applies silently before finalizing (SEO relevance,
  AEO usefulness, clarity, product accuracy, conversion).
- **No em dashes inside JSON string values.**

### 8.3 Two-pass generation

Blog and YouTube description support outline → draft → **self-audit**, where the audit step
resolves a separate model (`blogAuditModel` / `youtubeAuditModel`). The house recommendation
is to spend on the audit, not the draft: a premium model catches drift in a rewrite far more
cheaply than it writes the first draft.

### 8.4 Model defaults

| Job | Default | Rationale |
|---|---|---|
| Global text | `gpt-5.6` or `claude-sonnet-5` | Flagship quality at sane cost |
| Blog / YouTube audit pass | `claude-opus-4-8` | Premium catches rewrite drift; runs only on the audit |
| Social posts, angles, image suggestions | `claude-haiku-4-5` / cheap OpenAI default | Short structured output |
| Transcription | `gpt-4o-transcribe` | Accuracy/cost sweet spot; `-diarize` for speakers |
| Images | `gpt-image-2` | Needed for reference-image fidelity |

---

## 9. The SEO / AEO subsystem

### 9.1 Two tiers, deliberately separated

| | `POST /api/v1/seo/analyze` | `POST /api/v1/seo/report` |
|---|---|---|
| Purpose | Cheap grounding for prompts | The full dashboard artifact |
| Runs | Automatically before campaign / blog / social generation | Only on explicit **Run / Re-run analysis** |
| DataForSEO calls | 3–4 | up to ~20 |
| Output | `{ keywords, serp }` | Persisted `seo_report` artifact |

The report never runs on load. Project **write** permission is checked before any credit is
spent.

### 9.2 The nine DataForSEO endpoints

All via `Services/DataForSeoClient.cs`, base `https://api.dataforseo.com/v3`, HTTP Basic
with a base64 `login:password` the client never sees. 60-second timeout.

| # | Endpoint | Fields extracted | Used in |
|---|---|---|---|
| 1 | `keywords_data/google_ads/search_volume/live` | `search_volume`, `competition`, `competition_level`, `cpc` | analyze + report (also the connection test) |
| 2 | `dataforseo_labs/google/bulk_keyword_difficulty/live` | `keyword_difficulty` 0–100 | analyze + report |
| 3 | `dataforseo_labs/google/search_intent/live` | `keyword_intent.label` | analyze + report |
| 4 | `serp/google/organic/live/advanced` | top 15 organic rows, `ai_overview` presence, `featured_snippet`, `people_also_ask` | analyze + report |
| 5 | `dataforseo_labs/google/keyword_suggestions/live` | keyword + volume / cpc / competition / KD / main intent | report |
| 6 | `dataforseo_labs/google/ranked_keywords/live` | keyword, volume, KD, `rank_absolute`, url, `etv` | report |
| 7 | `backlinks/summary/live` | rank (one-hundred scale), backlinks, referring domains, referring main domains, broken, spam score | report — your site **and each competitor** |
| 8 | `dataforseo_labs/google/domain_rank_overview/live` | organic `pos_1` / `pos_2_3` / `pos_4_10` / `count` / `etv`, paid count/etv | report — competitors |
| 9 | `ai_optimization/{provider}/llm_responses/live` | answer text sections + citation annotations | report — the AEO scorecard |

Request details that matter: SERP fetches `depth = 20` because non-organic rows count toward
depth and DataForSEO bills advanced SERP in depth-10 increments — 20 is 2 units and reliably
yields 15 organic results. Keyword ideas clamp to 25; ranked keywords to 50
(`item_types: ["organic"]`, ordered by `etv desc`). Backlink targets are normalized to a
bare domain (scheme, `www.`, and path stripped).

### 9.3 The AEO scorecard

| Provider | Model | `web_search` |
|---|---|---|
| `chat_gpt` | `gpt-4.1-mini` | `true` |
| `gemini` | `gemini-2.5-flash` | `true` |
| `claude` | `claude-sonnet-4-0` | `true` |
| `perplexity` | `sonar` | **omitted** — Perplexity always searches and rejects the flag |

All four receive the identical prompt (capped at 500 chars by the endpoint, 1024 output tokens):

> `For {audience}: what are the best resources and answers for: {primaryKeyword}?`
> — the audience clause is dropped when the brief has none.

Scoring: `domainCited` is true when any citation URL contains your site domain
(case-insensitive); `visibilityScore = citing / succeeded × 100`, rounded to one decimal. A
failed engine records `succeeded: false` and is **excluded from the denominator** rather
than counted as a miss. A legacy single-answer field (`ai`) is also emitted — first
successful result, preferring ChatGPT — so pre-expansion clients still render.

### 9.4 Orchestration and failure model

`SeoReportService.GenerateAsync` runs behind a `SemaphoreSlim(6)` so one report never opens
twenty sockets at once:

- **Phase 1** (inputs only, concurrent): keyword metrics, SERP, keyword ideas, ranked
  keywords, your backlinks, and the four AEO calls.
- **Phase 2** (needs the SERP): take up to `CompetitorLimit` distinct domains from the top
  results excluding your own, then fan out backlinks + rank-overview per competitor.
- **Gap computation:** keyword ideas minus the keywords you already target.

Failure semantics are precise:

- Keyword metrics **hard-fail** (a `DataForSeoApiException` becomes a 400) — a report with
  no keyword data isn't a report.
- Every other section **soft-fails to `null`**, logged as a warning. One vendor outage never
  sinks the report.
- `null` vs `[]` carries meaning: `competitors: null` = deep-dive never attempted (no SERP);
  `competitors: []` = attempted, none found. The UI reads that distinction.
- Within keyword metrics, the three lookups each soft-fail to an empty map, so a keyword can
  return volume without intent rather than not at all.

### 9.5 Persistence and staleness

The server both generates **and** persists, upserting the project's root `seo_report`
artifact (`ParentId == null`), bumping `Version`, and writing an audit event. Re-runs
explicitly carry the existing share link forward — a refresh must not make a published URL
vanish.

Three staleness signals, derived purely with no extra state:

| Signal | Derivation |
|---|---|
| `inputsStale` | Report `context` snapshot (audience, primary keyword, site domain, sorted secondary keywords) vs the live brief. `null` = legacy report with no snapshot → *"input tracking starts after the next run"* |
| `anglesStale` | `report.fetchedAt > summary.anglesGeneratedAt` — **not** `updatedAt`, so editing summary prose doesn't falsely clear the badge |
| `shareStale` | `share.publishedAt < report.fetchedAt` |

### 9.6 Sharing the report

`POST /seo/report/share` renders the artifact through an embedded, self-contained HTML
template (`Templates/SeoShareReport.html`, dark theme, its own ApexCharts rendering). The
server injects only title, date, and the report JSON — re-serialized through
`System.Text.Json` with the HTML-safe encoder, which is what makes embedding it inside an
inline `<script>` injection-safe regardless of the artifact's contents.

It uploads to `seo-reports/{guid}.html` in *your* container, mints a read SAS (default 90
days, clamped 1–365), **deletes the previous blob** so revoked links actually die, and
best-effort writes the share metadata back onto the artifact (a persistence hiccup must not
fail a request whose link already published). `DELETE` removes the blob — the real
revocation, since a minted SAS is useless without it — path-guarded to `seo-reports/`.

### 9.7 How the data becomes content options

Two formatters in `services/orchestration/seoContext.ts`, at two fidelities.

**Tier 1 — `formatSeoContextForPrompt`** (~1.5k tokens, from `/analyze`). Used by campaign,
blog, and social generation, and by the distribution generators via the brief's stored
snapshot. Emits keyword metrics and the SERP snapshot under the heading *"real search data —
use it; do not contradict it"*, top results labeled *"use these to find gaps; do NOT copy"*,
then five imperatives:

- Place the primary keyword in the first sentence of the YouTube description.
- Match the dominant search intent shown above.
- If AI Overview is present, write to be excerptable: short answer-style first paragraph,
  clear question→answer chunks for People Also Ask.
- Beat the top organic results on completeness, specificity, and links/timestamps.
- Include high-volume secondary keywords naturally; do not stuff.

**Tier 2 — `formatSeoReportForPrompt`** (~2k tokens, from the full report). Used **only** by
content-angle regeneration. It reshapes each dataset into a directive:

| Data | Framing given to the model |
|---|---|
| Keyword ideas | *"Keyword **GAPS** — related queries this content set does NOT target yet, prime angle targets"* |
| Ranked keywords | *"Keywords the domain **ALREADY** ranks for — don't duplicate, extend or defend"* |
| AEO scorecard | *"N of M AI engines cite the domain. NOT cited by: gemini, claude — prefer answer-shaped angles (direct Q&A, definitions, comparisons) designed to win citations there"* |
| SERP top results | *"find the gaps; do NOT propose angles they already cover better"* |
| People Also Ask | *"each is a candidate answer-shaped angle"* |
| Backlinks vs competitors | *"N referring domains vs best competitor M — where outmatched, prefer long-tail / lower-difficulty targets"* |

That last row is the most interesting product logic in the subsystem: link authority
quantitatively steers the model toward achievable keyword difficulty instead of aspirational
head terms.

### 9.8 The angle regeneration loop

*Regenerate angles from this report* → `useAngleRegeneration` → `regenerate_content_angles`.
The prompt asks for 4–6 angles as `{ angle, audienceNeed, suggestedAsset }` under these rules:

- Ground every angle in the SEO/AEO data; name the target keyword inside `suggestedAsset`.
- Where the scorecard shows engines not citing the domain, prefer answer-shaped angles.
- Exploit competitor weaknesses (thin answers, missing intents); don't propose what the top
  results already cover better.
- Stay honest to the transcript — no bait the source can't back up.
- No hype words; no em dashes inside JSON string values.

The result merges **only** `contentAngles` plus a fresh `anglesGeneratedAt` stamp;
executive summary, takeaways, and keyword opportunities are untouched. Each angle then seeds
a blog, whose prompt is prefixed with
`Base this post on the content angle "…" (audience need: …)`.

**Full loop:** DataForSEO → report artifact → formatted prompt block → angles → blog seed →
blog draft (grounded again by a fresh Tier-1 snapshot) → social + images under that blog.

---

## 10. Performance & reliability patterns

- **Lazy image content.** `?light=true` on project open swaps `ContentJson` for the
  precomputed `PreviewJson` on image artifacts; individual image artifacts load on demand
  when their view opens. Both endpoints emit `Server-Timing` (`auth`, `db`, `serialize`) —
  the perf work is instrumented, not guessed.
- **Prefetch on intent.** Hovering or focusing a project card in the sidebar or on Home
  prefetches its artifacts.
- **Optimistic concurrency.** Artifact `Version` + ETags; the client surfaces a plain-language
  conflict message.
- **Soft-fail sections.** The SEO report, the blog metadata panel, and reference-asset
  attachment all degrade rather than fail.
- **Bounded fan-out.** `SemaphoreSlim(6)` on the SEO report; sequential loops with visible
  progress for "generate all" / "publish all".
- **Legible waits.** Every long operation renders a `GenerationOverlay` with a phase
  checklist or a descriptive subtitle rather than a bare spinner.
- **Prompt transparency.** Every generation's exact prompt is retrievable from the header bar.

---

## 11. Known gaps and observations

Not defects to fix today, but facts worth carrying:

1. **AEO measures one prompt, once.** A single generic question against four engines, no
   prompt set, no repetition, no history — `visibilityScore` is a 4-sample point measurement
   with no trend. Storing report snapshots over time (rather than one root artifact per
   project) is the natural next step.
2. **`domainCited` is a substring match.** A short domain can match inside an unrelated URL.
   The same `Contains` check drives competitor exclusion, so a competitor whose domain
   contains yours is silently dropped from the deep-dive.
3. **The report page under-fills its own API.** `runAnalysis` sends keywords, primary
   keyword, domain, location, language, and audience — but not `brandName` or
   `contentSummary`, both of which the endpoint accepts and the share page uses. That's why
   shared reports title as *"SEO & AEO Report: {keyword}"* instead of leading with the brand.
   `competitorLimit` also defaults to 3 with no UI control (the API clamps 0–5).
4. **Tier-1 SEO instructions are YouTube-shaped.** `formatSeoContextForPrompt` says *"Place
   the primary keyword in the first sentence of the YouTube description"* even when
   grounding a blog or a LinkedIn post. It reads as harmless generic advice to the model,
   but it's the wrong instruction in three of its four call sites.
5. **Doc drift in `user-guide.md`.** It places content angles on the Summary screen (they
   moved to the AEO/SEO Report), and lists blog publishing identity and the Whisper model
   size under Settings (they live in the blog Schema panel and the New Project dialog
   respectively).
6. **Two editors coexist.** The markdown editor and the TipTap rich editor both operate on
   blog bodies. Intentional today; worth consolidating eventually.
7. **Web version deferred.** Generation requires the desktop runtime. The server is already
   cloud-hosted, so a web client is a client-side problem (ffmpeg.wasm, browser-side
   provider calls), not an architectural one.

---

## 12. Repository map

```text
PostFoundry/
├── Client/                          Tauri 2 desktop app
│   ├── src/
│   │   ├── components/
│   │   │   ├── layout/              AppShell · TopBar · Sidebar · ProjectTreePane
│   │   │   │                        ContentHeaderBar · WorkspaceContent · tree/
│   │   │   ├── project/             HomeScreen · NewProjectDialog · ProjectOverview
│   │   │   │                        AssetLibrary · SettingsDialog · AssetPicker
│   │   │   ├── artifacts/           One view per artifact family (YouTube, Blog,
│   │   │   │                        Images, Social, Clips, Email, Landing, Notes)
│   │   │   ├── seo/                 SeoReportPage + its seven sections
│   │   │   ├── editor/              TipTap rich editor, slash commands, insert dialogs
│   │   │   ├── buffer/              BufferComposer · PlatformPreview
│   │   │   ├── share/               ShareDialog · UserSearchCombobox
│   │   │   └── ui/                  Design-system primitives
│   │   ├── services/
│   │   │   ├── orchestration/       generateCampaign · seoContext · imagePrompt
│   │   │   │                        distributionInputs · exportCampaign · cropToYouTube
│   │   │   ├── storage/             Typed repositories over the server API
│   │   │   ├── tauri/               Rust command bindings
│   │   │   ├── blog/                metadata (JSON-LD/OG) · validateBlog · imageStubs
│   │   │   └── auth/                msAuth · userPhotos
│   │   ├── state/                   zustand stores + generation hooks
│   │   └── types/                   Artifact, navigation, settings, asset, schemas
│   ├── src-tauri/src/               Rust core (see §3.1)
│   ├── tests/ · e2e/                vitest · Playwright
│   └── PostFoundry-Spec.md          Historical 2025 build brief
├── Server/
│   ├── src/PostFoundryServer/
│   │   ├── Endpoints/               12 endpoint groups
│   │   ├── Services/                Vendor clients, secret accessors, cipher,
│   │   │                            audit, tenancy, SEO report + HTML renderer
│   │   ├── Entities/ · Data/        EF Core model + AppDbContext
│   │   └── Templates/               SeoShareReport.html (embedded resource)
│   ├── tests/ · tools/
│   └── IT-DEPLOYMENT.md
├── docs/
│   ├── architecture.md              ← this document
│   ├── user-guide.md
│   ├── roadmap-blog-hierarchy-and-seo.md
│   ├── analysis/                    competitive landscape · feature roadmap
│   └── deployment/
└── website/                         Marketing landing page
```
