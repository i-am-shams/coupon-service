# Architecture

**Coupon service — pizza ordering with coupon support.**
A .NET 8 API behind Azure API Management, a React single-page app, an Azure SQL database, and
an Azure DevOps pipeline that builds all of it from an empty subscription.

This document describes the shape of the system, the coupon/ordering split, and why the two
domains ship in one deployable. Authentication has its own document
([authentication.md](authentication.md)); so does deployment ([deployment.md](deployment.md)).

---

## 1. The shape

```
                    +------------------------------------------+
   browser          |            Azure subscription            |
 +----------+       |                                          |
 |  React   |--1--> |  +------------------------------------+  |
 |   SPA    |       |  |  API Management (Consumption)      |  |
 |  (MSAL)  |       |  |   - subscription key on every call |  |
 +----------+       |  |   - validate-jwt on POST /orders   |  |
      |             |  |   - CORS, correlation ID           |  |
      2             |  |   - managed identity to backend    |  |
      |             |  +----------------+-------------------+  |
      |             |                   3                      |
      v             |                   v                      |
 +----------+       |  +------------------------------------+  |
 |  Entra   |<------+--|  App Service (Linux B1)            |  |
 |    ID    |       |  |   PizzaShop.Api                    |  |
 +----------+       |  |   - Easy Auth: gateway identity    |  |
                    |  |     only                           |  |
                    |  +----------------+-------------------+  |
                    |                   4                      |
                    |                   v                      |
                    |  +------------------------------------+  |
                    |  |  Azure SQL (Basic)                 |  |
                    |  |   Entra-only auth, no SQL login    |  |
                    |  +------------------------------------+  |
                    |                                          |
                    |  Storage account ($web) -- serves the SPA
                    |  App Insights + Log Analytics -- both tiers
                    +------------------------------------------+

  1  browser -> gateway     subscription key; access token on POST /orders only
  2  browser -> Entra ID    authorization code + PKCE, redirect flow
  3  gateway -> backend     the gateway's user-assigned managed identity
  4  backend -> database    the App Service's user-assigned managed identity
```

Four hops, three trust relationships, and no password anywhere in the running system.

**The App Service is not reachable directly.** Its Easy Auth configuration accepts exactly one
caller — the gateway's managed identity — so a request arriving at
`app-couponsvc-lab-*.azurewebsites.net` without that identity's token is rejected before any
application code runs. The caller and the backend never trust each other; both trust the
gateway, separately.

---

## 2. The coupon / ordering split

This is what the brief asks about most directly, so it comes first and in full.

### The interface

```csharp
public interface ICouponEvaluator
{
    Task<CouponEvaluation> EvaluateAsync(
        string code,
        CouponBasket basket,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}

public sealed record CouponBasket(decimal Subtotal);

public sealed record CouponEvaluation(
    bool IsValid,
    decimal DiscountAmount,
    string? Description,
    CouponRejectionReason? Reason);

public enum CouponRejectionReason
{
    NotFound,
    Expired,
    MinimumSpendNotMet,
    RedemptionLimitReached
}
```

Three properties of that signature carry weight.

**It takes a subtotal, not a basket.** `PizzaShop.Ordering.Basket` carries per-line unit prices;
passing it would hand the coupon project pizza IDs and prices. `CouponBasket` means the coupon
code cannot see them — the boundary is structural rather than a convention the coupon code is
trusted to respect. Every condition in the locked coupon set (expiry, minimum order value,
redemption limit) needs only the order's value, so nothing in scope is lost.

*The cost, stated plainly:* an item-scoped coupon — "10% off pizzas only", buy-one-get-one, a
category restriction — would mean reopening this signature and passing item data across the
boundary. That is a deliberate design change, not a small edit. It is the right trade here
because the coupon set is deliberately locked to two types and three conditions.

**It never returns a bare boolean.** A rejection carries a `CouponRejectionReason`, and that one
enum drives the customer-facing message, the log entry, and the test assertion. Changing the
wording of a message cannot break a test, and a new rejection reason cannot be added without
every consumer seeing it.

**It takes the current time as a parameter.** No coupon rule calls `DateTime.UtcNow`. Expiry is
otherwise untestable without manipulating the machine clock.

### Who calculates what

