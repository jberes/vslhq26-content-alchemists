# Plan — source media, a first-class image editor, and a grounded Tech Edit

Date: 2026-09-05. Status: **BUILT** — the owner approved every phase the same day; all of it shipped
under ADR-054/055/056/057 and ADR-F54/F55/F56 with 363 UI + 460 API tests green. The sections below
are kept as the design rationale; "Shipped" notes mark where the implementation differs.

## 0. What the code actually does today (findings)

| Question | Finding (file) |
|---|---|
| Where is the Mill Floor audio/video? | **Desktop local media never leaves the machine and its path is not persisted.** `DesktopMediaPipeline` transcribes the picked file with Whisper and only the transcript segments are sent to the API; `PickedMedia.Path` lives in `LastPicked` for the session. The Mill Floor "player" is a decorative duotone box (`cm-source__player`, `aria-hidden`), there is no playback. **Cloud uploads persist**: `MediaUploadEndpoints` stores the file as a private `Asset` at `tenants/{tenant}/media/{uploadId}/{name}` (block-blob, resumable, 2 GB), transcribes, and keeps the blob (deleted only on cancel). `SourceAsset` already has `OriginalUri`/`BlobPath` columns. |
| Is Tech Edit wired? | **Yes, end to end.** `POST /ai/campaigns/{c}/artifacts/{a}/tech-edit` → `AiOrchestrator.RunTechEditAsync`: same-schema-in/same-schema-out, re-validated by the pass-1 validator, snapshots a `tech-edit` revision, version++. Provider = `chat-tech-edit` alias; with `TechEditKey` (Anthropic) stored it crosses model families, otherwise Foundry. |
| Can it consult my RAG? | **Already built, unconfigured.** `KnowledgeBaseClient` posts `{ <QueryField>: question }` to `KnowledgeBase:BaseUrl + QueryPath` with the `KnowledgeBaseToken` secret, expects `{ output, title, citations[{url,title}], confidenceLevel, suggestions }` (extra fields ignored) and injects it as a "Knowledge base" block. The Focus checkbox "Consult knowledge base" appears when `/ai/status` reports it ready. |
| Do Regenerate and Tech Edit both take Steering? | **Yes.** Both pass `_steering` (`FocusView.RegenerateAsync` / `TechEditAsync`); the orchestrator prepends `WithContentType(campaign, steering)` and `BuildPrompt(..., steering, evidence, brand, kind)`. |
| Why are images wrong? | Four causes, two fixed today. (1) Providers paint 1536×1024 for a 16:9 slot; `ImageComposer.CentreCrop` loses ~11% of top/bottom — the "safe margin" is prompt advice the model ignores. (2) The auto prompt pasted up to 5,000 chars of the artifact's **raw JSON** into the prompt — **fixed** (`ContentDigest`). (3) The prompt was invisible in Auto mode — **fixed** (preview endpoint + drawer/lightbox display). (4) The composed prompt stacks six partly contradictory rule blocks (76% vs 60% safe zones; "prefer keyword wording in text" vs "render no new text") — Phase 2 below. |
| "This campaign only" on The Wire? | It was a Pipeline-only toggle bound to whichever campaign the rail last opened. **Replaced** by a campaign selector in the Wire header that scopes both projections (ADR-F54). |
| gpt-image-2 via Foundry vs OpenAI direct? | Same weights. Differences are operational: Foundry's content filters block real-person faces (`moderation_blocked`) and MAI refuses faces outright; OpenAI direct exposes `moderation: "low"` and accepts `input_fidelity=high` on models that support it. Both providers plus Nano Banana are **already built** behind their own credential slots (`OpenAiImageKey`, `NanoBananaKey`, Settings → Credentials). Store a key and it appears in the model picker; the new **Compare N models** button renders the same prompt on every ready model in one run. |

## 1. Source media (Mill Floor)

**Decision proposed: local-first, explicit opt-in upload, re-linkable.**

