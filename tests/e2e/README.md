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

The deep SEO scenario drives the real browser UI and local API through:

`sign-in → transcript → AI context + selected-brand voice → deep SEO/AEO report → approval → production brief → Focus Mode`

It verifies the report contains successful provenance for DataForSEO Keyword Suggestions,
Keyword Ideas, Keyword Overview, and advanced SERP; requires multi-keyword topical competitor
visibility, authority, ranking footprint, and successful live responses from ChatGPT, Gemini,
Claude, and Perplexity; asserts the pre-approval production gate; replaces the seeded blog in
place; generates owned social and three-pass YouTube output; checks campaign lifecycle and
staleness; verifies Focus hierarchy/grounding; and checks that ApexCharts and ApexTree both
render SVG output in Chromium. Temporary campaign and brand records are deleted in a `finally`
block.

This test is opt-in because it makes metered DataForSEO calls, queries four answer engines,
and uses the configured text model. It requires the normal development configuration and demo
account in `src/Castmill.Api/appsettings.Development.json`.

```sh
npm run install-browser --workspace=castmill-e2e
CASTMILL_E2E_LIVE=1 npm run test:e2e
```

Without `CASTMILL_E2E_LIVE=1`, Playwright discovers the scenario and reports it as skipped.
