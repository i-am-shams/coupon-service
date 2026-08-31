# Deployment

**One pipeline, six stages, an empty resource group in and a working system out.**

Deleting the resource group and re-running `azure-pipelines.yml` produces a running system with
no manual intervention. That is the acceptance test for this project, and everything below is
organised around keeping it true.

**It has been run.** On 2026-08-30 the resource group was deleted and the pipeline re-run from
empty: build 22, 17 minutes, six of six smoke assertions, and a signed-in order placed through
the rebuilt system afterwards by hand. Stage timings and what did and did not come back
identical are in §6.

---

## 1. What gets deployed

One resource group, `rg-coupon-service`, in `centralindia`.

| Resource | Name (this deployment) | Notes |
|---|---|---|
| API Management | `apim-couponsvc-lab-dtjori` | Consumption tier |
| App Service | `app-couponsvc-lab-dtjori` | Linux B1, on `plan-couponsvc-lab` |
| Azure SQL server | `sql-couponsvc-lab-dtjori` | Entra-only authentication |
| Azure SQL database | `sqldb-couponsvc-lab` | Basic, 5 DTU |
| Storage account | `stcouponsvclabdtjori` | `$web` static site; **name deterministic**, see below |
| Managed identity | `id-couponsvc-lab-api` | App Service → SQL |
| Managed identity | `id-couponsvc-lab-gateway` | APIM → App Service |
| Application Insights | `appi-couponsvc-lab` | Gateway and backend |
| Log Analytics | `log-couponsvc-lab` | Workspace behind App Insights |
| Metric alert | `alert-app-…-http5xx` | 5xx rate |

**Live URLs**

- Frontend — <https://stcouponsvclabdtjori.z29.web.core.windows.net>
- Gateway — `https://apim-couponsvc-lab-dtjori.azure-api.net`

Every name carries a `uniqueString` suffix and none is computable outside ARM. The storage
account matters more than the others, because its name is in the frontend URL and that URL is a
registered Entra redirect URI — so it has to survive a teardown and rebuild.

It does, and the mechanism is worth stating precisely because "pinned" would be the wrong word:
the suffix is `uniqueString(subscription().id, resourceGroup().name)`, and **neither input
changes when the group is deleted and recreated in the same subscription**. The name is
therefore reproduced identically rather than held. `main.bicep` has a `storageAccountName`
parameter that would pin it literally; the pipeline does not pass it, because it has not needed
to. Deploying to a different subscription or a differently named group *would* change the name,
and that is what the parameter is for.

See §6 for the part of that URL that is still not guaranteed even so.

Roughly **$20/month** at rest. One resource group, so `az group delete -n rg-coupon-service`
removes all of it.

---

## 2. Day 0 — done once, by hand, and documented

A pipeline cannot create the credential it uses to log in. Being clear about where that line
falls is better than pretending it is not there.

**Five items.** No database credential, no tenant-level role grant, no Key Vault — but five,
not three. Two of them are configuration rather than identity and were previously left implicit
here, while §4 below listed them; a reviewer reading "three" and then meeting a fourth is
exactly the failure this section exists to prevent.

| | Item | Why a pipeline cannot do it |
|---|---|---|
| 1 | Azure DevOps project, this repository, and a pipeline pointed at `azure-pipelines.yml` | The pipeline cannot create itself |
| 2 | An Azure service connection | A pipeline cannot create the credential it logs in with |
| 3 | Two Entra ID app registrations | They live in Microsoft Graph, not ARM — and this completes in **two sittings** |
| 4 | A reviewer test account, and tenant security defaults off | Identity governance, and a tenant-wide switch |
| 5 | The pipeline variables in `azure-pipelines.yml` set for your subscription and tenant | They name the service connection and the app registrations from items 2 and 3 |

Items 1 and 5 are unavoidable and uninteresting, which is why they were missing: nobody thinks
of "create the pipeline" as a step. They are steps. Item 5 in particular is an **edit to a file
in this repository** — the variables block in §3 — and it has to happen after item 3, because
it carries those client IDs.

Items 2, 3 and 4 are the ones with substance, and they are below.

### Item 2 — Azure subscription and an Azure DevOps service connection

Workload identity federation with OpenID Connect — **not** a service principal secret. There is
nothing to store and nothing to rotate; Azure DevOps and Entra trust each other directly and the
pipeline receives a short-lived token per run.

