#!/usr/bin/env bash
set -euo pipefail

ACTION="${1:-validate}"
SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-fd25223e-a9f8-488d-a4ff-0fa509c394c5}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-rg-apps-demo}"
LOCATION="${AZURE_LOCATION:-eastus}"
CONFIG_FILE="${CASTMILL_CONFIG_FILE:-src/Castmill.Api/appsettings.Development.json}"

case "$ACTION" in
  validate|what-if|deploy|code) ;;
  *)
    echo "usage: $0 [validate|what-if|deploy|code]" >&2
    exit 2
    ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE="$REPO_ROOT/infra/appservice-existing.bicep"
PARAMETERS="$REPO_ROOT/infra/appservice-existing.parameters.json"
CONFIG_PATH="$REPO_ROOT/$CONFIG_FILE"
DEPLOYMENT_NAME="castmill-appservice"

command -v az >/dev/null || { echo "Azure CLI is required." >&2; exit 1; }
command -v dotnet >/dev/null || { echo ".NET SDK is required." >&2; exit 1; }
if [[ "$ACTION" == "deploy" || "$ACTION" == "code" ]]; then
  command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
  command -v sqlcmd >/dev/null || { echo "sqlcmd is required for the managed-identity database grant." >&2; exit 1; }
  command -v zip >/dev/null || { echo "zip is required." >&2; exit 1; }
fi

az account show --subscription "$SUBSCRIPTION_ID" --output none

if [[ "$ACTION" == "code" ]]; then
  APP_NAME="$(az deployment group show \
    --subscription "$SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DEPLOYMENT_NAME" \
    --query properties.outputs.deployment.value.appName \
    --output tsv)"
  APP_URL="$(az deployment group show \
    --subscription "$SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DEPLOYMENT_NAME" \
    --query properties.outputs.deployment.value.appUrl \
    --output tsv)"

  umask 077
  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/castmill-deploy.XXXXXX")"
  trap 'rm -rf "$WORK_DIR"' EXIT
  PUBLISH_DIR="$WORK_DIR/publish"
  PACKAGE_FILE="$WORK_DIR/castmill.zip"

  echo "Publishing the combined API and Blazor client..."
  dotnet publish "$REPO_ROOT/src/Castmill.Api/Castmill.Api.csproj" \
    --configuration Release \
    --output "$PUBLISH_DIR"
  (
    cd "$PUBLISH_DIR"
    zip -qr "$PACKAGE_FILE" .
  )

  echo "Deploying package to $APP_NAME..."
  az webapp deploy \
    --subscription "$SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$APP_NAME" \
    --src-path "$PACKAGE_FILE" \
    --type zip \
    --async true \
    --clean true \
    --restart true \
    --output none

  echo "Checking deployed health endpoints..."
  curl --fail --silent --show-error \
    --retry 18 --retry-all-errors --retry-delay 5 \
    "$APP_URL/health"
  echo
  curl --fail --silent --show-error \
    --retry 18 --retry-all-errors --retry-delay 5 \
    "$APP_URL/health/db"
  echo
  echo "Deployed: $APP_URL"
  exit 0
fi

echo "Validating App Service infrastructure..."
az deployment group validate \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE" \
  --parameters "@$PARAMETERS" location="$LOCATION" \
  --output none

if [[ "$ACTION" == "validate" ]]; then
  echo "Validation passed."
  exit 0
fi

if [[ "$ACTION" == "what-if" ]]; then
  az deployment group what-if \
    --subscription "$SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE" \
    --parameters "@$PARAMETERS" location="$LOCATION"
  exit 0
fi

[[ -f "$CONFIG_PATH" ]] || {
  echo "Configuration file not found: $CONFIG_PATH" >&2
  exit 1
}

umask 077
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/castmill-deploy.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT
SETTINGS_FILE="$WORK_DIR/appsettings.json"
PUBLISH_DIR="$WORK_DIR/publish"
PACKAGE_FILE="$WORK_DIR/castmill.zip"

echo "Creating App Service resources..."
az deployment group create \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$TEMPLATE" \
  --parameters "@$PARAMETERS" location="$LOCATION" \
  --output none

APP_NAME="$(az deployment group show \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.deployment.value.appName \
  --output tsv)"
APP_URL="$(az deployment group show \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.deployment.value.appUrl \
  --output tsv)"
IDENTITY_NAME="$(az deployment group show \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.deployment.value.identityName \
  --output tsv)"

RUNTIME_KEY_COUNT="$(az webapp config appsettings list \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --query "length([?name=='Jwt__SigningKey' || name=='Castmill__EncryptionKey'])" \
  --output tsv)"

KEY_ARGUMENT=()
if [[ "$RUNTIME_KEY_COUNT" != "2" ]]; then
  KEY_ARGUMENT=(--generate-runtime-keys)
fi

echo "Applying production configuration without exposing secret values..."
dotnet run \
  --project "$REPO_ROOT/tools/Castmill.AzureConfig/Castmill.AzureConfig.csproj" \
  --configuration Release \
  -- export "$CONFIG_PATH" "$SETTINGS_FILE" "${KEY_ARGUMENT[@]}"
az webapp config appsettings set \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --settings "@$SETTINGS_FILE" \
  --output none

echo "Granting the managed identity data access in Azure SQL..."
sqlcmd \
  -S jberes.database.windows.net \
  -d castmill \
  -G \
  -b \
  -l 60 \
  -Q "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$IDENTITY_NAME') CREATE USER [$IDENTITY_NAME] FROM EXTERNAL PROVIDER; IF IS_ROLEMEMBER(N'db_datareader', N'$IDENTITY_NAME') <> 1 ALTER ROLE db_datareader ADD MEMBER [$IDENTITY_NAME]; IF IS_ROLEMEMBER(N'db_datawriter', N'$IDENTITY_NAME') <> 1 ALTER ROLE db_datawriter ADD MEMBER [$IDENTITY_NAME];"

echo "Applying EF Core migrations with the local Entra identity..."
(
  cd "$REPO_ROOT/src/Castmill.Api"
  ASPNETCORE_ENVIRONMENT=Development dotnet ef database update
)

echo "Publishing the combined API and Blazor client..."
dotnet publish "$REPO_ROOT/src/Castmill.Api/Castmill.Api.csproj" \
  --configuration Release \
  --output "$PUBLISH_DIR"
(
  cd "$PUBLISH_DIR"
  zip -qr "$PACKAGE_FILE" .
)

echo "Deploying package to $APP_NAME..."
az webapp deploy \
  --subscription "$SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --src-path "$PACKAGE_FILE" \
  --type zip \
  --async true \
  --clean true \
  --restart true \
  --output none

echo "Checking deployed health endpoints..."
curl --fail --silent --show-error \
  --retry 18 --retry-all-errors --retry-delay 5 \
  "$APP_URL/health"
echo
curl --fail --silent --show-error \
  --retry 18 --retry-all-errors --retry-delay 5 \
  "$APP_URL/health/db"
echo
echo "Deployed: $APP_URL"