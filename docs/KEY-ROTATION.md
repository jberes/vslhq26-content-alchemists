# Key rotation runbook

All keys live outside the repo (dev: `appsettings.Development.json`, gitignored; prod: App Service settings / Key Vault references). Rotation is config-only — no code changes, no redeploy beyond an app restart.

## Jwt:SigningKey (HS256, ≥32 bytes)

1. Generate: `openssl rand -base64 48`
2. Replace the value where the app reads it (dev file / App Service setting).
3. Restart the API.

**Effect:** every outstanding access token (≤15 min) and refresh token stops validating instantly — all users must sign in again. That is the desired behavior for a suspected leak. For routine rotation on a personal deployment, the sign-in cost is acceptable; if it ever isn't, introduce a two-key validation window (`ValidKeys` array) before rotating.

## Castmill:EncryptionKey (AES-256-GCM, exactly 32 bytes)

An encrypted `UserSetting` row is readable only with the key that wrote it. Since ADR-079 the
app reads through a **fallback list**, so rotation no longer destroys anything:

- `Castmill:EncryptionKey` — the active key. **Every write uses this**, always.
- `Castmill:PreviousEncryptionKeys` — an array of retired keys, tried in order on read only.

### Rotating

1. Generate the new key: `openssl rand -base64 32`.
2. Move the current value into `Castmill:PreviousEncryptionKeys` and put the new one in
   `Castmill:EncryptionKey`.
3. Restart. Existing rows still open via the fallback; anything written from now on uses the
   new key. Re-enter a secret (or write it once) to migrate that row.
4. Drop the retired key from the list once every row has been rewritten under the new one.

A read deliberately does **not** re-encrypt under the active key: where several hosts share one
database, that would repair the reading host and break the writing one, and the two would take
turns invalidating each other's rows.

### Several hosts, one database

The local API and the App Service hold **independent** keys — `tools/Castmill.AzureConfig
--generate-runtime-keys` mints a fresh one per export — while both read the same Azure SQL
database. A secret entered through one host is therefore unreadable on the other unless each
lists the other's key under `Castmill:PreviousEncryptionKeys`. Symptom when they don't: every
stored secret shows **RE-ENTER** at once, and the log reports
`AuthenticationTagMismatchException` — intact ciphertext, wrong key. The boot log and that
error both name the active key's fingerprint (first 12 hex of SHA-256), which is safe to
share and is the fastest way to tell two hosts apart.

**Losing every key that wrote a row loses that secret** — that is the security property. But a
rotation with the old key still listed loses nothing, and a key mismatch between hosts is now
a config fix rather than a re-entry.

## Foundry API key / broker token / SEO / Speech keys

Rotate at the provider (Azure portal / broker dashboard), then update:

- Foundry: `Ai:Foundry:ApiKey` (dev) or re-`PUT /api/v1/settings/secrets/FoundryKey` (per-user custody).
- Broker: `PUT /api/v1/settings/secrets/BrokerToken`.
- SEO: `Seo:ApiKey`. Speech: `Ai:Speech:Key`.

## Storage

No storage account key is in use anywhere (user-delegation SAS via Entra RBAC). If a connection string was ever configured as the fallback, rotate the account key in the portal and update `Storage:ConnectionString` — or better, remove it and grant the identity **Storage Blob Data Contributor** instead.

## Azure SQL

Access is via Entra identities (`Authentication=Active Directory Default`); there is no SQL password to rotate. If SQL auth was used, rotate the login's password in the portal and update `ConnectionStrings:Castmill`.
