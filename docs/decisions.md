## Phase A — domain model, ICouponEvaluator, BDD scenarios (2026-08-27)

### CouponBasket carries only the subtotal

`ICouponEvaluator.EvaluateAsync` receives a `CouponBasket(decimal Subtotal)` rather than
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
`PricingService.PriceAsync` also floors the total at zero via `Math.Max(0m, ...)`.
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
only way to obtain a priced basket is `Basket.FromLinesAsync(IEnumerable<BasketLine>, IMenu)`,
which reads every unit price from the menu. `BasketLine` carries `PizzaId` and
`Quantity` and has no field a price could arrive in.

Verified by compiling a probe in `PizzaShop.Api` that tried to fabricate a priced line:

    error CS1729: 'BasketItem' does not contain a constructor that takes 3 arguments

So binding a request DTO onto a priced basket is not something that can be written from
outside the Ordering assembly — it fails the build rather than passing review.

`FromLinesAsync` also rejects a quantity below one. A negative quantity would subtract from
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

**2026-08-31 — the count is five, not three.** Day 0 was later found to have two items
that were always there and had been left implicit: the Azure DevOps project and pipeline,
and the pipeline variables. So "a fourth item" above reads as "a sixth" today. The current
list is deployment.md §2, which also explains the undercount. approach.md §7 still says
three deliberately, as the record of what was believed when it was written.

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

### Decided: an order records no customer identity, and that is final for this build

Previously recorded as deliberate-but-open, with a note that phase D would make a reviewer
ask about it. Phase D has landed, there is now a signed-in user on every order, and the
decision stands. Recording it as closed so it is not reopened by the next person who
notices the gap.

**The argument that settles it.** Recording the caller's `oid` without enforcing anything
with it collects personal data for no requirement. The scenario that would justify the
column — one account draining a coupon's redemption limit — is not fixable by storing an
identifier. It needs per-user limits enforced inside the evaluator, which reopens the
coupon set that approach.md §2 and §10 deliberately lock to two types and three
conditions. The column on its own is cost with no corresponding capability.

The secondary argument is worth stating to a reviewer: this system holds **no personal data
at all**. That is a real property, and adding an identity column that nothing ever reads
back trades it away for nothing.

Authentication on `POST /orders` therefore authorises the action rather than personalising
it. The token proves the caller may place an order. The assignment never asks for order
history, per-user limits, or "my orders".

**Where this is weakest, stated plainly rather than defended.** `CouponRedemptions` exists
as an audit trail — it records which order consumed which redemption, and when, which is
something the `UsageCount` column alone cannot tell you. An audit trail with no actor
answers "was this coupon redeemed" but not "by whom", and "by whom" is the question anyone
investigating coupon abuse actually asks. That is a genuine gap, not a scope decision
dressed up as one.

If it were to be closed, the narrowest change is one nullable `RedeemedByOid` column on the
**redemption**, not on the order — keeping the order itself anonymous. `oid` rather than
`preferred_username`, per the spike 3 finding on `#EXT#` guests.

**What would reopen this:** per-user coupon limits entering scope, or any requirement for
order history. Neither is in the brief.

## Phase E — React frontend, MSAL, static website (2026-08-28)

### The frontend's APIM subscription key is public, and that is the answer rather than the problem

The React bundle carries an APIM subscription key, because every call through the gateway
needs one. A browser cannot hide it: anyone who opens developer tools can read it.

**The sentence that answers this in review:** an APIM subscription key is a *client
identifier and a rate-limit handle, not a credential*, and the only mutating endpoint is
protected by something that is not the key — an Entra access token carrying `Orders.Write`,
validated at the gateway by `validate-jwt`.

That is exactly the distinction approach.md §5 draws: the key shows which client and
product are calling and is how the gateway applies rate limits; it does not prove who the
caller is. Embedding it in a public single-page app is consistent with that reading rather
than a departure from it. What it costs is that `GET /menu` and `POST /coupons/validate`
can be called by anyone who extracts the key — both read-only, and one of them is a
deliberate anonymous endpoint anyway.

**A second APIM subscription, `frontend`, separate from `smoke-test`.** The two are
independently revocable, so rotating a leaked frontend key does not blind the pipeline's own
verification. Both keys are generated by APIM, read with `listSecrets` at the moment they
are needed, and never written to a file or published as a pipeline variable.

The alternative — dropping `subscriptionRequired` for the frontend — was rejected: it
contradicts brief requirement 5 and approach.md §5, and trades a documented, bounded
exposure for an undocumented one.

### Redirect flow with the basket in sessionStorage, not popup

MSAL is configured for `loginRedirect`, and the basket is persisted in `sessionStorage`.

**Why not popup**, which would sidestep the problem entirely: popups are blocked under some
enterprise policies and behave poorly on mobile Safari, and a blocked popup looks like a
sign-in button that does nothing — a failure with no error message anywhere. Redirect works
everywhere.

**The cost of redirect, and why it is paid rather than avoided.** The redirect to
`login.microsoftonline.com` and back reloads the page, discarding React state. Without
persistence a customer would authenticate *in order to buy a basket* and return to an empty
one, at the worst possible moment. `sessionStorage` survives the round trip because it is
scoped per origin per tab and the tab outlives the navigation.

`sessionStorage` rather than `localStorage`: a basket is a per-visit thing and should not
still be there tomorrow. MSAL's own token cache is in `sessionStorage` for the same reason.

Persisting the basket is worth doing regardless of authentication — a customer who refreshes
mid-order should not lose their cart either. The fix is not auth scaffolding.

**The basket stores quantities only.** No price is held client-side, so there is nothing
there to tamper with and send back. Rule 1 holds by construction on the client as well as
the server.

### Nothing renders until `handleRedirectPromise()` resolves

On the way back from Entra the URL fragment carries the authorization code. Rendering before
MSAL has processed it shows a signed-out UI for a moment and any component reading the
account list sees none — so the app would flash "Sign in to order" at somebody who has just
signed in.

### The development-only token claims panel is stripped at build, and the strip is verified

The panel decodes a live access token and displays `aud`, `iss`, `scp`, `roles` and `ver`.
It exists because every automated assertion on the token policy is a *negative*, and a
negative passes identically whether the policy is correct or reading the wrong token.
Checking the claims before trusting a 201 is what tells those apart.

On a deployed site it is a screenshot waiting to happen. So it is not hidden behind a
runtime flag. Its single call site is guarded by `import.meta.env.DEV`, which Vite replaces
with the literal `false` in a production build; the branch becomes dead code, Rollup removes
it, and the module has no other importer so it is tree-shaken out entirely.

