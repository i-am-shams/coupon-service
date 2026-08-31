## Phase A — domain model, ICouponEvaluator, BDD scenarios (2026-08-27)

### CouponBasket carries only the subtotal

`ICouponEvaluator.Evaluate` receives a `CouponBasket(decimal Subtotal)` rather than
the full `Basket`. A coupon rule needs nothing but the basket value; passing the full
basket would give the coupon project visibility into pizza IDs and prices, breaking the
dependency boundary.

### CouponEvaluator is in PizzaShop.Coupons, not PizzaShop.Infrastructure

The implementation works against an injected `IEnumerable<CouponRecord>` catalogue.
Phase B will supply that catalogue from EF Core via an adapter. Keeping the evaluator
in the domain project means the rules are testable without any infrastructure at all.

### Rounding: Math.Round once on the final discount, AwayFromZero

As required by the brief: one rounding call after the full discount is computed, before
capping at the subtotal. `MidpointRounding.AwayFromZero` matches standard retail
rounding (€0.005 rounds up to €0.01).

### Discount cap enforced in two places

`CouponEvaluator.CalculateDiscount` caps the rounded discount at the subtotal.
`PricingService.Price` also floors the total at zero via `Math.Max(0m, ...)`.
The second guard is redundant given the first, but it is the structural invariant the
brief requires — even if a future evaluator implementation skips the cap, the total
cannot go negative.

### Pending scenarios use @ignore, not @pending

Reqnroll's `@ignore` tag generates `[Fact(Skip = ...)]` in the xUnit output, which
registers as Skipped. The custom `@pending` tag + `ScenarioContext.Pending()` throws
`XUnitPendingStepException`, which xUnit counts as a failure. `@ignore` is the correct
mechanism for "this test will be implemented in a future phase".

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

## Phase B — persistence, menu, logging, health checks (2026-08-27)

### Local development database: LocalDB, not Docker

`(localdb)\mssqllocaldb` is used for local development. It requires no Docker installation,
no daemon, and no manual start; it wakes on first connection. This gives the fastest
migration-apply cycle on a Windows dev machine. Docker is the right choice in CI; LocalDB
is the right choice to unblock a developer immediately.

### DatabaseCouponEvaluator adapter — evaluator unchanged, only source changes

`CouponEvaluator` still takes `IEnumerable<CouponRecord>` and is fully testable without a
database. `DatabaseCouponEvaluator` in Infrastructure implements `ICouponEvaluator`, fetches
all coupons from the repository on each call, and delegates to a fresh `CouponEvaluator` with
that data. Fetching all coupons per evaluation is a deliberate trade for simplicity in phase B;
a caching layer (e.g. scoped per-request) would be the production answer.

### ICouponRepository lives in Infrastructure, not in Coupons

The repository interface is a persistence concern. Putting it in `PizzaShop.Coupons` would
add a dependency from the domain project to infrastructure concepts. It belongs in
`PizzaShop.Infrastructure` alongside its implementation.

### Atomic redemption via ExecuteUpdateAsync

Rule 4 requires a single SQL UPDATE. `ExecuteUpdateAsync` with a WHERE clause that includes
the limit check produces exactly that — no EF change-tracker, no read before the write.

### Migrations guarded by IsRelational() for test compatibility

Program.cs wraps `MigrateAsync()` and the seeder in `if (db.Database.IsRelational())`. This
lets WebApplicationFactory-based BDD tests use an in-memory provider without the startup
failing — `MigrateAsync` throws on non-relational providers. The in-memory database is seeded
explicitly by the test factory.

### BDD persistence scenarios use WebApplicationFactory with in-memory EF Core

The two previously @ignore scenarios now run through the real HTTP layer via
`WebApplicationFactory<Program>`. The real database is replaced with an in-memory provider
per scenario. Each test seeds its own data via `PizzaShopWebApplicationFactory.SeedDatabase`,
called after `CreateClient()` (which starts the test server and initialises DI).

### /health vs /health/ready separation

