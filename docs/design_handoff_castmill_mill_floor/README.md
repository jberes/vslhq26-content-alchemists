# Handoff: Castmill — The Mill Floor (campaign workspace)

## Overview
Castmill turns one source asset (video, podcast, webinar, transcript) into a full marketing campaign. This bundle covers the **campaign workspace** — the app's centerpiece — plus the flows around it: campaign creation and ingest, the fan-out "Press Run", the Mill Floor canvas with provenance threads, Focus Mode (manuscript + Producer rail), the **Image Studio**, SEO/AEO analysis, and The Wire scheduling strip.

Target stack (from the product roadmap, unchanged by this design):
- 100% Blazor UI on **Ignite UI for Blazor**, all screens in the `Castmill.UI` Razor Class Library
- Shells: .NET MAUI Blazor Hybrid (desktop) + Blazor WebAssembly (web)
- ASP.NET Core Minimal APIs, Azure SQL, Blob Storage; AI server-side on Azure AI Foundry

> **Implementation deltas (recorded 2026-07-28).** Two questions this handoff leaves open have been decided in the architecture docs, and one convention is deliberately not being implemented:
> - **Both token sheets ship**, not one — *Warm Editorial* (the roadmap brand sheet) and *Industry Blueprint* (the sheet used here), each light + dark, with a runtime switcher over a shared semantic token layer. See [Frontend-Architecture.md](../../Frontend-Architecture.md) ADR-F09 and [Roadmap-Blazor.md](../../Roadmap-Blazor.md) §1.2.
> - **Layout is fluid, not 1440 × 880.** The fixed canvas is treated as a drawing convention; pixel values below are read as ratios. Breakpoints, rail collapse and per-region scrolling are specified in ADR-F10 / roadmap E3.6.
> - Everything else here — navigation scopes, the ten screens, provenance behaviour, the no-spinner rule, the Image Studio, and all five scope additions at the bottom of this file — has been folded into the backlogs (frontend F1/F3/F5/F8/F9/F10, backend phase B9, roadmap E3.5–E3.6, E4.8–4.10, E6.10–6.14, E8.4, E11).

## About the design files
The files in this bundle are **design references written in HTML/CSS/JS** — prototypes of intended look and behavior. They are **not** production code and should not be ported line by line. The task is to **recreate these designs in `Castmill.UI` as Razor components**, using Ignite UI for Blazor where the usage map below says so, and custom Razor + CSS for the differentiated chrome (canvas, campaign header, Focus Mode, Wire, Press Run).

Where the prototype uses imperative DOM writes (it drives per-campaign content by setting `textContent` on hooks like `[data-campname]`), that is a prototype shortcut. In Blazor this is ordinary data binding: one `Campaign` view model rendered by each view.

## Fidelity
**Mid-to-high fidelity.** Layout, hierarchy, component structure, copy, interaction and state behavior are intentional and should be matched. Exact pixel values below are real and usable, but the color/type values come from the **Industry** design-system token sheet used for this exploration, *not* from Castmill's brand sheet in §1.2 of the roadmap (warm ivory `#F5F0E8` / terracotta `#C15F3C` / Source Serif 4). **Decide before building:** either implement the roadmap token sheet (recommended — the structure is unchanged) or keep the steel-blue sheet documented here. Map tokens onto Ignite UI theming either way (roadmap story E3.2).

## Screens / views

### 0. App shell
- Fixed **1440 px** wide design; left rail **216 px**, content pane flexes. Rail has a 1px right divider `rgba(29,31,32,.16)`.
- Rail is two groups, and this grouping matters: **WORKSPACE** (Front page · Campaigns *n* · The Wire — cross-campaign) and **IN THIS CAMPAIGN** (a card showing the active campaign, click to switch; the campaign switcher list; nothing else). The four campaign *views* are NOT rail items.
- Rail bottom: `⌘K` / `⌘G` / `⌘⇧I` hint block, then the primary **Start a run** button (creates a campaign).
- Rail item: 6px 8px padding, 13px text, 2px transparent left border; active = `rgba(89,128,166,.16)` background + accent left border; hover = `rgba(89,128,166,.10)`.
- Toast host: bottom-right of the content pane, dark panel (`#1d2d3d`, 12.5px text), auto-dismiss 2600 ms.

### 1. Front page (workspace)
Purpose: what needs a human today. Two columns, `1.35fr / 1fr`, 26px gap.
- **Ready for review** — artifact cards, 11px 13px padding, 3px left status bar, 20px Barlow Condensed title, one-line AI summary of what changed. Click → Focus Mode.
- **On the wire this week** — compact rows: 52px monospace timeslot, label, status tag right-aligned.
- **Drafts aging** — one sentence nudge, no chrome beyond the card.
- **Image slots waiting** — count of empty slots + model tags + "Open image studio →". This block is new in this design; it exists because empty image slots were otherwise invisible until publish time.

