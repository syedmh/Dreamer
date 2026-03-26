@description('The environment name (dev, staging, prod)')
param environment string = 'dev'

@description('The Azure region')
param location string = resourceGroup().location

@description('SQL Server admin password')
@secure()
param sqlAdminPassword string

var appName = 'khilat-${environment}'
var tags = {
  Project: 'Khilat'
  Environment: environment
}

// App Service Plan (B1 with auto-scale)
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${appName}-plan'
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: false
  }
}

// App Service for API
resource apiApp 'Microsoft.Web/sites@2023-01-01' = {
  name: '${appName}-api'
  location: location
  tags: tags
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      alwaysOn: true
      cors: {
        allowedOrigins: [
          'https://${appName}-web.azurewebsites.net'
        ]
      }
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: environment == 'prod' ? 'Production' : 'Development' }
        { name: 'KeyVault__VaultUri', value: keyVault.properties.vaultUri }
      ]
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
}

// App Service for Blazor WASM (static site)
resource webApp 'Microsoft.Web/sites@2023-01-01' = {
  name: '${appName}-web'
  location: location
  tags: tags
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
  }
}

// Azure SQL Server
resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: '${appName}-sql'
  location: location
  tags: tags
  properties: {
    administratorLogin: 'khilatadmin'
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
  }
}

// Azure SQL Database
resource sqlDb 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: 'KhilatDb'
  location: location
  tags: tags
  sku: {
    name: 'S0'
    tier: 'Standard'
  }
}

// SQL Firewall - Allow Azure services
resource sqlFirewall 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Storage Account for product images
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: replace('${appName}storage', '-', '')
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: true
  }
}

// Blob container for product images
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storageAccount.name}/default/product-images'
  properties: {
    publicAccess: 'Blob'
  }
}

// Key Vault
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${appName}-kv'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: apiApp.identity.principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
    ]
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    RetentionInDays: 90
  }
}

// CDN Profile
resource cdnProfile 'Microsoft.Cdn/profiles@2023-05-01' = {
  name: '${appName}-cdn'
  location: 'global'
  tags: tags
  sku: {
    name: 'Standard_Microsoft'
  }
}

// CDN Endpoint for static assets
resource cdnEndpoint 'Microsoft.Cdn/profiles/endpoints@2023-05-01' = {
  parent: cdnProfile
  name: '${appName}-static'
  location: 'global'
  tags: tags
  properties: {
    originHostHeader: storageAccount.properties.primaryEndpoints.blob
    origins: [
      {
        name: 'blob-origin'
        properties: {
          hostName: replace(replace(storageAccount.properties.primaryEndpoints.blob, 'https://', ''), '/', '')
        }
      }
    ]
    isCompressionEnabled: true
    contentTypesToCompress: [
      'text/css'
      'application/javascript'
      'image/svg+xml'
    ]
  }
}

// Auto-scale rules
resource autoScale 'Microsoft.Insights/autoscalesettings@2022-10-01' = {
  name: '${appName}-autoscale'
  location: location
  tags: tags
  properties: {
    targetResourceUri: appServicePlan.id
    enabled: true
    profiles: [
      {
        name: 'default'
        capacity: {
          minimum: '1'
          maximum: '3'
          default: '1'
        }
        rules: [
          {
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricResourceUri: appServicePlan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT5M'
              timeAggregation: 'Average'
              operator: 'GreaterThan'
              threshold: 75
            }
            scaleAction: {
              direction: 'Increase'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT5M'
            }
          }
          {
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricResourceUri: appServicePlan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT5M'
              timeAggregation: 'Average'
              operator: 'LessThan'
              threshold: 25
            }
            scaleAction: {
              direction: 'Decrease'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT5M'
            }
          }
        ]
      }
    ]
  }
}

// Outputs
output apiUrl string = 'https://${apiApp.properties.defaultHostName}'
output webUrl string = 'https://${webApp.properties.defaultHostName}'
output sqlConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=KhilatDb;'
output storageAccountName string = storageAccount.name
output appInsightsKey string = appInsights.properties.InstrumentationKey
output cdnEndpointUrl string = 'https://${cdnEndpoint.properties.hostName}'