`/health` (liveness) has no external check. A short database interruption must not restart a
healthy app. `/health/ready` checks the `PizzaShopDbContext` and is the target for readiness
probes. Both are registered but neither is exposed through APIM — that is phase C.

## Phase A — the coupon evaluator sees a subtotal, not a basket
DateTimeOffset asOf)`. The implementation passes a `CouponBasket(decimal Subtotal)`
instead. Recording the divergence because the approved design document says otherwise
and a reviewer will read it.

**Why.** `Ordering.Basket` carries `BasketItem.UnitPrice`, so passing it would hand the
coupon domain pizza prices and product IDs — exactly what the `AGENTS.md` dependency
rule forbids ("Coupons must not know about pizza prices, delivery, or orders"). With a
subtotal only, the boundary is not a convention the coupon code is trusted to respect:
it genuinely cannot see what is in the basket.

**The trade, stated plainly.** Every condition in the locked coupon set — expiry,
minimum order value, redemption limit — needs only a subtotal, so nothing in scope is
lost. What this closes off is any *item-scoped* coupon: "10% off pizzas only", buy one
get one, a category restriction, a minimum item count. Adding one of those means
reopening this boundary and passing item data across it, which is a deliberate design
change rather than a small edit.

That is the right trade here. approach.md §2 already rejects free-item coupons for the
same reason — they would tie coupons to the product catalogue for no real benefit — and
§10 locks the set to two types and three conditions. The tighter signature makes that
decision structural instead of implicit.

**Action:** approach.md §2's interface listing should be updated to `CouponBasket` when
that document is next revised.

## Phase A — rule 1 is enforced by construction, not by convention

`Basket` has no public constructor and `BasketItem`'s constructor is `internal`. The
only way to obtain a priced basket is `Basket.FromLines(IEnumerable<BasketLine>, IMenu)`,
which reads every unit price from the menu. `BasketLine` carries `PizzaId` and
`Quantity` and has no field a price could arrive in.

Verified by compiling a probe in `PizzaShop.Api` that tried to fabricate a priced line:

    error CS1729: 'BasketItem' does not contain a constructor that takes 3 arguments

So binding a request DTO onto a priced basket is not something that can be written from
outside the Ordering assembly — it fails the build rather than passing review.

`FromLines` also rejects a quantity below one. A negative quantity would subtract from
the subtotal, which is the same class of problem as accepting a price from the client
and would otherwise have been reachable through a perfectly valid-looking request.

Rule 1 is described in approach.md §4 as the most important decision in the design. It
was previously true only because no code violated it yet.

## Phase B — the in-memory provider is SQLite, not EF InMemory

approach.md §8 says the database "is swapped for an in-memory one". That now means
**SQLite in-memory** specifically, and the distinction is not cosmetic.

The EF Core in-memory provider supports neither of the two mechanisms the design
depends on. Both were confirmed by running them:

    TryRedeemAsync => InvalidOperationException: The LINQ expression 'DbSet<CouponEntity>()...'
    BeginTransaction => Transactions are not supported by the in-memory store

`ExecuteUpdateAsync` is rule 4's atomic redemption, and the transaction is what keeps a
redemption and its order from diverging. Under the EF in-memory provider every order
carrying a coupon returned a 500 — and the suite passed anyway, because the only order
scenario sent no coupon code. The most important concurrency invariant in the design
had no coverage and could not have had any.

SQLite in-memory supports both. The test host holds one open `SqliteConnection` for the
lifetime of the factory, since a SQLite in-memory database exists only while a
connection to it is open, and creates the schema with `EnsureCreated()`.

**Consequence for startup migrations.** The guard in `Program.cs` is now
`!app.Environment.IsEnvironment("Test")` rather than `db.Database.IsRelational()`.
SQLite *is* relational, and the migrations are SQL Server specific, so the old guard
would have run them against SQLite and failed. The trade is that the migration path
itself is not exercised by the test suite — it was not exercised before either, and
the pipeline's deployment is what covers it.

**Action:** approach.md §8 should say "SQLite in-memory" rather than "an in-memory
provider" when that document is next revised, with the reason — otherwise the next
person reaches for the EF provider, which is the obvious choice and the wrong one.

## Phase B — an order records no customer identity

`OrderEntity` has `CreatedAt`, amounts, coupon fields and lines. It has no user column,
and the order endpoint reads no caller identity.

This is deliberate and consistent with approach.md §10 assumption 5: orders are stored
but not fulfilled, and there is no payment step. Nothing in the assignment requires
knowing who placed an order.

**The part worth having visible:** phase D puts `validate-jwt` on `POST /orders`, so
from that point there *is* a signed-in user on every order — and their orders still will
not be attributable to them. A reviewer may reasonably ask why an authenticated endpoint
discards the identity it just authenticated.

The answer, if it stays this way, is that authentication here authorises the action
rather than personalising it: the token proves the caller may place an order, and the
assignment never asks for order history, per-user rate limits, or "my orders". If any of
those were in scope, the token's `oid` claim is the column to add — `oid` rather than
`preferred_username`, per the spike 3 finding on `#EXT#` guests.