### 2. Campaigns index
3-up card grid, 18px gap. Each card: 104px duotone media band, kicker (`Webinar · 58:12`), 20px title, one line of state, status tags. Click → that campaign's Mill Floor.
Scaling rule documented in the design: the rail lists campaigns up to ~12; past that show the 5 most recent plus this index, and let ⌘K search all (including transcript text).

### 3. New campaign — 3 steps (this is the only entry point for a campaign)
1. **Pick a source.** Full-width dashed drop zone (34px 24px padding) — "Drop a video or audio file here", accepted formats, size limit; plus three secondary cards: Paste a URL · Paste a transcript · Pick from assets. Footnote: desktop = local Whisper, web = cloud transcription.
2. **Transcription progress.** File card, 3px progress bar, monospace log line stepping through: extracting audio → transcribing by range → diarizing → "214 timed segments · suggesting brief…". ~620 ms per step in the prototype; real duration is the job's.
3. **The brief.** 2×2 field grid (title, audience, brand voice, angle) prefilled by small-model suggestions; then a 3-column **fan-out checklist** (blog, social ×6, email sequence, newsletter, landing page, show notes, clips ×3, SEO report, **image plan ×6**), all checked by default; then **Press Run**.

### 4. Campaign header + view tabs (persistent on all four campaign views)
- 13px 22px padding, 1px bottom divider. Left: "Campaigns /" link + 20px campaign name. Right: "n/6 images" monospace counter, then a segmented control of four **buttons**: Mill Floor · Focus mode · Image studio · SEO analysis.
- Active tab = accent fill, paper text. Inactive tabs must keep the DS hover tint and the `:focus-visible` 2px accent ring — they are buttons, keyboard reachable (roadmap §4.2 requires keyboard-only operation).

### 5. Mill Floor (the canvas)
- Two regions: **Source Master** card 286px fixed, and the swimlane board (horizontally scrollable, `transform: scale()` zoom).
- Source Master: header, 96px player band with a 38px play glyph, scrubber row (`12:04 … 58:12` monospace), then the timed transcript list — each segment `[data-seg]`: 8px 12px padding, 11px monospace `00:41 · HOST`, 12.5px text, 1px hairline separators.
- Board: one row per channel — Blog · Social · Email · Clips · Images · Page/SEO. 66px uppercase lane label, cards 120–220px wide, 9px 11px padding, 15px Barlow Condensed title, 11.5px meta.
- **Status is a 3px left bar**: draft `#b7b7ba` · in review `#94bce3` · queued `#416180` · published `#1d2d3d`. Card hover border → accent.
- Zoom: segmented control (Overview 0.72 / 100% 1) applying `transform: scale()` with `transform-origin: top left` — CSS transform only, never re-layout (roadmap risk: canvas perf on WASM; virtualize cards, target 60 fps at 100 cards).
- **Press Run**: no spinners. Cards start at `opacity:0; translateY(6px)` and are revealed one at a time (210 ms apart in the prototype; drive from real per-artifact completion events), with one narrated log line: "Printing: LinkedIn post · 3 of 14". Footer keeps the completion line.

### 6. Provenance threads (the trust feature)
- Hover an artifact card → an SVG overlay draws one cubic path per cited transcript segment, from the segment row's right edge to the card's left edge, plus a 2.5r dot at the source and the segment ID as a 10px monospace label. Cited segment rows highlight `#d6ebff`.
- Click a card to **pin** the threads; hovering elsewhere restores the pinned set.
- Geometry: measure `getBoundingClientRect()` of the SVG (as the coordinate base), the card, and each segment row; control points at 55% of the horizontal span. Recompute on scroll/zoom.
- Data: every generator persists the transcript segment IDs it used with the artifact (roadmap E6 acceptance: every artifact shows ≥1 traceable segment).

### 7. Focus Mode
- Manuscript column max 820px (content ~72ch), Producer rail 326px with a 1px left divider.
- Manuscript: hero image (rendered figure when the slot is filled; dashed **image stub** with a Generate button when empty), 40px h1, 15.5px/1.62 body, 26px h2s, inline figures/stubs between sections, cited phrases underlined 2px `#94bce3`.
- Producer rail: steering textarea + "Regenerate section / Whole"; two-pass log ("Outlining… Drafting… Auditing against <brand voice>…") with the audit model named; **image slots in this artifact** with EMPTY/DONE badges; version filmstrip (52×64 thumbs, active one outlined accent); footer validator line + "Mark reviewed & schedule".
- Image stub markers persist in the markdown as real blockquote-style markers so they survive editor round-trips (roadmap §2.5/§3.3.6).

