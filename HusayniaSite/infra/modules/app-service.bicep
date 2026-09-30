@description('Controls whether this module can create any resource.')
param deployResources bool
param location string
param tags object
param appServicePlanName string
param webAppName string
param appServicePlanSkuName string
param appServicePlanSkuTier string
param linuxFxVersion string
param publicNetworkAccess string
param appServiceAccessRestrictions array
param storageAccountName string
param logAnalyticsWorkspaceResourceId string

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = if (deployResources) {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: appServicePlanSkuName
    tier: appServicePlanSkuTier
  }
  tags: tags
  properties: {
    reserved: true
    perSiteScaling: false
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = if (deployResources) {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    publicNetworkAccess: publicNetworkAccess
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      healthCheckPath: '/health'
      ipSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [for rule in appServiceAccessRestrictions: {
        name: rule.name
        ipAddress: rule.ipAddress
        action: 'Allow'
        priority: rule.priority
        description: rule.description
      }]
      scmIpSecurityRestrictionsUseMain: true
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
    }
  }
}

resource ftpPublishingCredentialsPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = if (deployResources) {
  parent: webApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmPublishingCredentialsPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = if (deployResources) {
  parent: webApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource mediaContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'media'
}

resource importEvidenceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'import-evidence'
}

resource reportsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'reports'
}

resource operationalBlobWriterNoDeleteRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (deployResources) {
  name: guid(resourceGroup().id, 'OperationalBlobWriterNoDelete')
  properties: {
    roleName: 'Husaynia Operational Blob Writer No Delete'
    description: 'Read, write, and append blobs in explicitly assigned operational containers without delete permission.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource mediaBlobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployResources) {
  name: guid(mediaContainer.id, webApp!.id, 'StorageBlobDataContributor')
  scope: mediaContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: webApp!.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource importEvidenceBlobWriterNoDelete 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployResources) {
  name: guid(importEvidenceContainer.id, webApp!.id, operationalBlobWriterNoDeleteRole!.id)
  scope: importEvidenceContainer
  properties: {
    roleDefinitionId: operationalBlobWriterNoDeleteRole!.id
    principalId: webApp!.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource reportsBlobWriterNoDelete 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployResources) {
  name: guid(reportsContainer.id, webApp!.id, operationalBlobWriterNoDeleteRole!.id)
  scope: reportsContainer
  properties: {
    roleDefinitionId: operationalBlobWriterNoDeleteRole!.id
    principalId: webApp!.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployResources) {
  scope: webApp
  name: 'app-to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceResourceId
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
      {
        category: 'AppServiceAuditLogs'
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
