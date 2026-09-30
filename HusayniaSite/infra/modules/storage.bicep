param deployResources bool
param location string
param tags object
param storageAccountName string
param storageSkuName string
param storageAccessTier string
param publicNetworkAccess string
param allowedIpRules array
param storageResourceLockEnabled bool
param mediaVersionRetentionDays int
param operationalEvidenceRetentionDays int
param logAnalyticsWorkspaceResourceId string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = if (deployResources) {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: storageSkuName
  }
  properties: {
    accessTier: storageAccessTier
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: [for rule in allowedIpRules: {
        value: rule.value
        action: 'Allow'
      }]
      virtualNetworkRules: []
    }
    encryption: {
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (deployResources) {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    isVersioningEnabled: true
  }
}

resource mediaContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployResources) {
  parent: blobService
  name: 'media'
  properties: {
    publicAccess: 'None'
  }
}

resource importEvidenceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployResources) {
  parent: blobService
  name: 'import-evidence'
  properties: {
    publicAccess: 'None'
  }
}

resource reportsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployResources) {
  parent: blobService
  name: 'reports'
  properties: {
    publicAccess: 'None'
  }
}

resource blobServiceDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployResources) {
  scope: blobService
  name: 'storage-to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceResourceId
    logs: [
      {
        category: 'StorageRead'
        enabled: true
      }
      {
        category: 'StorageWrite'
        enabled: true
      }
      {
        category: 'StorageDelete'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Transaction'
        enabled: true
      }
    ]
  }
}

resource managementPolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (deployResources) {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'mediaVersionLifecycle'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'media/'
              ]
            }
            actions: {
              version: {
                delete: {
                  daysAfterCreationGreaterThan: mediaVersionRetentionDays
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: mediaVersionRetentionDays
                }
              }
            }
          }
        }
        {
          name: 'operationalEvidenceLifecycle'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'import-evidence/'
                'reports/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: operationalEvidenceRetentionDays
                }
              }
              version: {
                delete: {
                  daysAfterCreationGreaterThan: operationalEvidenceRetentionDays
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: operationalEvidenceRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

resource storageLock 'Microsoft.Authorization/locks@2020-05-01' = if (deployResources && storageResourceLockEnabled) {
  scope: storageAccount
  name: 'media-protection'
  properties: {
    level: 'CanNotDelete'
    notes: 'Protect media lifecycle resources after an authorized deployment.'
  }
}
