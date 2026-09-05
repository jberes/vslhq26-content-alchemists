# Castmill agents

Castmill's model calls were one-shot until 2026-09-05: one prompt, one JSON answer, validated
and stored. Three places now run a **bounded tool loop** instead — a model that can call a small
set of tools, look at what came back, and decide what to do next, inside a hard budget. Each
agent is a decorator around the prior one-shot path and degrades back to it whenever it cannot
run, so nothing that worked before depends on an agent working now (ADR-058).

All three use the same machinery: `Microsoft.Extensions.AI` function invocation
(`ChatClientBuilder(client).UseFunctionInvocation()`), tools declared with
`AIFunctionFactory.Create`, a per-run tool budget, a `trace` of every tool call, and a
`PromptLog` entry (kind suffix `-verify`, `-critic`, or `seo-research-agent`) so the run is
auditable in the dev prompt log.

| Agent | Where it runs | Model alias | Tools | Budget (config) |
|---|---|---|---|---|
| Tech Edit verifier | after every Tech Edit that returns claims, when a knowledge base exists | `chat-tech-edit` (Anthropic `claude-opus-5` when the Tech Edit key is stored, else Foundry) | `ask_knowledge_base`, `fetch_source`, `search_skills` | `Ai:Agents:TechEditVerifier:MaxToolCalls` = 12 |
| Image art director | Image Studio generate with **Art director pass** on | `chat-audit` (must be a vision-capable deployment) | none — it judges the image bytes | `Ai:Agents:ImageCritic:MaxRounds` = 2, `AcceptScore` = 70 |
| SEO research agent | SEO Targets research, when DataForSEO is configured | `chat` | `keyword_ideas`, `keyword_metrics`, `serp_snapshot`, `people_also_ask`, `knowledge_questions` | `Ai:Agents:SeoResearch:MaxToolCalls` = 14 |

Each agent has an `Enabled` flag under the same `Ai:Agents:*` section. Setting it to `false`
restores the previous behaviour exactly.

---

## 1. Tech Edit verifier — `TechEditVerificationAgent`

**Problem it solves.** The Tech Edit (ADR-056) asks the editor model to return a `claims[]`
list: the technical statements it asserted and where each came from. Before the agent, a claim
with an http(s) `sourceUrl` counted as *verified* — the model's word. Nobody had checked that
the page exists, let alone that it says what the claim says.

**What it does.** After the edited artifact has passed validation and been written, the
orchestrator hands the claims list to the verifier. The verifier prompts the Tech Edit model as
a fact-checker with three tools:

- `ask_knowledge_base(question)` — the brand's RAG endpoint (or the workspace gateway). Every
  citation URL the answer returns is added to the fetch allow-list.
- `fetch_source(url)` — downloads a page and returns its readable text (scripts, styles and
  markup stripped, capped at 6,000 characters). **Only https, and only hosts that a claim, the
  knowledge base, or the brand's campaign-context links already cited** — the model cannot point
  the server at an arbitrary URL. Fetches use a dedicated named `HttpClient` with a 20 s
  timeout and a 2 MB cap.
- `search_skills(term)` — greps the brand's skill files that apply to the artifact kind and
  returns the matching lines with one line of context.

The model replies with a verdict per claim: `verified`, `sourceUrl`, `quote`, `note`.

**The policy is in code, not in the prompt.** `Merge` decides the final state: a claim is
*verified* only when the model said so **and** supplied a quote **and** an http(s) source. A
"verified" verdict without a quote stays unverified. Claims the model did not answer keep their
prior state. The Focus view's Verification panel shows the quote and the source next to each
claim so a producer can click through.

**Result.** `TechEditResult.Claims` now carries `Quote`; `KnowledgeAttached` gains a line such
as `agent: verifier (4 tool calls, 3/3 verified)` or `agent: verifier failed — …`. The edit
itself is never blocked or altered by the verifier: if the model is unreachable, the claims come
back as they were with the failure noted.

**When it does not run.** No claims returned; no knowledge base configured for the workspace and
none on the brand; `Ai:Agents:TechEditVerifier:Enabled = false`.

---

## 2. Image art director — `ImageCriticAgent`

**Problem it solves.** Image models paint text, cut subjects off at the crop edge, invent
product UI and clutter the headline zone. Producers found this by eye, one take at a time, then
re-rolled. Render → look → re-roll is exactly a loop, and the judging half can be a model with
vision.

**What it does.** When the producer ticks **Art director pass** in the studio's Variants row,
each take goes through `RenderOneAsync` in `ImageSlotEndpoints` like this:

1. Render the take as before (same composer, same `ImagePromptRules`, same provider frame).
2. Send the WebP plus the brief to the critic. It checks, in order: subject present and not cut
   off; painted text; headline zone calm enough for the composited headline; product-UI
   fidelity; craft. It returns `score` (0–100), `accept`, `defects[]`, and **one `fix` sentence
   the image model can act on**.
3. If rejected and rounds remain, re-render with the brief plus
   `Art director's fix for the previous render: <fix>` and judge again. Keep the best-scoring
   take. At most `MaxRounds` re-renders per take, so a take costs at most 1 + `MaxRounds`
   renders — the studio says "up to 3× spend".
4. The final verdict rides on the take's `SteeringNote` (`art director 86/100 · 1 re-render`)
   and on the run's item list, so the gallery and the run drawer show why a take looks the way
   it does.

**Policy.** `Parse` overrules the model: an "accept" below `AcceptScore` is a rejection; a
"reject" above it is honoured (the model saw something). If the critic cannot run — the
`chat-audit` deployment has no vision, the JSON was malformed, a timeout — the first render is
kept and the note says `art director unavailable`. Generation never fails because the critic
did.

**The fix never rewrites the slot.** The re-render prompt is a render-time instruction; the
take's stored `Prompt` remains the producer's brief. `ImagePromptRules` still apply because the
retry goes through `ImageRenderer.RenderExactAsync` like every other render.

**Off by default.** The toggle is per generate and unchecked, because it multiplies spend and
latency. Compare mode, steer, region edit and the pending-images batch do not use the critic.

---

## 3. SEO research agent — `SeoResearchAgent`

**Problem it solves.** The fixed pipeline (`SeoResearch`) ran Seed → Expand → People-Also-Ask →
knowledge base in a straight line: one seed prompt, one expansion per keyword, one PAA lookup.
It could not notice that a phrase's SERP is owned by an AI Overview, price an unexpected phrase
a suggestion surfaced, or pull PAA for the phrase that actually turned out to be primary.

**What it does.** The agent gives the `chat` model the transcript and five tools over the
DataForSEO provider, with a plan to follow (seeds → expand → price → read the SERP for the
phrases it wants to win → questions) and a stopping rule (8–12 keywords and 6–10 defensible
questions, or the budget). Every tool call records the exact provider endpoint into
`ProviderLookups`, as the fixed pipeline did.

| Tool | DataForSEO endpoint | What the model learns |
|---|---|---|
| `keyword_ideas(seed)` | `dataforseo_labs/google/keyword_suggestions/live` | related phrases with volume, difficulty, CPC, intent |
| `keyword_metrics(keywords[])` | `dataforseo_labs/google/keyword_overview/live` | exact numbers for phrases it already has in mind |
| `serp_snapshot(keyword)` | `serp/google/organic/live/advanced` | top-10 titles and domains, AI Overview / featured snippet present |
| `people_also_ask(keyword)` | `serp/google/organic/live/advanced` (PAA + question-shaped related searches) | the questions people demonstrably ask |
| `knowledge_questions(topic)` | brand RAG gateway | what the brand's own readers ask |

**Policy.** `Assemble` builds the `SeoResearchResponse` from the plan **and only from tool
results**: a keyword the model lists gets provider metrics only if a tool returned that exact
term; otherwise it is marked `source: model` with no volume. A question may claim
`source: "paa"` or `"knowledge-base"` only if that tool actually returned it — otherwise it is
downgraded to `model`. The agent's `primaryKeyword` is moved to position 0 because the SEO
Targets UI treats position 0 as primary. Leftover PAA and knowledge-base questions the model
did not pick are appended so nothing real is lost.

**Fallback.** Disabled, DataForSEO unconfigured, model failure, or an empty plan → the fixed
`SeoResearch` pipeline runs exactly as before.

---

## How the search and answer-engine guidance reaches the writers

Agents find the targets; the generators have to hit them. Two prompt-level changes ship with
the agents:

- **`Generators.AeoGuidance`** is appended by `AiOrchestrator.BuildPrompt` to every reader-facing
  kind (blog, YouTube package, social, newsletter, email sequence, landing page, show notes) —
  never to internal kinds (transcript, image prompts, SEO reports, clip lists). It sits directly
  after the campaign's SEO targets block so the nearest instruction to the source is the target
  list plus how to write for it. The rules, distilled from current AEO/SEO practice:
  answer-first opening paragraph; each priority question as a question-phrased heading with a
  2–4 sentence direct answer; exact product/API names; a short FAQ on long-form pieces drawn only
  from the target questions; first-party specifics; primary keyword in title, first heading and
  first 100 words; for video, keyword in the first two sentences of the description, chapter
  titles that read as H2s, a pinned-comment question; and the syndication rule below.
