targetScope = 'subscription'

@sealed()
type CostConfiguration = {
  @minLength(1)
  location: string

  @minLength(1)
  appServicePlanSkuName: string

  @minLength(1)
  appServicePlanSkuTier: string

  @minLength(1)
  sqlSkuName: string

  @minLength(1)
  sqlSkuTier: string

  @minValue(0)
  sqlSkuCapacity: int

  @minLength(1)
  sqlBackupStorageRedundancy: string

  @minLength(1)
  storageSkuName: string

  @minLength(1)
  storageAccessTier: string

  @minLength(1)
  logAnalyticsSkuName: string

  @minLength(1)
  keyVaultSkuName: string
}

@sealed()
type AppServiceAccessRestriction = {
  @minLength(1)
  name: string

  @minLength(1)
  ipAddress: string

  action: 'Allow'

  @minValue(100)
  priority: int

  @minLength(1)
  description: string
}

@sealed()
type SqlFirewallRule = {
  @minLength(1)
  name: string

  @minLength(7)
  startIpAddress: string

  @minLength(7)
  endIpAddress: string
}

@sealed()
type IpRule = {
  @minLength(3)
  value: string
}

@sealed()
type NetworkConfiguration = {
  appServicePublicNetworkAccess: 'Disabled' | 'Enabled'
  appServiceAccessRestrictions: AppServiceAccessRestriction[]
  sqlPublicNetworkAccess: 'Disabled' | 'Enabled'
  sqlFirewallRules: SqlFirewallRule[]
  storagePublicNetworkAccess: 'Disabled' | 'Enabled'
  storageIpRules: IpRule[]
  keyVaultPublicNetworkAccess: 'Disabled' | 'Enabled'
  keyVaultIpRules: IpRule[]
}

@sealed()
type AvailabilityConfiguration = {
  @minLength(1)
  locationId: string

  @minValue(0)
  frequencySeconds: int

  @minValue(0)
  timeoutSeconds: int
}

@sealed()
type ActionGroupEmailReceiver = {
  @minLength(1)
  name: string

  @minLength(3)
  emailAddress: string
}

@sealed()
type AlertConfiguration = {
  approvedEmailReceivers: ActionGroupEmailReceiver[]

  @maxLength(2048)
  runbookUrl: string

  @maxLength(128)
  owner: string
}

@sealed()
type RetentionConfiguration = {
  @minValue(1)
  logAnalyticsDays: int

  @minValue(0)
  mediaVersionDays: int

  @minValue(0)
  operationalEvidenceDays: int
}

@sealed()
type CapabilityRequests = {
  privateEndpoints: bool
  frontDoorOrCdn: bool
  deploymentSlots: bool
  defenderForStorage: bool
  zoneOrEnhancedBackupRedundancy: bool
}

@description('Selects one immutable T20 stage definition. Resource group, naming, tags, and Production policy are not caller inputs.')
@allowed([
  'Development'
  'Staging'
  'Production'
])
param stage string

@description('Non-production resource creation request. Every supplied stage file is false. Production remains blocked by code even if this value is overridden.')
param deploymentEnabled bool = false