1. **Persist the local link.** `SourceAsset` gets `LocalPath`, `ContentHash` (SHA-256 of the first/last 4 MB + size, cheap), `MediaAssetId` (nullable → the cloud copy). Desktop ingest writes the path + hash; the API stores them as opaque strings (never dereferenced server-side).
2. **Real player on the Mill Floor.** `IMediaPipeline.OpenLocalMediaAsync(path)` returns a shell-local URL: Catalyst serves the file through the BlazorWebView's custom scheme handler (`app://media/{hash}`), so `<video>/<audio>` plays it with the transcript scrubber wired to `currentTime`. Web: plays the SAS URL when a cloud copy exists, otherwise shows "Upload to play here".
3. **Re-link.** If the file is gone/moved: "Locate file…" → hash match confirms it is the same recording; a folder can be registered per workspace (`Settings → Media folders`) so re-links are searched automatically.
4. **Optional upload.** "Upload to Azure" on the source card reuses the existing resumable `MediaUpload` path (block blobs, pause/resume) and sets `MediaAssetId`; from then on every machine (and the clip worker) can read it. Retention = the existing `ExpiresAt` policy, surfaced in the card.
5. **Web stays upload-only** (it already is).

Effort: ~2 days. Migration: one (`SourceMediaLinks`). Tests: isolation test for the new columns, hash re-link unit test, bUnit player states.

## 2. Image creation and editing — a first-class editor

### Phase 1 — render at the slot's aspect, stop cropping meaning (1–2 days)
- **Per-provider native aspect.** MAI accepts arbitrary `width/height` (≥768 each, ≤1,048,576 px). *Shipped correction:* 1280×720 is NOT legal (720 < 768), so MAI paints 1365×768 — the same 16:9 to within a pixel, so the crop removes nothing but rounding; 1600×840 exceeds the budget and paints 1365×768 too (the 768 floor caps the ratio near 16:9). 1024×768 and any slot ≥768 on both edges within 1 MP render exactly. Nano Banana takes `imageConfig.aspectRatio: "16:9"` natively. gpt-image only emits 3 frames, so its 16:9 output always crops. `IImageModelCapabilities` grows `SupportsExactSize` / `SupportsAspect`, `ImageRenderer` chooses the frame per provider, and the picker shows a **"crop loss n%"** badge per model so the choice is informed.
- **Saliency crop instead of centre crop** for the providers that must crop: `ImageComposer` scores candidate windows by edge/entropy density and skin-tone/face heuristics (SkiaSharp, no model call) and crops where the content is.
- Studio copy already tells the truth about the frame and crop (shipped).

### Phase 2 — one coherent prompt (1 day)
Replace the six appended blocks with a single `ImagePromptComposer`: per-kind brief (thumbnail / header / inline / social) ≤ 250 words, brand style, *one* safe-area rule sized from the real crop, *one* text rule ("no text" — headlines are composited), reference note. Remove the keyword-in-text hint (it contradicts "no text"; keywords steer the *composited* headline instead). The preview endpoint reads from the composer, so what you see is what is sent. Rewrite `ImagePlanTests` prompt assertions accordingly.

### Phase 3 — overlay text editor, PowerPoint-style (3 days)
- **Model:** `OverlaySpec` JSON on the slot: text boxes with `x,y,w,h` as ratios, font family/size(ratio)/weight/colour/alignment, band `{ colour, opacity, padding, radius }`, optional logo box.
- **UI:** the lightbox canvas becomes an editor: drag/resize handles, inline text edit, band toggle, font/colour pickers from brand tokens, snap to safe area, live preview in the browser (Canvas 2D).
- **Server:** `ImageComposer.Composite(spec)` renders the same spec deterministically with SkiaSharp so the published image matches the preview pixel-for-pixel. Ship a licensed condensed face (the `Castmill:OverlayFontPath` stub is still empty — Barlow Condensed, OFL).
- Applies to every slot kind, not only thumbnails; composite stays free (no model call).

### Phase 4 — region edits: "replace the background", "add this face" (3–4 days)
- **UI:** rectangle/lasso mask on the lightbox canvas + instruction + optional reference from the brand kit.
- **API:** `POST variants/{id}/edit { maskPng, instruction, referenceAssetIds }` → new take with lineage (`SourceVariantId`), reusing the take gallery. Provider mapping: gpt-image `edits` with `mask`; MAI `images/edits` (exactly one image — composite the mask into alpha); Gemini image+instruction (no mask param; the mask is described as a region). Faces: gpt-image via OpenAI direct is the only path that reliably accepts a real-person reference; the UI says so up front instead of failing after 60 s.
- "Choose references…" is disabled when the campaign's brand has no kit assets — the button text should say "No kit assets yet — add on Brands" (small fix, Phase 4 start).