Scoped to the **subscription**, not to a resource group. Azure DevOps recommends the narrower
scope and in a long-lived environment that is right, but the pipeline creates its own resource
group as its first action, so a group-scoped connection would need someone to create that group
by hand first — which contradicts the no-manual-steps requirement. In a real environment the
connection would be scoped to a pre-provisioned group, treated as part of the platform rather
than the workload.

The connection needs **Contributor** on the subscription. It does *not* need User Access
Administrator, and deliberately does not have it — see [authentication.md](authentication.md) §9.

### Item 3 — Two Entra ID app registrations

`coupon-api` and `coupon-spa`. Both single-tenant. Details, IDs and the scope configuration are
in [authentication.md](authentication.md) §2.

App registrations live in Microsoft Graph, not in ARM, so Bicep cannot create them. Doing it from
a pipeline needs `Application.ReadWrite.All`, which most tenants block and which would let the
deployment grant itself more permissions. Creating identity objects belongs in identity
governance, not in an application pipeline. Their IDs are passed into Bicep as parameters.

**This item completes in two sittings**, because half of it is not knowable until the storage
account exists:

- **At the start:** create both registrations, expose `Orders.Write` on `coupon-api`, set
  `requestedAccessTokenVersion = 2`, grant the scope tenant-wide, and register
  `http://localhost:5173` on `coupon-spa` under the **SPA** platform.
- **After the first provision:** add the deployed static-site origin **alongside** the localhost
  one. Not replacing it — the failure mode is deleting the localhost URI on the assumption that a
  registration has one redirect URI, and losing local development for the rest of the project.

**Setting the redirect URIs is the step that goes wrong.** Two `az` commands look right and are
not:

- `az ad app update --set spa.redirectUris=...` fails with `Couldn't find 'spa' in ''` when the
  platform object does not exist yet, because `--set` walks a path into an absent node rather than
  creating it.
- `--public-client-redirect-uris` writes to the **wrong platform entirely, and succeeds**, which
  is worse. Sign-in then fails in the browser with no server-side error anywhere.

The route that works is a Graph PATCH with a body file:

```bash
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications/<OBJECT-ID>" \
  --headers "Content-Type=application/json" \
  --body "@spa-redirect-uris.json"
```

```json
{
  "spa":          { "redirectUris": ["http://localhost:5173",
                                     "https://stcouponsvclabdtjori.z29.web.core.windows.net"] },
  "publicClient": { "redirectUris": [] },
  "web":          { "redirectUris": [] }
}
```

Three details in that:

- It takes the application's **object ID**, not its application (client) ID. Graph addresses the
  directory object; `az ad app show --id <appId> --query id` returns it.
- A PATCH **merges**. Sending only `spa` leaves anything already under `publicClient` in place, so
  a registration written to the wrong platform first ends up with the URIs on both. The body must
  empty the platforms it is not using — which is why the JSON above sets all three.
- Graph returns **204 with no body** on success, so verify by reading the registration back rather
  than by trusting the exit code.

Under PowerShell, quote the URI: PowerShell parses the parentheses in a Graph URL. Or run it from
bash.

### Item 4 — A reviewer test account

A **member** of the tenant, not a guest. The tenant-wide `AllPrincipals` grant covers the
application, but a guest's first sign-in can still surface prompts, and a member account removes
that variable at no cost. Guest identities also carry an `idp` claim and no `upn`.

**Disable Entra security defaults on the tenant**, and treat this as part of the same Day 0 item
rather than an afterthought. Entra admin centre → Overview → Properties → *Manage security
defaults* → Disabled.

Security defaults are **on by default**, so a tenant rebuilt from scratch re-enables them and the
reviewer meets a wall the README says is not there. This is the one Day 0 item whose absence
fails nowhere on the server: the pipeline goes green, the smoke test passes, and the failure is a
person unable to sign in.

Signing in once to clear the registration prompt is *not* sufficient on its own, and that is the
trap. Registration and challenge are different things: clearing the registration screen still
leaves MFA enforced, and a reviewer signing in from an unfamiliar device, network and country is
exactly the risk profile that triggers a challenge — for which the second factor is on your
phone, not theirs. A username and password would not be enough no matter how thoroughly the
account was registered.

Conditional Access would scope the exemption to this one account instead of the whole tenant, and
is the right answer anywhere real. It needs an Entra ID P1 licence; this tenant has no licensed
SKUs, so the tenant-wide switch is the only lever. See [assumptions.md](assumptions.md) §1.6.