@description('Cost-bearing values are placeholders in every supplied stage file. T20 does not approve a SKU, capacity, region, or retention bill.')
param costConfiguration CostConfiguration = {
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

@description('Deny-by-default public network capability. Deployable non-production input must provide only explicit narrow allow rules.')
param networkConfiguration NetworkConfiguration = {
  appServicePublicNetworkAccess: 'Disabled'
  appServiceAccessRestrictions: []
  sqlPublicNetworkAccess: 'Disabled'
  sqlFirewallRules: []
  storagePublicNetworkAccess: 'Disabled'
  storageIpRules: []
  keyVaultPublicNetworkAccess: 'Disabled'
  keyVaultIpRules: []
}

@description('Synthetic web-test location and timing. The target URL is derived from the deterministic stage web-app name and /health.')
param availabilityConfiguration AvailabilityConfiguration = {
  locationId: 'UNAPPROVED'
  frequencySeconds: 0
  timeoutSeconds: 0
}

@description('At least one approved receiver plus an HTTPS runbook and owner are required before a non-production deployment can be effective.')
param alertConfiguration AlertConfiguration = {
  approvedEmailReceivers: []
  runbookUrl: ''
  owner: ''
}

@description('Lifecycle values require privacy, recovery, operations, and cost approval. Zero is deliberately inert for data deletion.')
param retentionConfiguration RetentionConfiguration = {
  logAnalyticsDays: 30
  mediaVersionDays: 0
  operationalEvidenceDays: 0
}

@description('Unimplemented cost-reserved features are request-only. Any true value blocks effective deployment until a future approved implementation.')
param capabilityRequests CapabilityRequests = {
  privateEndpoints: false
  frontDoorOrCdn: false
  deploymentSlots: false
  defenderForStorage: false
  zoneOrEnhancedBackupRedundancy: false
}

@description('Optional non-secret App Configuration endpoint handed to T21. T20 does not own App Service application settings.')
@maxLength(2048)
param appConfigurationEndpoint string = ''

var stageDefinitions = {
  Development: {
    code: 'dev'
    resourceGroupName: 'rg-husaynia-development'
    dataClassification: 'non-production'
    t20DeploymentAllowed: false
    storageLockRequired: false
    automaticPromotionAllowed: true
  }
  Staging: {
    code: 'stg'
    resourceGroupName: 'rg-husaynia-staging'
    dataClassification: 'non-production'
    t20DeploymentAllowed: false
    storageLockRequired: false
    automaticPromotionAllowed: false
  }
  Production: {
    code: 'prd'
    resourceGroupName: 'rg-husaynia-production'
    dataClassification: 'production'
    t20DeploymentAllowed: false
    storageLockRequired: true
    automaticPromotionAllowed: false
  }
}

var selectedStage = stageDefinitions[stage]
var stageCode = selectedStage.code
var resourceGroupName = selectedStage.resourceGroupName
var stableSubscriptionToken = uniqueString(subscription().subscriptionId)

var appServicePlanName = 'husaynia-plan-${stageCode}'
var webAppName = 'husaynia-web-${stageCode}-${stableSubscriptionToken}'
var sqlServerName = 'husaynia-sql-${stageCode}-${stableSubscriptionToken}'
var sqlDatabaseName = 'husaynia-${stageCode}'
var storageAccountName = 'husaynia${stageCode}${stableSubscriptionToken}'
var keyVaultName = 'hsy-kv-${stageCode}-${stableSubscriptionToken}'
var logAnalyticsName = 'husaynia-log-${stageCode}'
var applicationInsightsName = 'husaynia-ai-${stageCode}'
var availabilityTestName = 'husaynia-availability-${stageCode}'
var actionGroupName = 'husaynia-ag-${stageCode}'
var linuxFxVersion = 'DOTNETCORE|10.0'
var availabilityTestUrl = 'https://${webAppName}.azurewebsites.net/health'

var targetRpoHours = 24
var targetRtoHours = 4

var invalidAppServiceRules = filter(networkConfiguration.appServiceAccessRestrictions, rule => empty(trim(rule.name)) || empty(trim(rule.description)) || rule.action != 'Allow' || parseCidr(rule.ipAddress).cidr != (contains(rule.ipAddress, ':') ? 128 : 32))

var invalidSqlFirewallRules = filter(networkConfiguration.sqlFirewallRules, rule => empty(trim(rule.name)) || !contains(rule.startIpAddress, '.') || trim(rule.startIpAddress) != trim(rule.endIpAddress) || trim(rule.startIpAddress) == '0.0.0.0')

var invalidStorageIpRules = filter(networkConfiguration.storageIpRules, rule => parseCidr(rule.value).cidr != (contains(rule.value, ':') ? 128 : 32))

var invalidKeyVaultIpRules = filter(networkConfiguration.keyVaultIpRules, rule => parseCidr(rule.value).cidr != (contains(rule.value, ':') ? 128 : 32))

var networkRulesAreNarrow = length(invalidAppServiceRules) == 0 && length(invalidSqlFirewallRules) == 0 && length(invalidStorageIpRules) == 0 && length(invalidKeyVaultIpRules) == 0
var appServiceConnectivityApproved = networkConfiguration.appServicePublicNetworkAccess == 'Enabled' && length(networkConfiguration.appServiceAccessRestrictions) > 0
var sqlConnectivityApproved = networkConfiguration.sqlPublicNetworkAccess == 'Enabled' && length(networkConfiguration.sqlFirewallRules) > 0
var storageConnectivityApproved = networkConfiguration.storagePublicNetworkAccess == 'Enabled' && length(networkConfiguration.storageIpRules) > 0
var keyVaultConnectivityApproved = networkConfiguration.keyVaultPublicNetworkAccess == 'Enabled' && length(networkConfiguration.keyVaultIpRules) > 0
var allConnectivityInputsApproved = appServiceConnectivityApproved && sqlConnectivityApproved && storageConnectivityApproved && keyVaultConnectivityApproved && networkRulesAreNarrow

var costInputsApproved = costConfiguration.location != 'UNAPPROVED-LOCATION' && costConfiguration.appServicePlanSkuName != 'UNAPPROVED' && costConfiguration.appServicePlanSkuTier != 'UNAPPROVED' && costConfiguration.sqlSkuName != 'UNAPPROVED' && costConfiguration.sqlSkuTier != 'UNAPPROVED' && costConfiguration.sqlSkuCapacity > 0 && costConfiguration.sqlBackupStorageRedundancy != 'UNAPPROVED' && costConfiguration.storageSkuName != 'UNAPPROVED' && costConfiguration.storageAccessTier != 'UNAPPROVED' && costConfiguration.logAnalyticsSkuName != 'UNAPPROVED' && costConfiguration.keyVaultSkuName != 'UNAPPROVED'
var availabilityInputsApproved = availabilityConfiguration.locationId != 'UNAPPROVED' && availabilityConfiguration.frequencySeconds > 0 && availabilityConfiguration.timeoutSeconds > 0
var retentionInputsApproved = retentionConfiguration.mediaVersionDays > 0 && retentionConfiguration.operationalEvidenceDays > 0
var invalidAlertReceivers = filter(alertConfiguration.approvedEmailReceivers, receiver => empty(trim(receiver.name)) || !contains(receiver.emailAddress, '@') || contains(receiver.emailAddress, ' '))
var normalizedRunbookUrl = toLower(trim(alertConfiguration.runbookUrl))
var runbookUrlApproved = startsWith(normalizedRunbookUrl, 'https://') && length(normalizedRunbookUrl) > 11 && contains(replace(normalizedRunbookUrl, 'https://', ''), '.') && !contains(normalizedRunbookUrl, '@') && !contains(normalizedRunbookUrl, '?') && !contains(normalizedRunbookUrl, '#')
var alertingInputsApproved = length(alertConfiguration.approvedEmailReceivers) > 0 && length(invalidAlertReceivers) == 0 && runbookUrlApproved && !empty(trim(alertConfiguration.owner))
var allDeploymentInputsApproved = costInputsApproved && availabilityInputsApproved && retentionInputsApproved && alertingInputsApproved

var reservedCapabilityRequested = capabilityRequests.privateEndpoints || capabilityRequests.frontDoorOrCdn || capabilityRequests.deploymentSlots || capabilityRequests.defenderForStorage || capabilityRequests.zoneOrEnhancedBackupRedundancy
var deploymentRequested = deploymentEnabled
var storageAccountNameValid = length(storageAccountName) >= 3 && length(storageAccountName) <= 24 && storageAccountName == toLower(storageAccountName)
var keyVaultNameValid = length(keyVaultName) >= 3 && length(keyVaultName) <= 24 && !startsWith(keyVaultName, '-') && !endsWith(keyVaultName, '-')
var webAppNameValid = length(webAppName) >= 2 && length(webAppName) <= 60 && !startsWith(webAppName, '-') && !endsWith(webAppName, '-')
var sqlServerNameValid = length(sqlServerName) >= 1 && length(sqlServerName) <= 63 && !startsWith(sqlServerName, '-') && !endsWith(sqlServerName, '-')
var resourceGroupNameValid = length(resourceGroupName) >= 1 && length(resourceGroupName) <= 90
var resourceNamesValid = storageAccountNameValid && keyVaultNameValid && webAppNameValid && sqlServerNameValid && resourceGroupNameValid
var stageDeploymentEnabled = deploymentRequested && selectedStage.t20DeploymentAllowed && allDeploymentInputsApproved && allConnectivityInputsApproved && resourceNamesValid && !reservedCapabilityRequested

var deploymentBlockers = concat(
  stage == 'Production' ? [
    'Production is unconditionally blocked in T20. Enabling it requires a future CTO-authorized code and pipeline change.'
  ] : [],
  stage != 'Production' ? [
    'Development and Staging are also non-deployable in T20. T21 must introduce a reviewed stage-scoped enablement change after cost, identity, network, and operations approval.'
  ] : [],
  deploymentRequested && !costInputsApproved ? [
    'Cost-bearing region, SKU, capacity, and redundancy values remain unapproved.'
  ] : [],
  deploymentRequested && !availabilityInputsApproved ? [
    'A valid HTTPS synthetic target, Azure test location, frequency, and timeout are required.'
  ] : [],
  deploymentRequested && !retentionInputsApproved ? [
    'Media-version and operational-evidence retention require privacy, recovery, operations, and cost approval.'
  ] : [],
  deploymentRequested && !alertingInputsApproved ? [
    'At least one approved receiver, a non-empty owner, and an HTTPS runbook are required.'
  ] : [],
  deploymentRequested && !allConnectivityInputsApproved ? [
    'All public paths require explicit narrow allow lists; wildcard, allow-all, malformed, and Azure-services SQL rules are rejected.'
  ] : [],
  reservedCapabilityRequested ? [
    'An unimplemented cost-reserved capability was requested. This remains NEEDS_DECISION and blocks deployment.'
  ] : []
)

var commonTags = {
  application: 'husaynia-site'
  environment: stage
  stageCode: stageCode
  dataClassification: selectedStage.dataClassification
  managedBy: 'bicep'
  topology: 'isolated-paas-baseline'
  deploymentState: stageDeploymentEnabled ? 'effective-nonproduction-infrastructure' : 'inert-capability-baseline'
  applicationReadiness: 'awaiting-t21-package-and-secret-bootstrap'
  stageDeploymentIdentity: 'T21_STAGE_SCOPED_WORKLOAD_IDENTITY_REQUIRED'
  crossStageIdentityUseProhibited: 'true'
  productionDeploymentAllowedByT20: 'false'
  productionStorageLockRequired: string(selectedStage.storageLockRequired)
  rpoTargetHours: string(targetRpoHours)
  rtoTargetHours: string(targetRtoHours)
  costApprovalRequired: 'true'
}

resource stageResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = if (stageDeploymentEnabled) {
  name: resourceGroupName
  location: costConfiguration.location
  tags: commonTags
}

module monitoring './modules/monitoring.bicep' = if (stageDeploymentEnabled) {
  name: 'monitoring-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    logAnalyticsName: logAnalyticsName
    applicationInsightsName: applicationInsightsName
    logAnalyticsSkuName: costConfiguration.logAnalyticsSkuName
    logRetentionDays: retentionConfiguration.logAnalyticsDays
    availabilityTestName: availabilityTestName
    availabilityTestUrl: availabilityTestUrl
    availabilityTestLocations: [
      {
        Id: availabilityConfiguration.locationId
      }
    ]
    availabilityTestFrequencySeconds: availabilityConfiguration.frequencySeconds
    availabilityTestTimeoutSeconds: availabilityConfiguration.timeoutSeconds
    webAppName: webAppName
  }
}

