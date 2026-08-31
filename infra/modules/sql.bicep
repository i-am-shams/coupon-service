// Azure SQL — Entra-only authentication, no password anywhere.
//
// There is no administratorLogin and no administratorLoginPassword on this server, so
// there is no credential for rule 7 to be violated by. The only way in is an Entra
// token: the pipeline's service principal is the server administrator, and the App
// Service reaches the database as a contained user created from its identity's SID by
// infra/scripts/grant-db-access.sql.

@description('Location for the server and database.')
param location string

@description('Logical SQL server name.')
param sqlServerName string

@description('Database name.')
param sqlDatabaseName string

@description('Entra tenant of the administrator.')
param tenantId string

@description('OBJECT ID of the pipeline principal that administers this server. The schema is explicit here: sid is the object ID. The client-ID form in docs/decisions.md applies to the contained USER created inside the database, which is a different thing and the most common way to get this wrong.')
param sqlAdminObjectId string

@description('Display name recorded for the administrator.')
param sqlAdminLogin string

@description('Tags applied to both resources.')
param tags object = {}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
    // ARM applies `administrators` at create time only; on a re-deploy it is ignored
    // rather than rejected, which is what makes re-running the pipeline against an
    // existing server safe. Changing the administrator later needs the child API.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Application'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenantId
      // No SQL login exists on this server at all. Password authentication is not
      // merely unused — it is unavailable.
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
    zoneRedundant: false
    // No autoPauseDelay property appears here, and none is missing. Auto-pause is a
    // serverless-tier feature; the Basic tier has no such behaviour to disable, so
    // approach.md §6's "auto-pause off" is satisfied by the tier choice rather than by
    // a setting. See docs/decisions.md before going looking for a property.
  }
}

// The only firewall rule. 0.0.0.0-0.0.0.0 is the special "allow Azure services" form —
// it does not mean "allow the internet". It covers the App Service and the
// Microsoft-hosted pipeline agent, both of which connect from Azure address space.
//
// No home-IP rules. The spike left three overlapping ones behind; docs/decisions.md
// flags them as spike-only.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

@description('Fully qualified domain name of the server.')
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName

@description('Server name.')
output sqlServerName string = sqlServer.name

@description('Database name.')
output sqlDatabaseName string = sqlDatabase.name
