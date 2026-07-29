# Castmill infrastructure

Everything provisions from an empty resource group with one command (G6), using **your `az login` identity only** — no app registration, no service principal, no stored deployment credentials.

```bash
az login
./infra/deploy.sh rg-castmill eastus
```

## What it creates

| Resource | Notes |
|---|---|
| Log Analytics + App Insights | telemetry wired into the API automatically |
| Storage account | Entra-only (`allowSharedKeyAccess: false`); `private` + `public` containers, `clip-jobs` queue |
| Azure SQL (serverless) | **Entra-only auth** — no SQL passwords exist; scales to zero when idle |
| App Service (Linux B1) | system-assigned managed identity with Storage Blob/Queue Data Contributor; `Jwt__SigningKey` / `Castmill__EncryptionKey` generated at first deploy |
| Container Apps env + job | clip-export ffmpeg worker, KEDA-scaled by the `clip-jobs` queue, scale-to-zero (pass `CLIPJOB_IMAGE=<registry>/clipjob:tag` once the image is pushed) |
| Metric alert | API 5xx spike (attach an action group to get notified) |

## Clip-job worker image

```bash
cd infra/clipjob
az acr build -r <your-registry> -t clipjob:latest .   # or docker build + push
CLIPJOB_IMAGE=<registry>.azurecr.io/clipjob:latest ../deploy.sh rg-castmill
```

## Existing hand-made resources

The dev SQL server (`jberes.database.windows.net`) and storage account (`castmill` in `rg-apps-demo`) were created manually before this template existed. The template is the source of truth for a fresh environment; it does not manage those. Deploy to a **new resource group** to avoid name collisions (storage account names are globally unique — override with `BASE_NAME=castmillprod`).

## Post-deploy one-timers

1. Grant the API's managed identity SQL access (printed by the script): `CREATE USER [castmill-api] FROM EXTERNAL PROVIDER; ALTER ROLE db_owner ADD MEMBER [castmill-api];`
2. Fill AI settings as App Service settings (`Ai__Foundry__Endpoint`, `Ai__Models__chat`, …) or store per-user via `/api/v1/settings/secrets`.
3. Attach an action group to the `castmill-api-5xx` alert.
