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
// The two data-plane calls need different permissions, verified before this was written:
//
//   service-properties update  works over OAuth with the pipeline's existing Contributor,
//                              because it maps to the management-plane action
//                              Microsoft.Storage/storageAccounts/blobServices/write.
//   blob upload-batch          does NOT. Contributor gets "You do not have the required
//                              permissions needed to perform this operation", because
//                              writing blob content is a data action.
//
// Hence the role assignment below, and no account key anywhere.
//
// primaryEndpoints.web is populated regardless, so the origin this module outputs is
// available to the CORS policy on the very first deployment.

@description('Location for the storage account.')
param location string

@description('Storage account name. Lowercase alphanumeric, 3-24 characters.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Object ID of the principal that runs the pipeline. Granted Storage Blob Data Contributor so the frontend upload can use OAuth instead of an account key.')
param deployingPrincipalObjectId string

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

// Storage Blob Data Contributor. Scoped to this account, not the resource group: the
// pipeline needs to write $web here and nothing else.
//
// The GUID is the built-in role's well-known definition ID. A deterministic name means
// re-running the pipeline updates the same assignment rather than failing on a conflict.
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource uploadRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, deployingPrincipalObjectId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: deployingPrincipalObjectId
    // Without this, a role assignment for a principal ARM cannot yet resolve fails with
    // "Principal does not exist in the directory". The pipeline's principal does exist,
    // but stating the type skips the lookup and the associated replication race.
    principalType: 'ServicePrincipal'
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
