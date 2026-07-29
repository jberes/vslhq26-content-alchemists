#!/usr/bin/env bash
# Castmill one-command deploy (G6). Runs entirely under your `az login`
# identity — no app registration or service principal required.
#
#   ./infra/deploy.sh <resource-group> [location]
#
# Idempotent: re-running updates infrastructure and redeploys the API.
set -euo pipefail

RG="${1:?usage: deploy.sh <resource-group> [location]}"
LOCATION="${2:-eastus}"
BASE_NAME="${BASE_NAME:-castmill}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "==> Resolving your Entra identity (SQL admin)"
ADMIN_LOGIN=$(az ad signed-in-user show --query userPrincipalName -o tsv)
ADMIN_OID=$(az ad signed-in-user show --query id -o tsv)

echo "==> Resource group: $RG ($LOCATION)"
az group create -n "$RG" -l "$LOCATION" -o none

echo "==> Deploying Bicep (baseName=$BASE_NAME)"
az deployment group create -g "$RG" -f "$REPO_ROOT/infra/main.bicep" \
  -p baseName="$BASE_NAME" sqlAdminLogin="$ADMIN_LOGIN" sqlAdminObjectId="$ADMIN_OID" \
  ${CLIPJOB_IMAGE:+-p clipJobImage="$CLIPJOB_IMAGE"} \
  -o none
API_URL=$(az deployment group show -g "$RG" -n main --query properties.outputs.apiUrl.value -o tsv)
API_APP="$BASE_NAME-api"

echo "==> Setting runtime keys (generated here; never stored in source)"
# Only set on first deploy so rotation stays a deliberate act (see docs/KEY-ROTATION.md).
EXISTING=$(az webapp config appsettings list -g "$RG" -n "$API_APP" \
  --query "[?name=='Jwt__SigningKey'] | length(@)" -o tsv)
if [ "$EXISTING" = "0" ]; then
  az webapp config appsettings set -g "$RG" -n "$API_APP" -o none --settings \
    "Jwt__SigningKey=$(openssl rand -base64 48)" \
    "Castmill__EncryptionKey=$(openssl rand -base64 32)"
fi

echo "==> Publishing the API"
dotnet publish "$REPO_ROOT/src/Castmill.Api" -c Release -o /tmp/castmill-publish
(cd /tmp/castmill-publish && zip -qr /tmp/castmill-api.zip .)
az webapp deploy -g "$RG" -n "$API_APP" --src-path /tmp/castmill-api.zip --type zip -o none

echo "==> Applying EF migrations against the provisioned SQL"
SQL_FQDN=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlServerFqdn.value -o tsv)
MY_IP=$(curl -s https://api.ipify.org)
az sql server firewall-rule create -g "$RG" -s "$BASE_NAME-sql" -n deployer \
  --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" -o none
(cd "$REPO_ROOT/src/Castmill.Api" && \
  ConnectionStrings__Castmill="Server=tcp:$SQL_FQDN,1433;Database=castmill;Authentication=Active Directory Default;Encrypt=True;" \
  dotnet ef database update)

echo "==> Health check"
curl -fsS "$API_URL/health" && echo
echo "Deployed: $API_URL"
echo
echo "Post-deploy (one-time): grant the API's managed identity access to SQL —"
echo "  run in the castmill database as admin:"
echo "    CREATE USER [$API_APP] FROM EXTERNAL PROVIDER;"
echo "    ALTER ROLE db_owner ADD MEMBER [$API_APP];"