Recorded now rather than discovered during phase D.

## Phase C — infrastructure and pipeline (2026-08-27)

### User-assigned managed identities, not system-assigned

Both identities are `Microsoft.ManagedIdentity/userAssignedIdentities`: one for the App
Service (to reach SQL), one for API Management (to reach the App Service).

**Why the obvious choice does not work.** A system-assigned identity exposes only
`principalId` and `tenantId` to ARM. There is no `clientId` anywhere in the resource, and
the client ID is needed twice — as the SQL contained-user SID, and in Easy Auth's
`allowedApplications`, which pins the backend to the gateway. Recovering a client ID from
a principal ID means `az ad sp show`, a Microsoft Graph call. The Azure DevOps service
connection's principal has no Graph application permissions, so it returns
`Authorization_RequestDenied`. Granting it some would add a fourth item to the Day 0 list
in approach.md §7, which is the thing that list exists to prevent.

**The deciding argument was durability, not convenience.** A system-assigned identity dies
with its App Service. Recreate the app alone and the client ID changes while the name does
not, so the SQL contained user — matched by name — survives carrying a SID that no longer
belongs to anything. The app then fails to authenticate, and it fails in a way that reads
as a firewall or connection-string problem. On a project whose acceptance test is "delete
the resource group and re-run the pipeline", that is disqualifying.

A third benefit fell out of it: creating both identities in their own module, before
anything consumes them, removes what would otherwise be a circular reference — the App
Service's auth settings need the gateway's identity, and the gateway needs the App
Service's hostname.

### `AZURE_CLIENT_ID` — a delta from what spike 1 verified

Spike 1 proved this connection string, with no credential and no client ID in it:

    Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<db>;
    Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;

That string is unchanged in `infra/modules/appservice.bicep`. What changed is that it no
longer works on its own.

`Active Directory Default` resolves `DefaultAzureCredential`, which on App Service picks
the **system-assigned** identity with nothing else configured — the spike's setup. With a
user-assigned identity there is no single obvious identity to resolve, so it has to be
named. The App Service therefore carries one extra app setting:

    AZURE_CLIENT_ID = <the api identity's client ID>

`DefaultAzureCredential` reads it and authenticates as that identity. It is not a
credential — a client ID is a public identifier — so rule 7 is untouched.

**Recorded because it reopens a verified result.** The end-to-end proof in spike 1 was
obtained against a system-assigned identity. The connection string survives that change;
the credential resolution does not, and this app setting is the whole of the difference.
The first failure if it were missing would be a login failure from the application with a
perfectly correct connection string, which is a bad place to start debugging.

### Basic SQL: approach.md §6's "auto-pause off" is satisfied by the tier, not a property

The database is `Basic` (5 DTU, 2 GB, ~$5/month), matching the cost guardrail in
EXECUTION-PLAN §8.

Auto-pause is a **serverless**-tier feature. The Basic tier has no such behaviour, so
there is no `autoPauseDelay` in `infra/modules/sql.bicep` and none is missing. A comment
in the template says so at the point where somebody would look.

