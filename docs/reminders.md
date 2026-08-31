# Reminders

Dated things that will break silently if they lapse. Each entry says what expires,
when, and — more usefully — what the failure looks like when it does.

---

## Azure DevOps PAT — expires 2026-11-25

Held in the `PERSONAL_ACCESS_TOKEN` user environment variable, base64-encoded as
`":" + <pat>` (see `docs/decisions.md`, "The Azure DevOps MCP server's PAT mode expects
a pre-encoded credential"). It authenticates the Azure DevOps MCP server, which is how
failed pipeline runs get diagnosed from the terminal.

Created 2026-08-27 with a 90-day lifetime, scoped to Code (Read), Build (Read) and
Project and Team (Read) — the three the MCP server's `core`, `repositories` and
`pipelines` domains need, and nothing more.

The first token was issued with the 30-day default, which would have expired on
2026-09-25 — inside the project window. Regenerating at 90 days moves the expiry
past the assignment rather than into the middle of it.

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

Renew at https://dev.azure.com/khalid-shams/_usersSettings/tokens, then run
`infra/scripts/set-ado-pat.ps1`. That script validates the token against Azure DevOps
*before* storing it, so a bad paste fails immediately with a specific reason rather
than being written and surfacing later as an auth error. Do not hand-roll the
encoding — three attempts at it by hand produced two silently empty values.

Restart Claude Code fully afterwards. The MCP server inherits its environment from
the Claude Code process, which captured it at launch, so an MCP reconnect alone does
not pick up a new value.

### Why a PAT rather than `az login`

The `khalid-shams` organisation is MSA-backed and returns no `X-VSS-ResourceTenant`.
The `az login` identity is an MSA **guest** in tenant `8edd202c`, which Azure DevOps
resolves to a different identity than the MSA owning the organisation, so
`--authentication azcli` fails with `TF400813`. Interactive auth works but holds its
token only in memory, re-prompting for a browser sign-in on every server start.

The same guest identity is worth watching in spike 3 — an `#EXT#` principal can also
affect token claims in the PKCE flow.