module storage './modules/storage.bicep' = if (stageDeploymentEnabled) {
  name: 'storage-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    storageAccountName: storageAccountName
    storageSkuName: costConfiguration.storageSkuName
    storageAccessTier: costConfiguration.storageAccessTier
    publicNetworkAccess: networkConfiguration.storagePublicNetworkAccess
    allowedIpRules: networkConfiguration.storageIpRules
    storageResourceLockEnabled: selectedStage.storageLockRequired
    mediaVersionRetentionDays: retentionConfiguration.mediaVersionDays
    operationalEvidenceRetentionDays: retentionConfiguration.operationalEvidenceDays
    logAnalyticsWorkspaceResourceId: resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.OperationalInsights/workspaces', logAnalyticsName)
  }
  dependsOn: [
    monitoring
  ]
}

module keyVault './modules/key-vault.bicep' = if (stageDeploymentEnabled) {
  name: 'key-vault-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    keyVaultName: keyVaultName
    keyVaultSkuName: costConfiguration.keyVaultSkuName
    publicNetworkAccess: networkConfiguration.keyVaultPublicNetworkAccess
    allowedIpRules: networkConfiguration.keyVaultIpRules
    logAnalyticsWorkspaceResourceId: resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.OperationalInsights/workspaces', logAnalyticsName)
  }
  dependsOn: [
    monitoring
  ]
}