After disabling it, sign in once in a private window at the frontend URL and confirm you go
straight from password to the application.

Credentials go in [../README.md](../README.md), and the account should be deleted once the review
is finished.

---

## 3. Day 1 — the pipeline

`azure-pipelines.yml` at the repository root. Triggers on `master` and `main`. Microsoft-hosted
`ubuntu-latest`.

### Variables you would change to deploy this elsewhere

| Variable | Value here | What it is |
|---|---|---|
| `serviceConnection` | `azure-lab` | The Azure DevOps service connection |
| `resourceGroupName` | `rg-coupon-service` | Created by the pipeline |
| `location` | `centralindia` | |
| `namePrefix` / `environmentName` | `couponsvc` / `lab` | Feed every resource name |
| `apiClientId` | `90a27142-…` | `coupon-api`. Public identifier |
| `spaClientId` | `04a724e0-…` | `coupon-spa`. Public identifier |
| `tenantId` | `8edd202c-…` | Needed by the frontend build for MSAL's authority |
| `expectedFrontendOrigin` | `https://stcouponsvclabdtjori.z29.web.core.windows.net` | The record of what was registered on `coupon-spa` |
| `apimPublisherEmail` / `apimPublisherName` | | APIM service notifications only |

**No secret appears in this file.** Every GUID above is a public identifier.

### The stages

```
1  Build and test          dotnet build, BDD suite, publish artifacts
2  Provision               purge soft-deleted APIM, deploy infra/main.bicep, read outputs
3  Grant database access   derive the SID, create the contained user
4  Deploy backend          push the zip; EF migrations run at startup
5  Build and deploy UI     npm build with injected config, verify, upload to $web
6  Smoke test              six assertions through the gateway and against the static site
```

**Two orderings are load-bearing rather than tidy.**

*Build and test run first*, so no Azure resource is ever created for code that does not compile.

*Grant database access runs before the backend is deployed.* The application runs its EF Core
migrations at startup, so an app deployed before its database user exists starts, fails, and
reports a **deployment** problem for what is actually a **permissions** problem.

The frontend build comes after provisioning because the gateway URL is compiled into the
JavaScript. That is a real dependency, not a preference.

### Stage 1 — Build and test

`dotnet restore`, `build`, `test` (the Reqnroll suite), publish test results, publish the API as a
zip, and publish the `infra/` and `web/` sources as artifacts so later stages do not need the
repository checked out again.

### Stage 2 — Provision

Creates the resource group, then **purges any soft-deleted API Management service for this
project** before deploying — see §6, this is the step that keeps "delete the group and re-run"
true.

Deploys `infra/main.bicep` and republishes its outputs as stage outputs: App Service name,
identity name and client ID, SQL FQDN and database, APIM name and gateway URL, both subscription
names, storage account name, frontend origin.

Bicep uses `loadTextContent` for the policy XML and the OpenAPI contract, so both stay reviewable
as real files rather than being embedded as strings.

### Stage 3 — Grant database access

`infra/scripts/grant-db-access.ps1` converts the identity's client ID to SID bytes and runs
`grant-db-access.sql`. The mechanics — why the SID is the client ID, the byte order, and why the
script compares SIDs rather than names — are in [authentication.md](authentication.md) §7.

It uses `Invoke-Sqlcmd -AccessToken`, not `sqlcmd -G`: `sqlcmd`'s Entra modes do not read the
Azure CLI token cache on a Microsoft-hosted agent, and under workload identity federation there
is no secret and no interactive session to fall back on. The script asks `az` for a token scoped
to `https://database.windows.net/` and hands it over directly. A side benefit is that the `.sql`
file stays a real reviewable file, because `Invoke-Sqlcmd -Variable` uses the same `$(Name)`
substitution syntax it was already written for.

### Stage 4 — Deploy backend

A guard step fails with a message naming the cause if `appServiceName` came through empty — see
§6 — then `AzureWebApp` pushes the zip. Migrations and seeding run at startup.

### Stage 5 — Build and deploy frontend

In one task, so the subscription key is fetched, used and discarded without ever becoming a
pipeline variable:

1. **The redirect URI guard** — compare the deployed origin against `expectedFrontendOrigin` and
   fail with both strings side by side if they differ (§6).
2. Read the `frontend` APIM subscription key with `listSecrets`.
3. `npm ci`, then `npm run build` with the five `VITE_` variables injected. The build **fails on a
   missing variable** rather than shipping `undefined`.
