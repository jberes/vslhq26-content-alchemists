# Castmill live E2E

The deep SEO scenario drives the real browser UI and local API through:

`sign-in → transcript → context → deep SEO/AEO report → approval → generation → SEO desk`

It verifies the report contains successful provenance for DataForSEO Keyword Suggestions,
Keyword Ideas, Keyword Overview, and advanced SERP; requires multi-keyword topical competitor
visibility; asserts the pre-approval production gate; and checks that ApexCharts and ApexTree
both render SVG output in Chromium. The temporary campaign is deleted in a `finally` block.

This test is opt-in because it makes metered DataForSEO calls, queries four answer engines,
and uses the configured text model. It requires the normal development configuration and demo
account in `src/Castmill.Api/appsettings.Development.json`.

```sh
npm run install-browser --workspace=castmill-e2e
CASTMILL_E2E_LIVE=1 npm run test:e2e
```

Without `CASTMILL_E2E_LIVE=1`, Playwright discovers the scenario and reports it as skipped.
