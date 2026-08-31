The service connection is scoped to the subscription rather than a resource group. Azure DevOps recommends the narrower scope, and in a long-lived environment that's right — but the pipeline creates its own resource group as its first action, so a group-scoped connection would require someone to create it manually first, which contradicts the no-manual-steps requirement. In a real environment I'd scope the connection to a pre-provisioned group and treat that group as part of the platform, not the workload.
Commits are structured by phase rather than squashed, so the sequence of work is visible.
## Service connection
Workload identity federation with OpenID Connect, not a service principal secret.
Nothing to store or rotate; Azure DevOps and Entra trust each other directly and the
pipeline receives a short-lived token per run.

Scoped to the subscription rather than a resource group. Azure DevOps recommends the
narrower scope, but the pipeline creates its own resource group as its first action,
so a group-scoped connection would need someone to create that group by hand first —
which contradicts the no-manual-steps requirement.

Verified 2026-08-26: pipeline created and deleted a resource group successfully.

## Spike 1 — passwordless SQL via managed identity

Verified 2026-08-26.

Created an Azure SQL server with Entra-only authentication (no SQL login exists),
an App Service with a system-assigned managed identity, and a contained database
user for that identity — without granting the SQL Server the Directory Readers role.

The usual route, `CREATE USER ... FROM EXTERNAL PROVIDER`, makes SQL look the
identity up in Entra ID and therefore needs Directory Readers, which requires a
tenant administrator. Supplying the SID directly avoids the lookup entirely.

    CREATE USER [appspike98843] WITH SID = 0x875F35AF9A3FB34BB8F9EA1DC1F2F687, TYPE = E;

Confirmed in `sys.database_principals`:

    name             type_desc       sid
    appspike98843    EXTERNAL_USER   0x875F35AF9A3FB34BB8F9EA1DC1F2F687

### The SID is the client ID, not the object ID

The identity has two GUIDs:

- object ID  86c1786f-7168-43b9-bc90-29eadea1af6d  — used for RBAC role assignments
- client ID  af355f87-3f9a-4bb3-b8f9-ea1dc1f2f687  — used for the SQL SID

SQL wants the client ID. Using the object ID creates a user that exists and then
fails to authenticate.

### Byte order

The GUID's first three fields are little-endian; the last eight bytes are not:

    af355f87 -> 875F35AF
    3f9a     -> 9A3F
    4bb3     -> B34B
    b8f9ea1dc1f2f687 -> unchanged

Derived with `[guid]::ToByteArray()`. String manipulation would produce a SID that
looks plausible and silently fails.

### Conclusion

Passwordless SQL is achievable inside a pipeline with no tenant-admin involvement.
The approach document's §6 stands. No fallback to SQL authentication needed.

### Verified end to end 2026-08-27

The half of the spike that was still open — whether the App Service can actually
*authenticate* as that contained user — is now closed. A throwaway minimal API
(`spike/sql-probe/`) deployed to `appspike98843` opened a connection and returned:

    {
      "ok": true,
      "login": "af355f87-3f9a-4bb3-b8f9-ea1dc1f2f687@8edd202c-5474-4669-9b35-253da7a27cd2",
      "user": "appspike98843",
      "database": "spikedb",
      "isDataReader": true, "isDataWriter": true, "isDdlAdmin": true
    }

The connection string carries no credential and no client ID:

    Server=tcp:sqlspike49500.database.windows.net,1433;Initial Catalog=spikedb;
    Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;

`Active Directory Default` resolves the system-assigned managed identity on App
Service with nothing else configured. This is the string Bicep will set on the real
App Service in phase C.

#### `SUSER_SNAME()` returns the client ID, not the user name

Worth knowing before it looks like a bug. Because the user was created from a SID
rather than `FROM EXTERNAL PROVIDER`, SQL never asked Entra for a display name, so
the login surfaces as `<client-id>@<tenant-id>`. `USER_NAME()` returns the expected
`appspike98843`. Any diagnostic that checks the identity should read `USER_NAME()`;
`SUSER_SNAME()` on its own reads as if the wrong principal connected.

The role membership check was worth including for the same reason: it proves the
`db_datareader` / `db_datawriter` grants landed, not merely that the login succeeded.

#### Two things not to carry into the real template

- `httpsOnly` is `false` on the spike App Service. The real one must set it true.
- The spike SQL server has three overlapping firewall rules pinning a home IP
  (`AllowMe`, `AllowMe2`, `AllowMyRange`). The real template needs `AllowAzure` only.