**Recorded so nobody reads §6, hunts for the property, and concludes the requirement was
skipped.** The alternative reading of §6 — serverless with `autoPauseDelay: -1` — would
satisfy it literally at roughly six times the cost for the same behaviour.

### The pipeline purges soft-deleted API Management services before provisioning

`az group delete` does not free an API Management name. The service is soft-deleted, held
for 48 hours, and keeps its name reserved. Recreating it fails with **"Api Management
service name is not available"**.

**The failure mode this prevents.** That message reads as a naming collision — someone
else took the name, or the `uniqueString` suffix collided — and sends you off to rename
the resource. It is nothing of the sort: it is residue from the previous teardown, and the
name becomes available again on its own after two days, or immediately after a purge.

Without this step, the project's stated acceptance test — delete the resource group, run
the pipeline, get a working system — is false for the next 48 hours after every teardown.

The purge matches on the project's name prefix rather than the exact name, because the
exact name comes from `uniqueString` and is not computable outside ARM.

### `grant-db-access.sql` compares the SID instead of only checking the name

The previous version was `IF NOT EXISTS (... WHERE name = @AppName)`. That is idempotent
for a re-run and wrong for a rebuild.

**The failure mode this prevents.** If the identity is recreated, the client ID changes
and the name does not. The old script finds a user with the right name, does nothing, and
reports success. The App Service then cannot authenticate, and nothing in the pipeline log
points at the grant — that stage was green. The symptom appears one stage later as a
failed application start, or later still as a 500 from a deployed app.

The script now reads the existing user's SID, drops and recreates the user if it differs,
and prints which of the three paths it took. Re-running is genuinely idempotent rather
than merely non-erroring.

### Phase E compares the deployed frontend origin against the registered redirect URI

The provision stage prints the static website origin and a note that it must match the
`coupon-spa` redirect URI character for character.

Pinning the storage account name — which this log already records as the reason not to let
Bicep generate one — makes the URL stable while the account exists. It does not make it
predictable across a full teardown: the `zNN` segment of
`https://<name>.zNN.web.core.windows.net` is a DNS zone assigned at account creation, and
nothing guarantees a recreated account is assigned the same one.

**The failure mode this prevents.** Entra does exact string matching on redirect URIs,
with no wildcards. A mismatch does not fail the deployment, does not fail the smoke test,
and produces no server-side error anywhere — the frontend deploys green and then sign-in
fails in the browser with `AADSTS50011`. Comparing the two values in the pipeline turns a
silent, browser-only failure into a stage that fails with the two strings side by side.

### Easy Auth reuses the `coupon-api` registration; no third app registration

The App Service validates tokens against `coupon-api` (`90a27142-…`) with
`allowedApplications` restricted to the gateway identity's client ID. No new registration
is created, so Day 0 stays at the three items in approach.md §7.

Two things make this work without any directory grant. A managed identity can obtain an
app-only token for `coupon-api` without an app-role assignment — client credentials
returns a token with no `roles` claim, which is fine because Easy Auth's
`allowedApplications` check does not look at roles. And a *user* token for the same
audience is still rejected, because its `appid` is the SPA's, not the gateway's.

`allowedAudiences` carries both `90a27142-…` and `api://90a27142-…`. Spike 3 observed the
bare GUID for a v2 token; the `api://` form is how the resource is requested. Accepting
both removes a class of 401 that reads as a broken policy.

### The caller's token is stashed before the gateway overwrites it

**This entry replaces an earlier one that was wrong, and the wrong version is worth
stating because the reasoning behind it still holds.**

The original design put `authentication-managed-identity` in the policy's `<backend>`
section. The reasoning: it rewrites the `Authorization` header with the gateway's own
token, and inbound sections run outermost-first — API scope before operation scope — so
running it in inbound would replace the caller's token *before* the operation-scope
`validate-jwt` that phase D puts on `POST /orders` ever read it. `validate-jwt` would then
validate the gateway's own token. It would pass. An order endpoint that accepts every
request while reporting successful token validation is the worst available version of that
bug, and no smoke test would catch it.

