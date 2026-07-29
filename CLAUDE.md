# Castmill — conventions

Architecture docs are authoritative: [Backend-Architecture.md](Backend-Architecture.md), [Frontend-Architecture.md](Frontend-Architecture.md), [Roadmap-Blazor.md](Roadmap-Blazor.md). Decisions are ADRs — append, never rewrite.

## Stack
- `src/Castmill.Api` — ASP.NET Core (net10.0) Minimal APIs. Route groups under `/api/v1`, one file per group. No MVC controllers.
- `src/Castmill.Core` — domain models + DTOs shared client/server. No ASP.NET dependencies.
- `tests/Castmill.Api.Tests` — xUnit **v3** + WebApplicationFactory + Testcontainers (SQL Server). Requires Docker.
- EF Core with **real migrations** (`EnsureCreated` is banned). Migrations folder is analyzer-exempt.
- Frontend (later phases): all UI in a `Castmill.UI` RCL; Ignite UI for Blazor; shells are bootstrap-only.

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
- Build: `dotnet build` (warnings are errors)
- Test: `dotnet test` (needs Docker running for Testcontainers)
- Run API: `dotnet run --project src/Castmill.Api` → `/health`, OpenAPI at `/openapi/v1.json`
- **Dev testbed UI:** `/dev/testbed` — plain-HTML page to exercise register/login/refresh/me without any Blazor client (Development only, never published)
- Migrations: `cd src/Castmill.Api && dotnet ef migrations add <Name>` (run from the project dir — rc.1 tool path bug)

## Style
- File-scoped namespaces; primary constructors where natural; `TimeProvider` (never `DateTime.UtcNow` in services).
- Comments only for constraints code can't express (security invariants, protocol rules).
- Test names: `Snake_case_sentences`.
