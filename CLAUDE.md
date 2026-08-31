# Castmill — conventions

Architecture docs are authoritative: [Backend-Architecture.md](Backend-Architecture.md), [Frontend-Architecture.md](Frontend-Architecture.md), [Roadmap-Blazor.md](Roadmap-Blazor.md). Decisions are ADRs — append, never rewrite.

## Stack
- `src/Castmill.Api` — ASP.NET Core (net10.0) Minimal APIs. Route groups under `/api/v1`, one file per group. No MVC controllers.
- `src/Castmill.Core` — domain models + DTOs shared client/server. No ASP.NET dependencies.
- `tests/Castmill.Api.Tests` — xUnit **v3** + WebApplicationFactory + Testcontainers (SQL Server). Requires Docker.
- EF Core with **real migrations** (`EnsureCreated` is banned). Migrations folder is analyzer-exempt.
- Frontend (later phases): all UI in a `Castmill.UI` RCL; Ignite UI for Blazor; shells are bootstrap-only.
  - **Two theme families** (Warm Editorial + Industry Blueprint) × light/dark behind a shared **semantic** token layer. Feature CSS uses semantic tokens only — never a family's raw colour, never a literal (ADR-F09).
  - **Fluid layout, no fixed page canvas** (ADR-F10). The design handoff's 1440 × 880 is a drawing convention; its pixel values are ratios. Only the provenance overlay measures pixels at runtime.
  - Rail = workspace scope; the four campaign views are header tabs (ADR-F11). No indeterminate spinners anywhere (ADR-F13).
  - Design reference: [docs/design_handoff_castmill_mill_floor/](docs/design_handoff_castmill_mill_floor/README.md) — recreate as Razor components; the prototype's imperative DOM writes are a prototype shortcut, not a pattern.

## Generated images (non-negotiable)
- **Rendered text is never clipped.** Providers emit only a fixed size set, so every render
  centre-crops to the slot's aspect and loses up to ~11% of an edge. `ImagePromptRules` is
  applied inside `ImageRenderer` — the single choke point every render passes through — so
  no call site or user prompt can bypass the safe-margin rule. New render paths go through
  `ImageRenderer`; if one ever cannot, it applies `ImagePromptRules.Apply` itself.
- Any prompt authored by a generator (`Generators.cs`) must ask for clear edge margins and
  centre-weighted composition. Prompts that place headlines, logos or key subjects near an
  edge are wrong regardless of how good the image looks before cropping.
- Headlines that must be exact are composited after generation (`ImageComposer`), never
  spelled by the model.

## Security rules (non-negotiable)
- **No secrets in the repo.** Committed `appsettings.json` holds structure only.
  - Dev config lives in `src/Castmill.Api/appsettings.Development.json` — **gitignored and publish-excluded**; it is the single local source for `ConnectionStrings:Castmill` (Azure SQL) and `Jwt:SigningKey` (≥32 bytes). Keys documented in the committed `appsettings.Development.template.json`. User-secrets are cleared/unused — don't reintroduce them, they silently override the file.
  - Prod: App Service settings / Key Vault references.
  - CI runs gitleaks over full history; a leaked secret fails the build.
- Auth is **ASP.NET Core Identity, email+password** (ADR-010) — no external IdP. API issues ~15-min access JWTs + rotating refresh tokens (SHA-256 hashed at rest, single-use, family-revoked on reuse, revoked on logout/password change).
- Startup guards refuse to boot on missing/short signing key, or localhost CORS in Production. Extend the guards when adding new secret kinds.
- Tenant isolation is structural: entities implement `ITenantScoped`, `CastmillDbContext` applies global query filters from `ITenantProvider` (JWT `tenant` claim only — never headers/route). New tenant-scoped entities MUST get a query filter + an isolation test.
- Rate limits: `auth` per-IP, `writes` per-user (config: `RateLimits:*`). New anonymous endpoints need a limiter policy.
- NuGet audit runs as a build **error**; fix vulnerable transitives by pinning patched versions in the csproj (see existing pins), never by suppressing.

## Commands
- **SDK: .NET 10 GA**, pinned by `global.json` to 10.0.302 / package band `10.0.10` (ADR-018). The MAUI workload must be installed for the same band (`sudo dotnet workload install maui`).
- Build: `dotnet build Castmill.NoDesktop.slnf` for the everyday loop (warnings are errors). Plain `dotnet build` also builds the MAUI shell, which is slow and macOS/Windows-only — the solution filter exists so the inner loop doesn't pay for it.
- Test: `dotnet test Castmill.NoDesktop.slnf` (Api.Tests needs Docker for Testcontainers; UI.Tests does not)
- UI validation: every UI change must be built and checked in **both** `Castmill.Web` and `Castmill.Desktop` (Mac Catalyst on macOS). Catalyst is a required runtime, not an optional follow-up. Confirm the Catalyst app bundle contains the current `Castmill.UI` static assets; an incremental `-t:Run` can relaunch a stale copied CSS bundle, so clean/rebuild when the bundle hash differs from the RCL source.
- Run API: `dotnet run --project src/Castmill.Api` → `/health`, OpenAPI at `/openapi/v1.json`
- Run web client: `dotnet run --project src/Castmill.Web` → http://localhost:5084
- Run desktop client: `dotnet build src/Castmill.Desktop -f net10.0-maccatalyst && dotnet build src/Castmill.Desktop -t:Run -f net10.0-maccatalyst`. Do not run the `Run` target alone: it can launch an existing app bundle without rebuilding changed RCL static assets.
- Editor bundle: `npm install` once at the repo root (npm workspaces), then `npm run build` / `npm test`. The RCL rebuilds the bundle automatically on build once `node_modules` exists; `-p:SkipEditorInterop=true` opts out. **`npm test` is the G2 gate** — the markdown round-trip corpus plus the < 250 KB gzip bundle budget. Run `npm run build` before `npm test`: the budget test measures the built asset.
- **Dev testbed UI:** `/dev/testbed` — plain-HTML page to exercise register/login/refresh/me without any Blazor client (Development only, never published)
- **Client style guide:** `/dev/style-guide` in either shell — every semantic token, the type scale, status encoding and the shared components, with family × mode × density switchers. Dev-only: the page refuses to render unless `IShellInfo.IsDevelopment`.
- **Demo login:** `Dev:SeedDemoUser` in the gitignored `appsettings.Development.json` seeds `demo@castmill.local` on API startup. The password lives only in that file. `DemoUserSeeder` throws if it is ever invoked outside Development.
- Migrations: `cd src/Castmill.Api && dotnet ef migrations add <Name>` (run from the project dir — was an rc.1 tool path bug; not re-tested since the GA upgrade)

## Style
- File-scoped namespaces; primary constructors where natural; `TimeProvider` (never `DateTime.UtcNow` in services).
- Comments only for constraints code can't express (security invariants, protocol rules).
- Test names: `Snake_case_sentences`.
