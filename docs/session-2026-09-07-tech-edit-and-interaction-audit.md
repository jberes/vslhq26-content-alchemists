# Tech Edit resilience and interaction audit - 2026-09-07

This document records the Tech Edit recovery work, API and browser interaction tests, test-environment repairs, and remaining validation gaps from the session. It describes the current working tree, which also contains unrelated work that predates this audit. Credentials, tokens, connection strings, and local secret values are intentionally omitted.

## Outcome

- Tech Edit now distinguishes malformed or incomplete model JSON from a response containing no JSON and reports a bounded line and byte location without echoing model output.
- A malformed Tech Edit completion gets one fresh completion attempt using the original evidence and instructions plus a JSON-format correction.
- A second malformed completion fails safely. No artifact revision is written and the original manuscript remains unchanged.
- The retry does not bypass content, evidence, or citation validation. A structurally valid retry with invalid citations is still rejected.
- Browser downloads can read `Content-Disposition` across the configured development origin, preserving the server-provided export filename.
- Added real API workflows for settings and secret deletion, Brand templates and knowledge configuration, asset linking, and Markdown/DOCX export.
- Added focused browser workflows for account security, settings persistence, Focus editing/export/recovery, and Wire range navigation.
- Added API route observation to the shared integration-test fixture. The report is evidence of requests observed during that fixture's tests, not proof of full endpoint behavior coverage.

This session does **not** establish complete regression or interaction coverage. The concrete gaps are listed below.

## Tech Edit changes

The JSON extraction path still accepts valid JSON returned directly, inside a code fence, or with surrounding prose. When candidate JSON is present but cannot be parsed, it now preserves the parser location in a safe error message rather than classifying the reply as if it contained no JSON.

The Tech Edit orchestration path performs at most two model completions:

1. Run the ordinary edit request with the original manuscript, evidence, and producer direction.
2. If and only if the first result contains malformed or incomplete JSON, request one fresh completion with an explicit JSON-format correction.
3. Run the normal validators against the retry result before writing anything.
4. If parsing or validation still fails, return a failure and leave the artifact and revision history untouched.

The implementation deliberately does not attempt to reconstruct truncated JSON or infer missing manuscript content locally.

Relevant implementation and regression tests:

- `src/Castmill.Api/Services/Ai/AiOrchestrator.cs`
- `src/Castmill.Api/Services/Ai/GenerationEvidenceContext.cs`
- `tests/Castmill.Api.Tests/ModelJsonParsingTests.cs`
- `tests/Castmill.Api.Tests/TechEditApiTests.cs`

## Export filename fix

The API returned the correct `Content-Disposition` header, but browser JavaScript could not read it on a cross-origin development request. `Program.cs` now exposes `Content-Disposition` alongside `ETag` and `X-Correlation-ID`. Allowed origins, request methods, and request headers were not broadened.

The Focus browser regression now verifies that a Markdown export is offered as `preserved-draft.md`, and the API workflow checks both the exposed-header contract and a valid DOCX ZIP containing `word/document.xml`.

## Added test infrastructure

### API interaction report

`ApiInteractionCoverage` is installed by `CastmillApiFactory` as a test-only startup filter. It records the HTTP method, registered raw route, response status, and count after a matched endpoint completes. It does not record request bodies, query values, credentials, or response bodies.

The latest full API test run registered 178 `/api/v1` routes:

| Measure | Routes |
| --- | ---: |
| Registered | 178 |
| Observed at least once | 153 |
| Observed with at least one success response | 150 |
| Not observed | 25 |

The generated report is written under the API test output at `TestResults/api-interactions.json`. These counts have important limits:

- A route hit is not proof that all branches, authorization cases, validation errors, or persistence effects were tested.
- A route absent from the report is not proof that no test covers its underlying service or behavior.
- Middleware short-circuits and tests using a different host fixture may not be recorded.
- The report reflects the latest complete run and can be overwritten by a later focused API test.

### Browser fixture

The Playwright fixture routes only the WebAssembly development settings request so the browser uses the dedicated test API at `http://localhost:5015/`. API behavior remains real unless an individual test explicitly mocks an expensive or external boundary.

The fixture records method, URL path, and status for browser-observed `/api/v1` responses and attaches that list to the test result. Because Playwright also observes fulfilled route mocks, the attachment alone cannot distinguish a real server response from a mocked response; the test source remains authoritative for that distinction.

