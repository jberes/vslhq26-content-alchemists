# Campaign Editing, Generation Resilience, and Release Session

- Date: August 26-27, 2026
- Repository: `jberes/vslhq26-content-alchemists`
- Branch: `main`
- Primary implementation commit: `c49bba20f798cb6e0938e57d9edd94c2bd2ef544`

## Summary

This session fixed two production generation failures, improved the campaign creation and
Focus mode workflows, completed keeper-image and download behavior across Web and Mac
Catalyst, produced a verified Mac build, pushed the implementation to GitHub, and deployed
the combined API/Web application to Azure App Service.

The final implementation provides:

- resilient Press Runs after an App Service `504` response;
- deterministic repair of malformed YouTube A/B/C title metadata;
- per-platform social output selection, with X selected by default;
- non-scrolling artifact selection in Focus mode;
- standard rich editing for YouTube descriptions and social posts;
- formatted clipboard copy with Mac Catalyst fallback behavior;
- a visible regeneration overlay over the current manuscript;
- bounded Focus mode layout and independently scrolling rails;
- keeper previews on their owning artifacts;
- authenticated full-resolution image downloads;
- browser downloads through normal Web download behavior;
- native Mac Catalyst downloads directly into `~/Downloads`;
- a verified Apple Silicon production bundle; and
- a successful Azure production deployment.

## 1. Desktop build investigation

### Windows target attempted on macOS

The initial command was:

```bash
dotnet build src/Castmill.Desktop -f net10.0-windows10.0.19041.0
```

The first failure was `NETSDK1100`, which suggested setting
`EnableWindowsTargeting=true`. A command-line probe cleared that error, but restore then
failed because the Windows target was conditionally excluded on non-Windows hosts. Making
the target visible allowed restore to proceed, but the build then reached the real platform
boundary: Windows App SDK invoked `XamlCompiler.exe`, which cannot run natively on macOS.

The temporary project-file experiment was reverted. The repository retains its intended
behavior:

- Mac Catalyst builds on macOS;
- the Windows target is included when MSBuild runs on Windows; and
- Windows x64 production output must be built on a Windows machine or Windows CI runner.

### Windows x64 command

Run this from the repository root on Windows PowerShell:

```powershell
npm ci
npm run build
dotnet workload install maui

dotnet publish src\Castmill.Desktop\Castmill.Desktop.csproj `
  -f net10.0-windows10.0.19041.0 `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:WindowsPackageType=None `
  -p:WindowsAppSDKSelfContained=true `
  -p:SkipEditorInterop=true `
  -o artifacts\Castmill-windows-x64
```

Expected executable:

```text
artifacts\Castmill-windows-x64\Castmill.Desktop.exe
```

This is an unpackaged self-contained build. MSIX packaging remains a later roadmap item.

### Mac production build

The Apple Silicon build command was:

```bash
dotnet build src/Castmill.Desktop/Castmill.Desktop.csproj \
  -f net10.0-maccatalyst -c Release -r maccatalyst-arm64