That analysis is correct. The remedy was not. Build 2 failed in the provision stage with:

    ValidationError, target 'backend':
    Error in element 'backend' on line 78, column 6:
    backend section allows only one policy to be specified

Two separate things were wrong, and the documentation states both plainly:

- `authentication-managed-identity`'s **policy sections are `inbound` only**. It cannot go
  in `backend` at all.
- The `backend` section accepts exactly one policy, so nothing can sit alongside `<base />`
  there regardless.

**The actual fix.** The API-scope inbound policy captures the caller's bearer token into a
`callerToken` variable *before* `authentication-managed-identity` runs, stripping the
`Bearer ` prefix. Phase D's operation policy reads it with `validate-jwt`'s `token-value`
attribute instead of `header-name` — a documented alternative, and the docs are explicit
that the value must not include the `Bearer ` prefix.

`authentication-managed-identity` is now the last statement in inbound at API scope.

An empty `callerToken` for a caller that sent no token is correct and deliberate:
`validate-jwt` fails an empty token value with the configured 401, which is exactly the
third smoke assertion.

**What this cost, for the record:** one seven-minute provision stage. What it did not cost
is phase D discovering a `validate-jwt` that passes everything — which is what the original
ordering analysis was protecting against, and which is still the reason this file has an
entry at all.

### The gateway requests its backend token for the bare application ID

`authentication-managed-identity`'s `resource` is `90a27142-…` rather than
`api://90a27142-…`. The documented example for a caller's own Entra application uses the
bare application (client) ID. Easy Auth's `allowedAudiences` carries both forms, so the
token validates either way; this simply stays on the documented path.

### The measured cold start is 212 seconds, not 33

The smoke test's backend budget was 180 seconds, taken from the spike's ~33 second
measurement with headroom. Build 4 failed it with a flat 404 across 28 attempts — no
warming trend, which initially reads like a routing fault rather than a timing one. The
system was fine: a manual call a minute later returned 200 and the full seeded menu.

The App Service console log gives the real breakdown for a first start after deployment:

    13:56:47  container start
    13:56:55  Updating certificates in /etc/ssl/certs...
    13:58:45  4 added, 0 removed; done.              <- 110s on CA certificates alone
    13:58:58  Running the command: dotnet "PizzaShop.Api.dll"
    14:00:19  Now listening on: http://[::]:8080     <- 81s of EF migrations, seed and JIT

Two things the spike could not have shown. The platform spends nearly two minutes
rehashing CA certificates before .NET starts at all, and this application creates its
schema and seeds it against a Basic (5 DTU) database on first boot, where the spike app
opened one connection and read a row.

The budget is now 420 seconds. A generous budget costs nothing on a healthy deploy — the
poll exits on first success — and only changes how long a genuinely broken one takes to
fail. The smoke test now prints elapsed seconds on success as well as failure, so the real
number is recorded on every run rather than rediscovered.

**The reasoning in EXECUTION-PLAN §2 was right and its number was wrong.** Polling rather
than calling once is what made this diagnosable at all; had the stage made a single call it
would have failed identically and told us nothing about whether the app was coming up.

### `stageDependencies` only reaches stages the current stage directly depends on

Build 3's deploy stage failed in seventeen seconds with `Error: Input required: appName`.

`DeployBackend` read `stageDependencies.Provision.Provision.outputs['deploy.appServiceName']`
while declaring `dependsOn: GrantDbAccess`. `GrantDbAccess` depends on `Provision`, so the
stage ran in the right order and the value looked like it should be reachable. It is not:
a transitive dependency resolves to an **empty string**, with no warning at the reference.

The tell was that stage 3 worked and stage 4 did not, using the identical expression form.
Stage 3 declares `dependsOn: Provision` directly.

**The failure mode.** An empty variable is not an error where it is referenced — it is an
error wherever it is eventually consumed, stripped of any connection to its origin. Here it
surfaced as a missing task input, which reads as a malformed task definition. `SmokeTest`
had the same defect and would have failed next for a third apparently unrelated reason.