Playwright now uses dedicated API and Web ports, raises the development auth limit, selects Azure CLI credentials explicitly, and refuses to reuse an existing server. This prevents a user-owned development process from changing the test result.

## Added API workflows

`ScreenInteractionApiTests` adds three cross-boundary workflows:

- Settings and write-only secrets can be removed by their owner, cannot be removed anonymously or by another tenant, and remain unchanged after rejected requests.
- Brand templates, skills, MCP server configuration, and product assets can be created, updated, read, linked, unlinked, and deleted. Write-only authorization remains absent from responses, and a null update preserves the stored value.
- Focus exports persisted content as Markdown and DOCX, rejects anonymous and cross-tenant access, rejects unsupported formats, and exposes the download filename header to the configured browser origin.

The tests use generated accounts and nonfunctional placeholder values. They do not call real MCP servers or publishing providers.

## Screen and workflow evidence

| Surface | Evidence added or exercised | Boundary and remaining gap |
| --- | --- | --- |
| Sign in and register | Signed-out redirect, real registration, sign in, sign out, rejected old password after password change | External identity browser flows were not comprehensively audited |
| Settings and security | Real link persistence, write-only credential set/remove, password change, API ownership and tenant checks | Provider credential validity and every settings error state were not exercised |
| Campaign list and New Campaign | Existing browser flows create and clean up real campaigns; source-review flow checks approval gating | No exhaustive control-by-control browser pass |
| Mill Floor | Existing navigation regression opens the exact artifact and preserves completed progress state | Generation response is mocked in that test; not every artifact action is covered |
| Focus | Real editor save/reload, direct API persistence read, exact Markdown filename, review transition, failed Tech Edit draft preservation | Browser test mocks the Tech Edit failure response; the real retry is covered at API level |
| Image Studio | Existing browser test covers Brand asset types and in-place controls | Image generation/provider behavior is mocked; real provider and download variants remain gaps |
| SEO/AEO | Metered live test reached a real report with provider metrics and more than five keywords | Live run failed because Keyword Ideas provenance was absent; later production and hierarchy assertions did not execute |
| The Wire | Real keyboard and drag scheduling, projection switching, range navigation, and no-refetch behavior | No live broker publish; local staging only. Combined-suite timing remains to be reconfirmed |
| Brands | API round trips cover templates, skills, MCP configuration, and asset relationships | Browser coverage is partial; real MCP calls are not exercised |
| Media and voice | Existing browser workflow reaches analysis and Press Run | Transcription and other expensive boundaries are mocked, so it is not live-provider proof |
| Front page and not-found/dev pages | Existing component and entry-route coverage | No dedicated browser interaction audit for every state |

## Verified test results

### .NET

Final command:

```bash
dotnet test Castmill.NoDesktop.slnf --nologo \
  --logger 'trx' \
  --logger 'console;verbosity=minimal' \
  --results-directory "$PWD/artifacts/test-results/final"
```

Result on 2026-09-07:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Castmill.Api.Tests | 601 | 0 | 0 |
| Castmill.UI.Tests | 386 | 0 | 0 |
| Castmill.Media.Tests | 13 | 0 | 0 |
| **Total** | **1,000** | **0** | **0** |

The focused JSON parser and Tech Edit set passed 16 tests: seven parser regressions and nine HTTP orchestration tests. The three new screen-interaction API workflows also passed independently before the complete run.

### Editor interop

```bash
npm run build
npm test
```

Result: 62 tests passed across six files. The Markdown round-trip corpus and gzip bundle-budget gate passed.

### Browser

Focused current regressions passed independently:

- account registration, password change, sign out, and sign back in;
- settings and write-only credential persistence/removal;
- Focus save, export filename, failed Tech Edit preservation, and review transition;
- Wire keyboard/drag scheduling and projection behavior;
- the existing webpage source-review workflow after routing it through the dedicated fixture.

An earlier correctly routed eight-test suite passed six tests and failed two: the SEO test had stale selectors and the Wire test raced range navigation. Both test implementations were corrected, and Wire passed its focused rerun.

The final combined eleven-test Playwright command did not produce a report, so there is no current all-browser-suite result to claim. The intended command is:

```bash
PLAYWRIGHT_JSON_OUTPUT_NAME="$PWD/artifacts/test-results/final/playwright.json" \
CASTMILL_E2E_LIVE=1 \
npm exec --workspace=castmill-e2e -- playwright test --reporter=list,json
```

