// Storage account hosting the React frontend as a static website.
//
// The account name is DETERMINISTIC rather than random, because the static website URL has
// to be registered as an Entra redirect URI and Entra does exact string matching. See
// docs/decisions.md, "Redirect URIs: production must match the deployed frontend exactly".
//
// Deterministic, not pinned. This module takes whatever name the caller passes, and
// main.bicep composes it from uniqueString(subscription().id, resourceGroup().name) unless
// its storageAccountName parameter is set — which the pipeline does not set. Neither input
// to that hash changes when the resource group is deleted and recreated in the same
// subscription, so the name comes back identical, which is the property that matters.
//
// It is a weaker guarantee than a literal, and the difference is worth knowing: deploy this
// to a different subscription or a differently named resource group and the name changes.
// main.bicep's storageAccountName parameter exists for that case — set it to recover an
// existing URL. Nothing pins it today because nothing has needed to.
//
// Enabling the static website itself is NOT possible from ARM. It is a data-plane
// setting on the blob service, so the pipeline turns it on with
//   az storage blob service-properties update --static-website ...
// in phase E, alongside the upload. A deploymentScript could do it here, but it would
// spin up a container instance on every provision to set one flag.
//
// The two data-plane calls need different permissions, verified rather than assumed:
//
//   service-properties update  works over OAuth with the pipeline's existing Contributor,
//                              because it maps to the management-plane action
//                              Microsoft.Storage/storageAccounts/blobServices/write.
//   blob upload-batch          does NOT. Writing blob content is a data action and
//                              Contributor returns "You do not have the required
//                              permissions needed to perform this operation".
//
// There is deliberately NO role assignment here to close that gap. Contributor's
// notActions include Microsoft.Authorization/*/Write, so the pipeline cannot create one
// — a role assignment in this template fails the whole deployment with
// "does not have permission to perform action Microsoft.Authorization/roleAssignments/write".
//
// Making it work would mean granting the service connection User Access Administrator,
// which is the power to grant itself any role. The upload instead uses the account key,
// fetched at deploy time and never stored. Contributor already includes listKeys, so that
// key is something this principal can obtain regardless: it adds no privilege, where the
// role-assignment route would add a great deal. See docs/decisions.md.
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
    // Required by the frontend upload, which authenticates with the account key because
    // the pipeline cannot grant itself the data role that would replace it. Turning this
    // off is the follow-on change if the service connection ever gains User Access
    // Administrator.
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
