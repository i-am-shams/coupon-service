// Log Analytics and Application Insights.
//
// Workspace-based Application Insights, because classic (instrumentation-key only)
// components are retired. Both the gateway and the backend write here, which is what
// lets one correlation ID join an APIM request to the application log lines it produced
// (approach.md §9).
//
// The 5xx alert rule is NOT here. It targets the App Service, and creating it in this
// module would mean either taking the site's resource ID as a parameter — which the
// App Service module cannot supply, since it consumes this module's connection string —
// or synthesising the ID before the site exists. It lives in appservice.bicep instead.

@description('Location for the workspace and the Application Insights component.')
param location string

@description('Log Analytics workspace name.')
param logAnalyticsName string

@description('Application Insights component name.')
param appInsightsName string

@description('Tags applied to both resources.')
param tags object = {}

@description('Retention in days. 30 is the free-tier allowance; nothing here justifies paying for more.')
param retentionInDays int = 30

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    // Sampling is left at 100%. approach.md §9 is explicit that production would drop
    // this to 5-10%; at review volume the full stream is worth more than the pennies.
    SamplingPercentage: 100
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    DisableLocalAuth: false
  }
}

@description('Log Analytics workspace resource ID, for diagnostic settings.')
output logAnalyticsResourceId string = logAnalytics.id

@description('Application Insights resource ID, for the APIM logger and diagnostics.')
output appInsightsResourceId string = appInsights.id

@description('Application Insights connection string. Not a secret in the rule 7 sense — it is an ingestion endpoint plus a key that grants write-only access to telemetry — but it is still never written to a file, only injected as an app setting at deploy time.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Instrumentation key, required by the APIM logger resource, which does not accept a connection string.')
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
