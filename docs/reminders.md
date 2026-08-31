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

---

## The Azure DevOps MCP server must use `--authentication pat`, not `envvar`

Broken on 2026-08-29 by an edit that swapped `--authentication pat` for
`--authentication envvar` plus an `ADO_MCP_AUTH_TOKEN` entry. Every MCP call then
returned:

    TF400813: The user 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' is not authorized

That GUID is the **anonymous identity**, which is the tell: the request reached Azure
DevOps carrying a credential it did not recognise, rather than being rejected for an
expired or malformed token.

### Why envvar cannot work with a PAT

From `@azure-devops/mcp`'s own `dist/index.js`:

```js
const authHandler = authType === "pat"
  ? getPersonalAccessTokenHandler(Buffer.from(accessToken,"base64").toString("utf8").split(":").slice(1).join(":"))
  : getBearerHandler(accessToken);
```

Two things happen **only** in `pat` mode. The token is decoded, stripped of its leading
`":"` and handed to the Basic-auth handler; and a global `fetch` interceptor is installed
that rewrites `Bearer` → `Basic` for the many tool call sites that build headers
themselves. In every other mode the value goes out as `Authorization: Bearer …`.

A PAT is not a bearer token. Measured with the same credential:

    Basic  <b64>  ->  200
    Bearer <b64>  ->  302   (redirect to sign-in, i.e. unauthenticated)

`envvar` mode is for an Entra/OAuth access token. Its error text says "Personal Access
Token", which is what makes the wrong choice look right.

### The diagnostic order still holds

An expired PAT and a wrong auth mode produce the same `401`/`TF400813`. Check in this
order, because each step is cheaper than the last:

1. **Is the token alive?** `Invoke-RestMethod` with `Authorization: Basic $b64` — see the
   PAT entry above. `200` means the token is fine and the problem is configuration.
2. **Is the mode `pat`?** Anything else sends Bearer.
3. **Only then** re-derive the encoding.

### Two clients, two config files — do not cross them

| Client | File | Notes |
|---|---|---|
| Claude Code | `.mcp.json` (repo root) | tracked in git |
| Copilot CLI | `~/.copilot/mcp-config.json` | `~/.copilot/servers/` is the registry cache, not user config |

Both use the `mcpServers` shape and both inherit the parent environment, so neither needs
an `env` block — `PERSONAL_ACCESS_TOKEN` is a User environment variable and is picked up
directly. That also keeps the credential out of both files.

`${env:VAR}` is **VS Code** substitution syntax and belongs in neither of these.

### After changing either file

The server process is spawned when the client starts. Editing the file changes nothing
until the client respawns it — reconnect the MCP server, or restart the client. A config
that looks correct on disk while every call still fails usually means exactly this.
