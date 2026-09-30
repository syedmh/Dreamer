param deployResources bool
param location string
param tags object
param logAnalyticsName string
param applicationInsightsName string
param logAnalyticsSkuName string
param logRetentionDays int
param availabilityTestName string
param availabilityTestUrl string
param availabilityTestLocations array
param availabilityTestFrequencySeconds int
param availabilityTestTimeoutSeconds int
param webAppName string

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (deployResources) {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: logAnalyticsSkuName
    }
    retentionInDays: logRetentionDays
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = if (deployResources) {
  name: applicationInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    DisableIpMasking: false
    Request_Source: 'rest'
  }
}

resource availabilityTest 'Microsoft.Insights/webtests@2022-06-15' = if (deployResources) {
  name: availabilityTestName
  location: 'global'
  kind: 'standard'
  tags: union(tags, {
    'hidden-link:${applicationInsights.id}': 'Resource'
    'hidden-link:${resourceId('Microsoft.Web/sites', webAppName)}': 'Resource'
  })
  properties: {
    SyntheticMonitorId: availabilityTestName
    Name: availabilityTestName
    Enabled: true
    Frequency: availabilityTestFrequencySeconds
    Timeout: availabilityTestTimeoutSeconds
    Kind: 'standard'
    RetryEnabled: true
    Locations: availabilityTestLocations
    Request: {
      RequestUrl: availabilityTestUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
      FollowRedirects: true
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}