`DeployBackend` and `SmokeTest` now list `Provision` alongside their ordering dependency.
Adding it changes no ordering, because the dependency already existed transitively; it only
makes the outputs visible.

A guard step in the deploy stage now fails with a message naming the cause when
`appServiceName` is empty. Phase E adds stages that will read the same outputs, and this is
the mistake they will make.

### The SQL administrator's object ID comes out of the ARM access token

`Microsoft.Sql/servers`'s `administrators.sid` is the **object** ID — the resource schema
says so explicitly — which is the opposite of the contained-user rule recorded under spike
1. The pipeline gets it by base64-decoding the payload of its own ARM access token and
reading the `oid` claim.

This needs no directory permission at all, which is the same constraint that drove the
user-assigned identity decision. `az ad sp show` would have been the obvious way to get
it, and it is a Graph call the pipeline principal cannot make.

### `Invoke-Sqlcmd -AccessToken`, not `sqlcmd -G`

`sqlcmd`'s Entra modes do not read the Azure CLI token cache on a Microsoft-hosted agent,
and under workload identity federation there is no secret and no interactive session to
fall back on. `infra/scripts/grant-db-access.ps1` asks `az` for a token scoped to
`https://database.windows.net/` and hands it to `Invoke-Sqlcmd` directly.

A side benefit: the `.sql` file stays a real reviewable file, because `Invoke-Sqlcmd
-Variable` uses the same `$(Name)` substitution syntax the script was already written for.

### No `healthCheckPath` on the App Service

App Service platform health probes are subject to Easy Auth. Pointing one at `/health`
would mark every instance unhealthy unless `/health` were added to
`globalValidation.excludedPaths` — which would publish it anonymously on the internet.

approach.md §4 keeps both health endpoints off the gateway; this keeps them off the public
hostname too. Readiness remains available to anything that can reach the app with a
gateway token.

### The OpenAPI contract has no `servers` block

API Management supplies the backend address from `serviceUrl` on the API resource, built
from the App Service's hostname at deploy time. A `servers` entry would be either a
relative URL the importer cannot resolve, or a hardcoded hostname that goes stale the
first time the resource group is recreated.

### Application Insights sink and correlation ID — approach.md §9 now has code behind it

Both were promised by §9 and neither existed. They land in phase C rather than later
because this is the phase where the infrastructure to receive them appears, and shipping a
phase where the design document and the code disagree is worse than the extra twenty
lines.

- `Serilog.Sinks.ApplicationInsights` writes log lines to the traces table.
  `Microsoft.ApplicationInsights.AspNetCore` is pinned to **2.22.0**: the Serilog sink
  requires `Microsoft.ApplicationInsights < 3.0.0`, and taking the 3.x AspNetCore package
  produces an `NU1107` version conflict.
- `CorrelationIdMiddleware` keeps the gateway's `x-correlation-id`, pushes it onto the
  Serilog log context, mirrors it onto `Activity.Current` as a **tag**, and echoes it in
  the response.
- `ProblemDetails` now reports that same value as `traceId`, so the ID a caller quotes
  from a failed response is the ID in the logs. It previously reported the Activity's own
  ID, which is a different string.

The Activity mirroring is a tag rather than a replacement of the trace ID on purpose:
overwriting the Activity's identifiers would break the W3C trace context that joins the
gateway's telemetry to the application's, which is the thing §9 wanted in the first place.
APIM's diagnostic sets `httpCorrelationProtocol: W3C` for the same reason.

## Phase D — token validation on the order endpoint (2026-08-27)

### `EnableRetryOnFailure`, and why it could not go in alone

Build 5's smoke test recorded an HTTP 500 on its first attempt. Application Insights has
the cause:

    System.Net.Sockets.SocketException at PizzaShop.Api.Endpoints.MenuEndpoints.GetMenu
    A connection was successfully established with the server, but then an error occurred
    during the login process. (provider: TCP Provider, error: 35 - An internal exception
    was caught)

