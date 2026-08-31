# Handoff spec — The Wire (Castmill scheduling & delivery surface)

**Status:** design approved. Three views ship: **Run of Show (1a)** primary, **Pipeline (1b)**, **Agenda (1c)**.
**Scope:** one workspace-level surface in `Castmill.UI`.
**Design reference:** `Castmill Wire Alternatives.dc.html` — turn 1 holds all three approved views (1a, 1b, 1c). Turn 2 (2a/2b/2c) is **deferred**, not part of this build; see §11.
**Do not port the HTML.** These files are design references written in HTML/CSS/JS. Recreate them as Razor components on Ignite UI for Blazor per §8.

---

## 1. Why this design exists

The previous Wire was a five-column, full-height week grid. It failed structurally:

1. **Five equal columns give every day 20% of the width** regardless of content. In a 1440px shell with a queue rail, each day lands at ~200px — narrower than a post title, so titles wrapped one word per line.
2. **A week holds ~6–12 items.** A full-height 5-column grid is sized for ~200. ~80% of the surface was empty ruled paper.
3. **Actions sat inline after the title**, so at that width the Edit/Schedule buttons landed on top of the wrapping text.
4. **Time of day was invisible** — a 07:00 newsletter and a 22:00 post looked identical.

### The four non-negotiables
These are the fix, and they apply to all three views. If an implementation detail conflicts with one, the detail is wrong.

1. **Titles get real width and clamp.** Two lines in cards, one line in dense rows. Never a third line, never a mid-word cut without an ellipsis.
2. **Actions live in a fixed-width column** at the end of the row, revealed on hover *and* `:focus-within` — never in the text flow.
3. **Rows size to their content.** An empty day is a single ~30px line; the weekend collapses to one row when there is no posting window.
4. **Time of day is spatial** — horizontal position against a ruler, not only a text label (1a only; 1b and 1c express time as a label by design).

A fifth, from the roadmap (§4.2): **every drag gesture has a keyboard equivalent.** Drag is never the only path.

---

## 2. Where it lives, and the three views

The Wire is **workspace scope, not campaign scope** — it shows all campaigns. It is reached from the left rail's WORKSPACE group, *not* from the campaign tab strip (Mill Floor / Focus mode / Image studio / SEO analysis). Do not add The Wire to that strip.

One switch in the header (`IgbButtonGroup`): **Run of show · Pipeline · Agenda**. Plus a range switch (**Week · Fortnight**) shown for Run of show and Agenda only.

| View | Answers | Design ref | Section |
|---|---|---|---|
| **Run of show** (default) | "What goes out, and when?" | 1a | §3 |
| **Pipeline** | "What's stuck?" | 1b | §4 |
| **Agenda** | "Everything, in order" — and the narrow-width fallback | 1c | §5 |

The three are **one data set on three axes**: time × day (1a), pipeline state (1b), and flat chronology (1c). No view has data the others lack — which is what makes the switch cheap to build and cheap to reason about.

Below ~1100px of content width the time ruler stops being legible: **force the Agenda view** and disable the Run-of-show option. That is also why Agenda is a first-class view rather than a responsive hack.

---

## 3. Run of Show — primary view (ref 1a)

### 3.1 Frame
- Container padding `18px 22px 20px`.
- Two regions, `22px` gap, `align-items: flex-start`:
  - **Queue rail** — `288px`, `flex: none`.
  - **Timeline** — `flex: 1`, **`min-width: 0`** (required — without it long titles blow out the flex basis; this is the single most common way to reintroduce the original bug).

### 3.2 Header
- Kicker `THE WIRE` — 10px monospace, .14em tracking, uppercase, accent.
- h3 — `Week of 31 Aug`, Barlow Condensed 600.
- State tags: `4 queued` (outline) · `2 sent` (neutral) · `3 ready to schedule` (accent).
- Right: the view switch, the range switch, then **← Prev** / **Next →**. All real `<button>`s — keyboard reachable, DS `:focus-visible` accent ring.

