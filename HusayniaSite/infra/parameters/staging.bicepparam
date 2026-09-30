using '../main.bicep'

param stage = 'Staging'
param deploymentEnabled = false
param costConfiguration = {
  location: 'UNAPPROVED-LOCATION'
  appServicePlanSkuName: 'UNAPPROVED'
  appServicePlanSkuTier: 'UNAPPROVED'
  sqlSkuName: 'UNAPPROVED'
  sqlSkuTier: 'UNAPPROVED'
  sqlSkuCapacity: 0
  sqlBackupStorageRedundancy: 'UNAPPROVED'
  storageSkuName: 'UNAPPROVED'
  storageAccessTier: 'UNAPPROVED'
  logAnalyticsSkuName: 'UNAPPROVED'
  keyVaultSkuName: 'UNAPPROVED'
}
param networkConfiguration = {
  appServicePublicNetworkAccess: 'Disabled'
  appServiceAccessRestrictions: []
  sqlPublicNetworkAccess: 'Disabled'
  sqlFirewallRules: []
  storagePublicNetworkAccess: 'Disabled'
  storageIpRules: []
  keyVaultPublicNetworkAccess: 'Disabled'
  keyVaultIpRules: []
}
param availabilityConfiguration = {
  locationId: 'UNAPPROVED'
  frequencySeconds: 0
  timeoutSeconds: 0
}
param alertConfiguration = {
  approvedEmailReceivers: []
  runbookUrl: ''
  owner: ''
}
param retentionConfiguration = {
  logAnalyticsDays: 30
  mediaVersionDays: 0
  operationalEvidenceDays: 0
}
param capabilityRequests = {
  privateEndpoints: false
  frontDoorOrCdn: false
  deploymentSlots: false
  defenderForStorage: false
  zoneOrEnhancedBackupRedundancy: false
}
