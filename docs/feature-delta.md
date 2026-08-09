# Feature delta: PostFoundry → Castmill

Candidate features for Castmill derived from [post-foundry.md](post-foundry.md) — the
"as built" spec of the predecessor product. **Candidates, not commitments.** Each item is
something PostFoundry has that Castmill does not (or has only a thinner slice of); honest
partial-overlap notes are included where they exist.

Deliberately excluded: everything Castmill already matches or beats — two-pass blog audit +
Tech Edit, clip export with captions/9:16, brand-from-URL, starter templates, schedule
mirror + broker cancel, press-run narration, diarization, blog pre-publish validators,
image stubs + WebP publish-for-web, prompt-contract rules, optimistic concurrency, light
previews, soft-fail sections, keyboard shortcuts, breadcrumbs, readiness-with-reason.

---

## Group 1 — Image generation & product fidelity *(§7.7, §7.9, §7.14)*

1. **Reference-image generation** — an image model that accepts actual reference images
   (gpt-image-2 class). Castmill's `IImageRenderer` is text-prompt-only; faces/backgrounds
   steer by *description* only. The single biggest visual-quality delta.
2. **The product-fidelity rule** — a `product` asset category whose screenshots (up to 3)
   auto-attach to every image generation with reproduce-the-real-UI instructions.
3. **Auto vs manual prompt per image card** — Auto rebuilds from transcript/brief/current
   references each run (removing a face really removes it); Manual is verbatim.
4. **Thumbnail concepts before pixels** — N concept descriptions as a cheap artifact;
   generate only the chosen one.
5. **Generate-all-pending images** across every slot in one narrated pass (Castmill
   batches variants per slot only).

## Group 2 — SEO/AEO depth *(§7.6, §7.7, §9)*

*Castmill already has research-before-run, PAA, the keyword plan, targets-into-prompts and
the cluster map; these go deeper.*

6. **AEO scorecard** — query 4 answer engines (ChatGPT/Gemini/Claude/Perplexity, web-search
   on) with the audience-shaped question; per-engine cites/does-not-cite cards, citation
   chips, visibility %; failed engines excluded from the denominator.