`CASTMILL_E2E_LIVE=1` enables metered provider calls. It should not be used casually as a routine local gate.

## Open issues and follow-up

### 1. Live SEO Keyword Ideas provenance

The metered SEO test reached a real report and observed successful provider paths for Keyword Suggestions, Keyword Overview, and organic SERP. It did not observe `dataforseo_labs/google/keyword_ideas/live`, so the test failed its provenance assertion.

Keyword Ideas is currently an optional lookup: supported HTTP, JSON, or operation failures are logged and omitted from successful-source provenance. The current evidence does not establish whether this was a transient provider failure, an account or request limitation, or a defect. Do not weaken the assertion merely to make the test green. Capture the provider response and application log without secrets, decide whether Keyword Ideas is required or optional product behavior, and align the contract and test with that decision.

Because the assertion stopped the scenario, its later AEO, approval, angle, generation, and hierarchy checks were not exercised in that run.

### 2. Anonymous legacy image-route diagnostic

A proposed all-route authorization sweep sent an anonymous request to:

```text
POST /api/v1/ai/campaigns/{campaignId}/render-images
```

The concrete GUID request returned 404 rather than the sweep's expected 401, while a known campaign route returned 401. The test-only endpoint recorder also did not observe a matched endpoint for that request. This is an unresolved routing/metadata observation, not a demonstrated authorization vulnerability. The unfinished sweep was removed from the retained suite after three local attempts.

Follow-up should first determine whether the route is intentionally legacy/unmapped, conditionally registered, or represented differently by endpoint metadata. Only then should a generalized authorization sweep define acceptable status behavior.

### 3. Final combined browser run

Run all eleven Playwright scenarios together after deciding whether to include the metered SEO test. Preserve the JSON report and distinguish:

- real browser-to-API workflows;
- API calls made directly by test setup;
- mocked external or expensive boundaries; and
- live provider calls.

Focused passes do not rule out shared-state, ordering, or combined-suite timing failures.

### 4. Remaining route and screen coverage

Review the 25 routes not observed in the latest API interaction report and the three observed routes without a success response. Prioritize by user impact and risk rather than trying to make the counter reach 100 percent. Known areas that still need deliberate coverage include Git publishing, broker publishing and queue operations, external sign-in browser completion, image variant downloads, latest media upload retrieval, and some SEO/scout/transcription paths.

Complete a control-level browser inventory for Campaigns, New Campaign, Mill Floor, Image Studio, SEO, Brands, Front Page, and the error/empty states. Existing component tests and route observations are useful evidence but do not substitute for that pass.

### 5. Web and Mac Catalyst runtime validation

The normal solution filter builds the Web client but excludes the desktop shell. This audit did not establish a fresh, recorded Mac Catalyst runtime pass or compare the bundled RCL static-asset hashes after these changes. Before release, build and run both shells and confirm that the desktop bundle contains current `Castmill.UI` assets:

```bash
dotnet run --project src/Castmill.Web
dotnet build src/Castmill.Desktop -f net10.0-maccatalyst
dotnet build src/Castmill.Desktop -t:Run -f net10.0-maccatalyst
```

### 6. Test data and artifacts

Browser resource fixtures generally delete created campaigns, Brands, and settings, but generated development user accounts may remain. Periodically clean the development database using a deliberate local process. Keep JSON reports, screenshots, traces, TRX files, and generated bundles under ignored artifact locations; do not commit credentials or captured provider payloads.

## Release interpretation

The retained changes have strong focused coverage and a green 1,000-test .NET run. They fix the bounded Tech Edit failure mode and the cross-origin export filename issue. They do not yet provide full interaction sign-off: the combined browser suite, live SEO provenance decision, legacy image-route diagnosis, remaining route prioritization, and recorded Web/Catalyst runtime checks are still open.

## Follow-up progress

Work resumed later on 2026-09-07 and closed or narrowed four of the five release-signoff gaps.

### SEO provenance root cause fixed

The missing Keyword Ideas provenance was not evidence of a provider rejection. The SEO research
agent exposed a function named `keyword_ideas`, but that function called
`GetSuggestionsAsync`, so the agent could never reach
`dataforseo_labs/google/keyword_ideas/live`. The agent now exposes separate
`keyword_suggestions` and `keyword_ideas` tools, with the latter calling
`GetKeywordIdeasAsync` for the complete source-grounded seed set. Its prompt explicitly requires
both phrase-match suggestions and category-adjacent ideas, and the agent regression verifies both
provider paths.

