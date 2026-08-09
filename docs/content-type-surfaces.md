# Content-type surface contract

This matrix is the product contract for where each persisted artifact belongs. The shared
server/client inventory lives in `Castmill.Core.ArtifactKinds`; display names, lane order and
client affordances live in `ArtifactDisplay`. A new generator is incomplete until the contract
tests prove it is represented here.

Legend: **Yes** means the type is a first-class item on that surface. **Supporting** means the
artifact powers the surface without appearing as a content item.

| Artifact type | Create / Print more | Brand template | Mill Floor | Focus | Image Studio | SEO cluster | Wire |
|---|---:|---:|---:|---:|---:|---:|---:|
| Blog post | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| YouTube package | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Show notes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| X post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| LinkedIn post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| Facebook post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| Instagram post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| Threads post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| Bluesky post | Social set | Yes | Yes | Yes | Yes | Yes | Yes |
| Email sequence | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Newsletter | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Landing page | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Clip suggestions | Yes | Yes | Yes | Yes | No | No | No |
| Campaign summary | No | No | Yes | Yes | No | No | No |
| SEO keyword plan | No | No | No | No | No | Dedicated SEO Analysis view | No |
| Deep SEO/AEO report | No | No | No | No | No | Dedicated view | No |
| Transcript | No | No | No | No | No | Supporting | No |
| Image prompts / thumbnail concepts | No | No | No | No | Supporting | No | No |

Campaign format is a separate campaign-level taxonomy—Tutorial, Product demo, Webinar and
Thought leadership. It is selected during creation and remains visible in the campaign header,
campaign index and workspace campaign switcher.

Why the exclusions matter:

- Campaign summaries are editable production strategy, but not items to publish.
- Keyword plans and deep SEO/AEO reports belong only to SEO Analysis. The legacy
  `seo-brief` generator is an internal research pass, not a product content type, and its
  temporary result is not persisted by new analysis runs.
- Clip suggestions are edit instructions; exported clip media enters publishing through the clip
  pipeline, not by scheduling the suggestion document.
- Deep reports, transcripts and image-planning artifacts have purpose-built workspaces and must
  never render as malformed Focus manuscripts.
