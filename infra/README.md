# Castmill infrastructure

## Single App Service with existing dependencies

The existing-resource path publishes `Castmill.Api`, `Castmill.Web`, and `Castmill.UI`
as one package on one Linux App Service. It creates only a Basic B1 plan, a web app,
a user-assigned managed identity, RBAC assignments, and diagnostics. It reuses:

- Azure SQL `jberes.database.windows.net` / `castmill`
- Storage account `castmill`
- Foundry endpoint, keys, resource map, and model aliases from the gitignored development configuration
- `appi-slingshotp-dev-1d1e` and `log-slingshotp-dev-1d1e` for telemetry

The target is subscription `fd25223e-a9f8-488d-a4ff-0fa509c394c5`, resource group
`rg-apps-demo`, and East US. Override `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`,
or `AZURE_LOCATION` when intentionally targeting somewhere else.

```bash
# No Azure changes
./infra/deploy-appservice.sh validate
./infra/deploy-appservice.sh what-if

# Creates/configures the app, grants SQL access, migrates, deploys, and smoke-tests
./infra/deploy-appservice.sh deploy

# Fast path after infrastructure exists: publish and deploy code only
./infra/deploy-appservice.sh code
```

`deploy` requires an existing Azure CLI login plus `dotnet`, `sqlcmd`, `zip`, and
`curl`. The script reads `src/Castmill.Api/appsettings.Development.json` by default;
set `CASTMILL_CONFIG_FILE` to select another gitignored JSONC file.

The configuration exporter copies only approved non-empty production sections to a
private temporary App Service settings file. It never copies the development SQL
connection, Storage connection string, CORS origins, demo user, or local font path.
The Bicep template supplies managed-identity SQL and Storage configuration. Fresh
production JWT and encryption keys are generated on the first deployment and preserved
on subsequent runs. Temporary settings and package files are removed on exit.

No separate web app needs to be created in the Azure portal. The Bicep deployment owns
the plan, app, identity, RBAC, runtime settings, health check, and diagnostic settings.

## Complete environment from scratch

Everything provisions from an empty resource group with one command (G6), using **your `az login` identity only** — no app registration, no service principal, no stored deployment credentials.

```bash
az login
./infra/deploy.sh rg-castmill eastus
```

## What it creates

| Resource | Notes |
| --- | --- |
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