```

Final outputs:

- app bundle: `src/Castmill.Desktop/bin/Release/net10.0-maccatalyst/maccatalyst-arm64/Castmill.app`
- distributable zip: `artifacts/Castmill-macos-arm64.zip`
- zip size: approximately 36 MB
- final SHA-256: `bbd560cfdb31b5f03d7332b75222e63e430d5fd3c3c71f813446e89a8c322ec9`
- executable architecture: Mach-O 64-bit arm64
- signature: valid ad-hoc signature verified with `codesign --verify --deep --strict`

The source and bundled `Castmill.UI` `views.css` hashes were compared after clean builds to
ensure the Catalyst app did not contain stale RCL static assets.

The zip is gitignored. There is no `.pkg` or `.dmg` installer yet. The current zip contains
the `.app`, which can be dragged to `/Applications`. Notarized package distribution remains
a later packaging task.

## 2. Press Run 504 recovery

### Observed 504 failure

A Press Run stopped with:

```text
The server returned 504.
```

The panel showed partial progress such as `6 of 10 printed`, even though the server-side run
was designed to survive a dropped client request.

### 504 recovery cause

`PressRunService` reattached to a server run after `HttpRequestException`, but a gateway
`504` was translated by `ApiClient` into `ApiException`. That exception took the terminal
error path instead of the existing reattachment path.

### 504 recovery fix

`PressRunService` now treats server-side `5xx` responses as recoverable when it has already
observed a pollable run row. It calls `ReattachAsync`, follows progress until the run reaches
a terminal state, and reconciles the campaign board rather than freezing at the gateway
error.

Relevant files:

- `src/Castmill.UI/State/PressRunService.cs`
- `tests/Castmill.UI.Tests/PressRunServiceTests.cs`

## 3. YouTube A/B/C generation failure

### Observed YouTube failure

The YouTube package failed while other artifacts printed. The exact validation error was:

```text
titleOptions must be A/B/C and use three distinct supported angle-taxonomy values.
```

### YouTube metadata cause

The model returned a complete package but used non-canonical title metadata, such as an SEO
synonym or a duplicated angle. The validator correctly required exactly three ordered A/B/C
rows with distinct values from the supported angle taxonomy, but there was no deterministic
normalization step between the model audit and validation.

### YouTube normalization fix

The YouTube pipeline now normalizes title-option metadata before strict validation:

- slots are ordered and canonicalized as `A`, `B`, and `C`;
- common SEO and curiosity synonyms are mapped to canonical values;
- invalid or duplicated angle labels are repaired with unused supported fallback values;
- titles, scores, rationales, citations, and the rest of the package remain unchanged; and
- strict validation still rejects incomplete title sets and invalid content fields.

Relevant files:

- `src/Castmill.Api/Services/Ai/AiOrchestrator.cs`
- `src/Castmill.Api/Services/Ai/Generators.cs`
- `tests/Castmill.Api.Tests/AiGenerationTests.cs`
- `tests/Castmill.Api.Tests/YoutubeTitleOptionNormalizationTests.cs`

## 4. Granular social output selection

The New Campaign output recipe previously exposed one `Social set (6 platforms)` checkbox.
Selecting it always generated X, LinkedIn, Facebook, Instagram, Threads, and Bluesky.

The output recipe now exposes each social platform separately. X is selected by default,
matching the primary workflow, while other platforms can be enabled independently.

Compatibility behavior:

- new recipes persist actual `social-*` kinds;
- old saved recipes containing the legacy `social` bundle still restore all six choices;
- the initial Press Run sends only the selected platform kinds; and
- image prompts and thumbnail concepts continue to be included automatically.

Relevant files:

- `src/Castmill.UI/Pages/NewCampaign.razor`
- `tests/Castmill.UI.Tests/N27StartRunTests.cs`

## 5. Focus mode artifact selection and scrolling

### Artifact click auto-scroll

Selecting a social artifact changed the route through Blazor navigation. The application-wide
`FocusOnNavigate` then focused the manuscript heading, and the browser scrolled the page to
that heading.

Artifact selection now updates browser history with `history.replaceState` and loads the
artifact directly. The URL remains a valid deep link, but the selection does not trigger a
page-navigation focus cycle.

A real browser check confirmed the manuscript `scrollTop` remained `0` while selecting an X
post and the query-string artifact id updated correctly.

### Rail overlap

Sticky artifact category headers overlapped the first artifact row while the left rail was
scrolled. Sticky positioning was removed from those category bands. A browser geometry check
confirmed zero overlap after scrolling.

### Focus layout overflow

The Producer rail could grow beyond the viewport, particularly after an Image Studio to Focus
mode transition. The Focus grid now uses bounded `minmax(0, ...)` tracks, `min-width: 0`, and
independent vertical scrolling for the artifact rail, manuscript, and Producer rail.

Web checks were performed at 1600x900, 1280x800, and 1024x768. At every width, document and
body scroll widths matched the viewport width. The Image Studio to Focus round trip did not
create horizontal overflow.

Relevant files:

- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `src/Castmill.UI/wwwroot/css/views.css`
- `tests/Castmill.UI.Tests/ArtifactTreeTests.cs`

## 6. YouTube and social editing

An intermediate implementation rendered a large typed form for YouTube title options,
description, chapters, tags, scores, and rationales. Native-client review showed that this
was too form-heavy and unlike the editing experience for other content.

The final design uses the standard `RichEditor` for the paste-ready YouTube description and
for social copy.

### YouTube behavior

- The editor shows the description exactly as it should be pasted into YouTube.
- Chapters, links, and final hashtags already contained in the generated description remain
  in the editor.
- Saving patches only the package's `description` field.
- Scored title options, citations, audit results, validation metadata, tags, pinned comment,
  and unrelated JSON fields remain intact.
- Title A/B/C regeneration remains available in the Producer rail.

### Social behavior

- The editor shows the post body and its final hashtag line.
- Saving splits a final all-hashtag line back into the structured `hashtags` JSON array.
- Other structured metadata remains intact.

Other structured content kinds that do not yet have a safe prose round trip continue to show
a formatted read-only preview.

Relevant files:

- `src/Castmill.UI/Editor/ArtifactContent.cs`
- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `tests/Castmill.UI.Tests/YouTubePackageRenderTests.cs`

## 7. Formatted clipboard copy

Focus mode now provides a visible copy action. For YouTube it is labeled:

```text
Copy for YouTube
```

The clipboard service writes both:

- `text/plain`, suitable for YouTube and plain-text destinations; and
- `text/html`, preserving headings, paragraphs, links, emphasis, and list formatting in rich
  destinations.

The implementation remains behind `IClipboardService`, so components do not call browser
clipboard APIs directly.

Compatibility paths:

1. Prefer `navigator.clipboard.write` with `ClipboardItem` containing HTML and plain text.
2. If browser permissions or Mac Catalyst WKWebView reject that API, create an offscreen rich
   HTML container, select it synchronously, and use `document.execCommand("copy")`.
3. Restore the prior focus and selection with `preventScroll` to avoid moving the editor.

Relevant files:

- `src/Castmill.UI/Design/ClipboardService.cs`
- `src/Castmill.UI/wwwroot/js/castmill-clipboard.js`
- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `tests/Castmill.UI.Tests/CastmillUiTestContext.cs`
- `tests/Castmill.UI.Tests/ArtifactTreeTests.cs`

## 8. Regeneration feedback

Whole-artifact regeneration, YouTube title regeneration, and Tech Edit now hide and blur the
existing manuscript while work is in progress. A centered live status identifies the active
operation, for example:

- `Regenerating YouTube package...`
- `Regenerating title B...`
- `Applying the Foundry tech edit...`

The current take cannot be edited or selected through the overlay while the replacement is
being drafted and audited. Existing content returns when the request completes or fails.

The title-regeneration controls use a producer-specific two-column grid, so title C wraps to
a new row instead of clipping outside the Mac Catalyst Producer rail.

Relevant files:

- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `src/Castmill.UI/wwwroot/css/views.css`
- `tests/Castmill.UI.Tests/TechEditTests.cs`

## 9. Keeper images in Focus mode

`Kept` and `Placed` remain separate states:

- **Mark as keeper** records the preferred take without publishing it into content.
- **Place in slot** explicitly fills/publishes the slot and rewrites a blog image stub when
  applicable.

Campaign previews now carry the preferred keeper thumbnail and keeper variant id for each
slot. Focus mode displays the chosen image in the Producer rail before it is placed.

### Ownership behavior

Image choices remain attached to the content item that owns their slot. This matters when a
campaign has several YouTube packages or blogs: an image chosen for one package must not
appear on another package.

For older campaigns, Focus mode supports compatible legacy campaign-wide slots. A legacy
chosen image can replace an empty artifact-owned slot only when its slot kind matches the
selected artifact:

- `youtube-thumbnail` for YouTube;
- `blog-*` or `content-image-1` for blogs; and
- `social-card` for social artifacts.

During live verification, the apparent missing keeper was traced to artifact ownership. The
selected first YouTube package had empty slots; the visible chosen image belonged to the
second YouTube package. Selecting the owning package displayed the image and its Download
control correctly.

Relevant files:

- `src/Castmill.Api/Endpoints/CampaignEndpoints.cs`
- `src/Castmill.Core/Resources/ResourceDtos.cs`
- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `tests/Castmill.UI.Tests/ArtifactTreeTests.cs`

## 10. Image downloads

### API

A tenant-authorized endpoint returns the full-resolution stored image as a WebP attachment:

```text
GET /api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/download
```

The endpoint resolves the variant through tenant-filtered EF queries, reads the server-owned
blob path, and returns `image/webp` with a download filename. Clients never submit or resolve
an arbitrary blob path.

### Image Studio

The take lightbox includes `Download image`, which downloads the full-size take rather than
the gallery thumbnail.

### Focus mode

Hovering or keyboard-focusing a keeper image reveals a Download control over the image.
The control fetches the full-resolution keeper through the authenticated API.

### Web behavior

The Web client uses the existing object-URL download helper and an `<a download>` element.
The browser saves to its configured Downloads location. Safari-compatible delayed object-URL
revocation remains in place.

Live validation confirmed:

- HTTP `200`;
- content type `image/webp`;
- attachment `Content-Disposition` with a generated filename; and
- the success notification `Image saved to Downloads.`

### Mac Catalyst behavior

The desktop shell overrides `IFileDownloader` with `DesktopFileDownloader`. Because the
Catalyst app is intentionally unsandboxed for its ffmpeg workflow, it can write directly to:

```text
~/Downloads
```

The downloader strips directory components from server filenames and avoids overwrites with
names such as `image (2).webp`.

Relevant files:

- `src/Castmill.Api/Endpoints/ImageSlotEndpoints.cs`
- `src/Castmill.UI/Http/ImagesClient.cs`
- `src/Castmill.UI/Design/FileDownloadService.cs`
- `src/Castmill.UI/Pages/Campaign/ImageStudioView.razor`
- `src/Castmill.UI/Pages/Campaign/FocusView.razor`
- `src/Castmill.Desktop/Platform/DesktopFileDownloader.cs`
- `src/Castmill.Desktop/MauiProgram.cs`
- `tests/Castmill.UI.Tests/ImageStudioLightboxTests.cs`

## 11. Validation evidence

Docker Desktop was started only for the Testcontainers suite and stopped again afterward.
No servers, desktop applications, or background processes started for validation were left
running.

### Final automated gates

```text
Release solution build: 8 projects, 0 warnings, 0 errors
.NET tests:            633 passed, 0 failed
  Castmill.Api.Tests:  345 passed
  Castmill.UI.Tests:   275 passed
  Castmill.Media.Tests: 13 passed
