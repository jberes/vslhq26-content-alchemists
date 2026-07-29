// Castmill infrastructure (G6: reprovisionable from an empty resource group).
// Deploy: ./deploy.sh  (uses your `az login` identity — no app registration needed)
targetScope = 'resourceGroup'

@description('Base name for all resources (lowercase, letters+digits).')
@minLength(3)
@maxLength(17)
param baseName string = 'castmill'

param location string = resourceGroup().location

@description('Entra admin for Azure SQL (your user).')
param sqlAdminLogin string
param sqlAdminObjectId string

@description('Container image for the clip-export job (build from /infra/clipjob).')
param clipJobImage string = ''

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------------------
// Storage (blob: private + public containers, queue: clip-jobs)
// ---------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: baseName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // Blob-level public read is used ONLY on the public container (published
    // WebP derivatives + SEO share snapshots); the private container stays private.
    allowBlobPublicAccess: true
    allowSharedKeyAccess: false // Entra-only: user-delegation SAS, no account keys
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource privateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'private'
  properties: { publicAccess: 'None' }
}

resource publicContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'public'
  properties: { publicAccess: 'Blob' }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource clipQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'clip-jobs'
}

// ---------------------------------------------------------------------------
// Azure SQL (Entra-only auth — no SQL passwords anywhere)
// ---------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${baseName}-sql'
  location: location
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenant().tenantId
    }
    minimalTlsVersion: '1.2'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'castmill'
  location: location
  sku: {
    name: 'GP_S_Gen5'   // serverless: scales to zero cost when idle
    tier: 'GeneralPurpose'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------------------------------------------------------------------------
// App Service (Linux) — the API. System-assigned managed identity gets blob
// RBAC so SAS minting is passwordless in production too.
// ---------------------------------------------------------------------------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan'
  location: location
  kind: 'linux'
  sku: { name: 'B1' }
  properties: { reserved: true }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-api'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'Storage__AccountName', value: storage.name }
        {
          name: 'ConnectionStrings__Castmill'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=castmill;Authentication=Active Directory Managed Identity;Encrypt=True;'
        }
        // Jwt__SigningKey and Castmill__EncryptionKey are set by deploy.sh —
        // generated values never appear in source or template.
      ]
    }
  }
}

var blobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var queueDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource apiBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, api.id, blobDataContributor)
  properties: {
    principalId: api.identity.principalId
    roleDefinitionId: blobDataContributor
    principalType: 'ServicePrincipal'
  }
}

resource apiQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, api.id, queueDataContributor)
  properties: {
    principalId: api.identity.principalId
    roleDefinitionId: queueDataContributor
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Container Apps job — clip export (queue-scaled, scale-to-zero, ADR-008)
// ---------------------------------------------------------------------------
resource acaEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${baseName}-aca'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource clipJob 'Microsoft.App/jobs@2024-03-01' = if (!empty(clipJobImage)) {
  name: '${baseName}-clipjob'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    environmentId: acaEnv.id
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      eventTriggerConfig: {
        scale: {
          minExecutions: 0
          maxExecutions: 4
          rules: [
            {
              name: 'clip-queue'
              type: 'azure-queue'
              metadata: {
                queueName: 'clip-jobs'
                queueLength: '1'
                accountName: storage.name
              }
              identity: 'system'
            }
          ]
        }
      }
    }
    template: {
      containers: [
        {
          name: 'clipjob'
          image: clipJobImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: [
            { name: 'STORAGE_ACCOUNT', value: storage.name }
            { name: 'PRIVATE_CONTAINER', value: 'private' }
            { name: 'QUEUE_NAME', value: 'clip-jobs' }
          ]
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Alerts (B8): 5xx spike on the API
// ---------------------------------------------------------------------------
resource serverErrorAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${baseName}-api-5xx'
  location: 'global'
  properties: {
    severity: 2
    enabled: true
    scopes: [api.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'http5xx'
          metricName: 'Http5xx'
          operator: 'GreaterThan'
          threshold: 10
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: []
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiPrincipalId string = api.identity.principalId
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output storageAccount string = storage.name
