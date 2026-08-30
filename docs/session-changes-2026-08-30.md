# Castmill session changes — 2026-08-30

This document summarizes the product, security, infrastructure, and UX work completed during the external-authentication, avatar, Brand-sharing, and editing-quality session. It intentionally omits credentials, token values, connection strings, and other secret material.

## External authentication

- Added provider-neutral Microsoft and Google external authentication while retaining Castmill-issued access JWTs and rotating refresh tokens for all business APIs.
- Kept provider access and ID tokens transient. Provider tokens never authorize Castmill campaign, Brand, asset, or settings APIs.
- Implemented Web authorization callbacks using URL-fragment proof delivery, immediate URL cleanup, short-lived session storage, PKCE, and one-time exchange credentials.
- Implemented Desktop system-browser authentication using provider OIDC PKCE plus Castmill proof-of-possession, nonce-bound loopback callbacks on `127.0.0.1`, polling, and one-time exchange.
- Added account linking and unlinking to ASP.NET Core Identity logins. Linking gives one Castmill user another sign-in method; it does not share data with another person or tenant.
- Added provider status APIs and passwordless-account UX.
- Hardened Microsoft claim handling for real work accounts that omit `email` or do not expose `iss` as a normal principal claim. Contact email may fall back to a valid `preferred_username`; immutable provider identity remains based on validated issuer plus Microsoft tenant/object identifiers.
- Persisted the validated token issuer through a private bridge populated from `SecurityToken.Issuer`.
- Verified real Microsoft sign-in end to end in both Web and Mac Catalyst Desktop.
- Google support is implemented but remains disabled in production until a Google OAuth registration is configured.

## Authentication reliability and security

- Persisted ASP.NET Core Data Protection keys in encrypted private Azure Blob Storage so external-auth attempts survive App Service restarts.
- Added startup validation for external-provider configuration, callbacks, signing keys, and production safety invariants.
- Serialized refresh, logout, and password-change token mutations with a per-user SQL application lock.
- Added deterministic concurrent refresh/logout coverage and verified production ends with no active refresh token after logout.
- Preserved the existing rotating, SHA-256-hashed, single-use refresh-token family behavior, reuse detection, and password/logout revocation.

## Provider avatars

- Added Microsoft 365 and Google avatar capture during external authentication.
- Microsoft photos are requested from Microsoft Graph using `User.Read`; Google uses the trusted user-info picture URL.
- Avatar download uses a dedicated no-redirect client, trusted HTTPS host checks, image content/magic-byte validation, and a 256 KB limit.
- Avatar bytes and content type are stored in Castmill SQL. Provider tokens and public provider image URLs are not persisted.
- Added authenticated `/api/v1/me/avatar` delivery with private caching and `HasAvatar` on `/api/v1/me`.
- Added in-memory client avatar state, circular header rendering, and initials fallback.
- Applied migration `20260830012809_ExternalAvatars` to Azure SQL and deployed the feature.
- Verified production renders an in-memory `data:image/...` avatar and does not render the initials fallback for the signed-in Microsoft user.

## Azure deployment hardening

- Updated `infra/deploy-appservice.sh` to preserve live `ExternalAuth__Providers__*` settings across full Bicep and App Service settings updates.
- Restored Microsoft provider settings after an earlier full Bicep deployment removed them.
- Kept deployment artifacts and generated settings in private temporary directories and avoided printing secret values.
- Continued to use the existing user-assigned managed identity for Azure SQL and Storage.
- Kept SQL permissions limited to data reader/writer roles and Storage roles limited to Blob Data Contributor, Blob Delegator, and Queue Data Contributor.
- Added a precise `.gitleaksignore` for eleven expired local Castmill test JWT fingerprints in one historical Playwright trace. New/current findings remain unignored and fail the full-history scan.
- Verified gitleaks 8.30.1 reports zero unignored findings.

## Desktop API endpoint behavior

- Removed the Debug-only hardcoded local API behavior from Desktop.
- Debug and Release Desktop builds now read the same MSBuild-injected `CastmillApiBaseAddress` assembly metadata.
- The default Desktop API is the deployed Azure App Service.
- Local API development is explicit through `-p:CastmillApiBaseAddress=https://localhost:7105/`.
- This prevents Desktop provider buttons from becoming unavailable merely because no local API is running.

## Direct Brand sharing