### Phase 5 — lightbox completeness (shipped + small)
Shipped today: **Regenerate** (same prompt, same model), **Delete forever**, **Compare N models**, prompt shown per take, deep link from Focus **Edit**. Remaining: side-by-side A/B of two takes, and a lineage filmstrip.

## 3. Tech Edit — skills, MCP servers, RAG; the accuracy plan

Goal: 100% accurate technical content. The lever is **grounding + verification**, not a bigger model.

### Options (Claude API surface, verified against the current API reference)
| Option | What Castmill owns | Skills / MCP | Fit |
|---|---|---|---|
| **A. Claude Messages API + Tool Runner (recommended)** — the API hosts the agent loop in-process (`client.Beta.Messages.ToolRunner` in the C# SDK), tools = KB query, brand-domain web fetch, code-sample compile check | Loop, tools, validation seam (unchanged) | **MCP connector**: `mcp_servers=[{type:"url", url, name}]` + `tools=[{type:"mcp_toolset", mcp_server_name}]`, beta `mcp-client-2025-11-20`. **Agent Skills**: upload SKILL.md bundles via `client.skills.*` and attach with `container.skills` + `code_execution_20260521` | Keeps the same-schema-in/out contract and the pass-1 validator; every hop is auditable in `/ai/log`. Model: `claude-opus-5`, adaptive thinking, effort `high`. |
| B. Managed Agents | Agent config + results | Skills + MCP native, hosted sandbox | Less code, but validation/citation enforcement moves outside the seam; sessions are stateful and priced per session. Right if the Tech Edit grows into multi-step research with file work. |
| C. Claude Agent SDK / Claude Code subprocess | A prompt | Uses the user's local skills/MCP | Desktop-only, not shareable per brand. Not recommended for a product feature. |

**Recommendation: A**, with B as the upgrade path if research fan-out is needed.

### Brand "Knowledge" tab (new)
- **Skills:** upload SKILL.md (+ files) per brand; stored as `BrandSkill` (blob + hash); attached to Tech Edit and, when "Technical piece" is on, to initial generation.
- **MCP servers:** name, URL, auth header (stored via `UserSecrets`, never in the row), allow-list of tools; health-checked on save. Attached with the MCP connector.
- **Knowledge base (RAG):** move from global `KnowledgeBase:*` config to per-brand `BrandKnowledgeEndpoint` (URL, query field, token secret) — the Infragistics RAG plugs in here with no code change if it returns `{output, citations}`; otherwise a 20-line adapter.
- **Verification policy:** "every technical claim must cite a KB/MCP result or a fetched brand-domain URL; unverifiable claims are flagged, not rewritten." Enforced by extending `Validate()` with a claims-with-citations check for kinds marked technical.

### Sequencing
1. Per-brand RAG endpoint + technical brief (item 4) — 1 day, immediately useful with the Infragistics RAG.
2. MCP connector + tool runner loop in `RunTechEditAsync` — 2 days.
3. Skills upload + attach — 1 day.
4. Claims-verification validator + Focus "Verification" panel listing every claim, its source, and unverified ones — 2 days.

## 4. Steering and the technical brief

Both actions already read Steering (finding above). Add an optional **Technical brief** per artifact (collapsible "Technical depth" section in the Producer rail): persisted on the artifact (`TechnicalBriefJson`: product/version, APIs, constraints, must-mention, must-not-claim), fed to *initial generation*, *Regenerate whole* and *Tech Edit*, and used as the KB query seed. Brand templates can carry a default brief per kind. Off by default; a campaign that never opens the section behaves exactly as today.

## Shipped 2026-09-05 (second session, all phases)

- Phase 1: `IImageProvider.FrameForAsync`, exact-size requests, `ImageComposer.ContentAwareCrop`, per-model crop badge in the studio.
- Phase 2: `ImagePromptComposer` replaces the six stacked blocks; `ImagePromptRules` is the single rules block.
- Phase 3: `OverlaySpec` on the slot, `PUT/DELETE image-slots/{id}/overlay`, `ImageComposer.ComposeOverlay`, the lightbox overlay editor (drag/resize, size, weight, align, colour, band).
- Phase 4: `POST variants/{id}/edit` with a mask, `IImageProvider.EditAsync` on Foundry (both dialects), OpenAI direct and Gemini; "Select region" tool in the lightbox.
- Phase 5: A/B compare and the take-history strip.
- §1 media: `LocalPath`/`ContentHash`/`MediaAssetId`, desktop `LocalMediaServer`, Mill Floor player with seekable transcript, Locate file…, Upload to Azure. *Not built:* registered media folders for automatic re-link (Locate file… covers the case).
- §3 knowledge: Brand Knowledge tab (RAG endpoint, skills, MCP servers), `AnthropicMcpClient`, claims verification with the Focus Verification panel.
- §4 technical brief: per-artifact, fed to generation and Tech Edit, KB query seed.
- Backlog: Wire day lanes (72 px, gapped), Debug desktop default to localhost, Choose File hint.

## Backlog (owner requests, not yet scheduled)

- **The Wire — day rows need breathing room.** Empty and collapsed day rows are 36 px high
  and sit flush against each other (`.cm-run-show__day--empty`, pinned by WirePageTests), so
  a quiet week reads as a jammed list. Target: a real lane height for every day (~72 px, day
  numeral with room above and below), a gap between day rows, the "nothing scheduled — drop
  here" copy vertically centred, and the weekend collapse visibly lighter rather than shorter.
  Re-pin the test to the new geometry. Owner: "this needs to look really nice — not so jammed
  up." (2026-09-05)

- **Kit upload — Choose File looked disabled.** Root cause: the file picker is gated on the
  description field (the label becomes prompt text) with no visible reason. Shipped: the hint
  now states the rule and the picker unlocks as you type; pinned by
  `ImageStudioKitPickerTests.Choose_file_says_why_it_is_locked_and_unlocks_once_a_description_is_typed`.
  Still open: choose-file-first with the label asked afterwards, drag-and-drop onto the kit
  row, and a progress bar for the 20 MB upload. (2026-09-05)
- **Running against the local API.** The desktop shell defaults to the production API unless
  built with `-p:CastmillApiBaseAddress=https://localhost:7105/`; the dev header now shows an
  `API · local/REMOTE · host` chip. Consider a Debug-default of localhost so a plain
  `dotnet build` can never point a dev bundle at production. (2026-09-05)

## Shipped in this session (for reference)
- ADR-054: variant prompt in the API, `GET image-slots/{id}/prompt-preview`, `ModelAliases` compare mode (concurrent renders, one run), `ContentDigest` auto prompt.
- ADR-F54: Wire dark mode, Wire type scale (11–18 px tokens), campaign selector.
- ADR-F55: dashboards/campaign views fill the frame; wider studio drawer.
- Focus: **Edit** beside Download on every image card → studio drawer + lightbox for the placed take.
- Studio: honest size/crop copy, prompt preview, per-take prompt, Regenerate, Compare.

## Addendum — agents (2026-09-05, evening) — BUILT
- ADR-058 / ADR-F57: three bounded tool-loop agents, see [agents.md](agents.md).
  - Tech Edit verifier: `ask_knowledge_base` / `fetch_source` (cited https hosts only) / `search_skills`; `ClaimCheck.Quote`; `agent: verifier (…)` in `KnowledgeAttached`.
  - Image art director: `GenerateVariantsRequest.Critique` → studio **Art director pass** toggle → `RenderBatchAsync` reject/re-render loop; verdict on `SteeringNote`.
  - SEO research agent decorates `SeoResearch`; metrics only from DataForSEO tool results.
- Prompts: `Generators.AeoGuidance` on every reader-facing kind (answer-first, question headings, FAQ, keyword placement, YouTube description rules, syndication canonical).
- Config: `Ai:Agents:{TechEditVerifier,ImageCritic,SeoResearch}` (structure in the committed template).
- **Follow-up (same night) — BUILT, ADR-059 / ADR-F58:** DataForSEO `ai_keyword_data` + `llm_mentions` in the deep report; `POST /seo/distribution` Published page check (OnPage instant crawl judged in code, referring domains, Content Analysis title mentions) with a panel on the SEO desk; `chat-audit` (gpt-5.6-sol) probed with an image — vision confirmed; tiptap 3.31.3 (npm audit clean).
- Backlog: critic for compare-mode takes; scheduled re-checks of the Published page check with diffs.