```
Ordering                                       Coupons
--------                                       -------
1. resolve unit prices from the menu
2. compute the subtotal
3. --- EvaluateAsync(code, subtotal, now) -->   4. find the coupon
                                               5. check expiry / minimum / limit
                                               6. compute and round the discount
                                               7. cap the discount at the subtotal
9. total = subtotal - discount    <---------   8. return CouponEvaluation
10. floor the total at zero
```

Ordering computes the subtotal from its own data, asks for a discount, and computes the total
itself. Two alternatives were rejected: if ordering applied the coupon rules, the coupon logic
would exist in two places; if the coupon side returned a final total, it would need to know
pizza prices and delivery, which turns it into a second ordering service.

**The discount cap is enforced twice**, in `CouponEvaluator.CalculateDiscount` and again as
`Math.Max(0m, ...)` in `PricingService.PriceAsync`. The second is redundant given the first, and it
stays: it is the structural invariant, so a future evaluator that forgets the cap still cannot
produce a negative total.

**Rounding happens once**, on the final discount, with `MidpointRounding.AwayFromZero` — standard
retail rounding, where €0.005 becomes €0.01.

### Why one deployable

Two App Services would mean two pipelines, two health checks, and a fourth authentication hop
between them. That is more platform work than the assignment needs, and the assignment puts its
emphasis on the gateway and the pipeline.

What matters is that the boundary is real, and it is:

```
src/PizzaShop.Api/              HTTP endpoints, composition root
src/PizzaShop.Ordering/         basket, pricing, orders   -> references ICouponEvaluator only
src/PizzaShop.Coupons/          coupon rules              -> no reference to Ordering
src/PizzaShop.Infrastructure/   EF Core, persistence, adapters
tests/PizzaShop.Bdd/            Reqnroll scenarios
web/                            React frontend
```

`Ordering` depends on `ICouponEvaluator` and on nothing else from the coupon side. `Coupons`
knows nothing about pizzas, delivery, or orders. The dependency is enforced by project
references, not by discipline.

`CouponEvaluator` itself works against an injected `IEnumerable<CouponRecord>`, so the rules are
testable with no infrastructure at all. `DatabaseCouponEvaluator`, in
`PizzaShop.Infrastructure`, is the adapter that supplies that catalogue from EF Core. The
repository interface lives in Infrastructure too, because persistence is not a domain concern.

### Splitting it later

The change is small, and it was kept that way on purpose:

1. `PizzaShop.Coupons` moves to its own App Service.
2. `DatabaseCouponEvaluator` is replaced by an HTTP client implementing the same
   `ICouponEvaluator`.
3. The coupon service becomes a second API in API Management, with the gateway authenticating to
   it exactly the way it already authenticates to this backend.

**The ordering code does not change.** Nor do the BDD scenarios, which exercise the interface
rather than the implementation.

The one thing that would need designing at that point is redemption: the atomic update currently
runs in the same database transaction as the order write. Across a service boundary that becomes
a compensating action or a two-phase reservation. That is the real cost of the split, and it is
a distributed-systems problem rather than a code-organisation one.

---

## 3. The server decides the price

**No endpoint accepts a price, a subtotal, or a total from the client.** Requests carry
`pizzaId` and `quantity`. The server resolves every price from its own data on every request.

This is enforced by construction rather than by review:

- `Basket` has no public constructor. `BasketItem`'s constructor is `internal`.
- The only way to obtain a priced basket is `Basket.FromLinesAsync(IEnumerable<BasketLine>, IMenu)`,
  which reads each unit price from the menu.
- `BasketLine` carries `PizzaId` and `Quantity` and has no field a price could arrive in.

Verified by compiling a probe that tried to fabricate a priced line from outside the assembly:

```
error CS1729: 'BasketItem' does not contain a constructor that takes 3 arguments
```

So binding a request DTO onto a priced basket fails the build rather than passing review.
`FromLinesAsync` also rejects a quantity below one — a negative quantity would subtract from the
subtotal, which is the same class of problem as accepting a price and would otherwise have been
reachable through a perfectly valid-looking request.

The React client holds quantities only. There is no price in `sessionStorage` to tamper with, so
the rule holds by construction on the client as well as the server.

---

## 4. Redemption

```sql
UPDATE Coupons SET UsageCount = UsageCount + 1
WHERE Id = @Id AND UsageCount < RedemptionLimit
```

