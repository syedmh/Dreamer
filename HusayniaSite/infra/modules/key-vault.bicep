param deployResources bool
param location string
param tags object
param keyVaultName string
param keyVaultSkuName string
param publicNetworkAccess string
param allowedIpRules array
param logAnalyticsWorkspaceResourceId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (deployResources) {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: keyVaultSkuName
    }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: [for rule in allowedIpRules: {
        value: rule.value
      }]
      virtualNetworkRules: []
    }
  }
}

resource keyVaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployResources) {
  scope: keyVault
  name: 'key-vault-to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceResourceId
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