This is not a bug in the application and not a misconfiguration. Azure SQL drops
connections; a transient failure during the login handshake is expected behaviour of the
platform. What was wrong is that `AddDbContext` had no retry strategy, so one such fault
became a 500 for the customer. Turning an expected transient into a customer-facing error
is a defect, not a tolerance.

`sql.EnableRetryOnFailure()` is now set on the connection.

**It could not be added on its own.** Enabling retries makes
`Database.CreateExecutionStrategy()` return `SqlServerRetryingExecutionStrategy`, and that
strategy *refuses* a user-initiated transaction:

    The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not
    support user-initiated transactions.

`OrderEndpoints.PlaceOrder` opens exactly such a transaction — it has to, because rule 4's
`ExecuteUpdateAsync` redemption commits on its own and the order write must not be able to
diverge from it. So every order carrying a coupon would have thrown, and the message reads
as an EF configuration fault rather than as the documented consequence of enabling retries.

The transaction is now opened inside `strategy.ExecuteAsync(...)`. Two details in that
block matter:

- **The coupon evaluation moved inside it.** A retry re-runs the whole block against a
  transaction that was rolled back, so a redemption decision taken on an earlier attempt
  must not survive into the next one — otherwise a retried order could be priced as though
  it still held a redemption it no longer has.
- **`ChangeTracker.Clear()` runs first.** A retry begins with a change tracker still
  holding the entities the failed attempt added. Clearing it is what makes the block
  genuinely repeatable rather than merely re-entered.

The BDD suite still passes unchanged. SQLite returns the default non-retrying strategy, so
`ExecuteAsync` runs the block once and the scenarios exercise the same code path.

### `UseHttpsRedirection` is Development-only

Every boot logged `Failed to determine the https port for redirect`. Behind API Management
the app is reached over the platform's own HTTPS listener and there is no HTTPS port for
the middleware to redirect to, so it no-ops; `httpsOnly` on the App Service is what
actually enforces the guarantee.

Removed from the production path rather than left to warn. A warning that fires on every
healthy start teaches people to ignore start-up warnings, which is worse than the
milliseconds of middleware it was costing.

### `validate-jwt` is attached to the operation, not the API

`infra/policies/orders-validate-jwt.xml` is applied to the `placeOrder` operation via
`Microsoft.ApiManagement/service/apis/operations/policies`. The operation itself is created
by the OpenAPI import, so it is referenced as `existing`; Bicep still emits the ordering
through the parent chain, and the linter flags an explicit `dependsOn` here as
unnecessary.

Scoping it to the operation rather than the API is what approach.md §5 asks for, and it is
also what the design requires: anonymous browsing of the menu and previewing a coupon need
a subscription key and nothing more. Only placing an order needs to know who is asking.

Every value in the policy is one spike 3 observed on a decoded token — bare client-ID
audience, v2 issuer with no trailing slash, `scp` containing `Orders.Write`, no `roles`
block. Placeholders are `__TENANT_ID__` and `__API_CLIENT_ID__`, substituted by Bicep.

### `validate-jwt` child element order is schema, not style

Build 6 failed provisioning with:

    ValidationError, target 'validate-jwt':
    Error in element 'validate-jwt' on line 27, column 10: The element 'validate-jwt' has
    invalid child element 'audiences'. List of possible elements expected: 'required-claims'.

The policy had `issuers` before `audiences`. The canonical statement in the validate-jwt
reference fixes the order as:

    openid-config, issuer-signing-keys, decryption-keys, audiences, issuers, required-claims

and the page states it explicitly: "Set the policy's elements and child elements in the
order provided in the policy statement."

**Worth recording because the documentation contradicts itself.** Searching for samples
returns two — both on the AI-APIs authorization page — that show `issuers` before
`audiences`. They would be rejected exactly as this was. Four other samples, including the
canonical "protect a backend with Microsoft Entra ID" one, show the documented order. A
sample is not the schema; the policy statement block is.

This is the second policy-schema failure in this project after the
`authentication-managed-identity` placement, and both cost a provision cycle to discover
because APIM validates policy content only at save time — `az bicep build`,
`az deployment group validate` and `what-if` all pass a policy that the APIM resource
provider will reject. There is no local validation for policy XML beyond well-formedness.