**That is the mechanism. The verification is separate, and there are two of them**, because
"it should be stripped" is not a claim worth making unchecked:

- the frontend stage greps `dist/` for the marker `PIZZASHOP_DEV_ONLY_TOKEN_CLAIMS_PANEL`
  and fails the build if it is present;
- the smoke test fetches the *deployed* bundle over HTTP and greps the served bytes.

Confirmed locally before the first deployment: zero occurrences of the marker, the panel's
heading, or `decodePayload` in the production bundle.

### `--auth-mode login` works for the static website switch but not for the upload

Verified before writing the role assignment, because the answer determined whether one was
needed at all. It is a split answer:

| Operation | OAuth | Why |
|---|---|---|
| `az storage blob service-properties update --static-website` | works | maps to the management-plane action `Microsoft.Storage/storageAccounts/blobServices/write`, which the pipeline's Contributor already has |
| `az storage blob upload-batch` | fails | writing blob content is a *data* action; Contributor does not include it |

The failure is explicit: `You do not have the required permissions needed to perform this
operation. Depending on your operation, you may need to be assigned one of the following
roles: "Storage Blob Data Owner"...`

So `infra/modules/storage.bicep` assigns **Storage Blob Data Contributor** to the deploying
principal, scoped to the storage account rather than the resource group, and no account key
appears anywhere in the pipeline.

The `deployingPrincipalObjectId` parameter is now used twice — as the SQL Entra
administrator and as this role's grantee — which is why it was renamed from
`sqlAdminObjectId`. It is the same object ID, derived the same Graph-free way from the ARM
token's `oid` claim.

### The frontend upload uses the storage account key, because the alternative is a much larger grant

**This reverses a decision made one build earlier, and the reversal is the interesting
part.** `infra/modules/storage.bicep` briefly assigned Storage Blob Data Contributor to the
deploying principal so the upload could use OAuth and the pipeline could claim no key
anywhere. Build 9 failed provisioning before touching a single resource:

    InvalidTemplateDeployment: Authorization failed for template resource ... of type
    'Microsoft.Authorization/roleAssignments'. The client ... with object id
    'd521e8e4-...' does not have permission to perform action
    'Microsoft.Authorization/roleAssignments/write'

**What was verified before, and what was not.** The earlier check established which
*data-plane* permissions each call needs, and that was correct: the static website switch
works over OAuth with Contributor, the upload does not. What went unchecked was whether the
pipeline could grant itself the missing one. It cannot. Contributor is `*` minus its
notActions, and `Microsoft.Authorization/*/Write` is in that list. Verified after the
failure:

    Contributor actions    : *
    Contributor notActions : Microsoft.Authorization/*/Delete
                             Microsoft.Authorization/*/Write
                             Microsoft.Authorization/elevateAccess/Action
                             ...

The lesson generalises: checking that a principal can perform an operation is not the same
as checking it can grant itself the permission for that operation, and a role assignment
inside a template fails the *entire deployment* rather than degrading.

**Why the account key is the better answer here, not merely the working one.**

Making the role assignment succeed means granting the service connection **User Access
Administrator** — the power to assign itself any role, on anything, permanently. That is a
standing privilege escalation for the benefit of one file upload.

The account key grants nothing new. Contributor already includes
`Microsoft.Storage/storageAccounts/listKeys/action` — it is not in notActions — so this
principal can obtain the key whenever it likes. Fetching it in the pipeline adds no
capability it did not already have; it only makes the existing one visible.

So the comparison is not "a key versus no key". It is "use a credential the principal can
already mint, or permanently grant it the right to escalate its own privileges". The key
wins on the security argument, not just on convenience.

**How it is handled.** `az storage account keys list` at the moment it is needed, used
within the same task, `unset` immediately after. Never a pipeline variable, never echoed,
never written to a file — the same pattern the APIM subscription keys already use. Rule 7
prohibits secrets in code, config and logs; this is in none of them.

**What this costs, stated plainly.** The pipeline can no longer claim "no credential of any
kind is used anywhere". The claim that survives, and that is worth more, is narrower and
true: *no secret is stored anywhere, and no credential is used that the deploying principal
could not already obtain*. The passwordless story for the running system is untouched — the
App Service still reaches SQL as a managed identity, and API Management still reaches the
App Service as one. This is a deployment-time credential, not a runtime one.

**What would change it back.** If the service connection is ever given User Access
Administrator for other reasons, the role assignment becomes free and
`allowSharedKeyAccess` can be set to `false` on the storage account, which would close the
key off entirely. The Bicep comment says so at the property.

### The redirect URI guard checks a record, not the registration

The frontend stage fails if the deployed origin stops matching the `expectedFrontendOrigin`
pipeline variable.

**What it cannot do, stated so a green run is not over-read:** it does not verify Entra. The
pipeline's principal cannot read an app registration — the same Microsoft Graph wall that
made `az ad sp show` unusable and drove the user-assigned identity decision. So the variable
is a *record of what a human registered*, and the guard proves that deployment still
produces that origin.

**The failure it prevents.** Entra matches redirect URIs as exact strings with no wildcards.
A mismatch fails nowhere on the server: the frontend deploys green, the smoke test passes,
and sign-in then fails in the browser with `AADSTS50011`. The guard turns a silent,
browser-only failure into a stage failure printing both strings.

The drift it is guarding against is real: the `zNN` segment of
`https://<name>.zNN.web.core.windows.net` is a DNS zone assigned at account creation and is
not guaranteed to be the same if the account is deleted and recreated. The current value is
`z29`.

### Registering the production redirect URI is a Day 0 item, done by hand

`coupon-spa` needs `https://stcouponsvclabdtjori.z29.web.core.windows.net` added **alongside**
the existing `http://localhost:5173`, not replacing it — the failure mode recorded earlier
in this log is deleting the localhost URI on the assumption that a registration has one.

It is done by hand, deliberately. App registrations live in Microsoft Graph, not ARM, and
approach.md §7 puts them on the Day 0 list precisely because creating identity objects from
an application pipeline needs permissions most tenants block. Adding a redirect URI to an
existing registration is part of that same Day 0 item, deferred only because the origin is
not knowable until the storage account exists.

**Action:** approach.md §7's Day 0 list should say so explicitly. Flagged, not edited.

### Day 0: the redirect URIs must be on the SPA platform, not publicClient and not web

Recorded because this was got wrong in practice while setting the project up, and the
failure is silent until sign-in. Anyone reproducing the Day 0 steps will reach for the same
wrong platform.