- Added `BrandCollaborator`, keyed to an existing Castmill user and unique by `(BrandId, UserId)`.
- Added migration `20260830024340_BrandCollaborators`.
- Sharing resolves an exact normalized ASP.NET Core Identity email. Unknown, owner, and same-tenant targets return a neutral not-available result rather than exposing account details.
- Added owner-only collaborator list, add, and revoke APIs.
- Added `IsOwner` to Brand responses and an owner-only Sharing tab in the Brand editor.
- Shared Brands appear in collaborators' Brand lists and remain fully editable.
- Collaborators can edit the Brand profile, templates, asset kit, and use the Brand in their own private campaigns and AI generation.
- Brand ownership does not share campaigns, credentials, settings, audit data, other Brands, or Git publishing profiles.
- Only the Brand owner can delete the Brand or manage collaborators.
- Revocation immediately detaches the recipient tenant's campaigns and removes that tenant's contributed asset links.
- Linked library assets cannot be deleted until removed from every Brand kit.
- Brand owners and collaborators can read and preview assets linked through an accessible Brand, including collaborator-contributed assets.
- Shared read-SAS and thumbnail minting acquire the same transaction-owned Brand lock used by revocation, recheck authorization, and hold the lock through final credential minting.
- Brand mutations and Brand-bearing campaign create/update operations use serializable SQL transactions and a transaction-owned Brand application lock.
- A custom zero-replay EF execution strategy supports those explicit transactions without automatically replaying non-idempotent HTTP mutations after an ambiguous commit.
- Added deterministic cross-tenant, generation, revocation, campaign attach/update race, asset-link race, SAS-mint race, owner-contributed-asset, and tenant-filter tests.
- Appended backend ADR-052. It supersedes the no-sharing part of ADR-011 only for Brand aggregates; campaigns remain private and single-owner.

## Focus editor and content formatting

- Normalized unmistakably malformed generated Markdown before rendering/editing:
  - repeated inline Unicode bullet separators become semantic Markdown list items;
  - run-on YouTube chapter lines become a Chapters heading plus separate timestamp lines;
  - both `m:ss` and `h:mm:ss` chapter timestamps are supported;
  - already-valid Markdown remains unchanged.
- Added explicit ordered/unordered list markers, indentation, and item spacing inside the rich editor for consistent WebView/browser rendering.
- Increased the default Producer pane width by roughly 20 percent.
- Added an explicit Expand/Restore Producer control. Wide mode can use about 42 percent of the Focus workspace, and the control hides below the breakpoint where Producer is already full-width.
- Moved review, Copy, Download, and optional GitHub publish actions into the Focus document header.
- Replaced three permanent export buttons with one accessible Download disclosure containing Markdown, Word, and whole-campaign ZIP actions.
- Removed the bottom Focus footer to reclaim manuscript space.
- Added explicit collapsed/expanded ARIA state coverage for the Download disclosure.
- Fixed slash-command keyboard navigation so the active option calls `scrollIntoView({ block: "nearest" })` and stays inside the palette viewport.

## ApexTree and Front page

- Fixed ApexTree side clipping by calling `fitScreen()` before applying the no-upscale content sizing rule.
- Changed the tree wrapper from hidden overflow to contained scrolling as a fallback, so no node can become permanently unreachable.
- Added an Edit action to each Front page Drafts aging row.
- The action appears on hover/focus for fine pointers, remains visible for coarse/touch pointers, and deep-links directly to the exact artifact in Focus Mode.

## Web and Desktop scale

- Identified the Web-size mismatch as a global `html { font-size: 125%; }`, which produced a 20 px body and approximately 41 px H1 at browser 100% zoom.
- Restored Web to the browser-standard 16 px rem root.
- Preserved the accepted 20 px root only for Mac Catalyst using explicit bootstrap host markers: `cm-shell-web` and `cm-shell-desktop`.
- Kept all feature typography and spacing in the shared semantic token layer; components were not forked by shell.
- Measured the local Web build at 100% browser zoom: 16 px root/body and approximately 34.5 px H1.
- Appended frontend ADR-F52 and added a host-scale regression test.

## Validation

- Editor bundle build and tests: 54/54 passed, including Markdown round-trip and bundle-budget gates.
- .NET solution tests: 785/785 passed with zero warnings or failures.
- Release Web/API build passed with zero warnings or errors.
- Clean Mac Catalyst Release arm64 build passed with zero warnings or errors.
- Verified the Catalyst executable is arm64.
- Verified Desktop-bundled `base.css`, `views.css`, editor JS, and ApexTree JS byte-for-byte match the RCL sources.
- Verified published Web contains `cm-shell-web` and Desktop contains `cm-shell-desktop`.
- Final read-only code review passed with no findings.
- `git diff --check` passed.
- Full-history gitleaks scan passed with zero unignored findings.

## Commits and deployment history

- `766770b` — refresh/logout concurrency hotfix, deployed and production-smoked.
- `c07a934` — external provider avatars, migration applied and deployed.
- `4ba2ddb` — preserve external-provider settings during deployment, pushed and deployed.
- `c86378a` — Brand collaboration, Desktop endpoint correction, editing UX, ApexTree, Front page, and shell-scale changes, pushed to `main`.

The `c86378a` release was committed and pushed. Azure pre-deployment validation is in progress at the time this document was created; update this section with the deployment and production verification result after rollout.
