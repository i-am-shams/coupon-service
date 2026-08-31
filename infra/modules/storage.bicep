// Storage account hosting the React frontend as a static website.
//
// The account name is pinned by the caller rather than generated, because the static
// website URL has to be registered as an Entra redirect URI and Entra does exact string
// matching. See docs/decisions.md, "Redirect URIs: production must match the deployed
// frontend exactly".
//
// Enabling the static website itself is NOT possible from ARM. It is a data-plane
// setting on the blob service, so the pipeline turns it on with
//   az storage blob service-properties update --static-website ...
// in phase E, alongside the upload. A deploymentScript could do it here, but it would
// spin up a container instance on every provision to set one flag.
//
// primaryEndpoints.web is populated regardless, so the origin this module outputs is
// available to the CORS policy on the very first deployment.

@description('Location for the storage account.')
param location string

@description('Storage account name. Lowercase alphanumeric, 3-24 characters.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Tags applied to the account.')
param tags object = {}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    // The static website endpoint serves $web without anonymous container access, so
    // this stays off. Turning it on would make every other container readable too.
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    accessTier: 'Hot'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// primaryEndpoints.web carries a trailing slash. A CORS <origin> must not — an origin
// with a trailing slash never matches the browser's Origin header, and the failure
// surfaces as a CORS error that reads like a missing policy.
var webEndpoint = storageAccount.properties.primaryEndpoints.web
var webHost = replace(replace(webEndpoint, 'https://', ''), '/', '')
var webOrigin = 'https://${webHost}'

@description('Storage account name.')
output storageAccountName string = storageAccount.name

@description('Static website origin with no trailing slash, for the CORS policy and the redirect-URI comparison in phase E.')
output staticWebsiteOrigin string = webOrigin

@description('Static website endpoint as Azure reports it, trailing slash included.')
output staticWebsiteEndpoint string = webEndpoint