The required end state on `coupon-spa`:

    spa           = ["http://localhost:5173",
                     "https://stcouponsvclabdtjori.z29.web.core.windows.net"]
    publicClient  = []
    web           = []

Both URIs under `spa`, no trailing slashes, and the localhost one kept alongside the
deployed one rather than replaced.

**Why `spa` and not `publicClient`.** They look interchangeable — both are "public client"
platforms in the sense that neither holds a secret — and Entra will happily accept a
browser origin under `publicClient`. It then fails at sign-in, because the two platforms
differ in something invisible from the registration: **Entra only enables CORS on the token
endpoint for redirect URIs registered under `spa`.** A browser redeeming an authorization
code against `/token` from a `publicClient` URI is blocked by the browser itself. The
`publicClient` platform is for native and mobile clients, which redeem their code from a
process that has no origin and no CORS to satisfy.

**Why not `web`.** The `web` platform expects a confidential client and requires a client
secret to redeem the authorization code. A browser cannot hold a secret — which is the
entire reason this project uses PKCE — so a `web` redirect URI rejects the flow. This is
already recorded under spike 3; the point here is that all three platforms exist, all three
accept the same string, and only one of them works.

**How to set it, and the two ways it goes wrong.**

`az ad app update --set spa.redirectUris=...` does not work: it fails with
`Couldn't find 'spa' in ''` when the platform object does not already exist, because
`--set` walks a path into an absent node rather than creating it.
`--public-client-redirect-uris` writes to the wrong platform entirely, and does so
successfully, which is worse.

The route that works is a Graph PATCH with a body file:

    az rest --method PATCH \
      --uri "https://graph.microsoft.com/v1.0/applications/<OBJECT-ID>" \
      --headers "Content-Type=application/json" \
      --body "@spa-redirect-uris.json"

Two details in that command:

- It takes the application's **object ID**, not its application (client) ID. Microsoft
  Graph addresses the directory object; `az ad app show --id <appId> --query id` returns it.
- A PATCH **merges**. Sending only `spa` sets the SPA URIs and leaves anything already under
  `publicClient` in place, so a registration that was written to the wrong platform first
  ends up with the URIs on both. The body must empty the platforms it is not using:

      {
        "spa":          { "redirectUris": ["http://localhost:5173", "https://.../"] },
        "publicClient": { "redirectUris": [] },
        "web":          { "redirectUris": [] }
      }

Graph returns 204 with no body on success, so the write must be verified by reading the
registration back rather than by the command's exit code.

**A note on shells.** The PATCH fails under PowerShell if the URI is unquoted, because
PowerShell parses the parentheses in the Graph URL. This is the same class of problem as
the `${APIM}.azure-api.net` brace rule in `CLAUDE.md`. Quote the URI, or run it from bash.

**Action:** approach.md §7's Day 0 list currently says only "Two Entra ID app
registrations (the API and the React app)". It should say that the React app's redirect
URIs go on the SPA platform, and that the deployed origin is added once the storage account
exists — the origin is not knowable before then, which is why this Day 0 item is completed
in two sittings rather than one. Flagged, not edited.

### An undefined `$(macro)` in a bash step becomes a shell command, silently

Build 10's frontend stage failed with two lines that describe the same fault at different
distances from it:

    line 41: tenantId: command not found
    Error: Missing required build variables: VITE_TENANT_ID

`VITE_TENANT_ID="$(tenantId)"` assumed a pipeline variable named `tenantId`. There was none
— `tenantId` is a *Bicep* parameter, defaulting to `subscription().tenantId`, which is why
it worked everywhere else and only failed here.

**Why this is worth an entry rather than a one-line fix.** Azure DevOps leaves an
unrecognised `$(name)` in the script text verbatim. Bash then reads it as command
substitution, tries to run a program called `tenantId`, fails, and substitutes the empty
string. Under `set -euo pipefail` this still does not abort, because the assignment was an
environment prefix on another command and the prefix's failure is not the command's.

So the default behaviour is: a typo'd or undefined variable becomes an empty value, quietly,
in a script that is otherwise configured to fail fast. It surfaces wherever the empty value
eventually matters, which can be a long way from the cause.

**What caught it.** The `VITE_` variable check in `vite.config.ts`, which fails the build
naming the missing variable. Without it the frontend would have deployed with
`authority: https://login.microsoftonline.com/undefined`, and the failure would have been a
sign-in button that does nothing — no server-side error, nothing in any log. That check
earned its place on its first real run.

**The systematic version.** Every `$(name)` in the pipeline can be cross-checked against the
`variables` block, each stage's `variables`, and the ADO built-in prefixes
(`Build.`, `Agent.`, `Pipeline.`, `System.`, `Common.`). Doing that across the whole file
found exactly one unresolved macro — this one — and confirmed the other twenty-seven
resolve. It is worth re-running after adding any stage, because the failure mode is silence
rather than an error.

A related check in the same family: extracting each bash `inlineScript` and running
`bash -n` over it. That caught mangled line continuations in the same commit, which would
otherwise have failed in the same stage on the next run.

### The frontend build fails on a missing variable rather than shipping `undefined`

`vite.config.ts` checks all five `VITE_` variables in production mode and throws. A missing
one would otherwise become `undefined` in the bundle and surface as a sign-in that silently
does nothing, or a 401 that reads like a gateway problem. Failing the build names the
variable instead.

## Phase F — documentation, hardening, and what using the app found (2026-08-28)

### The deliverable documents were extracted, and the extraction caught a contradiction

The four documents were assembled from approach.md and decisions.md as planned rather
than written fresh. Reading approach.md end to end for that pass found §4's worked
example still showing a 27.00 subtotal for a basket the seeded menu prices at 31.50 —
`pizzaId 1 ×2` at 10.00 plus `pizzaId 3 ×1` at 11.50. Corrected against the live menu.

That is the second contradiction an end-to-end read has found in that document which a
section-by-section read did not. Worth remembering the next time the reconciliation pass
looks skippable.

### Two API defects found by probing the deployment, not by reading the code

Both were found by calling the deployed gateway with deliberately awkward bodies.

    POST /coupons/validate {"couponCode":"PIZZA10"}   -> 500
    POST /coupons/validate quantity 2000000000        -> 200, subtotal 20000000000.00

**The 500 is the one that mattered.** A non-nullable reference type on a record property
is a compile-time annotation and nothing more; `System.Text.Json` does not enforce it, so
an absent `items` bound as null and the endpoint threw. A malformed request surfacing as a
server fault contradicts the contract all four deliverable documents state — 4xx for a bad
request, 5xx for a real fault — and a reviewer with curl finds it in a minute.