Editor tests:           46 passed, 0 failed
Editor test files:       4 passed
Mac Catalyst build:      succeeded
Catalyst codesign:       passed deep/strict verification
git diff --check:        passed
```

Focused regressions covered:

- YouTube title taxonomy synonym and duplicate repair;
- YouTube outline/draft/audit generation and title regeneration;
- Press Run reattachment after gateway `504`;
- granular output-recipe persistence;
- artifact selection and rail state;
- YouTube description JSON round trip;
- formatted HTML and plain-text clipboard payloads;
- regeneration state behavior;
- Focus keeper rendering and legacy keeper fallback;
- authenticated full-image download;
- keeper versus placement semantics;
- Focus and Image Studio slot ownership; and
- CSS token/reference integrity.

### Client validation

Web client:

- authenticated against the local API with real campaign data;
- selected an X artifact without moving manuscript scroll position;
- completed Image Studio to Focus mode round trips;
- checked 1600x900, 1280x800, and 1024x768 viewports;
- confirmed no document-level horizontal overflow;
- confirmed the Producer rail owns its vertical overflow;
- confirmed YouTube uses the normal rich editor;
- confirmed `Copy for YouTube` is visible;
- confirmed artifact-category overlap is zero while scrolling;
- confirmed the owning artifact displays its selected image;
- confirmed Download appears on hover; and
- confirmed the download endpoint returns the expected attachment.

Mac Catalyst client:

- clean-built for `maccatalyst-arm64`;
- launched in both Release and local Debug validation configurations;
- inspected in a native 1352x848 window;
- confirmed current shared static assets were embedded;
- verified Producer title controls fit the native rail; and
- compiled and registered the native Downloads implementation.

## 12. GitHub delivery

Before committing, local `main` was one commit behind `origin/main`. The incoming commit only
changed `.gitignore` and VS Code launch/task configuration, so `main` was updated with a
clean fast-forward.

Implementation commit:

```text
c49bba20f798cb6e0938e57d9edd94c2bd2ef544
fix: improve campaign editing and generation resilience
```

The commit was pushed directly to `origin/main`. Local `HEAD` and `origin/main` were verified
to match, and the working tree was clean after the push.

## 13. Azure deployment

The existing deployment was validated before mutation:

```bash
./infra/deploy-appservice.sh validate
```

Validation passed for:

- subscription: `fd25223e-a9f8-488d-a4ff-0fa509c394c5`
- resource group: `rg-apps-demo`
- App Service: `azappdzrs2vrhk6ote`
- production URL: <https://azappdzrs2vrhk6ote.azurewebsites.net>

The code-only deployment was then executed:

```bash
./infra/deploy-appservice.sh code
```

Kudu deployment completed successfully in approximately 82 seconds. Post-deployment checks:

```text
GET /health     -> 200 {"status":"healthy"}
GET /health/db  -> 200 {"status":"healthy", ...}
App Service     -> Running
```

An anonymous request to the new image download route returned `401`, confirming the deployed
route exists and remains protected by the tenant authorization policy.

The deployed Web client reached the production sign-in screen successfully. No production
credentials or secret values were written to logs or committed.

## 14. Operational notes

- Previously failed YouTube artifacts are not recreated automatically. Use Press Run or the
  appropriate regenerate action after deployment.
- A `504` can still be emitted by the App Service gateway for a long request; the client now
  follows the durable server run instead of treating it as the end of generation.
- A keeper appears only on the content artifact that owns the image slot. Check which YouTube
  package or blog is selected when a chosen image appears missing.
- `Mark as keeper` does not publish or place the image. Use `Place in slot` when the image
  should become the slot's published asset.
- Browser downloads use the browser's configured download location. Mac Catalyst writes
  directly to `~/Downloads`.
- The Mac zip is a verified application bundle, not a notarized installer. A signed/notarized
  `.pkg` or `.dmg` remains future packaging work.
- Windows x64 output still requires a Windows build host because Windows App SDK's XAML
  compiler is Windows-only.
