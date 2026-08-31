# Claude Code — start here

Paste this into your Claude Code session in VS Code, from the repo root.
(Assumes this folder is unpacked at `docs/design/the-wire/` — adjust the path if not.)

---

Read `docs/design/the-wire/SPEC-the-wire.md` in full, then open the design
reference it names (`Castmill Wire Alternatives.dc.html`) in a browser to see the
interaction. Turn 1 holds the three views we're building: 1a Run of Show,
1b Pipeline, 1c Agenda. Ignore turn 2 — it's deferred.

Implement §3 (Run of Show) in `src/Castmill.UI` as Razor components on Ignite UI
for Blazor per the usage map in §9. Do not port the reference HTML — recreate it.
Follow the build order in §11 and stop after step 1 so I can check the three
layout rules before you continue.

Hold these absolutely:
- the four non-negotiables in §1
- keyboard parity before drag (§7)
- no stock scheduler/calendar component, and no IgbGrid in these views (§9)
- `min-width: 0` on the timeline flex child (§3.1)
- the clamping trap in §10
- **no platform metrics anywhere** (§8) — sent items get a timestamp, a broker
  ref and a permalink, nothing else. Model `metrics` as nullable and treat
  "no provider configured" as the normal state.

---

Then, step by step:

    Continue the build order in §11, steps 2-4.
    ...
    Now steps 6-8: the Agenda view, the Pipeline view, and the view switch.

Notes:
- If the repo has no Wire surface yet, scaffold it as a workspace-scope route off
  the left rail's WORKSPACE group — not in the campaign tab strip (§2).
- Open decision, settle it before step 1: keep the Industry token values in §10,
  or implement the roadmap's §1.2 brand sheet (if so, pick a new error hue — its
  ember collides with this design's error color).
- The three views are one query with three projections (§7). Don't let them drift
  into three fetches.