One statement, expressed in EF Core as `ExecuteUpdateAsync` with the limit check in the `WHERE`
clause. No read-then-write, no change tracker, no explicit lock. Zero rows affected means the
coupon was exhausted between the check and the redemption, and the order is priced without it.
That is correct under concurrency with no locking of any kind.

**Preview never mutates.** `POST /coupons/validate` is read-only: it consumes no redemption and
writes nothing. Only `POST /orders` redeems. Previewing a coupon a hundred times leaves
`UsageCount` untouched — there is a scenario for it.

**The order and the redemption cannot diverge.** `PlaceOrder` opens a transaction covering both,
inside `strategy.ExecuteAsync(...)` so the SQL retry strategy can replay it. Two details in that
block matter: the coupon evaluation sits *inside* it, so a redemption decision from a failed
attempt cannot survive into the retry; and `ChangeTracker.Clear()` runs first, so a retry does
not begin holding entities the failed attempt added.

---

## 5. Data model

| Table | Purpose |
|---|---|
| `Pizzas` | Menu: name, description, price. Seeded at startup. |
| `Coupons` | Code, type, value, description, expiry, minimum order value, redemption limit, usage count. |
| `Orders` | Timestamp, subtotal, discount, total, coupon code, whether it applied. |
| `OrderLines` | Pizza, quantity, unit price captured at order time. |
| `CouponRedemptions` | Audit trail: which order consumed which redemption, and when. |

**An order records no customer identity.** There is no user column, and the order endpoint reads
no caller identity from the token it just validated. This is deliberate and settled rather than
open — see [assumptions.md](assumptions.md) §3, which also states where it is weakest.

`OrderLines` captures the unit price at order time rather than joining back to `Pizzas`, so a
later price change does not rewrite history.

Seeded data (idempotent, runs at startup after migrations):

| Pizzas | | Coupons | |
|---|---|---|---|
| Margherita | €10.00 | `PIZZA10` | 10% off |
| Pepperoni | €12.00 | `FIVEOFF` | €5 off |
| Veggie Supreme | €11.50 | `OLDCODE` | 10% off, expired 2020 |
| BBQ Chicken | €13.50 | `SPEND50` | 15% off orders over €50 |
| Four Cheese | €13.00 | | |
| Diavola | €12.50 | | |
| Prosciutto | €14.00 | | |
| Truffle Funghi | €15.00 | | |

The four coupons exist to make every rejection reason reachable from the browser without
touching the database. `NotFound` needs no seed data — any unknown code produces it.

---

## 6. API surface

| Endpoint | Method | Auth | Behaviour |
|---|---|---|---|
| `/api/v1/menu` | GET | Subscription key | Pizzas and prices |
| `/api/v1/coupons/validate` | POST | Subscription key | Preview a discount. Changes nothing. |
| `/api/v1/orders` | POST | Subscription key **+ access token** | Place an order |
| `/health` | GET | Not exposed through the gateway | Liveness |
| `/health/ready` | GET | Not exposed through the gateway | Readiness |

Request and response, in the shape both coupon-aware endpoints use:

```jsonc
// POST /api/v1/coupons/validate
{ "couponCode": "PIZZA10",
  "items": [ { "pizzaId": 1, "quantity": 2 }, { "pizzaId": 3, "quantity": 1 } ] }

// 200
{ "couponCode": "PIZZA10", "isValid": true, "rejectionReason": null,
  "subtotal": 31.50, "discountAmount": 3.15, "total": 28.35,
  "description": "10% off your order" }
```

`POST /orders` takes the same request shape and returns the same figures plus an `orderId` and
`couponApplied`.

### Status codes

**`POST /coupons/validate` returns 200 even when the coupon is rejected.** Its job is to answer
"can I use this code?", and "no, it expired" is a valid answer to that question. The rejection
travels in `isValid` and `rejectionReason`.

**`POST /orders` returns 201 even when the coupon failed**, with `couponApplied: false` and a
reason. The order succeeded; the discount did not.

4xx and 5xx are for malformed requests, authentication failures, and real faults. They use RFC
7807 `ProblemDetails` with a `traceId` carrying the correlation ID, and never a stack trace.

### Request validation

A malformed request is a **400**, never a 500. Four things are rejected:

| Condition | Response |
|---|---|
| No `items` property at all | 400 — *Missing basket* |
| `items` present but empty | 400 — *Empty basket* |
| A quantity below 1 or above `Basket.MaxQuantityPerLine` (50) | 400 — *Invalid basket* |
| More than `Basket.MaxLines` (50) lines | 400 — *Invalid basket* |

