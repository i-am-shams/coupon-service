# Stores an Azure DevOps PAT for the Azure DevOps MCP server configured in
# .mcp.json. Developer-machine setup, not part of any deployment: the pipeline
# authenticates with workload identity federation and has no PAT.
#
# Usage:  ./infra/scripts/set-ado-pat.ps1
# Then fully restart Claude Code. Expiry is tracked in docs/reminders.md.
#
# The server's `pat` auth mode uses PERSONAL_ACCESS_TOKEN *directly* as the HTTP
# Basic credential without encoding it, so the variable must hold the base64 of
# ":<pat>". A raw token there returns 401 on every call.
#
# The token is validated against Azure DevOps BEFORE being stored, so a bad paste
# fails here rather than surfacing later as an auth error that looks like a
# config problem. Nothing is echoed to the screen or written to command history.

$ErrorActionPreference = 'Stop'

$org = 'khalid-shams'

Write-Host ''
Write-Host 'Paste the PAT at the prompt below, then press Enter.' -ForegroundColor Cyan
Write-Host '(The text you type is input, not a command - it does not enter history.)'
Write-Host ''

$plain = (Read-Host 'PAT').Trim()

if ($plain.Length -lt 20) {
    Write-Host "Got $($plain.Length) characters - that is not a PAT. Nothing stored." -ForegroundColor Red
    Remove-Variable plain -ErrorAction SilentlyContinue
    return
}

$b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(':' + $plain))

Write-Host "Read a $($plain.Length)-character token. Checking it against $org ..." -ForegroundColor Cyan

$ok = $false
try {
    $r = Invoke-WebRequest -Uri "https://dev.azure.com/$org/_apis/projects?api-version=7.1" `
                           -Headers @{ Authorization = "Basic $b64" } `
                           -MaximumRedirection 0 -UseBasicParsing
    $ok = ($r.StatusCode -eq 200)
    $projects = ($r.Content | ConvertFrom-Json).value.name -join ', '
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 302) {
        Write-Host 'Azure DevOps redirected to sign-in: the credential was empty or malformed.' -ForegroundColor Red
    } elseif ($code -eq 401) {
        Write-Host 'Azure DevOps returned 401: the token is wrong, revoked, or expired.' -ForegroundColor Red
    } else {
        Write-Host "Azure DevOps returned $code." -ForegroundColor Red
    }
}

if (-not $ok) {
    Write-Host 'Nothing stored. The existing value (if any) is untouched.' -ForegroundColor Red
    Remove-Variable plain, b64 -ErrorAction SilentlyContinue
    return
}

[Environment]::SetEnvironmentVariable('PERSONAL_ACCESS_TOKEN', $b64, 'User')
$env:PERSONAL_ACCESS_TOKEN = $b64

$readback = [Environment]::GetEnvironmentVariable('PERSONAL_ACCESS_TOKEN', 'User')

Write-Host ''
Write-Host "Token valid. Projects visible: $projects" -ForegroundColor Green
Write-Host "Stored $($readback.Length) chars in the User environment (HKCU:\Environment)." -ForegroundColor Green
Write-Host ''
Write-Host 'Now fully restart Claude Code.' -ForegroundColor Yellow
Write-Host 'The MCP server inherits its environment from the Claude Code process,'
Write-Host 'which captured it at launch - a reconnect alone will not pick this up.'
Write-Host ''

Remove-Variable plain, b64, readback, r, projects -ErrorAction SilentlyContinue
