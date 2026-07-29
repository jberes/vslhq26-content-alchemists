# Castmill

**One source in. A full campaign out.** Castmill turns a single video, podcast, or transcript into a complete, human-reviewed marketing campaign — a long-form blog, six platform-tuned social posts, an email sequence, a newsletter, a landing page, show notes, clip suggestions, an SEO keyword plan, and AI-generated imagery — with every generated claim traceable to the exact sentence in the source.

## Team

- **Team name:** Content Alchemists
- **Members:**
  - Jason Beres (@jberes)

## Category

- **Primary:** Azure OpenAI/LLM app
- **Secondary:** AI agent/workflow automation

## What it does

Marketing teams produce one great "master" asset a week, then spend days manually slicing it into channel content. Castmill is the mill between the master and the channels: drop a recording in and a fan-out of **14 AI generators** produces every downstream artifact. Provenance is the feature — each generator must cite real transcript segment IDs or its output never persists, and deterministic validators enforce platform character caps, blog word bands, and clip time ranges before anything reaches review. A keyword-planning flow chains an AI SEO brief into **DataForSEO** for real search volume/difficulty, producing opportunity-ranked keywords and 3 A/B-testable YouTube titles.

## Architecture

- **API** — ASP.NET Core (net10.0) Minimal API: Identity auth (rotating refresh tokens with reuse detection), structural tenant isolation via EF global query filters, AES-256-GCM secret custody, user-delegation SAS (no storage keys exist anywhere), per-user rate limits. Full reference: [Backend-Architecture.md](Backend-Architecture.md).
- **AI orchestration** — everything behind one `Microsoft.Extensions.AI` seam with a model-alias table; aliases can route to different Foundry resources (`"eastus2:gpt-image-2"`), so a model swap is config, never code.
- **Media** — ≤25 MB Foundry transcription + Azure AI Speech for long media; ffmpeg clip export runs as a queue-scaled Azure Container Apps job ([infra/clipjob/](infra/clipjob/)), never on API instances.
- **Client (next phase)** — one Blazor codebase shipped as a .NET MAUI hybrid desktop app and Blazor WASM web app on Ignite UI for Blazor: [Frontend-Architecture.md](Frontend-Architecture.md) · [Roadmap-Blazor.md](Roadmap-Blazor.md).

## Tech stack

- **Languages:** C# (net10.0), Python (ffmpeg worker)
- **Frameworks/libraries:** ASP.NET Core Minimal APIs, EF Core, ASP.NET Core Identity, Microsoft.Extensions.AI, SkiaSharp, xUnit v3 + Testcontainers
- **AI models/services:** Azure AI Foundry (gpt-5.6-terra/sol chat + cross-model audit, gpt-image-2, gpt-4o-transcribe, MAI-Image-2.5-Pro), Azure AI Speech, DataForSEO
- **Hosting:** Azure App Service, Azure SQL (serverless, Entra-only), Azure Storage, Azure Container Apps — one-command Bicep deploy under [infra/](infra/)

## Getting started

### Prerequisites

- .NET 10 SDK, Docker (for tests), `az` CLI (logged in)
- An Azure SQL database and storage account
- An Azure AI Foundry resource with chat/image/transcription deployments; a DataForSEO account (optional, for SEO features)

### Setup

```bash
git clone https://github.com/jberes/vslhq26-content-alchemists.git
cd vslhq26-content-alchemists/src/Castmill.Api

# All local config lives in ONE gitignored file — copy the template and fill the stubs:
cp appsettings.Development.template.json appsettings.Development.json

dotnet ef database update
dotnet run
# → http://localhost:5005/dev/testbed  (zero-framework HTML testbed)
# → http://localhost:5005/openapi/v1.json  (full API surface)
```

Tests: `dotnet test` (62 tests; spins up SQL Server in Docker via Testcontainers).

### Configuration

Everything is documented inline in [appsettings.Development.template.json](src/Castmill.Api/appsettings.Development.template.json): connection string (Entra auth — no password), JWT signing key, AES-256-GCM encryption key, storage account, Foundry endpoints/model aliases, DataForSEO. **No secrets are ever committed** — the repo's history is gitleaks-verified and CI re-scans every push.

## Demo (required)

- Video link: _coming with the client phase_
- Deployed URL: _run locally per above; one-command Azure deploy: `./infra/deploy.sh rg-castmill`_

## Known limitations

- The Blazor/MAUI client hasn't started yet — the API is exercised through the dev testbed and OpenAPI.
- Publishing goes through a Buffer-class broker abstraction; the concrete broker isn't chosen yet, so `/publish` runs against config stubs.
- Clip export needs the worker image pushed + Container Apps deployed (code and Bicep are in the repo).
- Azure AI Speech (long-media path) is wired but not yet configured with a live resource.

## License

MIT