## Tooling notes

### The Azure DevOps MCP server's PAT mode expects a pre-encoded credential

Undocumented; found by testing. `--authentication pat` reads `PERSONAL_ACCESS_TOKEN`
and passes the value **straight through as the HTTP Basic credential without encoding
it**. So the variable must hold the base64 of `":" + <pat>`, not the raw token.

A raw PAT there produces `401` on every call while the same PAT returns `200` against
the REST API directly — which reads as a broken token rather than a wrongly-shaped
environment variable.

    $pat = [Environment]::GetEnvironmentVariable('PERSONAL_ACCESS_TOKEN','User').Trim()
    $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":" + $pat))
    [Environment]::SetEnvironmentVariable('PERSONAL_ACCESS_TOKEN','User', $b64)

Context for why a PAT at all: the `khalid-shams` organisation is MSA-backed and returns
no `X-VSS-ResourceTenant`, so `--authentication azcli` fails with `TF400813`. The
`az login` identity is an Entra guest in tenant `8edd202c`, which Azure DevOps maps to
a different identity than the MSA that owns the organisation. See `docs/reminders.md`
for the expiry consequence.

### App Service cold start after a zip deploy is ~33 seconds

Measured on the spike: the deployment reported `RuntimeSuccessful` while the container
was still warming, and a request 44 seconds before the startup probe passed got the
stock welcome page and a `404` — against an app that was entirely healthy.

**Consequence for the pipeline.** The smoke-test stage must poll with a retry loop and
an overall timeout, not make a single call. A single call placed straight after the
deploy stage will fail intermittently, and it will fail in the way most likely to be
misread — as a routing or policy fault rather than a timing one.

### The `SUSER_SNAME()` finding belongs in the deliverable documentation

Not just in this log. It is recorded above under spike 1, and it should also appear in
the final `docs/authentication.md` (or `docs/architecture.md`, wherever the managed
identity path is described).

The reason it earns space in a deliverable: anyone diagnosing a managed identity SQL
connection will reach for `SUSER_SNAME()` first, and for a SID-created contained user
it returns `<client-id>@<tenant-id>` rather than the user's name. A working connection
looks like the wrong principal connected. That is a false alarm worth pre-empting for
a reviewer, and it is not obvious from the Microsoft documentation.

## Spike 3 — app registrations and the PKCE flow

Created 2026-08-27. Named for the real project, not the spike: they are Day 0
artifacts per approach.md §7, and renaming them later would mean changing the
audience in the APIM policy and the client ID baked into the React build.

| Registration | Purpose | Application (client) ID |
|---|---|---|
| `coupon-api` | The API. Exposes `Orders.Write`. | `90a27142-cffb-4879-873a-bc0a99112ce7` |
| `coupon-spa` | The React frontend. Public client, PKCE. | `04a724e0-6bd9-4285-ab7f-b00d2ce30e2c` |

Application ID URI: `api://90a27142-cffb-4879-873a-bc0a99112ce7`.
Both are single-tenant (`AzureADMyOrg`). `Orders.Write` is granted tenant-wide with
`consentType: AllPrincipals`, so no user meets a consent prompt.

### `accessTokenAcceptedVersion` was set to 2 before the first token was issued

`api.requestedAccessTokenVersion = 2` on `coupon-api`, set at creation rather than
after the first failed policy. Without it the v2 endpoint still returns a v1 token
with `iss = https://sts.windows.net/{tenant}/` — note the trailing slash — and
`validate-jwt` compares the issuer as an exact string. Setting it first means the
token that gets decoded is the token the policy will eventually see.

### The SPA platform, not Web

`coupon-spa` has its redirect URI under `spa`, with `web` empty. A Web-platform
redirect URI rejects the PKCE flow unless a client secret is supplied, which a browser
cannot hold. The distinction is invisible in the portal until sign-in fails.

### Redirect URIs: production must match the deployed frontend exactly

`http://localhost:5173` is registered now for local development and the spike. It stays.

**The production redirect URI must equal the deployed frontend URL character for
character** — Entra does exact string matching, with no wildcards and no tolerance for
a trailing slash. This is precisely why approach.md §6 pins the storage account name
rather than letting Bicep generate one: the static site URL has to be known *before*
deployment so the redirect URI can be registered in advance. A generated name would
mean the URL is only knowable after the deploy that needs it.