Fixing the documents to describe a 500 would have been the wrong direction. `Items` is now
nullable so the compiler requires the check, and `BasketRequestValidation` answers null and
empty with a 400.

The bounds (50 per line, 50 lines) live in `Basket.FromLinesAsync` beside the existing
quantity check, so they hold for anything that builds a basket rather than only for what
arrives over HTTP. The line cap is not only arithmetic: `IMenu.GetUnitPriceAsync` is one
database round trip per line, and for an order the whole loop runs inside the redemption
transaction against a 5 DTU database.

### The positive order path is now proven, and the policy was read back to prove it

A manual PKCE sign-in produced a 201 with the token's claims checked rather than assumed:
bare client-ID `aud`, v2 `iss` with no trailing slash, `scp` containing `Orders.Write`,
`roles` absent, `ver` 2.0.

The claims panel alone would have been partly circular — it compares the token against the
same `.env.local` values used to build the app. So the deployed operation policy was read
back out of API Management, and it pins those same strings and carries

    token-value="@((string)context.Variables.GetValueOrDefault<string>("callerToken", ""))"

`token-value`, not `header-name`. That is direct evidence for what the phase D entry above
established only by inference from Application Insights failure reasons.

### Three frontend defects, all found by using the app rather than reading it

Recorded together because they share a cause: the frontend had been reviewed for its
authentication flow and verified through the API, and never actually looked at.

**The − and + buttons rendered invisibly.** `styles.css` declared
`color-scheme: light dark` while every surface in it is a hardcoded light value. That
declaration makes the browser resolve UA system colours for the user's preference, so on a
machine in dark mode `buttontext` became near-white while `.stepper button` kept its
explicit `background: #fff`. White glyphs on a white button. The Check button had the same
defect; `.primary` escaped only because it sets its own colour.

Declaring `color-scheme: light` is the truthful fix, and explicit `color` on button and
input makes it independent of that declaration. A second defect surfaced alongside it: the
stepper buttons inherited the generic `button` padding and, with no `box-sizing`
declaration anywhere, rendered around 3.8rem wide rather than square.

**The menu needed a manual refresh.** `useEffect` called `getMenu()` once with no retry, so
a load landing inside a cold start set an error and stayed there. The pipeline's smoke test
polls for up to 420 seconds because a first boot was measured at 212; the page a customer
actually looks at was the one place that did not. It now retries with backoff over ~55s,
says the service may be starting, and offers a button rather than expecting a refresh
nobody would think of.

**The coupon code was lost across the sign-in redirect, and lost silently.** The basket was
persisted in sessionStorage and the coupon code was not. After sign-in the order submitted
with `couponCode: null`, which `PricingService` prices at full price with
`RejectionReason: null` — nothing was asked about, so nothing was rejected. The
confirmation only explains a missing discount when there *is* a rejection reason, so the
customer was charged full price and told nothing.

**This is a worse failure than the one the original design guarded against.** An empty
basket after sign-in is visible and the customer rebuilds it; a missing discount is not,
and it disappears at the moment they have stopped checking. `SPEND50` on a 75.00 basket
lost 11.25 in silence. The code is now persisted alongside the basket, the preview re-runs
once on return so the figure shown matches what will be submitted, and `clear()` empties
both so a code cannot survive into the next order.

**The general lesson, for the record:** every automated check in this project passes against
an application whose buttons are invisible and whose discounts silently vanish. Smoke tests
assert on the response body, and the BDD suite exercises real routing and pricing — neither
of them looks at the thing a customer looks at.

## Phase F — adversarial review findings (2026-08-29)

Four findings from an adversarial review, all real. One defect and three stale
documentation references. The audit that followed found more of both kinds.

### An over-long coupon code returned 500, and why the earlier validation pass missed it

`OrderEntity.CouponCode` is `nvarchar(50)`. Nothing validated the incoming length, so a
longer code evaluated as `NotFound`, was written onto the order anyway — a rejected coupon
is still recorded — and threw when it met the column inside `SaveChangesAsync`. A malformed
request reported as a server fault, which is exactly the contract violation the missing
`items` array produced.

**Why it was missed, which is more useful than the patch.** The earlier pass added
`BasketRequestValidation` by looking at the request shape and asking what could be wrong
with it. From that vantage the basket has visible rules — an array can be absent, empty, or
absurdly large — and a string has none. **The 50 was in the schema and nowhere else.**
Anyone adding `HasMaxLength` to a column has no reason to think about an endpoint, and
anyone writing an endpoint guard has no reason to open an EF configuration. The two facts
never had to agree because nothing made them meet.

So the number now has one home, `CouponCodeRules.MaxLength`, referenced by all three EF
configurations and by the request validation. Changing it changes the column and the guard
together. The value is unchanged, so `has-pending-model-changes` reports none and no
migration is needed.

**The audit that followed, since one instance implies a class.** Every string column with a
schema constraint was checked against the paths that write it:

| Column | Constraint | Written from | Validated |
|---|---|---|---|
| `Orders.CouponCode` | 50 | **client request** | now, by the guard |
| `CouponRedemptions.CouponCode` | 50, required | the matched coupon's own code | bounded by `Coupons.Code` |
| `Coupons.Code` / `.Description` | 50 / 200 | seed data | no write endpoint exists |
| `Pizzas.Name` / `.Description` | 100 / 500 | seed data | no write endpoint exists |

`Orders.CouponCode` was the only client-reachable one. That is the whole class, and it is
closed — but it is closed by there being no admin surface, so **the first endpoint that
writes a coupon or a pizza reopens all of it.**

### The test suite could not have caught this, and that is the sharper problem

The scenario added for it asserts the guard, not the crash, because the crash is
unreproducible here. The suite runs on **SQLite, which is dynamically typed and does not
enforce `VARCHAR` length at all**. The oversized value would have been stored happily and
the order would have returned 201. Only SQL Server throws, and no test uses it.

So this is a defect class the BDD suite is structurally blind to: anything enforced by the
production database and not by the provider the tests run against. The scenario still has
teeth — verified by disabling the guard, which fails it — but it pins the validation rather
than the underlying constraint.

That is the cost of the SQLite decision recorded earlier in this log, and it was worth
paying for `ExecuteUpdateAsync` and transactions. It is not free, and this is the invoice.

### Ten stale code references, from the phase B sync-to-async change

