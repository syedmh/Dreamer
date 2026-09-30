param deployResources bool
param location string
param tags object
param sqlServerName string
param sqlDatabaseName string
param sqlSkuName string
param sqlSkuTier string
param sqlSkuCapacity int
param backupStorageRedundancy string
param publicNetworkAccess string
param firewallRules array
param logAnalyticsWorkspaceResourceId string

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (deployResources) {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    publicNetworkAccess: publicNetworkAccess
    minimalTlsVersion: '1.2'
  }
}

resource sqlServerAuditing 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview' = if (deployResources) {
  parent: sqlServer
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
    auditActionsAndGroups: [
      'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
      'FAILED_DATABASE_AUTHENTICATION_GROUP'
    ]
  }
}

resource sqlFirewallRuleResources 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = [for rule in firewallRules: if (deployResources && publicNetworkAccess == 'Enabled') {
  parent: sqlServer
  name: rule.name
  properties: {
    startIpAddress: rule.startIpAddress
    endIpAddress: rule.endIpAddress
  }
}]

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (deployResources) {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: sqlSkuName
    tier: sqlSkuTier
    capacity: sqlSkuCapacity
  }
  properties: {
    requestedBackupStorageRedundancy: backupStorageRedundancy
  }
}

resource sqlDatabaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployResources) {
  scope: sqlDatabase
  name: 'sql-to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceResourceId
    logs: [
      {
        category: 'Errors'
        enabled: true
      }
      {
        category: 'Timeouts'
        enabled: true
      }
      {
        category: 'Blocks'
        enabled: true
      }
      {
        category: 'Deadlocks'
        enabled: true
      }
      {
        category: 'SQLSecurityAuditEvents'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Basic'
        enabled: true
      }
    ]
  }
}
