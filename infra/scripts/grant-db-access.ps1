<#
.SYNOPSIS
    Creates the contained database user for the App Service's managed identity.

.DESCRIPTION
    Run from the pipeline's grant stage, inside an AzureCLI@2 task so that `az` is
    already signed in as the service connection.

    Two things here are less obvious than they look.

    The SID. It is the identity's CLIENT ID, not its object ID, and the GUID has to be
    converted with [guid]::ToByteArray() rather than by rearranging the string. The
    first three fields of a GUID are little-endian and the last eight bytes are not;
    string manipulation produces a SID that looks plausible and silently fails to
    authenticate. docs/decisions.md, spike 1.

    The authentication. sqlcmd's own Entra modes do not read the az CLI token cache on a
    hosted agent, and there is no interactive session to fall back on, so this asks az
    for a database-scoped access token and hands it to Invoke-Sqlcmd directly. That works
    identically under workload identity federation, where there is no secret to fall back
    on either.

    No password is used, read, or stored at any point.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SqlServerFqdn,
    [Parameter(Mandatory)] [string] $DatabaseName,

    # Becomes the contained user's name. The identity's resource name is used verbatim so
    # the database and the Azure resource are recognisably the same thing.
    [Parameter(Mandatory)] [string] $AppName,

    # The CLIENT ID. Passing the object ID here creates a user that exists and then fails
    # to authenticate, which is the single most expensive mistake on this path.
    [Parameter(Mandatory)] [string] $AppClientId,

    [string] $ScriptPath = (Join-Path $PSScriptRoot 'grant-db-access.sql')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Derive the SID -------------------------------------------------------------------

$clientGuid = [guid]::Empty
if (-not [guid]::TryParse($AppClientId, [ref] $clientGuid)) {
    throw "AppClientId '$AppClientId' is not a GUID. Expected the managed identity's client ID."
}

$sidLiteral = '0x' + (($clientGuid.ToByteArray() | ForEach-Object { $_.ToString('X2') }) -join '')

Write-Host "Server   : $SqlServerFqdn"
Write-Host "Database : $DatabaseName"
Write-Host "User     : $AppName"
Write-Host "Client ID: $AppClientId"
Write-Host "SID      : $sidLiteral"

# --- Acquire a database-scoped access token -------------------------------------------

Write-Host 'Requesting an access token for https://database.windows.net/ ...'
$tokenJson = az account get-access-token --resource 'https://database.windows.net/' --output json
if ($LASTEXITCODE -ne 0) {
    throw 'az account get-access-token failed. Is the AzureCLI task signed in?'
}

# The token is a credential. It is never echoed, never written to a file, and never
# published as a pipeline variable.
$accessToken = ($tokenJson | ConvertFrom-Json).accessToken

# --- Run the grant --------------------------------------------------------------------

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    Write-Host 'Installing the SqlServer module ...'
    Install-Module -Name SqlServer -Scope CurrentUser -Force -AllowClobber -Repository PSGallery
}
Import-Module SqlServer

Write-Host "Applying $ScriptPath ..."
Invoke-Sqlcmd `
    -ServerInstance $SqlServerFqdn `
    -Database $DatabaseName `
    -AccessToken $accessToken `
    -InputFile $ScriptPath `
    -Variable @("AppName=$AppName", "AppSid=$sidLiteral") `
    -QueryTimeout 120 `
    -ErrorAction Stop `
    -Verbose

Write-Host 'Database access granted.'
