# Design QA — Brand Asset Kit, Image Studio and AEO tabs

## Visual sources

- User-provided generated-image screenshot: essential headline and lower UI panels were clipped
  at the canvas edges. This is an output-quality reference rather than a screen to clone.
- Implementation captures: `/tmp/castmill-brand-asset-kit.png`,
  `/tmp/castmill-image-studio.png`, and `/tmp/castmill-aeo-tabs.png`, captured from the local
  Blazor app at 1280×720 during the non-metered browser regression.

## Comparison findings

| Severity | Count | Finding and resolution |
|---|---:|---|
| P0 | 0 | No broken core workflow, inaccessible control, or unreadable report content. |
| P1 | 0 | Asset type state, prompt mode, selected chips, and active AEO engine are visually explicit and keyboard-accessible. |
| P2 | 0 | Spacing, borders, typography and responsive widths follow the existing Castmill tokens. The AEO tab strip and response panel use the full report width; tab widths distribute across every available engine. |

The generated-image failure is addressed server-side on both generation paths: the final
instruction block now names the exact slot dimensions and reduced aspect ratio, reserves the
middle 76% for complete content, prohibits partial panels/rows, and tells the model to omit
secondary copy rather than clip or shrink it. Those constraints are appended after brand and
steering text so later instructions cannot override them.

## Functional evidence

- Asset type changed from Background to Face and the card moved groups in place.
- Prompt mode changed Auto → Manual with no second campaign-preview request.
- Constraint chip changed to an `Applied` selected state and persisted through the slot PATCH.
- AEO tab selection changed the active full-width panel and rendered headings, lists and strong
  text from sanitized Markdown.

## Final result

**Passed** — P0: 0, P1: 0, P2: 0.