**One registration holds both URIs.** Phase E *adds* the production URI alongside the
localhost one; it does not replace it. Recorded explicitly so it is not rediscovered
under time pressure — the failure mode is deleting the localhost URI on the assumption
that a registration has one redirect URI, and losing local development for the rest of
the project.

### The reviewer test account must be a member, not a guest

Decided 2026-08-27. approach.md §10 assumption 6 says a pre-created test account will
be documented in the README; it does not currently say what kind. It must be a
**member** of the tenant.

**Half of the original reasoning for this turned out to be wrong, and the decision
survives on the other half.** See "Spike 3 — observed token claims" below: a guest's
`preferred_username` came back as the clean `user@example.com`, *not* the
`#EXT#` form. The mangling is a directory artifact, not a token claim.

What still argues for a member account: consent. The tenant-wide `AllPrincipals` grant
covers the application, but a guest's first sign-in can still surface prompts that have
not been cleared in advance, and approach.md §10 assumption 6 promises a reviewer who
can simply sign in. A member account removes that variable at no cost. Guest identities
also carry the `idp` claim and no `upn`, so any future assumption about `upn` being
present would hold for a member and fail for a guest.

**Action:** approach.md §10 assumption 6 needs updating to say "member" when that
document is next revised. Flagged rather than edited, because approach.md is the
approved design document and changes to it should be deliberate.

### Spike 3 — observed token claims

Verified 2026-08-27. Authorization code + PKCE via `@azure/msal-browser` 3.28.1, signing
in as the tenant's own `#EXT#` guest identity. Every value below was read off a decoded
token, not predicted. The token was decoded in the browser — not pasted into jwt.ms,
which would mean handing a live access token to a third party.

#### The four assertions, as observed

| Check | Result | Observed |
|---|---|---|
| `aud` is this API | PASS | `90a27142-cffb-4879-873a-bc0a99112ce7` — the **bare client ID** |
| `iss` is the v2 endpoint | PASS | `https://login.microsoftonline.com/8edd202c-.../v2.0` |
| `scp` contains `Orders.Write` | PASS | `scp = Orders.Write` |
| `roles` absent (delegated flow) | PASS | absent |

#### `aud` is the bare client ID, not the `api://` URI

This is the value the APIM policy must carry, and it is not interchangeable with the
application ID URI. Entra can issue either form depending on token version and how the
scope was requested; **this tenant, for a v2 token, issues the bare GUID**. Writing
`api://90a27142-...` into `<audience>` instead would produce a 401 that reads as a
broken policy rather than a one-token-mismatch.

`ver` came back as `2.0` and `iss` carries the `/v2.0` suffix with no trailing slash,
confirming that setting `requestedAccessTokenVersion = 2` at creation did its job. A v1
token would have carried `https://sts.windows.net/{tenant}/` — trailing slash included.

The policy written from these values is `infra/policies/orders-validate-jwt.xml`.

#### The `#EXT#` guest read: partly right, and wrong on the claim that mattered

Recorded plainly because the wrong half was the half being relied on.

| Prediction | Outcome |
|---|---|
| PKCE flow works normally for a guest | **Held.** No friction, no extra prompt. |
| `iss` and `tid` are the resource tenant, not the MSA's home tenant | **Held.** `tid = 8edd202c-...`. |
| `idp` present where a member's token omits it | **Held.** `idp = live.com`. |
| `oid` is the stable identifier | **Held.** `oid = fe66ec40-...`, matching the directory object. |
| `preferred_username` arrives `#EXT#`-mangled | **Wrong.** It is the clean `user@example.com`. |

The `#EXT#` form — `user_example.com#EXT#@example.onmicrosoft.com` — is
real, and it is what `az ad signed-in-user show` returns and what the SQL server's Entra
admin login holds. But it is a **directory** artifact. The v2 token carries the user's
actual sign-in address instead.

The practical consequence is the opposite of what was assumed: matching a directory UPN
against a token's `preferred_username` will fail *because they differ*, not because the
token is mangled. Neither is a safe key. `oid` is, and `upn` is absent from this token
altogether.

#### Token lifetime is 82 minutes, not 60

`CLAUDE.md` says tokens last one hour. Observed lifetime was **82 minutes** — Entra
randomises access token lifetime between roughly 60 and 90 minutes to avoid fleets of
clients re-authenticating in lockstep. The debugging heuristic still holds (a call that
worked and now returns 401 is probably an expired token); the exact number does not.
