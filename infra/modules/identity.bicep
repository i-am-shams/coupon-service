// User-assigned managed identities.
//
// These are deliberately NOT system-assigned. A system-assigned identity exposes only
// principalId and tenantId to ARM — there is no clientId — and the client ID is needed
// in two places that cannot reach Microsoft Graph:
//
//   1. the SQL contained-user SID, which is the client ID and not the object ID
//      (docs/decisions.md, "Spike 1 — the SID is the client ID, not the object ID");
//   2. App Service Easy Auth `allowedApplications`, which pins the backend to the
//      gateway and takes application IDs.
//
// Recovering a client ID from a principal ID means `az ad sp show`, a Graph call the
// pipeline's service principal has no permission to make.
//
// The second reason is durability. A system-assigned identity is destroyed with its
// App Service and a recreated app gets a NEW client ID. The SQL contained user, matched
// by name, would survive with a stale SID and fail to authenticate — on a project whose
// acceptance test is "delete the resource group and re-run", that is disqualifying.
//
// This module deploys first so both identities exist before any consumer references
// them, which also keeps the App Service and APIM from depending on each other.

@description('Location for both identities.')
param location string

@description('Identity used by the App Service to reach Azure SQL.')
param apiIdentityName string

@description('Identity used by API Management to reach the App Service.')
param gatewayIdentityName string

@description('Tags applied to both identities.')
param tags object = {}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: apiIdentityName
  location: location
  tags: tags
}

resource gatewayIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: gatewayIdentityName
  location: location
  tags: tags
}

@description('Resource ID of the API identity, for assignment to the App Service.')
output apiIdentityResourceId string = apiIdentity.id

@description('Client (application) ID of the API identity. This is the value the SQL contained-user SID is derived from.')
output apiIdentityClientId string = apiIdentity.properties.clientId

@description('Object (principal) ID of the API identity. Used for Azure RBAC, never for the SQL SID.')
output apiIdentityPrincipalId string = apiIdentity.properties.principalId

@description('Name of the API identity. Used verbatim as the SQL contained-user name.')
output apiIdentityName string = apiIdentity.name

@description('Resource ID of the gateway identity, for assignment to API Management.')
output gatewayIdentityResourceId string = gatewayIdentity.id

@description('Client (application) ID of the gateway identity. Goes into Easy Auth allowedApplications and the APIM authentication-managed-identity policy.')
output gatewayIdentityClientId string = gatewayIdentity.properties.clientId

@description('Object (principal) ID of the gateway identity.')
output gatewayIdentityPrincipalId string = gatewayIdentity.properties.principalId