4. **Grep `dist/` for the dev-only claims panel marker** and fail if it is present.
5. Enable the static website (`--auth-mode login`; this one works over OAuth).
6. Upload to `$web` with the account key, which is `unset` immediately afterwards.

Why the account key rather than a role assignment is in
[authentication.md](authentication.md) §9. It is a smaller grant than the alternative, not a
shortcut.

### Stage 6 — Smoke test

`infra/scripts/smoke-test.ps1`. Two properties matter more than the assertions themselves.

**It polls.** The deploy step reports `RuntimeSuccessful` while the container is still warming.
Budget: 120s for the gateway, **420s for the backend**, polling every 5 seconds, exiting on the
first success. See §6 for where 420 came from.

**It asserts on content, not status.** A status-only check passes against a deployment that is
not serving the application at all — a single-page app whose fallback returns 200 for every path
is the classic version of that bug.

| # | Call | Expected |
|---|---|---|
| 1 | `GET /menu`, no subscription key | 401, body naming the missing key — so it is the key check and not something else returning 401 |
| 2 | `GET /menu`, with a key | 200, JSON array containing `Margherita` — a value that can only appear if the backend read it out of Azure SQL |
| 3 | `POST /orders`, key but no token | 401, body carrying the `validate-jwt` message naming `Orders.Write` |
| 4 | `POST /orders`, key and a malformed token | 401 from the gateway |
| 5 | `GET` the static site root | 200, title and a hashed bundle reference — the built app, not a fallback |
| 6 | The deployed JavaScript bundle | contains no development-only claims panel |

Each asserts **which component rejected the call**, not only the status, because a 401 from the
key check and a 401 from the token policy are indistinguishable by status alone. On failure the
script prints the 401-vs-403 reading table from §10 of
[authentication.md](authentication.md).

What these six do **not** prove is in [authentication.md](authentication.md) §8. It is a real
limit and it is stated there rather than glossed here.

---

## 4. Running it

### From an empty subscription

1. Complete Day 0 items 1 to 4 in §2, with `http://localhost:5173` as the only redirect URI on
   `coupon-spa` for now.
2. Day 0 item 5: set the pipeline variables in §3 for your subscription and tenant.
3. Run the pipeline. It will complete through stage 6 — the smoke test's frontend assertions pass
   because they do not involve Entra.
4. Take the frontend origin printed by stage 2, add it to `coupon-spa` under the SPA platform, and
   set `expectedFrontendOrigin` to match it character for character.
5. Sign in once with the reviewer account to clear MFA registration.

### Teardown and rebuild

```powershell
az group delete --name rg-coupon-service --yes --no-wait
```

Then re-run the pipeline. The APIM purge step handles the soft-delete residue. The only thing
that can require a Day 0 touch afterwards is the frontend origin, if the storage account's DNS
zone segment changes (§6) — and the redirect URI guard fails the run and tells you so rather than
letting sign-in break silently in a browser.

### Local development

```bash
# API — LocalDB, no Docker, no daemon; it wakes on first connection
dotnet run --project src/PizzaShop.Api

# tests
dotnet test

# frontend, against the deployed gateway
cd web
cp .env.example .env.local     # then fill in the five values
npm install
npm run dev                    # http://localhost:5173, strictPort
```

`http://localhost:5173` is registered on `coupon-spa` and allowed by the gateway's CORS policy, so
a local frontend talks to the deployed gateway with no changes. **The port is pinned** with
`strictPort` — Entra matches redirect URIs exactly, so falling back to 5174 fails sign-in with
`AADSTS50011`.

The five values for `.env.local`: gateway URL, `spaClientId`, `tenantId`,
`api://<apiClientId>/Orders.Write`, and a `frontend` APIM subscription key from
`listSecrets`. `.env.local` is gitignored.

Running with `npm run dev` also enables the **token claims panel**, which is stripped from every
production build. It is the fastest way to see what the gateway will be validating.

### Infrastructure changes

```bash
az bicep build --file infra/main.bicep
az deployment group validate -g rg-coupon-service -f infra/main.bicep --parameters ...
```

Both pass a policy that API Management will reject. **APIM validates policy XML only at save
time**, so budget a provision cycle per policy change — or attach the policy to a scratch API in
the running instance to get the error in seconds.

---

## 5. What is deliberately not automated