- **Syndication.** Copies for Medium, LinkedIn, dev.to must link the original article or video
  in the first paragraph, say where it was first published, and the canonical must point home.
  Medium's import tool sets `rel=canonical` automatically; LinkedIn articles do not, so the
  first-paragraph link is the only signal there. Publish the copy only after the original is
  indexed (a day or two), or the copy can become the canonical in Google's eyes.

---

## DataForSEO: what Castmill uses and why

DataForSEO exposes eleven API families. Castmill uses the ones that inform *what to write and
whether it worked*; the rest are either off-mission or wait on a real site to audit.

**In use today**

| Family / endpoint | Where | Purpose |
|---|---|---|
| Labs `keyword_overview`, `keyword_suggestions`, `keyword_ideas` | research agent, fixed pipeline | volume, difficulty, intent, CPC; expansion |
| SERP `organic/live/advanced` | research agent, deep report | who ranks, AI Overview, featured snippet, People-Also-Ask |
| Labs `ranked_keywords`, `domain_rank_overview`, `serp_competitors` | deep report | the site's own footprint, authority vs competitors, who wins the keyword set |
| Backlinks `summary` | deep report | authority gap |
| AI Optimization `llm_responses` | AEO scorecard | asks ChatGPT/Gemini/Claude/Perplexity the target questions and records whether the brand is cited |

**Worth adding next (in priority order)**

1. **AI Optimization — `ai_keyword_data` and `llm_mentions`.** Search volume as seen by AI
   assistants and where the brand is already mentioned in LLM answers. Directly measures the AEO
   goal; would slot into the scorecard beside `llm_responses`.
2. **Content Analysis.** Mentions of the brand/product across the web with sentiment — the
   natural way to *track syndicated copies and backlinks to the video or core page* (Medium,
   LinkedIn, dev.to) once they are live.
3. **OnPage.** Crawl the published blog URL and confirm the AEO structure actually shipped:
   headings, schema (`FAQPage`, `HowTo`, `Article`), canonical, word count. Belongs in the
   publish step, after Git publish.
4. **Backlinks `referring_domains` / `anchors`.** Which syndication targets are passing
   authority to the original — closes the loop on the syndication guidance.
5. **Keyword Data (Google Ads)** for paid-intent volume when a campaign has a landing page.

Not planned: Business Data (reviews/local), Merchant (shopping), App Data — off-mission for
developer-tool content.

**What ranks a YouTube video vs a blog, and how the data maps.** YouTube ranks on intent match
and retention, then metadata; the research agent's PAA and SERP reads say what the *intent* is,
and the package generator puts the primary phrase in the first two sentences of the description
(the part YouTube weights), chapters as headings, and a pinned-comment question. Blogs rank on
answering the query completely and being cited; the AEO rules above are the blog's half. Both
benefit from the same target list, which is why targets live on the campaign, not the artifact.

---

## Models

- **Tech Edit + verifier:** Anthropic `claude-opus-5` when the Tech Edit key is stored — the
  highest-accuracy option for reading pages and deciding whether a sentence supports a claim.
  Without the key both fall back to the Foundry `chat-tech-edit` alias.
- **Art director:** `chat-audit` must be a **vision-capable** deployment (GPT-4o-class or
  better on Foundry). A text-only deployment produces `art director unavailable` on every take
  and the loop costs nothing extra.
- **SEO research:** `chat` — planning, not prose; a mid-tier model is enough because every
  number comes from the tools.

---

## Testing

- `tests/Castmill.Api.Tests/AgentTests.cs` — unit tests with a scripted tool-calling
  `IChatClient`: the verifier's quote-and-source policy, cited-host allow-list, HTML→text, the
  disabled path and the failure path; the critic's score policy, that it sends the image bytes,
  and that a blind critic keeps the take; the SEO agent's tool-only metrics, PAA source
  validation, primary ordering, and provider-less fallback.
- `tests/Castmill.Api.Tests/ImageCriticLoopTests.cs` — the loop through the real endpoint:
  reject → re-render with the fix → verdict on the take; toggle off → critic never asked;
  unavailable critic → first render kept.
- `tests/Castmill.Api.Tests/TechEditApiTests.cs` — the verifier is called with the editor's
  claims when a knowledge base exists and its verdicts ride out on `TechEditResult`; without a
  knowledge base it is never asked.
- `tests/Castmill.UI.Tests/ImageStudioPromptTests.cs` — the studio toggle is off by default
  and rides on the generate request as `critique: true`.

To watch an agent live: sign in, run a Tech Edit on a brand with a knowledge base, and open the
dev prompt log (`/dev/testbed` → prompt log) for the `-verify` entry; or tick Art director pass
in Image Studio and read the take's note in the gallery.