The review named three. Grepping every document for identifiers quoted from the code found
ten, all the same root cause: phase B made the evaluator asynchronous and the documents kept
the signature they were written against.

- `ICouponEvaluator.Evaluate` → `EvaluateAsync` — approach.md §2, architecture.md §2 and its
  sequence diagram, assumptions.md §2, decisions.md phase A
- `Basket.FromLines` → `FromLinesAsync` — architecture.md §3 twice, decisions.md twice
- `PricingService.Price` → `PriceAsync` — architecture.md §2, decisions.md phase A
- `Orders` was listed as carrying a rejection reason column. It does not; `OrderEntity` has
  `CouponCode` and `CouponApplied` and no reason.
- One of mine from the previous commit: the SQL firewall rule was cited as `AllowAzure`; it
  is `AllowAllWindowsAzureIps`.

**Finding 3 is the one that matters.** `ICouponEvaluator` is the boundary the entire "one
deployable, extractable later" argument rests on, and two documents published a contract
that does not compile. A reviewer checking the central design claim against the source would
have found the source disagreeing with it.

**The general point:** prose and code drift silently, and quoted signatures drift worst
because they look like evidence. The check that found the extra seven was mechanical —
extract every identifier the documents quote, grep the source for each — and it takes
seconds. It belongs in the same category as the `$(macro)` cross-check recorded above:
cheap, boring, and it finds things reading does not.

## Phase G — final audit against the brief (2026-08-30)

A full audit of the delivered system against the assignment brief as written, rather than
against the plan. Nine findings, all accepted. This section records them as they were
fixed.

The audit was done by driving the deployed application in a real browser, probing the
gateway, reading the deployed APIM policies and Easy Auth configuration back from ARM, and
checking the Azure resource timeline — not by reading the repository. Everything below was
found that way, which is the same lesson as the three frontend defects in phase F and the
reason it was done that way again.

### The restored-preview effect fired on the first keystroke, not on the redirect

The worst of the nine, because it is the first thing anybody sees.

Phase F added an effect that re-previews a coupon code restored from `sessionStorage` after
the sign-in redirect, so a customer coming back does not see a filled-in coupon field with
no figure beside it. It had no signal for "this is the return leg", so it tested the state
instead: menu loaded, a code present, a non-empty basket, and a `useRef` to make it happen
once.

**That condition is also true the moment somebody types the first character of a code.** So
the app fired a preview for a one-character code, the server correctly answered `NotFound`,
and a red rejection panel appeared underneath a coupon the customer was still typing. The
ref then guaranteed it never corrected itself: the panel sat there reading
**"Not applied: NotFound"** while the field read `PIZZA10` — a code that works. Confirmed
from the other side in Log Analytics, which has the request:
`Coupon validation: code=P valid=false reason="NotFound"`.

A reviewer following the README, which tells them to try `PIZZA10`, met this within seconds
of loading the page.

**The fix is to have the signal rather than infer it.** `handleRedirectPromise()` returns
non-null exactly when MSAL has just consumed an authorization response out of the URL, and
null on an ordinary load. `main.tsx` passes that to `App` as `returnedFromRedirect`, and the
effect returns early without it.

**The general point, and it is the same one as `CouponCodeRules`:** a condition that is
*correlated* with the thing you mean is not the thing you mean. "There is a code and a
basket" correlates with "we came back from sign-in" only until somebody types. Deriving a
fact you could be handed is how you get a defect that is invisible to everyone who already
knows what the code was for.

Proven rather than asserted, because "it should only fire on the redirect" is exactly the
kind of claim that is easy to believe and cheap to check. Driving the app with the coupon
code typed one character at a time: **zero requests during typing**, one when Check is
pressed. And the round trip it exists for still works — sign in with a basket and a code,
come back, and the verdict is restored automatically.

### The page had no total until you asked about a coupon

The brief names four things the customer must be able to do, and the fourth is "view the
calculated total price". With items in the basket and the coupon field untouched, the word
"total" did not appear anywhere on the page. The only two routes to one were the coupon
preview and the order confirmation — so seeing what your basket cost meant asking a
question about a coupon you might not have.

And the obvious workaround was a trap. Pressing **Check** with an empty field sent an empty
`couponCode`, which the endpoint answers with `NotFound` — the right answer to "is the empty
string a valid coupon?" and the wrong thing to show somebody who only wanted their total. It
rendered as a red rejection panel under an empty input.

There is now a **Your basket** block carrying subtotal, discount and total, present as soon
as the basket is non-empty and updated whenever it changes.

**Where the figures come from matters more than the block does.** They are the server's.
`POST /coupons/validate` with no code is exactly "price this basket", and rule 2 makes it
read-only — it consumes no redemption and writes nothing — so calling it on every basket
change has no consequence beyond a request. The alternative was to multiply the menu price
by the quantity in the browser, which would have been one line and would have put a money
calculation on the client for the first time. Rule 1 is about what crosses the wire, so it
would not strictly have broken it; but `client.ts` claims "every money figure the UI
displays comes back from a server response", and that claim is worth more than the request.

The call is debounced at 350ms, because the stepper is clicked in bursts, and sequenced,
because a slow earlier response must not overwrite a newer one and leave the customer
looking at the total for a basket they have moved on from.

Two smaller decisions inside it:

- **Check with an empty field no longer asks the server anything.** There is nothing to
  check and nothing to reject; the total is already on screen. It clears any stale verdict
  and returns.
- **A coupon verdict is cleared when the basket changes.** It was priced against a basket
  that no longer exists, and a stale discount displayed beside a fresh subtotal is the same
  failure as the coupon code that vanished across the redirect in phase F — the customer is
  shown a number that is not the number they will be charged. They press Check again, which
  is the flow the README already describes for watching `SPEND50` cross its threshold.

### `ArgumentOutOfRangeException.Message` was reaching the browser

The `+` button incremented without limit. Clicking it past fifty produced a 400 from
`Basket.MaxQuantityPerLine` — correct — whose body the frontend displayed verbatim:

```
Quantity for pizza 1 must be at most 50. (Parameter 'lines')
Actual value was 52.
```

The first sentence is for the customer. The rest is .NET composing
`ArgumentOutOfRangeException.Message` out of the message, the parameter name and the actual
value, and it names a parameter of a method the customer has never heard of.

Fixed on both sides, because they are two different defects that happened to meet:

- **The UI should not be able to build a request that is guaranteed to fail.** The stepper
  is capped at the server's limit and the `+` button disables at it; `useBasket` clamps as
  well, so the cap holds for any future caller. The coupon input gained `maxLength`, which
  closes the phase-F over-long-code path from the browser too.
