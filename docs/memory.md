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
|---|---|
| Castmill.UI.Tests | 167 / 167 |
| Castmill.Media.Tests | 13 / 13 |
| Castmill.Api.Tests | 223 / 223 |
| editor-interop (npm) | 38 / 38 (unchanged this session) |

Nothing committed since `e842f10`; the working tree holds all of the above awaiting an
explicit commit instruction.
