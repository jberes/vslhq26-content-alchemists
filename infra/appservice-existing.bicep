targetScope = 'resourceGroup'

@description('Short environment name used to create deterministic resource names.')
@minLength(3)
@maxLength(12)
param environmentName string = 'castmill'

param location string = resourceGroup().location

@allowed([
  'B1'
])
param planSkuName string = 'B1'

type ExistingResources = {
  storageAccountName: string
  sqlServerFqdn: string
  sqlDatabaseName: string
  applicationInsightsName: string
  logAnalyticsWorkspaceName: string
}

param existingResources ExistingResources

var resourceToken = uniqueString(subscription().id, resourceGroup().id, location, environmentName)
var identityName = 'azid${resourceToken}'
var planName = 'azasp${resourceToken}'
var appName = 'azapp${resourceToken}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: existingResources.storageAccountName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: existingResources.applicationInsightsName
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: existingResources.logAnalyticsWorkspaceName
}

module identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: identityName
    location: location
    enableTelemetry: false
    tags: {
      application: 'castmill'
      environment: environmentName
    }
  }
}

module plan 'br/public:avm/res/web/serverfarm:0.7.0' = {
  params: {
    name: planName
    location: location
    kind: 'linux'
    reserved: true
    skuName: planSkuName
    skuCapacity: 1
    zoneRedundant: false
    enableTelemetry: false
    tags: {
      application: 'castmill'
      environment: environmentName
    }
  }
}

module app 'br/public:avm/res/web/site:0.24.0' = {
  params: {
    name: appName
    location: location
    kind: 'app,linux'
    serverFarmResourceId: plan.outputs.resourceId
    reserved: true
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        identity.outputs.resourceId
      ]
    }
    siteConfig: {
      alwaysOn: true
      appCommandLine: 'dotnet Castmill.Api.dll'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      cors: {
        allowedOrigins: []
        supportCredentials: false
      }
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: identity.outputs.clientId
        }
        {
          name: 'Storage__AccountName'
          value: storage.name
        }
        {
          name: 'Storage__PrivateContainer'
          value: 'private'
        }
        {
          name: 'DataProtection__BlobPath'
          value: 'system/data-protection/castmill-keyring.xml'
        }
        {
          name: 'ConnectionStrings__Castmill'
          value: 'Server=tcp:${existingResources.sqlServerFqdn},1433;Database=${existingResources.sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identity.outputs.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ApplicationInsights__ConnectionString'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'WEBSITE_HEALTHCHECK_MAXPINGFAILURES'
          value: '3'
        }
      ]
    }
    diagnosticSettings: [
      {
        name: 'azdiag${resourceToken}'
        workspaceResourceId: logs.id
        logAnalyticsDestinationType: 'Dedicated'
      }
    ]
    enableTelemetry: false
    tags: {
      application: 'castmill'
      environment: environmentName
    }
  }
}

var blobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var blobDelegator = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
)
var queueDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
)

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identityName, blobDataContributor)
  properties: {
    principalId: identity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributor
  }
}

resource blobDelegatorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identityName, blobDelegator)
  properties: {
    principalId: identity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDelegator
  }
}

resource queueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identityName, queueDataContributor)
  properties: {
    principalId: identity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: queueDataContributor
  }
}

output deployment object = {
  appName: app.outputs.name
  appUrl: 'https://${app.outputs.defaultHostname}'
  identityName: identity.outputs.name
  identityClientId: identity.outputs.clientId
  identityPrincipalId: identity.outputs.principalId
  planName: plan.outputs.name
}
