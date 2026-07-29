# Key rotation runbook

All keys live outside the repo (dev: `appsettings.Development.json`, gitignored; prod: App Service settings / Key Vault references). Rotation is config-only — no code changes, no redeploy beyond an app restart.

## Jwt:SigningKey (HS256, ≥32 bytes)

1. Generate: `openssl rand -base64 48`
2. Replace the value where the app reads it (dev file / App Service setting).
3. Restart the API.

**Effect:** every outstanding access token (≤15 min) and refresh token stops validating instantly — all users must sign in again. That is the desired behavior for a suspected leak. For routine rotation on a personal deployment, the sign-in cost is acceptable; if it ever isn't, introduce a two-key validation window (`ValidKeys` array) before rotating.

## Castmill:EncryptionKey (AES-256-GCM, exactly 32 bytes)

Encrypted `UserSetting` rows (Foundry credentials, broker token) are only readable with the key that wrote them. Rotation therefore re-encrypts:

1. Generate the new key: `openssl rand -base64 32`.
2. **Before swapping**, re-enter each stored secret so it's written under the new key:
   - Deploy/config the new key, restart.
   - Old rows now fail decryption (`CryptographicException`) — expected.
   - Re-set each secret: `PUT /api/v1/settings/secrets/{FoundryEndpoint|FoundryKey|BrokerToken}`.
3. Alternatively (zero-downtime, when it matters): add a one-off re-encryption migration that decrypts with the old key and encrypts with the new one, then swap.

**Losing this key loses the stored secrets** (by design — that's the security property). Recovery is re-entering them, never recovering them.

## Foundry API key / broker token / SEO / Speech keys

Rotate at the provider (Azure portal / broker dashboard), then update:

- Foundry: `Ai:Foundry:ApiKey` (dev) or re-`PUT /api/v1/settings/secrets/FoundryKey` (per-user custody).
- Broker: `PUT /api/v1/settings/secrets/BrokerToken`.
- SEO: `Seo:ApiKey`. Speech: `Ai:Speech:Key`.

## Storage

No storage account key is in use anywhere (user-delegation SAS via Entra RBAC). If a connection string was ever configured as the fallback, rotate the account key in the portal and update `Storage:ConnectionString` — or better, remove it and grant the identity **Storage Blob Data Contributor** instead.

## Azure SQL

Access is via Entra identities (`Authentication=Active Directory Default`); there is no SQL password to rotate. If SQL auth was used, rotate the login's password in the portal and update `ConnectionStrings:Castmill`.
