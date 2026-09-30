targetScope = 'subscription'

@description('Request-only metadata. No optional resource exists in T20 and every true request blocks the parent deployment.')
param privateEndpointsRequested bool
param frontDoorOrCdnRequested bool
param deploymentSlotsRequested bool
param defenderForStorageRequested bool
param zoneOrEnhancedBackupRedundancyRequested bool

output capabilities object = {
  privateEndpoints: {
    requested: privateEndpointsRequested
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  frontDoorOrCdn: {
    requested: frontDoorOrCdnRequested
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  deploymentSlots: {
    requested: deploymentSlotsRequested
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  defenderForStorage: {
    requested: defenderForStorageRequested
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  zoneOrEnhancedBackupRedundancy: {
    requested: zoneOrEnhancedBackupRedundancyRequested
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
}
