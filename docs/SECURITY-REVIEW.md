# Security review checklist (B8)

Verify on every release; each item is enforced in code/CI where possible. Status reflects the 2026-07-28 review.

## Secrets & key custody

- [x] No key material in the repo — gitleaks runs over full history in CI; committed `appsettings*.json` hold structure only.
- [x] Dev config (`appsettings.Development.json`) gitignored **and** publish-excluded (csproj `CopyToPublishDirectory=Never`).
- [x] Startup guards: missing/short `Jwt:SigningKey`, invalid `Castmill:EncryptionKey`, or localhost CORS in Production → process refuses to start.
- [x] User secrets (Foundry/broker) stored AES-256-GCM, tamper-authenticated; **no endpoint returns a secret value** (integration-tested).
- [x] NuGet audit runs as a build error; vulnerable transitives pinned to patched versions (never suppressed).
- [x] Prompt log excerpts contain prompt/response text only — credentials never enter prompts.

## Identity & sessions

- [x] PBKDF2 password hashing (Identity), 12-char NIST length-first policy, lockout after 5 failures.
- [x] Access JWTs ~15 min, HS256 pinned (`ValidAlgorithms`), issuer+audience+lifetime validated, 1-min clock skew.
- [x] Refresh tokens: 256-bit CSPRNG, SHA-256 hashed at rest, single-use rotation, family revocation on reuse, revoked on logout/password change (integration-tested).
- [x] Login responses identical for unknown user vs wrong password (no account enumeration; tested).

## Tenancy & authorization

- [x] `TenantAllowed` policy on every business route; tenant resolved from the validated JWT claim only — never headers/route.
- [x] EF global query filters on every tenant-scoped entity; cross-tenant reads are structurally empty (tested at DbContext and HTTP levels).
- [x] Clip-job worker callback: per-job 256-bit token, hash-stored, constant-time compared, burned at terminal status.

## Storage & SAS (G2)

- [x] No storage account key in use — user-delegation SAS via Entra RBAC.
- [x] SAS: single blob (`sr=b`), single operation (`sp=cw` or `sp=r`), 10-min default / 60-min cap (offline-tested).
- [x] Asset blob paths server-derived; client filenames sanitized (traversal-tested).
- [x] Public container holds only deliberately published content (WebP derivatives, SEO share snapshots) with immutable cache headers; share snapshots HTML-encode all dynamic values and carry `noindex`.

## Transport & platform

- [x] HSTS + HTTPS redirection outside Development; correlation IDs validated against a strict pattern (log-injection safe).
- [x] Rate limits: per-IP `auth` (10/min), per-user `writes` (60/min), `ai` (30/min), `searches` (60/min) — limiter runs **after** authentication so user partitions are real.
- [x] Outbound HTTP (broker, SEO, Speech) behind standard resilience handlers (retry/circuit-breaker/timeout); Azure SQL with `EnableRetryOnFailure`.
- [ ] Production CORS allowlist populated with the real SWA origin (deferred until the web client deploys).
- [ ] App Insights alerts wired to an action group (deferred until App Insights is provisioned — see `/infra`).

## Data

- [x] Artifact `ContentJson` validated as well-formed JSON and size-capped (512 KB) at the boundary.
- [x] Audit events on sign-in, lockout, refresh-reuse detection, password change, logout, publish schedule/cancel.
- [x] EF migrations gate schema changes (`EnsureCreated` banned).
