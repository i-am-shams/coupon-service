// API Management — the only public entrance to the system.
//
// Consumption tier, capacity 0. approach.md §6: it provisions in about three minutes,
// where the classic tiers take thirty to forty-five, which makes a from-scratch pipeline
// impractical. The cost of that choice is documented in approach.md §10 — no
// rate-limit-by-key, no response cache, no developer portal.
//
// The API is imported from infra/openapi/pizzashop.openapi.yaml and the policy from
// infra/policies/api-global.xml, both with loadTextContent. Neither is authored in the
// portal, and neither can drift from the repository.

@description('Location for the API Management service.')
param location string

@description('API Management service name. Forms the gateway hostname.')
param apimServiceName string

@description('Publisher email. Required by the resource; used for service notifications.')
param apimPublisherEmail string

@description('Publisher name.')
param apimPublisherName string

@description('Name of the subscription whose key the smoke test uses.')
param apimSubscriptionName string

@description('Resource ID of the user-assigned identity the gateway authenticates to the backend with.')
param gatewayIdentityResourceId string

@description('Client ID of that identity, named explicitly in the policy because a user-assigned identity is not resolvable by default.')
param gatewayIdentityClientId string

@description('Backend base URL, already carrying the /api/v1 suffix.')
param backendUrl string

@description('Resource the gateway requests a backend token for — the coupon-api application ID URI.')
param backendResource string

@description('Static website origin, no trailing slash, allowed by the CORS policy.')
param frontendOrigin string

@description('Local development origin, kept alongside the deployed one so local work keeps functioning against the deployed gateway.')
param localDevOrigin string = 'http://localhost:5173'

@description('Application Insights resource ID for the APIM logger.')
param appInsightsResourceId string

@description('Application Insights connection string, stored as a secret named value and referenced by the logger.')
@secure()
param appInsightsConnectionString string

@description('Log Analytics workspace resource ID for gateway diagnostic settings.')
param logAnalyticsResourceId string

@description('Tags applied to the service.')
param tags object = {}

var apiName = 'coupon-service-api'
var productName = 'coupon-service'
var loggerName = 'appinsights'
var appInsightsNamedValueName = 'appinsights-connection-string'

// Deploy-time substitution into the policy document. __NAME__ rather than {{NAME}}:
// double braces are APIM's own named-value syntax and would be resolved — or rejected —
// by APIM at save time rather than replaced here. Rule 10.
var apiPolicyXml = replace(
  replace(
    replace(
      replace(loadTextContent('../policies/api-global.xml'), '__FRONTEND_ORIGIN__', frontendOrigin),
      '__LOCAL_DEV_ORIGIN__', localDevOrigin),
    '__BACKEND_RESOURCE__', backendResource),
  '__GATEWAY_CLIENT_ID__', gatewayIdentityClientId)

resource apimService 'Microsoft.ApiManagement/service@2022-08-01' = {
  name: apimServiceName
  location: location
  tags: tags
  sku: {
    name: 'Consumption'
    capacity: 0
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${gatewayIdentityResourceId}': {}
    }
  }
  properties: {
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
    virtualNetworkType: 'None'
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2022-08-01' = {
  parent: apimService
  name: apiName
  properties: {
    displayName: 'Coupon Service API'
    description: 'Pizza ordering with coupon support.'
    path: 'api/v1'
    protocols: [
      'https'
    ]
    // The backend already carries /api/v1, and so does the gateway path, so a caller's
    // /api/v1/menu arrives at the App Service as /api/v1/menu.
    serviceUrl: backendUrl
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
    format: 'openapi'
    value: loadTextContent('../openapi/pizzashop.openapi.yaml')
  }
}

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2022-08-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: apiPolicyXml
  }
}

// The product is what carries the subscription requirement. Rate limiting would also be
// applied here rather than at API scope — the Consumption tier only supports the
// subscription-scoped rate-limit policy, and only at product scope.
resource product 'Microsoft.ApiManagement/service/products@2022-08-01' = {
  parent: apimService
  name: productName
  properties: {
    displayName: 'Coupon Service'
    description: 'Access to the pizza menu, coupon preview and order placement.'
    subscriptionRequired: true
    approvalRequired: false
    state: 'published'
  }
}

// One of only two explicit dependsOn in the whole template, and it is here because the
// implicit graph genuinely misses it: this resource identifies the API by a name string,
// not by a reference to the api resource, so nothing tells ARM the API must exist first.
resource productApi 'Microsoft.ApiManagement/service/products/apis@2022-08-01' = {
  parent: product
  name: apiName
  dependsOn: [
    api
  ]
}

// A named subscription so the pipeline can find it. The key itself is generated by APIM
// and read at smoke-test time with listSecrets — it is never an output of this template,
// never a pipeline variable that gets logged, and never written to a file. Rule 7.
resource smokeTestSubscription 'Microsoft.ApiManagement/service/subscriptions@2022-08-01' = {
  parent: apimService
  name: apimSubscriptionName
  properties: {
    displayName: 'Pipeline smoke test'
    scope: product.id
    state: 'active'
    allowTracing: false
  }
}

// The logger takes an instrumentation key or connection string as a credential, so it
// goes in as a secret named value rather than inline. This is a legitimate use of APIM's
// {{ }} syntax — a real named value reference, not a deploy-time placeholder.
resource appInsightsNamedValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apimService
  name: appInsightsNamedValueName
  properties: {
    displayName: appInsightsNamedValueName
    value: appInsightsConnectionString
    secret: true
  }
}

resource apimLogger 'Microsoft.ApiManagement/service/loggers@2022-08-01' = {
  parent: apimService
  name: loggerName
  properties: {
    loggerType: 'applicationInsights'
    description: 'Gateway telemetry for the coupon service.'
    resourceId: appInsightsResourceId
    credentials: {
      connectionString: '{{${appInsightsNamedValueName}}}'
    }
  }
  // The second explicit dependsOn, for the same reason: the logger reaches the named
  // value through a {{...}} string that ARM cannot see into. Without this the logger can
  // be created first and fail to resolve its own credential.
  dependsOn: [
    appInsightsNamedValue
  ]
}

// W3C correlation is the setting that makes approach.md §9 true. Without it the gateway
// and the application produce two unrelated traces in Application Insights and nothing
// joins them; with it, one request is one end-to-end operation.
resource apimDiagnostic 'Microsoft.ApiManagement/service/diagnostics@2022-08-01' = {
  parent: apimService
  name: 'applicationinsights'
  properties: {
    loggerId: apimLogger.id
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    verbosity: 'information'
    logClientIp: true
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
  }
}

resource apimDiagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: apimService
  name: 'to-log-analytics'
  properties: {
    workspaceId: logAnalyticsResourceId
    logs: [
      {
        category: 'GatewayLogs'
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

@description('API Management service name.')
output apimServiceName string = apimService.name

@description('Gateway base URL.')
output apimGatewayUrl string = apimService.properties.gatewayUrl

@description('Name of the smoke-test subscription. Its key is read with listSecrets, not emitted here.')
output apimSubscriptionName string = smokeTestSubscription.name

@description('Name of the imported API, for the phase D operation-level validate-jwt policy.')
output apiName string = apiName