### 3.3 Broker banner — shown only when no broker is configured
Full-width blueprint strip on the surface tone, `9px 12px`: monospace `NO BROKER` label + one sentence ("Nothing leaves Castmill until a publishing broker is connected — everything below stages locally.") + primary **Connect broker** pushed right.
This is a **persistent condition, not a toast.** While it shows, scheduling is still allowed and every would-be `QUEUED` tag reads `STAGED`.

### 3.4 Queue rail — "Ready to schedule"
Section header: 10px monospace uppercase label, count right-aligned.
Each card is `draggable`, `9px 11px` padding, 3px left status bar, and a flex row with exactly two children:
- **Text block** (`flex: 1; min-width: 0`): channel/meta kicker (10px Barlow Condensed uppercase) → title (15px Barlow Condensed / 1.18, **clamp 2**) → one monospace meta line (duration, character count, citations, validators).
- **Action column** (`flex: none`): stacked **Edit** and **Slot** buttons, `opacity: 0` until row hover or focus-within. **Slot** opens the date/time dialog — the keyboard path for scheduling.

Footnote card beneath the queue, 11.5px: clips need a durable published URL before scheduling; local file paths and short-lived worker download links are never sent to the broker.

### 3.5 Timeline
**Time ruler** across the top, offset by the day gutter (`padding-left: 106px`): 06:00 · 09:00 · 12:00 · 15:00 · 18:00 · 21:00 — each `flex: 1` except the last (fixed 44px), 10.5px monospace at 42% ink.

The ruler defines the only mapping in this view:

    left%    = (minutes − windowStart) / (windowEnd − windowStart) × 100
    minutes  = windowStart + (pointerX − laneLeft) / laneWidth × (windowEnd − windowStart)

Default window 06:00–22:00, a **workspace setting**, not a constant. Items outside the window clamp to the lane edge and show their true time in the label. Drop times snap to **15 minutes**.

**One row per day**, hairline separated (`rgba(29,31,32,.1)` interior; `.16` at the top and bottom of the stack):
- **Day gutter** — `92px`, `flex: none`: day + date (16px Barlow Condensed) over a 10.5px monospace state line (`1 sent`, `today · 2 queued`, `1 error`). Error counts take the error color; "today" takes the accent.
- **Slot lane** — `flex: 1; min-width: 0`, 1px dashed left border, `position: relative`. Scheduled items are absolutely positioned by time percentage; a `52px` spacer establishes lane height.

| Day state | Row treatment |
|---|---|
| Has items | `9px 0` padding, 52px lane |
| Today | Same + `rgba(89,128,166,.05)` row wash, accent "today" label |
| Empty | `7px 0`, no spacer — one line, "nothing scheduled — drop here", 38% ink |
| Weekend / no posting window | One collapsed row, 50% opacity, "collapsed — no posting window" |

**Scheduled item card** (absolute in the lane): 200–250px wide, `6px 9px` padding, 1px border, 3px left status bar. Line one: monospace time · channel kicker · status tag pushed right. Line two: title, **clamp 1**, 12.5px. Sent items drop the border and sit on the surface tone; error items take an error-tinted border.

**Overlap rule (not in the prototype — implement it):** when two items in a lane fall within 90 minutes of each other, stack them vertically inside the lane and grow the row height. Never let cards overlap.

---

## 4. Pipeline (ref 1b)

Columns are **pipeline states**, not days; the date is a chip on the card. This is the monitoring view — it answers "what's stuck?", which Run of Show structurally cannot, because a blocked item with no date has no day to sit in.

- Rendered on the **dark ground** (`#191b1c` bg, `#222425` cards, `#f2f2f3` titles) — it is a status surface, and the tonal shift signals the mode change without extra chrome.
- Header: kicker `THE WIRE · PIPELINE`, h3 "*n* items in flight", subline naming the scope ("Across all 3 campaigns…"), then a **This campaign only** toggle and **Connect broker**.
- **Four columns**, `repeat(4, 1fr)`, `gap: 14px`, `align-items: start`:

| Column | Header rule | Contents |
|---|---|---|
| Ready | 2px `#b7b7ba` | reviewed, no date — cards show a `no date` chip |
| Queued *(Staged with no broker)* | 2px `#416180` | cards show an accent date chip, `TUE 08:30` |
| Sent | 2px `#1d2d3d` | 78% opacity; monospace delivery line beneath the title |
| Needs attention | 2px `#C15F3C` | reason in prose + the resolving action inline |

- Column header: 11px Barlow Condensed uppercase label, count right-aligned in monospace at 45% ink. `min-height: 120px` on each column body so an empty column is still a legible drop target.
- **Card**: 3px left status bar, `9px 11px`, channel kicker → title (**clamp 2**, 15px) → chips/meta. The Needs-attention card additionally carries its reason at 11.5px in the error tint and a secondary action ("Export clip").
- **Drag between columns changes state.** Ready → Queued must open the date/time dialog (a queued item requires a date); every other transition is direct. Sent is not a drop target.
- The Needs-attention column is the reason this view ships: blocked items are otherwise only discoverable by scanning days.

---

## 5. Agenda (ref 1c)

One chronological list; day headers are rules. Densest of the three, scales to a quarter without redesign, and it is the **forced view below ~1100px**.

- Day header: 15px Barlow Condensed, .06em tracking, uppercase, with a monospace state note (`1 sent`, `1 blocked`); today takes the accent and the `· TODAY` suffix.
- **Item row** — a single flex line, `9px 4px`, 1px hairline below, 3px left status bar, with fixed-width columns so every row aligns:

| Slot | Width | Content |
|---|---|---|
| Time | 44px, `flex: none` | 11px monospace |
| Channel | 74px, `flex: none` | 10px Barlow Condensed uppercase, 50% ink |
| Title | `flex: 1; min-width: 0` | 13.5px, **clamp 1** |
| Status | `flex: none` | tag |
| Actions | `flex: none` | Edit / Move (Export for blocked), hidden until hover or focus-within |

- Empty day: one `9px 4px` line, "Nothing scheduled — drop an item here", 40% ink. Still a drop target.
- The ready-to-schedule queue lives in a **right dock** at wide widths, or collapses to an "*n* unscheduled" bar pinned to the top of the list at narrow widths. (The reference omits the dock for width; build it from §3.4 — same cards, same action column.)

---

## 6. Status vocabulary

| State | Color | Token | Meaning |
|---|---|---|---|
| Draft / unscheduled | `#b7b7ba` | neutral-400 | reviewed, no date |
| Queued (or Staged, no broker) | `#416180` | accent-700 | scheduled, not yet sent |
| Sent | `#1d2d3d` | accent-900 | broker reported success |
| Blocked / error | `#C15F3C` | — | cannot be scheduled, or delivery failed |

Status appears as a **3px left bar plus a tag** — never color alone. The error hue is the only non-accent color in this surface and is used **only** for this state.

---

## 7. Interactions

- **Drag to schedule** — queue card → day lane (1a), day row (1c), or column (1b). Hover feedback on the target: `rgba(89,128,166,.14)` background + 1px accent outline. On drop the item leaves the queue, lands at the mapped time, and a toast confirms "<name> scheduled Thu 3 Sep 09:00 via broker" (or "…staged locally" with no broker).
- **Drag within a view** reschedules (1a, 1c) or changes state (1b). Dropping an item on its own slot is a no-op.
- **Keyboard parity (required):** the queue card's **Slot** button and each item's **Move** action open an `IgbDialog` with `IgbDatePicker` + `IgbTimePicker`. Ship this path *first*; drag is the accelerator.
- **Hover reveals the action column** — and the same reveal must fire on `:focus-within`.
- **View switching preserves range and filters**, and does not refetch: same data, different projection.
- **Motion:** 200ms ease-out, on background / border / opacity only. No sliding cards, no bounce.