The bounds live in `Basket.FromLinesAsync`, alongside the existing quantity check, so they hold
for anything that builds a basket rather than only for what arrives over HTTP. The null and empty
checks are at the endpoints, in `BasketRequestValidation`, because binding is an HTTP concern.

Two of these came from probing the deployed API rather than from reading the code, and both are
now covered by scenarios:

- `{"couponCode":"PIZZA10"}` with no `items` returned **500**. A non-nullable reference type in a
  record is a compile-time annotation; `System.Text.Json` does not enforce it, so the property
  bound as null and the endpoint threw. The DTOs now declare `Items` as nullable so the compiler
  requires the check.
- A quantity of 2,000,000,000 returned **200** and a subtotal of €20,000,000,000. Rule 1 held —
  no price came from the client — but the server had priced a basket nobody could fulfil.

The line cap is not only about arithmetic. `IMenu.GetUnitPriceAsync` is one database round trip
per line, and for an order the whole loop runs inside the redemption transaction against a 5 DTU
database, so an uncapped line count is a cheap way to hold that transaction open.

**Preview is a hint; submission is the truth.** Both run the same calculation on the same input,
and they can legitimately disagree — if a coupon expires or exhausts in between, the order is
created at full price and the response says why. That case is logged as a warning.

### Health checks

`/health` checks nothing external. If liveness checked the database, a brief database
interruption would restart a perfectly healthy app. `/health/ready` checks the DbContext.

Neither is exposed through the gateway, and the App Service has no `healthCheckPath` — platform
probes are subject to Easy Auth, so pointing one at `/health` would mark every instance unhealthy
unless `/health` were added to `excludedPaths`, which would publish it anonymously on the
internet.

---

## 7. Logging and monitoring

Serilog behind `ILogger<T>`, writing to Application Insights and the console. Application code
depends on `ILogger`, never on Serilog.

**Structured properties, never interpolated strings.**

```csharp
_logger.LogInformation("Coupon {CouponCode} rejected: {Reason}", code, reason);
```

That gives fields you can query. String interpolation gives text you can only search.

**One request, one ID.** API Management stamps `x-correlation-id` on the way in — keeping the
caller's if they sent one — and returns it on the way out, including on the error path.
`CorrelationIdMiddleware` pushes it onto the Serilog log context so every line for that request
carries it, mirrors it onto `Activity.Current` as a **tag**, and `ProblemDetails` reports it as
`traceId`, so the ID a caller quotes from a failed response is the ID in the logs.

The Activity mirroring is a tag rather than a replacement of the trace ID on purpose: overwriting
the Activity's identifiers would break the W3C trace context that joins the gateway's telemetry
to the application's, which is the thing the correlation ID exists for. APIM's diagnostic sets
`httpCorrelationProtocol: W3C` for the same reason.

**Logged:** coupon results with their reason, order totals, redemption limits reached, and — as a
warning — any coupon that passed preview and failed at submission.

**Never logged:** tokens, subscription keys, connection strings, or personal data. The
correlation ID is safe to return because it identifies a request, not a person.

Sampling is 100% here; in production it would be 5–10%, because Application Insights bills on
data volume and it is the most commonly underestimated cost in an Azure estate. One alert rule
on the 5xx rate is declared in Bicep.

---

## 8. Testing

**Reqnroll, not SpecFlow.** SpecFlow reached end of life on 31 December 2024 and never supported
.NET 8. Reqnroll is the maintained continuation by the original author, with the same Gherkin
syntax.

**Fifteen scenarios, at two levels.** Eight drive `PricingService` and `CouponEvaluator`
directly, because the rules are pure and a rule is best tested without an HTTP round trip in
front of it. The other seven run against the API through `WebApplicationFactory<Program>`, so
they exercise real routing, model binding, status codes and the database — everything an
endpoint does that a direct call would skip.

Which level a scenario belongs at is decided by what it is claiming. "A discount never exceeds
the subtotal" is arithmetic and needs no server. "An order redeems exactly one use of a coupon"
is a claim about a transaction and cannot be made anywhere but through the endpoint.