The live scenario was rerun with `CASTMILL_E2E_LIVE=1` and passed both independently and as part
of the complete browser suite. It verified real phrase-match Suggestions and category-adjacent
Keyword Ideas provider paths, answer-engine provenance, later content generation, persisted parent
ownership, and the rendered ApexCharts/ApexTree hierarchy. For this repository, configured paid
providers are expected test dependencies: release validation should run the live scenario rather
than skip it solely because the calls are metered.

### Legacy image-route diagnostic resolved

`POST /api/v1/ai/campaigns/{campaignId}/render-images` is mapped and protected by
`TenantAllowed`. The earlier anonymous 404 is reproducible only when the sweep omits the required
JSON body and content type: endpoint selection falls through before authorization. An anonymous
request with a valid JSON shape reaches the endpoint and returns 401. A retained API regression now
pins that behavior. Future authorization sweeps must send a request shape accepted by endpoint
selection before interpreting the response as authorization evidence.

### Combined browser and shell validation

The first combined non-metered browser run was interrupted when macOS suspended network I/O;
Chromium recorded `net::ERR_NETWORK_IO_SUSPENDED`, and a nominal 30-second request timeout was
suspended for about ten minutes. The three affected scenarios passed immediately under
`caffeinate`, followed by a complete run:

| Browser result | Count |
| --- | ---: |
| Passed | 10 |
| Failed | 0 |
| Skipped (metered SEO) | 1 |

The replacement JSON report is
`artifacts/test-results/final/playwright-nonmetered-final.json`.

After the live-path fixes, the complete live-enabled suite also passed:

| Browser result | Count |
| --- | ---: |
| Passed | 11 |
| Failed | 0 |
| Skipped | 0 |

The final JSON report is
`artifacts/test-results/final-followup/playwright-live-combined-final.json`.

The same follow-up built and launched the Mac Catalyst app successfully with zero warnings or
errors, then closed it after the startup smoke test. SHA-256 hashes for the bundled `views.css`,
`castmill-editor.js`, and `castmill-apextree.js` exactly matched the current `Castmill.UI` source
assets. The combined Playwright run also exercised the freshly built Web shell.

### Final regression and route inventory

The complete non-desktop solution now passes 1,006 tests: 607 API, 386 UI, and 13 media. Focused
success-path checks were also added for latest media-upload retrieval and image-variant download.
The final regenerated interaction report contains 178 registered routes, 155 observed routes, and
152 routes with at least one success response. Its preserved copy is
`artifacts/test-results/final-followup/api-interactions.json`.

The 23 unobserved routes remain concentrated in these areas, in priority order:

1. Git repository configuration and GitHub preview/publish/readback.
2. Broker readiness, test, queue, cancellation, and related publishing behavior.
3. External sign-in browser completion and method-specific start variants.
4. Scout, transcription, direct SEO research/report retrieval, Brand lookup, and the development
   blob/test routes.

The three observed routes without a success response are the intentionally wrong-method external
auth start request (405), an absent campaign-sharing lookup (404), and invalid or missing campaign
image uploads (400/404). They should be reviewed by user impact rather than changed solely to move
the coverage counter.

### Live YouTube generation resilience

The live flow exposed three model-output variants that the original YouTube validator did not
handle safely: fields with the wrong JSON primitive type, display timestamps such as `00:10`
under `timestamp`, `time`, or `startTime`, and compact YouTube chapter lines such as
`0:10 Build versus buy`. The validator now returns actionable failures instead of throwing
`InvalidOperationException`. The pipeline performs one bounded corrective audit after a
deterministic rejection, and the normalizer converts only explicit display timestamps and titles
into the canonical numeric `startSeconds` schema. It does not invent chapter boundaries.

The browser regression records sanitized YouTube stage metadata and response excerpts on failure,
which keeps future live-provider schema drift diagnosable without exposing credentials. Focused
YouTube coverage passes six tests, including the corrective audit and every observed timestamp
shape.

### Current release interpretation

The live provider, combined-browser, legacy image-route, Web shell, and Mac Catalyst validation
gaps are closed. The remaining audit work is breadth rather than a known release defect: prioritize
the 23 unobserved routes and finish a deliberate control-level inventory for the listed screens,
especially Git/broker publishing, external sign-in completion, scout/transcription, direct SEO
routes, and empty/error states.
