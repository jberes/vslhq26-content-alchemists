# Authentication Blacklog

Status date: August 29, 2026

This is the live implementation backlog for adding Microsoft Entra ID and Google sign-in while
preserving Castmill-issued JWTs, rotating refresh tokens, and tenant isolation.

Legend:

- 🟢 → changing now
- ✅ implemented and validated
- ⏳ queued
- ⛔ blocked by an external prerequisite

## Verified baseline

- ✅ Castmill JWT bearer remains the business API authentication scheme.
- ✅ `AspNetUserLogins` already exists in Azure SQL migrations.
- ✅ Each external identity will resolve to a local `CastmillUser` and permanent `TenantId`.
- ✅ Web and Desktop will continue receiving only Castmill access and refresh tokens.
- ✅ Provider access and refresh tokens will not be saved (`SaveTokens = false`).
- ✅ Production API URL is `https://azappdzrs2vrhk6ote.azurewebsites.net`.
- ✅ Development API HTTPS URL is `https://localhost:7105`.
- ✅ Entra application ID `059a8fcd-6c45-4a83-b11a-3e739b0ae751` currently exists.
- ✅ Current Entra app is single-tenant, has a Clerk callback, and requests Graph `User.Read`.
- ✅ App Service currently has no external-auth or Data Protection settings.
- ✅ Mac Catalyst bundle ID is `ai.castmill.desktop`.
- ✅ Windows desktop is currently unpackaged; package URI activation cannot be assumed.

## Architecture decisions

- ✅ Use server-brokered provider authentication followed by a one-time Castmill code exchange.
- ✅ Use durable Azure SQL state for external attempts and one-time code hashes.
- ✅ Use separate provider middleware correlation/nonce and Castmill client PKCE.
- ✅ Use a nonce-bound `127.0.0.1` loopback callback for Desktop instead of custom URI activation.
- ✅ Keep JWT bearer as the default authentication and challenge scheme.
- ✅ Persist ASP.NET Core Data Protection keys in Azure Blob Storage for App Service restart/scale safety.
- ✅ Append ADR-051 for additive external login while preserving ADR-010's local account model.

## Backend

- ✅ Extract atomic local user-plus-tenant creation from the registration endpoint.
- ✅ Extract shared Castmill token-pair issuance from the authentication endpoint.
- ✅ Add provider-neutral external-auth options and provider readiness reporting.
- ✅ Add Microsoft OpenID Connect middleware using the multitenant v2 authority.
- ✅ Add Google server-side authentication middleware.
- ✅ Use only middleware correlation/nonce cookies; do not create an intermediary identity cookie.
- ✅ Add `ExternalAuthAttempt` durable persistence, expiry indexes, hashes, and atomic consumption.
- ✅ Add provider start, callback completion, desktop poll, exchange, link, unlink, and provider endpoints.
- ✅ Use Identity `FindByLoginAsync` / `AddLoginAsync` with immutable provider keys.
- ✅ Reject silent email-based account merging in the account service.
- ✅ Add safe public error codes and sanitized audit events.
- ✅ Add cleanup for expired external-auth attempts.
- ✅ Adapt password-change API behavior for users without a local password.
- ✅ Remove transferable start-time exchange proof and bind callback proof to Web fragments or Desktop loopback.
- ✅ Complete OIDC attempts directly from validated provider tickets without intermediary identity cookies.
- ✅ Add bounded retry while retaining short-lived callback proof only in session storage.
- ✅ Separate login, external start, external flow, and poll rate-limit budgets.

## Desktop first

- ✅ Add a shared provider-neutral launcher contract.
- ✅ Generate a 256-bit PKCE verifier and S256 challenge in the desktop shell.
- ✅ Start the provider flow through the API and open it in the system browser.
- ✅ Receive callback-generated proof through the nonce-bound loopback listener.
- ✅ Exchange the one-time Castmill code and verifier for the normal token pair.
- ✅ Store the resulting Castmill session through `DesktopTokenProvider`.
- ✅ Handle denial, cancellation, expiry, duplicate completion, and provider outage.
- ✅ Add Microsoft and Google controls to the shared sign-in screen, with readiness states.
- ✅ Add linked-login management and external-only password UX.
- ✅ Capture Microsoft 365 and Google avatars without persisting provider tokens, and render a shared initials fallback.
- ✅ Validate real Microsoft Desktop OIDC, loopback proof, PKCE exchange, JWT `/me`, refresh rotation, logout, and SQL persistence.
- ⏳ Validate the CoreGraphics-confirmed Mac Catalyst window with real provider sign-in.
- ⏳ Validate Windows x64 on a real Windows host.