**The database is swapped for SQLite in-memory — specifically SQLite, not the EF Core in-memory
provider.** The EF provider supports neither `ExecuteUpdateAsync` nor transactions, which are
exactly the two mechanisms redemption depends on. Under it, every order carrying a coupon
returned a 500 *and the suite still passed*, because no scenario exercised that path. The most
important concurrency invariant in the design had no coverage and could not have had any. The
test host keeps one `SqliteConnection` open for the lifetime of the factory — a SQLite in-memory
database exists only while a connection to it is open — and creates the schema with
`EnsureCreated()`.

**The pricing and coupon logic is never mocked.** A test that mocks the thing it is testing
proves nothing. Only the database is substituted.

The scenarios that matter most:

- A discount never makes the total negative.
- An order is priced from the server's own data, ignoring whatever the client claims.
- Previewing a coupon three times does not consume a redemption.
- Each rejection reason produces the right outcome.
- **An order carrying a valid coupon is discounted, increments `UsageCount` by exactly one,
  and writes one `CouponRedemptions` row against that order.** This is the brief's functional
  goal at the level the brief states it — the coupon affecting the final order price — and it
  is the only scenario that exercises rule 4's atomic `UPDATE` at all.
- An order against a coupon at its redemption limit is still created, at full price, with
  `couponApplied: false` and `RedemptionLimitReached`, and consumes nothing.
- A request with no `items` property is a 400 and **not a server error** — asserted separately
  from the status code, because "not 5xx" is the contract these documents make and a status
  equality check alone would not say so.
- A quantity above the maximum is refused, asserting against `Basket.MaxQuantityPerLine` rather
  than a literal, so raising the cap cannot silently turn the scenario into a test of nothing.

**What the redemption scenarios do and do not establish.** They establish that the redemption
runs, that it moves the counter by one, that the audit row is written, and that an exhausted
coupon is refused without consuming anything. Both were mutation-checked rather than trusted:
making `TryRedeemAsync` claim a redemption without performing the `UPDATE` fails the first, and
dropping the audit-row write fails it too, so neither is a scenario that would pass against a
broken implementation.

They do not establish the *concurrent* case — two callers racing for the last redemption. That
is deliberate: a multi-threaded timing assertion inside a deployment pipeline is flaky, and a
flaky test is worse than no test. The implementation is safe regardless, because the update is a
single statement with the limit in its `WHERE` clause. The coverage is the limitation, not the
behaviour, and it is listed as one in [assumptions.md](assumptions.md) §3.

Scenarios are written in business language; the mapping from a readable step to a rejection
reason lives in the step definition, so changing the wording of a message does not break a test.

---

## 9. Infrastructure choices

| Resource | Choice | Reason |
|---|---|---|
| API Management | **Consumption** | Provisions in ~3 minutes. Other tiers take 30–45, which makes a from-scratch pipeline impractical. |
| Backend | App Service, **Linux B1** | The free tier has a daily CPU quota that stops the app when exceeded. |
| Database | Azure SQL, **Basic** | Basic has no auto-pause, so the 30–60 second serverless wake-up that would look like a broken app cannot occur. Auto-pause is a *serverless* feature; there is no property to switch off, and none is missing. |
| Frontend | Storage account **static website** | Deploys cleanly and adds no second login system. Static Web Apps' built-in auth would sit alongside the gateway's, and two competing sign-in systems is confusing. |
| Telemetry | App Insights + Log Analytics | Connected to both the gateway and the backend. |

The storage account name is **deterministic rather than random**, so the frontend URL stays
stable across a teardown and rebuild and a registered redirect URI keeps working. It is composed
from `uniqueString(subscription().id, resourceGroup().name)`, and neither of those changes when
the group is deleted and recreated in the same subscription — so the name is reproduced, not
held. (`main.bicep` takes a `storageAccountName` parameter that would pin it literally; nothing
passes one.) It is not knowable *before* the first deployment either way, because the `zNN`
segment of `https://<account>.zNN.web.core.windows.net` is a DNS zone assigned at account
creation.
`errorDocument404Path` is `index.html`, so refreshing on a client-side route does not 404.

Both managed identities are **user-assigned, not system-assigned**. That is an authentication
decision and it is covered in [authentication.md](authentication.md) §6.

Roughly $20/month at rest: App Service B1 ~$13, SQL Basic ~$5, APIM Consumption effectively free
at this volume, storage and Application Insights pennies. One resource group, so
`az group delete` removes all of it.
