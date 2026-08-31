# Reminders

Dated things that will break silently if they lapse. Each entry says what expires,
when, and — more usefully — what the failure looks like when it does.

---

## Azure DevOps PAT — expires 2026-09-25

Held in the `PERSONAL_ACCESS_TOKEN` user environment variable, base64-encoded as
`":" + <pat>` (see `docs/decisions.md`, "The Azure DevOps MCP server's PAT mode expects
a pre-encoded credential"). It authenticates the Azure DevOps MCP server, which is how
failed pipeline runs get diagnosed from the terminal.

Created 2026-08-26 with the 30-day default lifetime. **2026-09-25 falls inside the
project window**, so this will expire mid-build rather than after it.

### What the failure looks like

An expired PAT does not announce itself. Every MCP call returns `401`, and the server
still starts cleanly and still lists its tools — so the symptom is indistinguishable
from a malformed `PERSONAL_ACCESS_TOKEN`, a wrong organisation name, or an auth-mode
mismatch. Tonight's genuine config bug (raw token where a base64 credential was
expected) produced *exactly the same* `401`, which is the trap: the obvious next move
is to re-check the encoding, and that is a dead end when the token has simply expired.

**Check expiry first, before re-deriving the encoding.** One command settles it:

```powershell
$b64 = [Environment]::GetEnvironmentVariable('PERSONAL_ACCESS_TOKEN','User').Trim()
Invoke-RestMethod -Uri "https://dev.azure.com/khalid-shams/_apis/projects?api-version=7.1" `
  -Headers @{ Authorization = "Basic $b64" } | Select-Object -ExpandProperty value | Select-Object name
```

`200` with the project list means the token is alive and the problem is elsewhere.
`401` here, when the encoding is known-good, means the token expired.

Renew at https://dev.azure.com/khalid-shams/_usersSettings/tokens — then re-encode
before storing, or it will fail in the way described above.

### Why a PAT rather than `az login`

The `khalid-shams` organisation is MSA-backed and returns no `X-VSS-ResourceTenant`.
The `az login` identity is an MSA **guest** in tenant `8edd202c`, which Azure DevOps
resolves to a different identity than the MSA owning the organisation, so
`--authentication azcli` fails with `TF400813`. Interactive auth works but holds its
token only in memory, re-prompting for a browser sign-in on every server start.

The same guest identity is worth watching in spike 3 — an `#EXT#` principal can also
affect token claims in the PKCE flow.