### 8. Image Studio — **new; the roadmap only had "image prompts → generation"**
Left: **image plan** (250px) — six typed slots per campaign, each with name, dimensions/ratio, and an EMPTY/DONE badge. Slots are reserved at run time, not improvised:
| Slot | Size | Notes |
|---|---|---|
| YouTube thumbnail | 1280 × 720 | text overlay + safe area |
| Blog header | 1600 × 840 | hero |
| Inline 1–3 | 1200 × 675 | figures inside the manuscript |
| Social card | 1200 × 1200 | |

Right: the studio.
- **Model** radio group: `gpt-image-2` (Foundry; text-in-image, reference edits) · `MAI-Image-2.5` (Foundry first-party; control-with-preservation) · **`nano banana`** (Google). Flagged in the UI: nano banana is **outside the Foundry catalog**, so it breaks §3.1's "100% Azure AI Foundry" rule — it needs its own `IImageGenerator` adapter plus a second BYO credential slot in the encrypted `UserSetting` store.
- **Prompt**: brand-aware textarea seeded per slot from the transcript moment it illustrates, labelled with its provenance ("built from segment s02 · 04:18"). Chips append constraints: brand palette · no baked text · quote the moment · match reference · thumbnail-safe.
- **Variants** 2/4/6 segmented control; **reference image** = a frame extracted from the source video (e.g. @04:22) for image-to-image edits with preservation.
- **Thumbnail text overlay**: headline field (≤32 chars) + safe-area toggle — **composited by Castmill, not baked by the model** (models still mangle small text). Rendered on the variant tiles: 22px Barlow Condensed, `#f2f2f3`, `text-shadow 0 2px 6px rgba(0,0,0,.5)`, dashed safe-area inset 8%.
- Generate → skeleton tiles (pulse 1.1s) → variant tiles labelled `v1 · <model>`; click a tile to place it into the slot: badge flips DONE, WebP is published to the public container, and the artifact's markdown stub is replaced in place. Cost estimate line updates with variants × model.

### 9. SEO / AEO analysis (new tab)
Three columns: scorecard (196px: score in 64px Barlow Condensed, one-line verdict, titles / JSON-LD / image-alt rows) · keywords table + share-of-voice bars · content angles (270px) with "Draft these as artifacts".
Honesty rule that must survive implementation: an unpublished campaign shows projected positions and says so.

### 10. The Wire
Left 250px queue of reviewed-and-unscheduled artifacts (`draggable`), right a 5-column day grid, 300px min height, dashed borders. Drag onto a day → accent border + 10% accent wash while hovering, then a scheduled chip appears and a toast confirms "…scheduled Thu 30 09:00 via broker". Queue/sent/error states flow back into the same strip.

## Interactions & behavior
- Motion: **200 ms ease-out** everywhere; no bounce, no parallax. The only "big" animation is the Press Run reveal.
- Navigation: rail = workspace scope; tabs = campaign scope. Switching campaign keeps the current view. Every campaign-scoped surface must re-render from the selected campaign — the prototype's own bug log was almost entirely "header changed, content didn't".
- Provenance: hover = transient, click = pinned, mouse-out restores pinned.
- Drag-to-schedule: HTML5 drag in the prototype; in Blazor use pointer events or a small JS interop island.
- Generation: request/response per artifact in v1; the Press Run reveal is driven by per-artifact completion, not a timer.
- Empty/loading: transcription = progress bar + narrated log; generation = printing cards; images = pulsing skeleton tiles. No indeterminate spinners anywhere.

## State
Per session: `screen` (front · camps · pick · transcribing · ingest · canvas · focus · images · seo · wire), `campaignId`, `selectedSlot`, `imageModel`, `variantCount`, `pinnedArtifactId`, `zoom`.
Per campaign (server): source + timed transcript (segment IDs, timestamps, speakers), artifacts (typed JSON content, status, citations, validator results, version history, ETag), image plan (slot → prompt, model, aspect, state, published URL), SEO report, schedule entries.
Rules: artifacts can't be marked reviewed until deterministic validators pass; image slots are created with the run; placing an image updates the slot AND the artifact markdown.