**Migrations run when the app starts**, not from a pipeline step. This avoids opening the SQL
firewall to build agents. The trade-off: with more than one instance they can race, and a failure
shows up as a failed *start* rather than a failed *deploy*. Migration bundles with a short
firewall window are the production answer.

**The static website switch is a data-plane setting** with no ARM property, so it cannot live in
Bicep and is a CLI call in stage 5.

**App registrations and redirect URIs** are Day 0, for the reasons in §2.

---

## 6. Things that have already cost a deploy cycle

Every entry here was found the expensive way. They are in the order you would meet them.

### APIM soft-delete keeps the name reserved for 48 hours

`az group delete` does not free an API Management name. The service is soft-deleted, held for 48
hours, and recreating it fails with **"Api Management service name is not available"**.

That message reads as a naming collision — someone took the name, or the `uniqueString` suffix
collided — and sends you off to rename the resource. It is nothing of the sort. Without the purge
step, the project's acceptance test is false for the next two days after every teardown. The
purge matches on the name **prefix**, because the exact name comes from `uniqueString` and is not
computable outside ARM.

**The purge itself was wrong until 2026-08-30, and the reason it survived that long is the
interesting part.** It used `az rest --method delete`. Purging a soft-deleted API Management
service is a *long-running* ARM operation: it answers `202 Accepted` with an async-operation
header and finishes later. `az rest` is a raw HTTP call and does not follow that header, so the
step returned instantly and the Bicep deployment on the next line raced a purge still in
progress — failing with the exact message the step exists to prevent.

**It survived because the branch had never executed.** Every run since the project began found
nothing to purge and logged `none found`, because the resource group had never actually been
deleted: every resource in it was created on 2026-08-27 and the seventeen deployments after that
were incremental. So the single step the headline claim depends on was the single step no run had
exercised, and a green pipeline said nothing about it either way.

It now uses `az apim deletedservice purge`, which polls the operation to completion, and then
polls `checkNameAvailability` on the exact name until ARM reports it free — because the CLI
waiting for the purge and ARM releasing the reserved name are two different events. If the name
is still held after five minutes the stage fails with a message saying so, rather than letting
the template be the thing that discovers it.

**Tested for real on 2026-08-30.** The resource group was deleted and the pipeline re-run;
build 22 succeeded in 17 minutes from empty. The purge took **91 seconds** between being issued
and completing, against a name that `checkNameAvailability` reported as unavailable immediately
beforehand - so the old `az rest` version, which returns in about a second, would have raced it
and failed. The availability poll then succeeded on its first attempt.

The soft-deleted Log Analytics workspace was recovered rather than recreated, as the new check
predicted: the workspace customer ID is identical across the teardown.

**The general point:** a conditional that has never been true is not tested by any number of
green runs. It is code that has been compiled and never run, sitting in the middle of the claim
the whole project is judged on.

### `stageDependencies` only reaches stages you directly depend on

A deploy stage failed in seventeen seconds with `Error: Input required: appName`. It read
`stageDependencies.Provision.Provision.outputs[...]` while declaring `dependsOn: GrantDbAccess`.
`GrantDbAccess` depends on `Provision`, so the ordering was right and the value looked reachable.
It is not: **a transitive dependency resolves to an empty string, with no warning at the
reference.**

An empty variable is not an error where it is referenced — it is an error wherever it is
eventually consumed, stripped of any connection to its origin. Here it surfaced as a missing task
input, which reads as a malformed task definition.

Stages that read provision outputs now list `Provision` alongside their ordering dependency.
Adding it changes no ordering; it only makes the outputs visible. A guard step fails with a
message naming the cause if `appServiceName` is empty.

### An undefined `$(macro)` in a bash step becomes a shell command, silently

The frontend stage failed with two lines describing the same fault at different distances from it:

```
line 41: tenantId: command not found
Error: Missing required build variables: VITE_TENANT_ID
```

`VITE_TENANT_ID="$(tenantId)"` assumed a pipeline variable named `tenantId`. There was none —
`tenantId` was a *Bicep* parameter defaulting to `subscription().tenantId`, which is why it worked
everywhere else.

Azure DevOps leaves an unrecognised `$(name)` in the script text verbatim. Bash reads it as
command substitution, tries to run a program called `tenantId`, fails, and substitutes the empty
string. **Under `set -euo pipefail` this still does not abort**, because the assignment was an
environment prefix on another command and the prefix's failure is not the command's.