module sql './modules/sql.bicep' = if (stageDeploymentEnabled) {
  name: 'sql-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    sqlServerName: sqlServerName
    sqlDatabaseName: sqlDatabaseName
    sqlSkuName: costConfiguration.sqlSkuName
    sqlSkuTier: costConfiguration.sqlSkuTier
    sqlSkuCapacity: costConfiguration.sqlSkuCapacity
    backupStorageRedundancy: costConfiguration.sqlBackupStorageRedundancy
    publicNetworkAccess: networkConfiguration.sqlPublicNetworkAccess
    firewallRules: networkConfiguration.sqlFirewallRules
    logAnalyticsWorkspaceResourceId: resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.OperationalInsights/workspaces', logAnalyticsName)
  }
  dependsOn: [
    monitoring
  ]
}

module appService './modules/app-service.bicep' = if (stageDeploymentEnabled) {
  name: 'app-service-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    appServicePlanName: appServicePlanName
    webAppName: webAppName
    appServicePlanSkuName: costConfiguration.appServicePlanSkuName
    appServicePlanSkuTier: costConfiguration.appServicePlanSkuTier
    linuxFxVersion: linuxFxVersion
    publicNetworkAccess: networkConfiguration.appServicePublicNetworkAccess
    appServiceAccessRestrictions: networkConfiguration.appServiceAccessRestrictions
    storageAccountName: storageAccountName
    logAnalyticsWorkspaceResourceId: resourceId(subscription().subscriptionId, resourceGroupName, 'Microsoft.OperationalInsights/workspaces', logAnalyticsName)
  }
  dependsOn: [
    monitoring
    storage
  ]
}

