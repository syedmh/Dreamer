param deployResources bool
param location string
param tags object
param actionGroupName string
param webAppName string
param sqlServerName string
param sqlDatabaseName string
param applicationInsightsName string
param availabilityTestName string
param approvedEmailReceivers array
param runbookUrl string
param alertOwner string

var invalidReceivers = filter(approvedEmailReceivers, receiver => empty(trim(receiver.name)) || !contains(receiver.emailAddress, '@') || contains(receiver.emailAddress, ' '))
var normalizedRunbookUrl = toLower(trim(runbookUrl))
var runbookUrlValid = startsWith(normalizedRunbookUrl, 'https://') && length(normalizedRunbookUrl) > 11 && contains(replace(normalizedRunbookUrl, 'https://', ''), '.') && !contains(normalizedRunbookUrl, '@') && !contains(normalizedRunbookUrl, '?') && !contains(normalizedRunbookUrl, '#')
var alertingInputsValid = length(approvedEmailReceivers) > 0 && length(invalidReceivers) == 0 && runbookUrlValid && !empty(trim(alertOwner))

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (deployResources && alertingInputsValid) {
  name: actionGroupName
  location: 'global'
  tags: union(tags, {
    alertOwner: alertOwner
    runbookUrl: runbookUrl
  })
  properties: {
    groupShortName: take(replace(actionGroupName, '-', ''), 12)
    enabled: true
    emailReceivers: [for receiver in approvedEmailReceivers: {
      name: receiver.name
      emailAddress: receiver.emailAddress
      useCommonAlertSchema: true
    }]
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' existing = {
  name: webAppName
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: sqlServerName
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' existing = {
  parent: sqlServer
  name: sqlDatabaseName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource availabilityTest 'Microsoft.Insights/webtests@2022-06-15' existing = {
  name: availabilityTestName
}

resource syntheticAvailabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployResources && alertingInputsValid) {
  name: '${webAppName}-synthetic-availability'
  location: 'global'
  tags: tags
  properties: {
    description: 'Synthetic availability failure. Owner: ${alertOwner}. Runbook: ${runbookUrl}'
    severity: 1
    enabled: true
    scopes: [
      availabilityTest.id
      applicationInsights.id
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: availabilityTest.id
      componentId: applicationInsights.id
      failedLocationCount: 1
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
    autoMitigate: true
    targetResourceType: 'Microsoft.Insights/webtests'
    targetResourceRegion: 'global'
  }
}

resource webAppHttp5xxAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployResources && alertingInputsValid) {
  name: '${webAppName}-http5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'App Service HTTP 5xx response count. Owner: ${alertOwner}. Runbook: ${runbookUrl}'
    severity: 2
    enabled: true
    scopes: [
      webApp.id
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'Http5xx'
          metricName: 'Http5xx'
          metricNamespace: 'Microsoft.Web/sites'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
    autoMitigate: true
    targetResourceType: 'Microsoft.Web/sites'
    targetResourceRegion: location
  }
}

resource sqlDatabaseCapacityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployResources && alertingInputsValid) {
  name: '${sqlServerName}-${sqlDatabaseName}-capacity'
  location: 'global'
  tags: tags
  properties: {
    description: 'Azure SQL capacity pressure. Owner: ${alertOwner}. Runbook: ${runbookUrl}'
    severity: 2
    enabled: true
    scopes: [
      sqlDatabase.id
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'SqlCpuPercent'
          metricName: 'cpu_percent'
          metricNamespace: 'Microsoft.Sql/servers/databases'
          operator: 'GreaterThan'
          threshold: 90
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
    autoMitigate: true
    targetResourceType: 'Microsoft.Sql/servers/databases'
    targetResourceRegion: location
  }
}
