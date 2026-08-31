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

    # The static website origin, no trailing slash. Optional so the script still runs
    # against a deployment without a frontend.
    [string] $FrontendOrigin = '',

    [int] $GatewayTimeoutSeconds = 120,

    # 420s, from a measured first start rather than a guess. Build 4's App Service log:
    #
    #   13:56:47  container start
    #   13:56:55  Updating certificates in /etc/ssl/certs...
    #   13:58:45  done                              <- 110s on CA certificates alone
    #   13:58:58  Running the command: dotnet "PizzaShop.Api.dll"
    #   14:00:19  Now listening on: http://[::]:8080 <- 81s of EF migrations, seed and JIT
    #
    # 212 seconds total. The ~33s in docs/decisions.md was a spike app with no migrations;
    # this one creates its schema and seeds it against a Basic database on first boot.
    #
    # A generous budget costs nothing when the deploy is healthy, because the loop exits on
    # the first success. It only changes how long a genuinely broken deploy takes to fail.
    [int] $BackendTimeoutSeconds = 420,

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

    $started = Get-Date
    $deadline = $started.AddSeconds($TimeoutSeconds)
    $attempt = 0
    $last = $null

    while ((Get-Date) -lt $deadline) {
        $attempt++
        $last = & $Probe

        if (& $IsSatisfied $last) {
            # The elapsed figure is printed on success as well as failure: it is the only
            # place the real cold-start time is recorded, and it is what the timeout above
            # should be set from.
            $elapsed = [int]((Get-Date) - $started).TotalSeconds
            Write-Host "  PASS  $Description (attempt $attempt, ${elapsed}s)"
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

# --- 3. POST /orders with a key but no token -> 401 from validate-jwt ------------------
#
# A real assertion as of phase D. Two things have to be true for this to pass, and they
# involve two different tokens:
#
#   the CALLER's token is what validate-jwt inspects, and
#   the GATEWAY's own managed identity token is still what reaches the backend.
#
# Assertion 2 above covers the second: /menu only returns 200 because the gateway's token
# was accepted by the App Service's Easy Auth, and /menu runs the same API-scope policy.

Write-Host '3. POST /api/v1/orders with a subscription key but no access token'

$orderBody = '{"couponCode":null,"items":[{"pizzaId":1,"quantity":1}]}'

$noToken = Wait-For `
    -Description 'gateway rejects an order with no access token (401)' `
    -TimeoutSeconds $GatewayTimeoutSeconds `
    -Probe {
        Invoke-Probe -Uri $ordersUrl -Method 'POST' `
            -Headers @{ 'Ocp-Apim-Subscription-Key' = $SubscriptionKey } `
            -Body $orderBody
    } `
    -IsSatisfied { param($r) $r.StatusCode -eq 401 }

# Asserting on the policy's own message, not just the status. A 401 here could come from
# the subscription-key check or from the backend; only validate-jwt produces this string.
# CLAUDE.md's table turns on exactly that distinction.
if ($noToken.Body -notmatch 'Orders\.Write') {
    throw "Expected the validate-jwt failure message naming Orders.Write; got: $($noToken.Body)"
}
Write-Host "        body carries the validate-jwt message, so this is the token policy and not the key check"
Write-Host ''

# --- 4. POST /orders with a key and a malformed token -> 401 --------------------------
#
# Distinguishes "the policy is reading the caller's token" from "the policy is reading
# whatever happens to be in the Authorization header". By the time the operation policy
# runs, the header holds the gateway's own token — so if the policy read the header, the
# value under test here would never be seen at all.

Write-Host '4. POST /api/v1/orders with a subscription key and a malformed access token'

$badToken = Invoke-Probe -Uri $ordersUrl -Method 'POST' `
    -Headers @{
        'Ocp-Apim-Subscription-Key' = $SubscriptionKey
        'Authorization'             = 'Bearer not-a-real-token'
    } `
    -Body $orderBody

if ($badToken.StatusCode -ne 401) {
    throw "Expected 401 for a malformed access token; got HTTP $($badToken.StatusCode): $($badToken.Body)"
}
Write-Host "  PASS  gateway rejects an order with a malformed access token (401)"
Write-Host ''

# --- 5. The static website serves the application ------------------------------------
#
# Content-asserted, not status-asserted. A storage static site answers every path with
# its 404 document, and that document is index.html here so a client-side route survives
# a refresh — which means a 200 proves nothing on its own. approach.md §7 records having
# shipped exactly that bug: a single-page app whose fallback returned 200 for every path.
#
# So this checks for markers only the built bundle carries.

if ([string]::IsNullOrWhiteSpace($FrontendOrigin)) {
    Write-Host '5. Frontend — skipped, no origin supplied'
    Write-Host ''
    Write-Host 'Smoke test: 4 assertions passed, 1 skipped.'
    return
}

Write-Host '5. GET the static website root'

$frontend = Wait-For `
    -Description 'static website serves the application shell (200)' `
    -TimeoutSeconds $GatewayTimeoutSeconds `
    -Probe { Invoke-Probe -Uri $FrontendOrigin } `
    -IsSatisfied { param($r) $r.StatusCode -eq 200 -and $r.Body -match '<div id="root">' }

if ($frontend.Body -notmatch '<title>Pizza Shop</title>') {
    throw "The static site responded but does not look like the app: $($frontend.Body.Substring(0, [Math]::Min(300, $frontend.Body.Length)))"
}
if ($frontend.Body -notmatch '/assets/index-') {
    throw 'The static site served index.html without a built asset reference. The upload may be incomplete.'
}
Write-Host '        title and hashed bundle reference both present, so this is the built app'
Write-Host ''

# The claims panel is stripped at build time and the pipeline greps the artifact for it.
# This is the same check against what is actually being served, which is the thing that
# would embarrass anyone.
Write-Host '6. The development-only token claims panel is not deployed'

$bundleMatch = [regex]::Match($frontend.Body, '/assets/(index-[A-Za-z0-9_-]+\.js)')
if (-not $bundleMatch.Success) {
    throw 'Could not find the bundle reference in index.html.'
}
$bundleUrl = "$($FrontendOrigin.TrimEnd('/'))/assets/$($bundleMatch.Groups[1].Value)"
$bundle = Invoke-Probe -Uri $bundleUrl
if ($bundle.StatusCode -ne 200) {
    throw "Could not fetch the deployed bundle at $bundleUrl (HTTP $($bundle.StatusCode))."
}
if ($bundle.Body -match 'PIZZASHOP_DEV_ONLY_TOKEN_CLAIMS_PANEL') {
    throw 'The deployed bundle contains the development-only token claims panel. It renders decoded access token claims and must never be served.'
}
Write-Host "  PASS  deployed bundle carries no token claims panel ($([int]($bundle.Body.Length / 1024)) kB checked)"
Write-Host ''

Write-Host 'Smoke test: 6 assertions passed.'