module alerts './modules/alerts.bicep' = if (stageDeploymentEnabled) {
  name: 'alerts-${stageCode}'
  scope: stageResourceGroup
  params: {
    deployResources: stageDeploymentEnabled
    location: costConfiguration.location
    tags: commonTags
    actionGroupName: actionGroupName
    webAppName: webAppName
    sqlServerName: sqlServerName
    sqlDatabaseName: sqlDatabaseName
    applicationInsightsName: applicationInsightsName
    availabilityTestName: availabilityTestName
    approvedEmailReceivers: alertConfiguration.approvedEmailReceivers
    runbookUrl: alertConfiguration.runbookUrl
    alertOwner: alertConfiguration.owner
  }
  dependsOn: [
    monitoring
    appService
    sql
  ]
}

module optionalCapabilities './modules/optional-capabilities.bicep' = if (stageDeploymentEnabled) {
  name: 'optional-capabilities-${stageCode}'
  params: {
    privateEndpointsRequested: capabilityRequests.privateEndpoints
    frontDoorOrCdnRequested: capabilityRequests.frontDoorOrCdn
    deploymentSlotsRequested: capabilityRequests.deploymentSlots
    defenderForStorageRequested: capabilityRequests.defenderForStorage
    zoneOrEnhancedBackupRedundancyRequested: capabilityRequests.zoneOrEnhancedBackupRedundancy
  }
}