### States
- **Empty week** — every row collapsed to one line; the queue rail carries the call to action. No full-page empty illustration: the week's shape *is* the information.
- **Blocked item** — error bar, `NO URL` tag, resolving action inline in the card.
- **Loading** — day rows render as hairlines with skeleton gutters. No spinners in this surface.
- **Send failure** — the item flips to the error color **in place**, with retry. It does not move or disappear.

---

## 8. Data — and the metrics question

Per schedule entry, **required**:

    id, artifactId, campaignId, channel,
    scheduledAtUtc, timeZone,
    status: Draft | Queued | Staged | Sent | Error | Blocked,
    blockedReason, brokerRef, sentAtUtc, lastError, permalink

Every one of those comes from Castmill's own database or from the broker's own send receipt. **All three views work completely on this set alone.** Nothing in §3–§5 requires a platform metric.

### Metrics are an optional tier — design for their absence
Reach, engagement, opens and completion rates require per-platform read APIs, each with its own auth, rate limits, approval process and eventual consistency. Several platforms won't give them to you at all on the tier you're likely on. So:

- **Do not put metrics in any of the three views.** No engagement column, no performance bars, no "best time to post" claim. (This is a change from an earlier draft, which had them in a Sent view — that view is deferred; see §11.)
- **Sent items display delivery facts only**: the send timestamp, the broker reference, and a **permalink** to the live post. A link the user can click is worth more than a stale number you fetched once, and it costs nothing.
- **Model metrics as nullable from day one** — `metrics: { reach?, engagement?, … }` — and treat "no metrics provider configured" as the *normal* state, not an error or an empty-state to apologise for. Never render a `0` where you mean "unknown", and never render a metrics panel that might be empty.
- **If and when a provider is connected**, metrics appear as one optional column in a later Sent view (§11) — additive, behind a capability flag per channel. Nothing in the approved three views changes.

