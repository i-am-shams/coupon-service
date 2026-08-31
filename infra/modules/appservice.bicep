// App Service plan and site.
//
// Two things make this the backend of a gateway rather than a public web app:
//
//   httpsOnly, and Easy Auth (authsettingsV2) with requireAuthentication. Every request
//   must carry a bearer token issued for coupon-api, and the token's application must be
//   the gateway identity. A browser hitting the azurewebsites.net hostname directly gets
//   401 with `WWW-Authenticate: Bearer realm=<app>.azurewebsites.net`; a caller holding a
//   valid token from some other application gets a bare 403. Both rows are in CLAUDE.md's
//   diagnosis table — that is what they mean when they appear.

@description('Location for the plan and the site.')
param location string

@description('App Service plan name.')
param appServicePlanName string

@description('Web app name.')
param appServiceName string

@description('Resource ID of the user-assigned identity the app runs as.')
param apiIdentityResourceId string

@description('Client ID of that identity. Required by DefaultAzureCredential: with a user-assigned identity there is no single obvious identity to resolve, so it has to be named.')
param apiIdentityClientId string

@description('Client ID of the gateway identity. The only application allowed to call this app.')
param gatewayIdentityClientId string

@description('Fully qualified SQL server name.')
param sqlServerFqdn string

@description('Database name.')
param sqlDatabaseName string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Log Analytics workspace resource ID for diagnostic settings.')
param logAnalyticsResourceId string

@description('Entra tenant issuing tokens this app accepts.')
param tenantId string

@description('Application (client) ID of the coupon-api registration — the audience of every token this app accepts.')
param apiClientId string

@description('Tags applied to every resource.')
param tags object = {}

// No credential of any kind. `Active Directory Default` was verified end to end on the
// spike; the only change is that DefaultAzureCredential now has to be told which
// user-assigned identity to use, which is the AZURE_CLIENT_ID app setting below.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  // B1 rather than F1: the free tier has a daily CPU quota that stops the app once
  // exceeded, which during a review looks like an outage. approach.md §6.
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appServiceName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      // Always On does not remove the cold start after a deploy — the spike measured
      // ~33 seconds — but it stops the app being unloaded between smoke runs.
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      webSocketsEnabled: false
      // No healthCheckPath. Platform health probes are subject to Easy Auth, so
      // pointing one at /health would mark every instance unhealthy unless /health were
      // added to excludedPaths — which would publish it anonymously on the internet.
      // approach.md §4 keeps both health endpoints off the gateway; this keeps them off
      // the public hostname too.
      appSettings: [
        {
          // GetConnectionString("PizzaShop") reads ConnectionStrings:PizzaShop, and the
          // double underscore is the configuration provider's section separator.
          name: 'ConnectionStrings__PizzaShop'
          value: sqlConnectionString
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: apiIdentityClientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          // The pipeline pushes an already-published artifact; Oryx must not try to
          // build it again on the way in.
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
      ]
    }
  }
}

// Easy Auth. This is the half of the trust relationship that the backend owns: the
// gateway trusts the caller's subscription key and token, and the backend trusts the
// gateway — the caller and the backend never trust each other. approach.md §5.
resource authSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: appService
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      requireAuthentication: true
      // Return401, not RedirectToLoginPage. This is an API: an unauthenticated caller
      // should get a status code, not an HTML sign-in page that a client would then
      // try to parse as JSON.
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          // No client secret and no certificate. This app only validates incoming
          // bearer tokens; it never performs a sign-in flow of its own, so it needs no
          // credential of its own either.
          clientId: apiClientId
          // environment().authentication.loginEndpoint is 'https://login.microsoftonline.com/',
          // trailing slash included, so this composes to exactly the v2 issuer observed in
          // docs/decisions.md — no trailing slash after v2.0. A v1 token would carry
          // https://sts.windows.net/<tenant>/ instead and be rejected here.
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
        }
        validation: {
          // Both forms. Entra issues the bare client ID as `aud` for a v2 token — that
          // is the observed value in docs/decisions.md, spike 3 — but the api:// form is
          // what the resource is requested as, and accepting both removes a class of
          // 401 that reads as a broken policy.
          allowedAudiences: [
            apiClientId
            'api://${apiClientId}'
          ]
          defaultAuthorizationPolicy: {
            // The line that makes the gateway the only way in. A token for coupon-api is
            // not sufficient; it must have been obtained by the gateway identity.
            allowedApplications: [
              gatewayIdentityClientId
            ]
          }
        }
      }
    }
    login: {
      tokenStore: {
        // Nothing to store: there is no sign-in flow here and no session to resume.
        enabled: false
      }
    }
  }
}

resource appServiceDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: appService
  name: 'to-log-analytics'
  properties: {
    workspaceId: logAnalyticsResourceId
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
        category: 'AppServiceAppLogs'
        enabled: true
      }
      {
        category: 'AppServicePlatformLogs'
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

// approach.md §9: one alert rule on the 5xx rate, declared in Bicep rather than clicked
// in the portal. It lives here rather than in the monitoring module because it targets
// the site, and the monitoring module runs before the site exists.
//
// `actions` is deliberately empty. Where the alert is delivered is an operational
// decision that belongs to whoever runs this, and an action group with a hardcoded email
// address in a repository is worse than none.
resource http5xxAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-${appServiceName}-http5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'Server errors from the coupon service backend over a five-minute window.'
    severity: 2
    enabled: true
    scopes: [
      appService.id
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'Http5xx'
          metricNamespace: 'Microsoft.Web/sites'
          metricName: 'Http5xx'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    autoMitigate: true
    actions: []
  }
}

@description('Web app name.')
output appServiceName string = appService.name

@description('Default hostname. Reachable, but every request to it without a gateway-issued token returns 401.')
output appServiceDefaultHostName string = appService.properties.defaultHostName
