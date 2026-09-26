# Self-hosted typefaces

All seven families are **SIL Open Font License 1.1**. The full licence text for each sits
beside the fonts as `LICENSE-<family>.txt`; OFL requires it to travel with the files.

Self-hosted rather than loaded from a CDN so the Static Web Apps CSP needs no third-party
font host (frontend §6, Security) and so the desktop shell works offline.

| File | Family | Weights | Used for |
|---|---|---|---|
| `source-serif-4-variable.woff2` | Source Serif 4 | variable 8–60 opsz, 400–600 | Warm Editorial display / artifact titles |
| `inter-variable.woff2` | Inter | variable | Warm Editorial UI + body |
| `barlow-condensed-{500,600}.woff2` | Barlow Condensed | 500, 600 | Industry Blueprint headings |
| `barlow-{400,500,600}.woff2` | Barlow | 400, 500, 600 | Industry Blueprint body |
| `ibm-plex-mono-{400,500}.woff2` | IBM Plex Mono | 400, 500 | timecodes, segment IDs, character counters — both families |
| `barlow-condensed-{400,700,800}.ttf`, `barlow-{700,800}.ttf`, `ibm-plex-mono-{600,700}.ttf`, `anton-400.ttf`, `dm-serif-display-400.ttf` | image-layer faces | as named | text layers in the image editor (ADR-082). TTF, byte-identical to `src/Castmill.Api/Assets/Fonts`, so the preview and the composite draw the same glyphs |

Latin subset only (~272 KB total). Fetched from the Google Fonts `css2` endpoint, which
serves the same binaries as the `google/fonts` repository.

**The server shares one of these.** `src/Castmill.Api/Assets/Fonts/BarlowCondensed-SemiBold.ttf`
is the same face as `barlow-condensed-600.woff2`, shipped as a TTF because the overlay
compositor needs SkiaSharp to read it (ADR-013). That is what makes the client's
thumbnail-headline preview and the server's composited output the same shape — keep the two
in step if either is ever replaced.