- **The API should not leak the exception type's own formatting to anyone**, including a
  caller who is not using the frontend. `InvalidBasketException` derives from
  `ArgumentOutOfRangeException` — so every existing `catch` and the type's meaning are
  unchanged — and carries `CustomerFacingMessage`, the sentence without the suffix. The
  endpoints use that for `ProblemDetails.detail`; logs and stack traces still get the full
  base `Message`, which is where the parameter name is useful.

The scenario that covers the bound now also asserts the customer-facing message contains
neither `Parameter` nor `Actual value`, so reverting the endpoints to `ex.Message` fails a
test rather than shipping.

**Mirrored constants, and why that is acceptable here.** `MAX_QUANTITY_PER_LINE` and
`COUPON_CODE_MAX_LENGTH` in `useBasket.ts` are copies of `Basket.MaxQuantityPerLine` and
`CouponCodeRules.MaxLength`. A copy can drift, and `CouponCodeRules` exists precisely
because a duplicated constant caused a defect. The difference is what each copy governs:
the server's value decides what is *accepted* and the client's decides what the UI lets you
*build*. Drift low and the UI is merely stricter than it needs to be; drift high and the
server still refuses. That is a different risk from two values that both had to be right.

### A network failure showed the customer a JavaScript type name

Found while testing the above, by aborting a request: a failure that never reaches the
gateway is not an `ApiError`, and the catch fell back to `String(e)` — which renders as
`TypeError: Failed to fetch`. The same objection as the section above, from the other end
of the stack. Both catches now go through one `describeFailure`, which keeps the gateway's
own message and correlation ID where there is one and says something a person can act on
where there is not.

### No test ever redeemed a coupon, and two documents said otherwise

The largest of the nine findings, and the one a reviewer was most likely to check.

The suite had thirteen scenarios. Exactly two reached `POST /orders`: one carrying
`couponCode: null`, one carrying an over-long code and expecting a 400. So
`CouponRepository.TryRedeemAsync` was never invoked by any test. `UsageCount` was asserted
only as *unchanged*, by the preview scenario whose whole point is that it stays at zero.

Nothing covered:

- an order actually being discounted by a coupon — the brief's functional goal, at the
  level the brief states it
- `UsageCount` incrementing — rule 4, the atomic `UPDATE`, the invariant this design leans
  on hardest
- the `CouponRedemptions` audit row
- the `strategy.ExecuteAsync` block and the transaction inside it, which is the most
  intricate code in the solution and was written to fix a real defect

Meanwhile `architecture.md` §8 listed "A coupon at its redemption limit is refused, and the
order is created at full price" among the scenarios that matter most, and `assumptions.md`
§3.2 said "The BDD scenarios cover sequential exhaustion". Neither was true. The `MAXED`
scenario prices a basket; it places no order and exhausts nothing.

**How the claim came to be false is the useful part.** It was written from the intent — the
design *does* handle sequential exhaustion, and the evaluator scenario for
`RedemptionLimitReached` does exist — and nothing forced it to be checked against the
feature file. That is the same shape as the `nvarchar(50)` recorded in phase F: a fact
stated in one place, relied on from another, with no path between them.

Two scenarios now close it, both through the endpoint because a claim about a transaction
cannot be made anywhere else:

- an order with a valid coupon returns 201, is discounted, moves `UsageCount` by exactly
  one, and writes one `CouponRedemptions` row against that order
- an order against a coupon at its limit returns 201 at full price with
  `couponApplied: false` and `RedemptionLimitReached`, and consumes nothing

**Mutation-checked, because a passing test proves nothing until it has been seen to fail** —
which is the explicit lesson of the EF in-memory provider decision earlier in this log,
where a green suite was hiding a 500 on every order carrying a coupon. Making
`TryRedeemAsync` return `true` without performing the `UPDATE` fails the first scenario;
deleting the audit-row write fails it as well. Both were run, both failed, and the code was
restored.

`architecture.md` §8 also claimed "Scenarios run against the API through
`WebApplicationFactory<Program>`" as a blanket statement. It was true of five of thirteen,
and is now true of seven of fifteen. It says so, and says how the level is chosen.

### Documentation that was true of the intent and not of the system

Three of the nine findings were documents disagreeing with the thing they describe. None of
them changes what the system does; all three would have been found by a reviewer checking a
claim, which is the cheapest kind of damage to avoid.

**"Three items. Nothing else."** `deployment.md` §2 asserted three Day 0 items and §4 of the
same document then listed five steps to run from an empty subscription. The two missing ones
are creating the Azure DevOps project and pipeline, and editing the variables block at the
top of `azure-pipelines.yml` for the target subscription and tenant — that second one is an
edit to a file in this repository, which is exactly the sort of manual step the brief's "no
manual configuration" line is about. They were omitted because nobody counts "create the
pipeline" as work. Both documents now say five and list them, and the README does too,
because the README is what gets read first and it carried the same three.

**"Pinned rather than generated."** Five places said the storage account name is pinned. It
is not: `main.bicep` composes it from `uniqueString(subscription().id, resourceGroup().name)`
and the pipeline never passes the `storageAccountName` parameter that would pin it. The
substance survives — neither input changes when the resource group is deleted and recreated
in the same subscription, so the name is reproduced identically and the registered redirect
URI keeps working — but the word was wrong in a way that matters, because it describes a
*weaker* guarantee than the one claimed. A literal name would hold anywhere; a deterministic
one holds in this subscription and this resource-group name. That distinction is the whole
reason the parameter exists, and `storage.bicep`'s own header comment said "pinned by the
caller" when the caller does nothing of the kind. Corrected everywhere, including the note
the pipeline prints at the end of the provision stage.

**Two stale references.** The OpenAPI contract described `BadRequest` as "an unknown pizza,
or a quantity below one" — it was written when those were the only two, and there are now
seven. It also says what is *not* a 400, because a rejected coupon looking like a bad request
is the confusion the status-code section exists to prevent. And `AGENTS.md`'s commands block
told an agent to validate the template with `--parameters @infra/params.json`, a file that
has never existed; the pipeline builds the argument list inline because two of the values are
only knowable at deploy time. It now shows that.

### The APIM purge had never run, and would not have worked

The headline claim of this project is that deleting the resource group and re-running the
pipeline produces a working system. One step stands between that claim and a green
pipeline: the purge of the soft-deleted API Management service, because `az group delete`
does not free an APIM name for 48 hours.