## Web

- ✅ Generate and hold Web PKCE state in session storage.
- ✅ Perform top-level navigation through the server provider start flow.
- ✅ Complete the one-time Castmill exchange after callback.
- ✅ Remove one-time state from browser history.
- ✅ Preserve the existing Web refresh-token and HTTP replay behavior.
- ✅ Validate Microsoft sign-in in real Chromium, including fragment scrubbing, session restoration, and sign-out cleanup.
- ⏳ Validate the Safari-compatible browser path.

## Account lifecycle

- ✅ New external identity creates exactly one local user and tenant.
- ✅ Existing mapping returns the same local user and tenant.
- ✅ Matching email returns `AccountLinkRequired` without revealing account existence.
- ✅ Authenticated linking binds a provider to the current server-resolved local user.
- ✅ Unlinking cannot remove the final usable sign-in method.
- ✅ External-only accounts do not show an unusable change-password workflow.
- ⛔ Microsoft identities without a usable verified email require a product decision and email-verification dependency.

## Azure and provider configuration

- ✅ Add external-auth configuration sections to templates/export tooling without exporting provider secrets.
- ✅ Add Data Protection configuration to templates/export tooling.
- ✅ Add durable Data Protection Blob configuration to App Service infrastructure.
- ✅ Change Entra audience to work/school accounts from any tenant plus personal Microsoft accounts.
- ✅ Replace the Clerk callback with the exact Castmill API callback.
- ⏳ Remove Graph `User.Read` when provider sign-in works without it.
- ✅ Rotate the Entra credential and install the active value outside the repository.
- ⏳ Add publisher branding and verification metadata.
- ⛔ Create/configure a Google OAuth Web application and supply its secret directly to local/Azure secret custody.
- ⛔ Complete Google production branding/verification if required by Google.

## Real testing gates

- ✅ Preserve and run account creation, password auth, refresh reuse, and tenant-isolation tests against real SQL Server (20/20 passed).
- ✅ Add real SQL-backed endpoint tests for provider protocol, linking, and account lifecycle (33/33 current external endpoint tests passed).
- ✅ Add concurrency tests proving one-time exchange permits one success.
- ✅ Add cross-tenant and account-linking isolation tests.
- ✅ Add no-secret/no-provider-token response checks.
- ✅ Run the complete Docker-backed API/UI/Media suite after provider-avatar support (770/770 passed).
- ✅ Run browser/editor external-auth tests after callback retry and fragment hardening (53/53 passed).
- ✅ Build Web and Mac Catalyst arm64 with zero warnings/errors and verify packaged static assets.
- ⏳ Build and validate Windows x64 on a real Windows host.
- ✅ Execute real Microsoft login E2E with a home-tenant work account through the Desktop protocol.
- ⏳ Execute real Microsoft login E2E with external-tenant and personal accounts.
- ⛔ Execute real Google Gmail and Workspace E2E after a Google OAuth client and test accounts are available.
- ✅ Validate App Service callback, OIDC restart survival, and durable Blob Data Protection with a real Microsoft account.
- ⏳ Verify normal business APIs reject raw Microsoft and Google tokens.

## Release gates

- ⏳ Update authoritative architecture documents and append the external-auth ADR.
- ⏳ Run migration against a disposable/isolated database before production.
- ✅ Validate Bicep and App Service configuration without exposing secrets.
- ⏳ Deploy behind provider feature flags.
- ⏳ Enable and validate Desktop Microsoft first.
- ⏳ Enable Desktop Google after real-provider E2E.
- ⏳ Validate Web provider flows.
- ✅ Run final regression tests, deploy, migrate, and execute production authentication smoke tests.
- ⏳ Commit and push only after all available gates are green.