@description('Non-secret T21 contract. T20 never creates secret values, assigns secret access, owns runtime app settings, or applies a package.')
output t21Handoff object = {
  stage: stage
  stageCode: stageCode
  immutableResourceGroupName: resourceGroupName
  expectedDeploymentIdentity: 'one stage-scoped federated workload identity with no cross-stage role assignments'
  deploymentEnabled: stageDeploymentEnabled
  deploymentRequested: deploymentEnabled
  deploymentBlockers: deploymentBlockers
  productionEnablement: 'FUTURE_CTO_AUTHORIZED_CODE_AND_PIPELINE_CHANGE_REQUIRED'
  nonProductionEnablement: 'FUTURE_REVIEWED_T21_CODE_AND_STAGE_PIPELINE_CHANGE_REQUIRED'
  productionStorageLockRequired: selectedStage.storageLockRequired
  webAppName: webAppName
  healthPath: '/health'
  readinessPath: '/ready'
  cleanApplicationHealthy: false
  healthClaimOwner: 'T21_AFTER_PACKAGE_SECRETS_AND_SMOKE_TESTS'
  packageSettingKey: 'WEBSITE_RUN_FROM_PACKAGE'
  packageConfigurationOwner: 'T21_PIPELINE'
  secretBootstrapOwner: 'T21_PIPELINE'
  sqlAdministratorBootstrapOwner: 'T21_STAGE_SCOPED_PIPELINE'
  sqlAdministratorBootstrapRequirement: 'Use the immutable approved group for this stage; no caller-supplied principal and no cross-stage administrator.'
  secretBootstrapSequence: [
    'Deploy the inertly configured infrastructure with the stage-scoped workload identity.'
    'Create approved Key Vault secret values outside T20 without logging or committing them.'
    'Assign the immutable stage-approved Microsoft Entra SQL administrator and enable Entra-only authentication outside T20.'
    'Create secret-scoped read assignments for the web identity only after each approved secret exists.'
    'Apply Key Vault-reference app settings and the immutable package setting in one T21-controlled configuration step.'
    'Run health, readiness, synthetic, alert-receiver, rollback, and staging restore evidence before promotion.'
  ]
  sqlConnectionStringSecretName: 'SqlConnectionString'
  applicationInsightsSecretName: 'ApplicationInsightsConnectionString'
  sqlConnectionStringSettingKey: 'ConnectionStrings__HusayniaDatabase'
  applicationInsightsSettingKey: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
  appConfigurationEndpointSettingKey: 'AppConfiguration__Endpoint'
  appConfigurationEndpoint: appConfigurationEndpoint
  storageAccountSettingKey: 'Storage__AccountName'
  storageAccountName: storageAccountName
  keyVaultUriSettingKey: 'KeyVault__Uri'
  keyVaultName: keyVaultName
  sqlServerName: sqlServerName
  sqlDatabaseName: sqlDatabaseName
  applicationInsightsName: applicationInsightsName
  availabilityTestName: availabilityTestName
  logAnalyticsName: logAnalyticsName
  actionOwner: alertConfiguration.owner
  runbookUrl: alertConfiguration.runbookUrl
}

@description('All unimplemented cost-bearing features remain non-effective request metadata. Any request blocks deployment and is NEEDS_DECISION.')
output reservedCapabilities object = {
  privateEndpoints: {
    requested: capabilityRequests.privateEndpoints
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  frontDoorOrCdn: {
    requested: capabilityRequests.frontDoorOrCdn
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  deploymentSlots: {
    requested: capabilityRequests.deploymentSlots
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  defenderForStorage: {
    requested: capabilityRequests.defenderForStorage
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
  zoneOrEnhancedBackupRedundancy: {
    requested: capabilityRequests.zoneOrEnhancedBackupRedundancy
    effective: false
    status: 'REQUEST_ONLY_NEEDS_DECISION'
  }
}

@description('Recovery targets remain planning goals until the T21 Staging restore exercise proves them.')
output recoveryTargets object = {
  rpoHours: targetRpoHours
  rtoHours: targetRtoHours
  stagingRestoreExerciseRequired: true
  sqlPointInTimeRestoreCapability: true
  mediaVersioningAndSoftDeleteCapability: true
  operationalEvidenceVersionAndSnapshotLifecycleCapability: true
  productionStorageLockRequired: true
}