**That step had never executed.** Checked against Azure rather than against the log: every
resource in `rg-coupon-service` was created between `2026-08-27T13:31:08Z` and `13:34:28Z`
and had existed continuously since, and the seventeen Bicep deployments after that were
incremental updates onto them. The subscription's `deletedservices` list was empty and
always had been. Build 19's provision log reads, like every run before it,
`==> Purging any soft-deleted API Management service for this project` /
`none found`.

**And the code was wrong.** It used `az rest --method delete`. Purging a soft-deleted APIM
service is a long-running ARM operation — `202 Accepted` plus an async-operation header,
completing later — and `az rest` is a raw HTTP call that does not follow that header. So the
step would have returned immediately and `az deployment group create` on the next line would
have raced a purge still in flight, failing with exactly the "service name is not available"
the step exists to prevent.

Two changes:

- `az apim deletedservice purge`, which polls the operation to completion. The service name
  and location come out of the deleted service's resource id by parameter expansion rather
  than `sed`, because the id's shape is fixed and one less external command is one less
  thing to escape.
- A `checkNameAvailability` poll afterwards, because the CLI waiting for the purge and ARM
  releasing the reserved name are two different events. It asks precisely the question the
  deployment is about to ask, thirty times at ten-second intervals, and fails the stage with
  a message naming the cause if the answer is still no.

No `$(( ))` arithmetic anywhere in the block. Azure DevOps leaves an unrecognised `$(name)`
in the script text verbatim so it would have been harmless, but this project has already
lost a cycle to a `$(macro)` that silently became an empty string under `set -euo pipefail`,
and the attempt number carries the same information as the elapsed seconds. Both bash
`inlineScript` blocks were extracted and run through `bash -n`, which is the check
`deployment.md` §6 prescribes after touching a stage, and the macro cross-check found
nothing unresolved outside comments.

**The general point, and it is the sharpest one in this audit:** a conditional that has
never been true is not tested by any number of green runs. Eleven green pipelines said
nothing whatsoever about the branch the entire claim rests on, because they all took the
`else`. The only way to find that out is to look at what the branch would have done, or to
make it happen.

### The second soft-delete: Log Analytics

Raised while planning the from-scratch test, on the grounds that "it usually just works"
was the phrase that had covered the APIM purge for eleven green runs while that step was
broken. One unverified instance of it was enough.

A deleted Log Analytics workspace is held for **14 days** with its name reserved. Same
shape as APIM, different resolution: there is no purge API for a workspace. Recreating one
with the same name, resource group and location **recovers** the soft-deleted instance,
which is the outcome this deployment wants — the name and the workspace ID come back, and
`log-${namePrefix}-${environmentName}` carries no `uniqueString` suffix so it is stable by
construction.

So the provision stage does not fix anything here; it **says** something. It lists
`deletedWorkspaces` and, if ours is in there, states that the deployment is about to
recover it rather than create it. The value is entirely diagnostic: a soft-deleted
workspace nobody mentioned would make a failed recovery read as a fresh provisioning fault
and send someone to the wrong layer — which is precisely how the APIM soft-delete presents
itself as a naming collision.

Verified against the live subscription before being written, rather than after: the
endpoint is `.../providers/Microsoft.OperationalInsights/deletedWorkspaces` and the
api-version is `2023-09-01`. `2021-06-01` returns `InvalidResourceType`, so the version is
load-bearing and not decorative.

### The from-scratch claim, finally tested (2026-08-30)

`rg-coupon-service` was deleted and the pipeline re-run. **Build 22 succeeded in 17
minutes**, from an empty resource group, and the result is that the headline claim of this
project is now a tested statement rather than a designed one.

It had never been tested before. Every resource in the group was created on 2026-08-27 and
the seventeen deployments after it were incremental.

**The purge branch executed, and the 91 seconds is the whole argument:**

```
==> Purging any soft-deleted API Management service for this project
    purging apim-couponsvc-lab-dtjori (centralindia)        16:16:56
    waiting for the name to be released                     16:18:27
    apim-couponsvc-lab-dtjori released (attempt 1 of 30)    16:18:28
```

Ninety-one seconds between issuing the purge and the operation completing. The previous
`az rest --method delete` would have returned in about one second and handed straight over
to `az deployment group create`, which would have met a name that `checkNameAvailability`
reported — measured, immediately before the run — as `nameAvailable: false`,
`reason: AlreadyExists`. This was not a defensive fix.

The name-availability poll succeeded on its first attempt, so the CLI's own wait turned out
to be sufficient. That is only knowable because it was measured; it stays, because the cost
of a check that passes first time is nothing and the cost of the alternative is 48 hours.

**The Log Analytics check was not hypothetical either.** `log-couponsvc-lab` was genuinely
in its 14-day window, the stage said so, and the proof that the recovery happened is that
the workspace's customer ID is byte-identical across the teardown:

```
log analytics customerId   BEFORE  8246a698-4c19-449d-88da-a92aca314763
log analytics customerId   AFTER   8246a698-4c19-449d-88da-a92aca314763
```

Recovered, not recreated, exactly as the step predicted.

**What came back identical, and what did not.** Recorded before the delete so the
comparison is evidence rather than recollection:

| | Before | After |
|---|---|---|
| storage account | `stcouponsvclabdtjori` | same |
| web endpoint | `…z29.web.core.windows.net/` | same — the `zNN` segment held |
| gateway URL | `apim-couponsvc-lab-dtjori.azure-api.net` | same |
| App Service, SQL FQDN | | same |
| API identity client ID | `8fd2fc7e-…` | `b79157a8-…` |
| gateway identity client ID | `0632cec4-…` | `abe0672e-…` |

The two identity client IDs are new principals, which is expected and is why nothing
hardcodes them: the APIM policy's `client-id`, Easy Auth's `allowedApplications` and the SQL
contained user's SID are all wired from Bicep outputs, and all three worked first time.

**The `zNN` segment surviving is luck, not design, and the guard is what makes that safe.**
It held here, so no Entra work was needed. `deployment.md` §6 already says it is not
guaranteed across a delete-and-recreate; that remains true and untested, because this run
did not exercise it.

**Stage timings from empty**, worth having beside the incremental ones:

```
1  Build and test        1m30s     15 BDD scenarios
2  Provision             8m46s     purge 91s, Bicep 6m49s (vs 2m12s incremental)
3  Grant DB access         50s
4  Deploy backend        3m16s
5  Build and deploy UI   1m10s     redirect URI guard passed
6  Smoke test            1m26s     6/6
```