**Consequence for phases E and F:** a policy change is not testable before deployment.
Budget a provision cycle for each one, or attach the policy to a scratch API in the running
APIM instance first to get the validation error in seconds rather than minutes.

### The two tokens, and which parts of them the smoke test can prove

A request to `POST /orders` involves two different tokens and both have to be right:

| Token | Obtained by | Validated by | Carries |
|---|---|---|---|
| Caller's | The React app, PKCE | `validate-jwt` at the operation | `scp = Orders.Write` |
| Gateway's | APIM's user-assigned identity | App Service Easy Auth | no `scp`; app-only |

They are kept apart by the `callerToken` variable: the API-scope policy captures the
caller's bearer token before `authentication-managed-identity` overwrites the header, and
the operation policy reads it with `token-value`.

`backendResource` and `apiClientId` are separate Bicep parameters that hold the same GUID,
deliberately. One is what the gateway asks Entra for; the other is the audience a caller
token must carry. Collapsing them would hide that they answer different questions.

**What the smoke test proves.** That the gateway's token still reaches the backend —
assertion 2, because `/menu` returns 200 only if Easy Auth accepted it, and `/menu` runs
the same API-scope policy. That a request with no token is rejected by `validate-jwt`
specifically — assertion 3 asserts on the policy's own message naming `Orders.Write`,
which neither the subscription-key check nor the backend can produce.

**Confirmed after the fact, from Application Insights.** The smoke assertions alone cannot
say *which* token `validate-jwt` inspected, because both possibilities return 401. The
gateway's own token is app-only and carries no `scp`, so a policy reading the Authorization
header would fail on the required claim and look identical from outside.

APIM records the validation failure reason, and it discriminates:

    placeOrder | TokenNotPresent at validate-jwt | JWT not present.
    placeOrder | Invalid JWT. at validate-jwt    | IDX12741: JWT must have three segments

The first is the no-token assertion. "JWT not present" means an **empty** value was
validated — the caller sent no Authorization header, so `callerToken` was empty. Had the
policy read the header, it would have found the gateway's token there: a real, well-formed,
three-segment JWT, failing on a missing claim rather than on absence.

The second is the malformed-token assertion, rejecting the literal string
`not-a-real-token` for having no segments. The gateway's token has three. Again, only
explicable if the value under validation came from the caller.

So both tokens are confirmed: the caller's is what `validate-jwt` inspects, and the
gateway's is what reaches the backend.

**What is still not proven, and this is the honest limit.** No automated assertion here
covers the positive case: a *valid* caller token producing a 201. That needs a real user
token, which needs an interactive PKCE sign-in, which cannot be done from a pipeline agent
without storing a credential. Until phase E puts MSAL in front of this endpoint, the
positive path is confirmed by a manual browser sign-in and not by the pipeline.

Worth stating plainly because the negative assertions look more complete than they are: if
the policy were reading the Authorization header instead of `callerToken`, it would be
validating the gateway's own app-only token, which has no `scp` claim — so it would *also*
return 401, and every assertion here would still pass while the endpoint was permanently
broken for every real user.

### Flagged for the approach.md revision pass

Not edited — approach.md is the approved design document and the revision list is being
collected for a single deliberate pass. Running list:

1. §2's interface listing should show `CouponBasket`, not `Basket`.
2. §8 should say "SQLite in-memory" rather than "an in-memory provider", with the reason.
3. §10 assumption 6 should say the reviewer account is a **member**, not a guest.
4. §6's "auto-pause off" is satisfied by the Basic tier having none — worth a clause so
   nobody hunts for a property.
5. §7's smoke-test table should carry the measured cold start (212s first boot, 38s warm)
   rather than leaving the budget implicit.
6. §5 says the caller and the backend never trust each other. True, and now worth one
   sentence on *how*: the caller's token is captured before the gateway replaces it, so
   the two tokens are validated by two different parties against two different rules.