## Design tokens (as used in this prototype — Industry sheet)
Colors: bg `#f2f2f3` · surface `#e9e9ea` · text `#1d1f20` · divider `rgba(29,31,32,.16)` · accent `#5980a6`; accent ramp 100 `#eef6ff` · 200 `#d6ebff` · 300 `#b5d9fd` · 400 `#94bce3` · 500 `#749dc4` · 600 `#597ea3` · 700 `#416180` · 800 `#2c455d` · 900 `#1d2d3d`; neutral 400 `#b7b7ba`.
Type: **Barlow Condensed** 600 headings (h1 42 · h2 32 · h3 25 · h4 20), **Barlow** 400 body 15px/1.55; monospace for timecodes, character counters, IDs. Kickers: 10px monospace, 0.12–0.14em tracking, uppercase.
Spacing scale: 3.4 · 6.8 · 10.2 · 13.6 · 20.4 · 27.2 px. Radius: **0** (square corners; `.blueprint` frames + `+` corner marks). Shadows: sm `0 1px 2px`, md `0 3px 10px`, lg `0 12px 32px` ink at 14/16/22%.
If you adopt the roadmap's brand sheet instead: paper `#F5F0E8`/`#1E1B18`, ink `#1A1815`/`#EDE7DD`, ember `#C15F3C`/`#D97757`, brass `#8A7248`, sage `#5F7355`, panel `#FBF8F2`/`#26221E`, rule `#DDD3C2`/`#3A342E`; serif display + humanist sans.

## Ignite UI for Blazor usage map
| Surface in these designs | Component |
|---|---|
| SEO keyword table, queue lists | `IgbGrid` / `IgbList` |
| SEO share-of-voice + report charts | `IgbCategoryChart` / `IgbDataChart` |
| New-campaign brief, steering, image insert, prompt log | `IgbDialog` |
| Metadata tabs (combined / head / JSON-LD) | `IgbTabs` |
| Brief fields, model selects, Wire slot date/time | `IgbInput`, `IgbSelect`, `IgbCheckbox`, `IgbDatePicker` |
| Toasts behind `INotifier` | `IgbToast` / `IgbSnackbar` |
| Status/platform tags, avatars | `IgbChip`, `IgbBadge`, `IgbAvatar` |
| Asset browser | `IgbTree` |
Custom Razor + CSS (do not fight the library): the canvas and its threads, the campaign header/tab strip, Focus Mode chrome, the Wire, the Press Run animation.

## Assets
No production imagery. Every image is a placeholder: hatched `repeating-linear-gradient` bands inside a `.duotone` wrapper, sized to the real slot ratio. Replace with generated/real media; keep the duotone treatment if you keep this token sheet. Icons: Lucide, stroke-width 1.5 (none inlined here).

## Files in this bundle
- `Castmill Prototype.dc.html` — the interactive prototype: all ten screens, three seeded campaigns, working Press Run, provenance threads, image generation and drag-to-schedule. **Primary reference.**
- `Castmill Canvas Alternatives.dc.html` — the three navigation-model explorations that preceded it (free canvas / multi-pane desk / vertical run sheet) plus the annotated provenance studies. Useful for *why* the Mill Floor looks like this.
- `_ds/industry-.../styles.css` — the token sheet both files consume.
Open either file in a browser. In the prototype, start at Front page → Start a run to walk the whole flow; use the campaign switcher to compare a mid-flight campaign (Blazor), a finished one (podcast) and a stale one (Field guide).

## Suggested build order (maps to the roadmap's epics)
1. Token sheet + Ignite theming (E3) — decide the brand sheet question first.
2. App shell, rail grouping, campaign header + tabs (E4.1).
3. Campaign create → ingest → transcript (E7.1/7.3/7.4, E6.2).
4. Mill Floor board + Press Run (E4.3, 4.7).
5. Provenance threads once citations exist (E4.4 after E6).
6. Focus Mode + editor (E4.5, E5).
7. Image plan + Image Studio (E6.7 — note the scope additions below).
8. SEO view (E9), Wire (E8).

## Scope additions this design introduces (not in the roadmap — plan for them)
1. **Typed image slots** per campaign (thumbnail / header / inline / social) reserved at run time, each with prompt, model, aspect and state — replaces "a bag of image prompts".
2. **Overlay compositor** for thumbnail headlines with a safe-area guide, server-side, applied after generation.
3. **Reference-frame extraction** from the source video for image-to-image edits with preservation.
4. **A second image provider adapter** (+ credential slot) if nano banana is in scope, since it is outside Azure AI Foundry.
5. **Image state surfaced everywhere** — front page ("image slots waiting"), campaign header counter, Focus Mode slot list — because empty slots were previously invisible until publish.