So a typo'd or undefined variable becomes an empty value, quietly, in a script otherwise
configured to fail fast. What caught it was the `VITE_` check in `vite.config.ts`. Without it the
frontend would have deployed with `authority: https://login.microsoftonline.com/undefined`, and
the failure would have been a sign-in button that does nothing — no server-side error, nothing in
any log.

**Two checks worth re-running after adding any stage**, because the failure mode is silence
rather than an error:

- cross-check every `$(name)` against the `variables` blocks and the ADO built-in prefixes
  (`Build.`, `Agent.`, `Pipeline.`, `System.`, `Common.`);
- extract each bash `inlineScript` and run `bash -n` over it. That caught mangled line
  continuations in the same commit.

### The measured cold start is 212 seconds, not 33

A spike measured ~33 seconds for a zip deploy and the smoke budget was set to 180 with headroom.
The stage then failed with a flat 404 across 28 attempts — no warming trend, which reads like a
routing fault rather than a timing one. The system was fine; a manual call a minute later
returned the full seeded menu.

The App Service console log gives the real breakdown for a first start after deployment:

```
13:56:47  container start
13:56:55  Updating certificates in /etc/ssl/certs...
13:58:45  4 added, 0 removed; done.               <- 110s on CA certificates alone
13:58:58  Running the command: dotnet "PizzaShop.Api.dll"
14:00:19  Now listening on: http://[::]:8080      <- 81s of EF migrations, seed and JIT
```

Two things a spike could not have shown: the platform spends nearly two minutes rehashing CA
certificates before .NET starts at all, and *this* application creates its schema and seeds it
against a Basic (5 DTU) database on first boot, where the spike app opened one connection and
read a row.

The budget is now **420 seconds**, and the smoke test prints elapsed seconds on success as well as
failure so the real number is recorded on every run. A generous budget costs nothing on a healthy
deploy, because the loop exits on the first success; it only changes how long a genuinely broken
one takes to fail.

**Polling rather than calling once is what made this diagnosable at all.** A single call would
have failed identically and told you nothing about whether the app was coming up.

### The redirect URI guard checks a record, not the registration

The frontend stage fails if the deployed origin stops matching `expectedFrontendOrigin`.

**What it cannot do, so a green run is not over-read:** it does not verify Entra. The pipeline's
principal cannot read an app registration — the same Microsoft Graph wall that made `az ad sp
show` unusable. The variable is a *record of what a human registered*, and the guard proves that
deployment still produces that origin.

The drift it guards against is real. The `zNN` segment of
`https://<name>.zNN.web.core.windows.net` is a DNS zone assigned at account creation and is not
guaranteed to be the same if the account is deleted and recreated. Pinning the account name makes
the URL stable while the account exists; it does not make it predictable across a full teardown.
The current value is `z29`.

Without the guard, a mismatch fails nowhere on the server: the frontend deploys green, the smoke
test passes, and sign-in then fails in the browser with `AADSTS50011`.

### APIM policy schema errors only appear at save time

Two provision cycles were spent on this, and both are recorded in
[authentication.md](authentication.md):

- `authentication-managed-identity` is **inbound-only** and the `backend` section accepts exactly
  one policy, so nothing can sit alongside `<base />` there.
- `validate-jwt` requires `audiences` before `issuers`. Two Microsoft samples show the reverse and
  would be rejected identically.

There is no local validation for policy XML beyond well-formedness.

### `EnableRetryOnFailure` cannot be added on its own

A smoke test recorded an HTTP 500 whose cause was in Application Insights:

```
System.Net.Sockets.SocketException at MenuEndpoints.GetMenu
A connection was successfully established with the server, but then an error occurred
during the login process. (provider: TCP Provider, error: 35)
```

Azure SQL drops connections; a transient failure during the login handshake is expected platform
behaviour. What was wrong is that `AddDbContext` had no retry strategy, so one such fault became a
500 for the customer.

Enabling retries makes `CreateExecutionStrategy()` return `SqlServerRetryingExecutionStrategy`,
which **refuses a user-initiated transaction** — and `PlaceOrder` opens exactly such a
transaction, because it has to. So adding the retry alone would have made every order carrying a
coupon throw, with a message that reads as an EF configuration fault rather than as the documented
consequence of the change you just made. The transaction now runs inside `strategy.ExecuteAsync`;
see [architecture.md](architecture.md) §4 for the two details inside that block.
