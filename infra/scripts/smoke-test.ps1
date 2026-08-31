<#
.SYNOPSIS
    Verifies the deployed system through the gateway, not around it.

.DESCRIPTION
    Two properties matter more than the assertions themselves.

    It polls. The deploy step reports RuntimeSuccessful while the container is still
    warming; the spike measured a ~33 second cold start after a zip deploy, and a request
    made before the startup probe passed got the stock App Service welcome page and a 404
    from an entirely healthy app. A single call here fails intermittently, and it fails in
    the way most likely to be misread — a 404 sends you to the APIM routing configuration
    when the only problem was timing.

    It asserts on content. A status-only check passes against a deployment that is not
    serving the application at all.

    Readiness is established in two steps, gateway first and backend second, so a failure
    says which layer is at fault rather than just that something is wrong.
#>

[CmdletBinding()]
param(
    # e.g. https://apim-couponsvc-lab-abc123.azure-api.net
    [Parameter(Mandatory)] [string] $GatewayUrl,

    # Read from APIM with listSecrets at call time. Never echoed, never written to a file.
    [Parameter(Mandatory)] [string] $SubscriptionKey,

    # A value that can only appear if the backend read it out of Azure SQL.
    [string] $ExpectedPizzaName = 'Margherita',

    [int] $GatewayTimeoutSeconds = 120,
    [int] $BackendTimeoutSeconds = 180,
    [int] $PollIntervalSeconds = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$menuUrl   = "$($GatewayUrl.TrimEnd('/'))/api/v1/menu"
$ordersUrl = "$($GatewayUrl.TrimEnd('/'))/api/v1/orders"

function Invoke-Probe {
    param(
        [string] $Uri,
        [string] $Method = 'GET',
        [hashtable] $Headers = @{},
        [string] $Body
    )

    $requestArgs = @{
        Uri                 = $Uri
        Method              = $Method
        Headers             = $Headers
        SkipHttpErrorCheck  = $true
        MaximumRedirection  = 0
        TimeoutSec          = 30
        ErrorAction         = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $requestArgs.Body        = $Body
        $requestArgs.ContentType = 'application/json'
    }

    try {
        $response = Invoke-WebRequest @requestArgs
        return [pscustomobject]@{
            StatusCode = [int] $response.StatusCode
            Body       = [string] $response.Content
            Error      = $null
        }
    }
    catch {
        # A transport-level failure — DNS not yet resolving, connection reset while the
        # gateway is still coming up. Not a test failure on its own; the caller decides
        # whether the budget is exhausted.
        return [pscustomobject]@{
            StatusCode = 0
            Body       = ''
            Error      = $_.Exception.Message
        }
    }
}

function Wait-For {
    param(
        [string] $Description,
        [int] $TimeoutSeconds,
        [scriptblock] $Probe,      # returns the probe result
        [scriptblock] $IsSatisfied # takes the probe result, returns bool
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    $last = $null

    while ((Get-Date) -lt $deadline) {
        $attempt++
        $last = & $Probe

        if (& $IsSatisfied $last) {
            Write-Host "  PASS  $Description (attempt $attempt)"
            return $last
        }

        $detail = if ($last.Error) { $last.Error } else { "HTTP $($last.StatusCode)" }
        Write-Host "  ...   $Description — not yet ($detail), retrying in ${PollIntervalSeconds}s"
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    Write-Host ''
    Write-Host "  FAIL  $Description — gave up after ${TimeoutSeconds}s ($attempt attempts)"
    if ($null -ne $last) {
        Write-Host "        Last status: $($last.StatusCode)"
        if ($last.Error) { Write-Host "        Last error : $($last.Error)" }
        if ($last.Body)  { Write-Host "        Last body  : $($last.Body.Substring(0, [Math]::Min(500, $last.Body.Length)))" }
    }
    Write-Host ''
    Write-Host '        Reading the failure (CLAUDE.md):'
    Write-Host '          "missing subscription key"                  -> APIM rejected it, key check'
    Write-Host '          the policy error message                    -> APIM rejected it, validate-jwt'
    Write-Host '          WWW-Authenticate: Bearer realm=*.azurewebsites.net -> the backend rejected the gateway token'
    Write-Host '          403 with an empty body                      -> backend: gateway known, not on allowedApplications'
    Write-Host '          404 from APIM, no backend headers           -> path matched no operation; no policy ran at all'
    throw "Smoke test failed: $Description"
}

Write-Host "Gateway: $GatewayUrl"
Write-Host ''

# --- 1. GET /menu with no subscription key -> 401 from the gateway --------------------
#
# This is also the readiness probe for APIM itself: it is answered by the gateway alone
# and never reaches the backend, so a pass here means routing and the key policy are live
# even if the App Service is still warming.

Write-Host '1. GET /api/v1/menu with no subscription key'

$noKey = Wait-For `
    -Description 'gateway rejects a call with no subscription key (401)' `
    -TimeoutSeconds $GatewayTimeoutSeconds `
    -Probe { Invoke-Probe -Uri $menuUrl } `
    -IsSatisfied { param($r) $r.StatusCode -eq 401 }

if ($noKey.Body -notmatch 'subscription key') {
    throw "Expected the gateway's missing-subscription-key message; got: $($noKey.Body)"
}
Write-Host "        body mentions the subscription key, so this is APIM's key check and not something else returning 401"
Write-Host ''

# --- 2. GET /menu with a key -> 200, and real data from Azure SQL ---------------------

Write-Host '2. GET /api/v1/menu with a subscription key'

$withKey = Wait-For `
    -Description "backend serves the menu containing '$ExpectedPizzaName' (200)" `
    -TimeoutSeconds $BackendTimeoutSeconds `
    -Probe { Invoke-Probe -Uri $menuUrl -Headers @{ 'Ocp-Apim-Subscription-Key' = $SubscriptionKey } } `
    -IsSatisfied { param($r) $r.StatusCode -eq 200 -and $r.Body -match [regex]::Escape($ExpectedPizzaName) }

# Asserting on the shape as well as the name: a page that merely contains the word
# would satisfy a substring check without being the menu.
$menu = $withKey.Body | ConvertFrom-Json
if (-not ($menu -is [array]) -or $menu.Count -lt 1) {
    throw "Expected a non-empty JSON array from /menu; got: $($withKey.Body)"
}
Write-Host "        $($menu.Count) items returned, read from Azure SQL through the managed identity"
Write-Host ''

# --- 3. POST /orders with a key but no token -> 401 (PENDING until phase D) -----------

Write-Host '3. POST /api/v1/orders with a subscription key but no access token'

$orderBody = '{"couponCode":null,"items":[{"pizzaId":1,"quantity":1}]}'
$noToken = Invoke-Probe -Uri $ordersUrl -Method 'POST' `
    -Headers @{ 'Ocp-Apim-Subscription-Key' = $SubscriptionKey } `
    -Body $orderBody

Write-Host "  PENDING  observed HTTP $($noToken.StatusCode); expected 401 once phase D applies validate-jwt"
Write-Host '           Not asserted. infra/policies/orders-validate-jwt.xml exists but is not yet attached to the'
Write-Host '           placeOrder operation, so this endpoint is currently reachable with a key alone. A green run'
Write-Host '           of this stage does NOT mean the token policy has been verified.'
Write-Host ''

Write-Host 'Smoke test: 2 assertions passed, 1 pending (phase D).'
