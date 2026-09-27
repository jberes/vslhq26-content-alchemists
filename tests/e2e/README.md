# Castmill live E2E

The always-on browser regression opens `/` with an empty session and proves that the app's
default entry point redirects to the sign-in form. It does not call metered providers.

The always-on Image UX regression creates disposable Brand, asset, campaign, artifact, image
slot and report rows, then proves that Asset Kit reclassification persists, Image Studio mode
and constraint controls update without a campaign reload, and AEO engine tabs switch between
sanitized Markdown responses. It also verifies the workspace rail presents one metadata-bearing
Campaigns list and reveals the standard trash-can delete action on hover. The Brand Templates
check opens the full-height YouTube editor and persists a prompt longer than the former
4,000-character limit. It makes no model or DataForSEO calls and removes all disposable content
rows after the run.

The always-on webpage-import regression creates disposable campaigns while stubbing only the
external page response. It verifies immutable metadata and eligible-image review, page-specific
intent choices, exclusion plus revision approval, horizontal containment, and the honest recovery
message for a JavaScript-only shell. API integration tests separately exercise the real bounded
fetch, parser, SSRF guard, structured-data extraction, and prompt-injection boundary.

The always-on media/voice regression uses Chromium's deterministic fake microphone and mocked
metered transcription/SEO responses while keeping real authentication, campaign persistence and
shared RCL behavior. It completes resumable media selection and voice Record → Pause → Resume →
Stop → playback → Use recording, verifies block checksums and content types, reviews timed evidence,
and reaches analysis approval plus Press Run for both sources without calling a paid provider.

The always-on activity-feedback regression (ADR-F74) slows or fails selected API calls in the
browser and proves every API-backed control says it is working: the header rename waits for the
campaign and reads "Saving…", a refused rename keeps the typed name, a control activated by
click, Enter or Space is marked busy and swallows repeat presses, a failed call clears the busy
state and reports the error, and deleting a campaign from the rail or the Campaigns page covers
the main pane until the delete returns (a failed delete lifts the cover and keeps the campaign).

The always-on seed-blog regression seeds a real campaign, SEO/AEO report and blog, stubs only
the metered generation call, and proves "Seed blog from this angle" runs in the background: the
card says it is writing, the producer can move to another view, and a sticky toast links straight
to the new piece in Focus mode (or, on failure, says why).

The always-on Focus outline regression proves titles clamp to two lines and that one shared
tooltip — by pointer or keyboard — shows the whole title (or the parent document's title).

The microphone-picker regression runs against each engine's fake inputs (Chromium's "Fake Audio
Input 1/2", WebKit's "Mock audio device 1–4") and reads "Recording from …" off the live audio
track: it records from a chosen input, proves the choice is remembered on a fresh page, switches
inputs, follows a microphone being unplugged and plugged back in, and checks that a remembered
microphone that is gone falls back to the system default with a notice while the choice is kept.

The live webpage import imports `https://www.revealbi.io/ai` through the real bounded fetch and
checks its main copy is captured, not a menu card or a JavaScript-shell verdict. It calls no model
but needs the public internet.

The deep SEO scenario drives the real browser UI and local API through:

`sign-in → transcript → AI context + selected-brand voice → deep SEO/AEO report → approval → production brief → Focus Mode`

It verifies the report contains successful provenance for DataForSEO Keyword Suggestions,
Keyword Ideas, Keyword Overview, and advanced SERP; requires multi-keyword topical competitor
visibility, authority, ranking footprint, and successful live responses from ChatGPT, Gemini,
Claude, and Perplexity; asserts the pre-approval production gate; replaces the seeded blog in
place; generates owned social and three-pass YouTube output; checks campaign lifecycle and
staleness; verifies Focus hierarchy/grounding; and checks that ApexCharts and ApexTree both
render SVG output. Temporary campaign and brand records are deleted in a `finally` block.

It runs on every pass. It makes metered DataForSEO calls, queries four answer engines and uses
the configured text model, so it needs the normal development configuration and demo account in
`src/Castmill.Api/appsettings.Development.json`.

Every spec runs in both Chromium and WebKit (the Safari / Mac Catalyst engine); nothing is
skipped or opt-in.

```sh
npm run install-browser --workspace=castmill-e2e
npm run test:e2e
```