7. **Competitor deep-dive** — backlinks summary + domain rank overview per SERP competitor;
   position footprint (#1 / #2–3 / #4–10); your row pinned.
8. **"Keywords you already rank for"** — ranked-keywords endpoint; frames angles as
   extend/defend vs duplicate.
9. **Authority-aware angle steering** — backlink gap vs best competitor quantitatively
   steers generation toward achievable (long-tail / low-KD) targets.
10. **Staleness badges** — derived `inputsStale` / `anglesStale` / `shareStale` signals
    ("Inputs changed — re-run analysis", "New SEO data since these angles").
11. **Content-angle regeneration loop** — angles regenerated *from the report* (gaps, PAA,
    uncited engines), each one-click seeding a blog. Castmill's cluster map + Scout are
    adjacent; the report→angles→seed loop with staleness is the delta.
12. **AI-Overview & featured-snippet surfacing** — Castmill already fetches advanced SERP
    for PAA but discards `ai_overview` / `featured_snippet`; surface them plus the
    "write to be excerptable" imperative.
13. **Angle-labeled, scored YouTube titles** with per-slot regenerate (A/B/C slots; angle
    taxonomy: seo / curiosity / how-to / problem-solution / thought-leadership).
14. **Suggested pinned comment generator** (references a concrete moment, ends with a
    question).
15. **Two-pass (outline → draft → self-audit) for the YouTube description**, audited
    against platform rules (125-char hook, hashtag hoist, chapter keywords). Castmill's
    two-pass is blog-only.
16. **Per-artifact grounding panel** — "Real search data informing this content" on the
    YouTube/blog screens (keyword badges, SERP list, AI-Overview badge, PAA), with an
    honest empty state when no provider is configured.

## Group 3 — Collaboration & sharing *(§5, §7.14)*

*Castmill is single-owner (ADR-011); this whole group is a strategic reversal, not
incremental work.*

17. **Project → org sharing** (tenant-wide read) with a Private/Public badge;
    corporate-domain classifier.
18. **Invite-by-name project members** — type-ahead user search, cross-tenant, removable.
19. **Asset-level sharing** (AssetShare) alongside project and settings shares.
20. **Settings/secret sharing** with explicit enumeration of which secrets become visible.
21. **M365/Entra sign-in + Graph avatars/identity chrome** — *listed for completeness
    only*: conflicts with ADR-010 (email+password, no external IdP) and the standing
    no-Azure-app-registration constraint.

## Group 4 — Structure & content model *(§2, §6.3, §7.3–7.5)*

22. **Artifact parent/child hierarchy** — a blog owns its own social posts and images.
    Castmill scoped image slots per-blog already; socials/email are campaign-level only.
23. **Per-blog social sets** + blog accordion tree with availability dots / approval
    checks / count badges.
24. **Multi-file sources** — several recordings combine into one campaign transcript with
    per-source labels.
25. **Placeholder blog seeded from the strongest angle** (renders the Generate panel and
    seeds its first generation with that angle).
26. **Summary screen/artifact** — persisted, editable executive summary + numbered key
    takeaways + keyword opportunities. Castmill's AI brief summary (run flow) is transient
    and never stored; no summary surface exists after the run.
27. **Campaign status lifecycle** (draft → ready) with a status badge, plus inline
    campaign rename in the header.
28. **Content-type field on the brief** (tutorial / product demo / webinar / thought
    leadership) steering every generator.

## Group 5 — Publishing & distribution *(§7.10–7.13, §7.8)*

29. **Live vendor publish queue** — the Wire reads Castmill's mirror; PostFoundry reads the
    Buffer account live (status tabs, cancel-for-good, channel resolution).
30. **Full composer** — multi-channel picker, per-channel truncation warnings + live
    preview, attach an image via short-lived link, next-slot/schedule/send-now, plus a
    local "Add to Queue" staging step per social post.
31. **Send a clip to the scheduler** (upload → hand the link to the broker).
32. **Per-blog Schema & metadata panel** — canonical URL builder, author/org identity,
    `VideoObject` schema, Combined / HTML-head / JSON-LD-only outputs with one-click copy.
    Castmill has Article JSON-LD only; FAQPage/VideoObject is roadmap 9.4.
33. **Export ZIP includes generated images** (Castmill's ZIP is markdown-only).
34. **Email `[YOUTUBE_VIDEO_URL]` placeholder convention** for ESP replacement.

## Group 6 — Models & steering *(§7.15, §7.14, §7.11, §8.4)*

35. **User-facing per-generator model overrides** (blog, audit, YouTube description,
    socials), provider inferred from the model name, OpenAI/Anthropic mixable,
    "(use default)" inheritance. Castmill's aliases are server-config only.
36. **Brand-voice distillation** — exemplar texts → a distilled style card
    (`distill_brand_voice`).
37. **Richer Brand "AI Context" document** — positioning, ICP, personas, competitors,
    differentiators, campaign priorities — injected into every text generator. Castmill's
    style card (voice/audience/tagline) is a thinner slice.
38. **Clip discovery instruction + review-state persistence** — user scouting directive;
    re-suggest replaces only un-reviewed candidates; lossless stream-copy fast path when
    neither captions nor reframe is on.
39. **Whisper model-size picker** (base … large-v3) at ingest, with first-use download
    progress.

## Group 7 — UX & platform patterns *(§6.2, §7.1, §10)*

40. **Prompt transparency in the chrome** — a header control showing the exact prompt
    behind every generation this session (Castmill has `/ai/log`; no UI surface).
41. **Prefetch-on-intent** — hover/focus on a campaign card prefetches its artifacts.
42. **Server-Timing instrumentation** (`auth` / `db` / `serialize`) on hot endpoints.
43. **Home pipeline card** — the 11-step visual of everything the product produces.
44. **In-place report-inputs panel** — edit the brief's SEO fields on the report page
    itself, with a re-run nudge (a second edit surface over the same data).

## Group 8 — Leapfrog opportunities *(§11 — PostFoundry's own admitted gaps)*

*Castmill can skip past PostFoundry here, not just catch up.*

45. **AEO trend over time** — snapshot reports per run instead of one root artifact;
    visibility trend charts.
46. **AEO prompt *sets*** — several question phrasings per keyword, with repetition for
    variance, instead of one generic question asked once.
47. **Exact-domain citation matching** (host-part comparison, not substring `Contains`).

---

Groups 1, 2 and 4 carry the most product weight. Group 3 reverses ADR-011/ADR-010 and
needs a deliberate strategic decision before any of it is built. Several group-7 items are
afternoon-sized.