**The smoke test's polling earned its keep on exactly the failure it was written for.**
Assertion 2 took five attempts over 45 seconds, and the first four returned **404** — which
`deployment.md` §6 records as the reading that sends you to APIM routing when the only
problem is that the app has not finished starting. A single call would have failed and
told you nothing.

**Verified afterwards by hand, because six green assertions are all negatives plus one
positive hop:** the full browser path on the rebuilt system — menu, quantities, coupon
preview, sign-in redirect, order. It produced **Order #1**. The identity counter restarting
at 1 is the cleanest available proof that the database is new rather than recovered:
nothing was carried over, the schema was created by EF migrations at first boot and seeded
from scratch, and the coupon that discounted the order was redeemed against a fresh
`UsageCount`.

---

## Phase H — GitHub Actions port (2026-09-09)

### The APIM purge filter matches more than the deployment it belongs to

`azure-pipelines.yml` finds services to purge with

```
az apim deletedservice list --query "[?starts_with(name, 'apim-couponsvc-lab-')].id"
```

`az apim deletedservice list` is **subscription-wide**. The prefix is everything in the
service name except the `uniqueString` suffix, and that suffix is a function of the resource
group name — so the prefix is shared by every deployment of this project in the subscription,
and matching on it selects other deployments' soft-deleted services along with this one's.

This was invisible while there was one pipeline. The GitHub Actions workflow deploys into
`rg-coupon-service-gh`, which makes a second service name under the same prefix, and turns a
latent bug into a live one in both directions.

**What is actually lost is the 48-hour restore window, not a running service.** The list
only ever contains services that are already soft-deleted; a purge cannot touch a live APIM.
So the failure mode is: someone deletes a resource group, intends to restore it, and finds
the name purged by the other pipeline's next run. That is a real loss — the restore window
is the only thing standing between an accidental teardown and a 48-hour wait — but it is not
destruction of anything serving traffic.

It is also not always unwelcome. After a genuine teardown, a soft-deleted service **blocks
reuse of its own name**, which is the entire reason the purge step exists. A cross-pipeline
purge of a name nobody intends to restore does the next run a favour. The bug is that it is
not a decision anyone made, and the log line reads `purging` either way.

**Fixed in the GitHub workflow only.** It resolves the exact service name first and matches
on equality:

```
az apim deletedservice list --query "[?name=='${EXPECTED_APIM_NAME}'].id"
```

The name cannot be computed on the agent — `uniqueString` is an ARM-side hash with no local
equivalent — so the provision job deploys a resource-less ARM template carrying the identical
expression from `main.bicep` and reads the name out of its outputs. Asking ARM is the only
way to get the name rather than a guess at it.

**`azure-pipelines.yml` is deliberately unchanged.** It is retained as the original, and the
Azure DevOps repository is a frozen deliverable. Anyone reinstating that pipeline as the only
one should make the same change; anyone running both should make it now.

### The Log Analytics soft-delete check was scoped to the subscription

Same shape, smaller consequence. `log-couponsvc-lab` carries no `uniqueString` suffix, so
both deployments use the identical workspace name, and the subscription-wide
`deletedWorkspaces` query reports the other deployment's workspace as if it were this one's.

Nothing acts on the result — the step only prints — so no deployment is affected. But the
message exists solely to stop a failed recovery being read as a fresh provisioning fault, and
a message that names the wrong workspace sends the reader to the wrong resource group. The
workflow queries the resource-group-scoped endpoint instead.

### The storage account name is pinned here, and that still does not pin the URL

`main.bicep` derives the name from `uniqueString(subscription().id, resourceGroup().name)`.
The resource group name differs between the two pipelines, so the derived name differs, and
the GitHub deployment would produce a static website URL that is not the one registered on
`coupon-spa`. The workflow therefore passes `storageAccountName` explicitly —
`stcouponsvclabgh` — which is what that parameter was added for.

**Pinning the account name does not pin the endpoint.** The URL is
`https://<account>.zNN.web.core.windows.net`, and `zNN` names the DNS zone of the storage
stamp the account is placed on, assigned at account creation and not derivable from the name.
The Azure DevOps account resolves through stamp `pn1prdstr10a` in `centralindia`, whose zone
is `z29`; the workflow targets the same region, so `expectedFrontendOrigin` is set to
`https://stcouponsvclabgh.z29.web.core.windows.net`.

That is a **prediction**, and it is left as one on purpose. A region has many stamps and a new
account can land on a different one. If the prediction is wrong the redirect-URI guard fails
the frontend job with both strings side by side, which is where a redirect-URI mismatch should
surface — rather than silently, in a browser, as `AADSTS50011`, after a green run. Phase G
recorded that the `zNN` segment surviving a delete-and-recreate was luck rather than design;
this is the same property, being relied on across accounts instead of across time, and guarded
the same way.

### Separate resource groups, because both pipelines claim the SQL administrator

Each pipeline sets the SQL server's Entra administrator to its own deploying principal, read
from the `oid` claim of its own ARM token. Sharing a resource group would mean sharing the SQL
server, and whichever pipeline ran last would own it — the other's grant stage would then fail
to authenticate, reporting a permissions problem some hours after the change that caused it.

Distinct group names also give distinct `uniqueString` suffixes, which keeps the
globally-scoped names — SQL server, App Service, APIM — apart without any further thought.
The storage account is the exception that proves it: pinned, so it needed a distinct literal.

### Contributor at subscription scope is the correct grant for the federated identity

Recorded because it was questioned, and because the intuition that argues against it is a good
one: the deployment creates two managed identities, and Contributor cannot create role
assignments.

**It does not need to.** There is no Azure RBAC role assignment anywhere in `infra/` —
`grep -rn "Microsoft.Authorization" infra/` returns only the comments in `storage.bicep`
explaining why there is none. The two identities are *created* and *attached* to resources,
which is `Microsoft.Web/sites/write` and `Microsoft.ApiManagement/service/write`, not
`Microsoft.Authorization/roleAssignments/write`. They then obtain access through two
mechanisms that are not RBAC at all: a SQL contained user created in T-SQL by
`grant-db-access.ps1`, and Easy Auth `allowedApplications` on the App Service.

That is not a coincidence, it is the design. `authentication.md` §9 records the experiment
that settled it — a Bicep-assigned Storage Blob Data Contributor failed the entire deployment
with `does not have permission to perform action 'Microsoft.Authorization/roleAssignments/write'`
— and the frontend upload uses an account key rather than closing that gap with User Access
Administrator. The GitHub federated identity replicates the Azure DevOps service connection
exactly: Contributor, subscription scope, nothing else.