### Other data rules
- The views query a **date range across all campaigns**, grouped by local day (1a, 1c) or by status (1b).
- Timezone: schedule in the **workspace** timezone, store UTC, label the ruler in local time, state the zone in the header.
- Posting window (default 06:00–22:00) and collapsed days are workspace settings.
- **Blocked is computed, not stored by the UI:** an artifact whose asset has no durable published URL is Blocked regardless of its schedule. Enforce server-side; the UI only reflects it.
- An artifact cannot be scheduled until its deterministic validators pass (existing rule — don't duplicate the logic client-side).

---

## 9. Ignite UI for Blazor usage map

| Surface | Component |
|---|---|
| View switch, range switch | `IgbButtonGroup` |
| Prev / Next / Connect broker / row actions | `IgbButton`, `IgbIconButton` |
| Slot + Move date/time dialog | `IgbDialog` + `IgbDatePicker` + `IgbTimePicker` |
| Status and channel tags, date chips | `IgbChip`, `IgbBadge` |
| Channel / campaign filters | `IgbSelect` |
| Toasts behind `INotifier` | `IgbSnackbar` / `IgbToast` |

**Custom Razor + CSS** (do not fight the library): the time ruler, the day rows, the absolute positioning of scheduled items, the pipeline columns, the agenda rows, and the drag interaction.

> Explicitly: **do not** use a stock scheduler or calendar component for Run of Show. Every one of them is built on fixed-height day columns — the exact model this design replaces. Row-per-day plus a time ruler is the design.
>
> `IgbGrid` is **not** needed for any of the three approved views. The Agenda's rows are a custom layout, not a data grid — a grid's column model fights the clamped-title + revealed-actions pattern.

---

## 10. Tokens (Industry sheet, as built)

**Color** — bg `#f2f2f3` · surface `#e9e9ea` · text `#1d1f20` · divider `rgba(29,31,32,.16)` · hairline `rgba(29,31,32,.1)` · accent `#5980a6` · accent-400 `#94bce3` · accent-700 `#416180` · accent-900 `#1d2d3d` · neutral-400 `#b7b7ba` · error `#C15F3C`.
**Pipeline dark ground** — bg `#191b1c` · card `#222425` · text `#e7e7ea` · title `#f2f2f3` · divider `rgba(231,231,234,.18)` · accent-on-dark `#94bce3` · error-on-dark `#e0a08a`.
**Type** — Barlow Condensed 600: day labels 16px, channel names 15px, card titles 15px/1.18. Barlow 400: body 12.5–13.5px. Monospace: times, counts, IDs. Kickers 10px monospace, .12–.14em, uppercase.
**Radius 0** throughout. Framed elements use `.blueprint` + four `.corner` marks and stay transparent line drawings; the primary button is the one solid accent fill.
Every interactive element needs the DS hover tint and the 2px `:focus-visible` accent ring.

**Clamping — the one CSS trap.** `-webkit-line-clamp` requires `display: -webkit-box`. Any inline `display: block` on a clamped element silently disables it (this bug shipped once in the prototype and was caught in review). Use a utility class and give it `width: 100%` where it must fill a cell — never override its `display`.

If the roadmap's brand sheet (§1.2: ivory `#F5F0E8`, ember `#C15F3C`, serif display) is adopted instead, the structure is unchanged — only the token layer swaps. **Note:** that sheet's ember *is* this design's error color, so choose a distinct error hue in that case.

---

## 11. Build order

1. **Run of Show: day rows + time ruler**, static data. Verify at the real 1440px width: the 2-line clamp, the empty-day collapse, the weekend collapse. If those three don't hold, stop — nothing after this matters.
2. **Queue rail** with the action column and the Slot dialog. **Keyboard path first.**
3. **Drag-to-schedule**: x→time mapping, 15-minute snapping, target hover feedback, toast.
4. **Status, blocked and broker states**, including the STAGED relabel and the permalink on sent items.
5. **Overlap stacking**, then the Fortnight range.
6. **Agenda view** (§5) — cheapest of the three and it unlocks narrow widths.
7. **Pipeline view** (§4) with drag-between-columns.
8. **View switch** wiring: shared query, three projections, preserved range and filters.

---

## 12. Deferred and rejected

**Deferred — build only if the need proves out:**
- **Cadence** (ref 2a) — a month grid of day cells with counts and a gap callout, for spotting posting gaps. Defer until users actually plan a month ahead; the Fortnight range may cover it.
- **By channel** (ref 2b) — channel rows × day columns, catching over-concentration (three LinkedIn posts, newsletter dormant). Genuinely useful, but it is a transposition of data the approved views already show; ship after the three.
- **Sent / performance** (ref 2c) — **explicitly deferred pending the metrics question in §8.** Without platform metrics this view is just a list of delivery receipts, which the Agenda already gives you filtered to Sent. Revisit only once at least one metrics provider is connected, and build it additively.

**Rejected — closed, not open questions:**
- A per-campaign view. That is a filter on the three views, not a fourth view.
- A separate mobile layout. The forced Agenda below ~1100px covers it.
- A stock scheduler component (see §9).

---

## 13. Files in this bundle

- `Castmill Wire Alternatives.dc.html` — **primary reference.** Turn 1 holds all three approved views: **1a** Run of Show, **1b** Pipeline, **1c** Agenda. Turn 2 (2a/2b/2c) is the deferred set from §12 — read it for intent, don't build it. Interactive: drag a queue card onto any day lane or column.
- `Castmill Prototype.dc.html` — the full campaign workspace this surface belongs to (shell, rail grouping, campaign tabs, Mill Floor, Focus mode, Image studio, SEO), for context on shared chrome.
- `_ds/industry-…/styles.css`, `_ds_bundle.js`, `support.js` — the token sheet and runtime both files consume.

Open either HTML file directly in a browser.
