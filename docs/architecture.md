# Architecture

**Coupon service — pizza ordering with coupon support.**
A .NET 8 API behind Azure API Management. A React single-page app. An Azure SQL database. An
Azure DevOps pipeline that builds all of it from an empty subscription.

This document covers the shape of the system, the coupon/ordering split, and why the two domains
ship in one deployable. Authentication has its own document
([authentication.md](authentication.md)). So does deployment ([deployment.md](deployment.md)).

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
caller: the gateway's managed identity. A request that arrives at
`app-couponsvc-lab-*.azurewebsites.net` without that identity's token is rejected before any
application code runs. The caller and the backend never trust each other. Both trust the gateway,
separately.

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

Three parts of that signature carry weight.

**It takes only the subtotal.** `PizzaShop.Ordering.Basket` carries per-line unit prices. Passing
it would hand the coupon project pizza IDs and prices. `CouponBasket` carries one number, so the
coupon code cannot see them. The boundary is structural. It does not rely on the coupon code
choosing to look away. Every condition in the locked coupon set needs only the order's value:
expiry, minimum order value, redemption limit. Nothing in scope is lost.

*The cost, stated plainly:* an item-scoped coupon would mean reopening this signature and passing
item data across the boundary. "10% off pizzas only", buy-one-get-one, a category restriction.
That is a deliberate design change. It is not a small edit. The trade is right here, because the
coupon set is deliberately locked to two types and three conditions.

**It never returns a bare boolean.** A rejection carries a `CouponRejectionReason`. That one enum
drives the customer-facing message, the log entry and the test assertion. Changing the wording of
a message cannot break a test. Nobody can add a new rejection reason without every consumer
seeing it.

**It takes the current time as a parameter.** No coupon rule calls `DateTime.UtcNow`. Testing
expiry any other way means manipulating the machine clock.

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

Ordering computes the subtotal from its own data. It asks for a discount. It computes the total
itself. Two alternatives were rejected. If ordering applied the coupon rules, the coupon logic
would live in two places. If the coupon side returned a final total, it would need to know pizza
prices and delivery, which turns it into a second ordering service.

**The discount cap is enforced twice.** Once in `CouponEvaluator.CalculateDiscount`, and again as
`Math.Max(0m, ...)` in `PricingService.PriceAsync`. The first makes the second redundant. The
second stays. It is the structural invariant, so a future evaluator that forgets the cap still
cannot produce a negative total.

**Rounding happens once**, on the final discount, with `MidpointRounding.AwayFromZero`. That is
standard retail rounding: €0.005 becomes €0.01.

### Why one deployable

Two App Services would mean two pipelines, two health checks, and a fourth authentication hop
between them. That is more platform work than the assignment needs. The assignment puts its
emphasis on the gateway and the pipeline.

What matters is that the boundary is real. It is:

```
src/PizzaShop.Api/              HTTP endpoints, composition root
src/PizzaShop.Ordering/         basket, pricing, orders   -> references ICouponEvaluator only
src/PizzaShop.Coupons/          coupon rules              -> no reference to Ordering
src/PizzaShop.Infrastructure/   EF Core, persistence, adapters
tests/PizzaShop.Bdd/            Reqnroll scenarios
web/                            React frontend
```

`Ordering` depends on `ICouponEvaluator` and on nothing else from the coupon side. `Coupons` knows
nothing about pizzas, delivery or orders. Project references enforce that dependency. It does not
rest on discipline.

`CouponEvaluator` itself works against an injected `IEnumerable<CouponRecord>`, so you can test
the rules with no infrastructure at all. `DatabaseCouponEvaluator`, in
`PizzaShop.Infrastructure`, is the adapter that supplies that catalogue from EF Core. The
repository interface lives in Infrastructure too, because persistence is not a domain concern.

### Splitting it later

The change is small. It was kept that way on purpose:

1. `PizzaShop.Coupons` moves to its own App Service.
2. `DatabaseCouponEvaluator` is replaced by an HTTP client implementing the same
   `ICouponEvaluator`.
3. The coupon service becomes a second API in API Management. The gateway authenticates to it
   exactly the way it already authenticates to this backend.

**The ordering code does not change.** Neither do the BDD scenarios. They exercise the interface
rather than the implementation.

One thing would need designing at that point: redemption. The atomic update currently runs in the
same database transaction as the order write. Across a service boundary it becomes a compensating
action or a two-phase reservation. That is the real cost of the split. It is a
distributed-systems problem rather than a code-organisation one.

---

## 3. The server decides the price

**No endpoint accepts a price, a subtotal, or a total from the client.** Requests carry
`pizzaId` and `quantity`. The server resolves every price from its own data on every request.

The code enforces this, so a reviewer does not have to catch it:

- `Basket` has no public constructor. `BasketItem`'s constructor is `internal`.
- The only way to obtain a priced basket is `Basket.FromLinesAsync(IEnumerable<BasketLine>, IMenu)`,
  which reads each unit price from the menu.
- `BasketLine` carries `PizzaId` and `Quantity` and has no field a price could arrive in.

A probe that tried to fabricate a priced line from outside the assembly fails to compile:

```
error CS1729: 'BasketItem' does not contain a constructor that takes 3 arguments
```

So binding a request DTO onto a priced basket breaks the build. It never reaches review.
`FromLinesAsync` also rejects a quantity below one. A negative quantity would subtract from the
subtotal, which is the same class of problem as accepting a price, and a perfectly valid-looking
request could otherwise reach it.

The React client holds quantities only. There is no price in `sessionStorage` to tamper with, so
the rule holds by construction on the client as well as the server.

---

## 4. Redemption

```sql
UPDATE Coupons SET UsageCount = UsageCount + 1
WHERE Id = @Id AND UsageCount < RedemptionLimit
```

One statement. EF Core expresses it as `ExecuteUpdateAsync`, with the limit check in the `WHERE`
clause. No read-then-write, no change tracker, no explicit lock. Zero rows affected means the
coupon was exhausted between the check and the redemption, and the order is priced without it.
That is correct under concurrency with no locking of any kind.

**Preview never mutates.** `POST /coupons/validate` is read-only. It consumes no redemption and
writes nothing. Only `POST /orders` redeems. Previewing a coupon a hundred times leaves
`UsageCount` untouched. There is a scenario for it.

**The order and the redemption cannot diverge.** `PlaceOrder` opens a transaction that covers
both. It runs inside `strategy.ExecuteAsync(...)`, so the SQL retry strategy can replay it. Two
details in that block matter. The coupon evaluation sits *inside* it, so a redemption decision
from a failed attempt cannot survive into the retry. And `ChangeTracker.Clear()` runs first, so a
retry does not start out holding entities the failed attempt added.

---

## 5. Data model

| Table | Purpose |
|---|---|
| `Pizzas` | Menu: name, description, price. Seeded at startup. |
| `Coupons` | Code, type, value, description, expiry, minimum order value, redemption limit, usage count. |
| `Orders` | Timestamp, subtotal, discount, total, coupon code, whether it applied. |
| `OrderLines` | Pizza, quantity, unit price captured at order time. |
| `CouponRedemptions` | Audit trail: which order consumed which redemption, and when. |

**An order records no customer identity.** There is no user column. The order endpoint reads no
caller identity from the token it just validated. This is deliberate and settled. See
[assumptions.md](assumptions.md) §2, which also states where it is weakest.

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

The four coupons exist to make every rejection reason reachable from the browser without touching
the database. `NotFound` needs no seed data. Any unknown code produces it.

---

## 6. API surface

| Endpoint | Method | Auth | Behaviour |
|---|---|---|---|
| `/api/v1/menu` | GET | Subscription key | Pizzas and prices |
| `/api/v1/coupons/validate` | POST | Subscription key | Preview a discount. Changes nothing. |
| `/api/v1/orders` | POST | Subscription key **+ access token** | Place an order |
| `/health` | GET | Not exposed through the gateway | Liveness |
| `/health/ready` | GET | Not exposed through the gateway | Readiness |

Both coupon-aware endpoints use this request and response shape:

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
"can I use this code?". "No, it expired" is a valid answer to that question. The rejection travels
in `isValid` and `rejectionReason`.

**`POST /orders` returns 201 even when the coupon failed**, with `couponApplied: false` and a
reason. The order succeeded. The discount did not.

4xx and 5xx are for malformed requests, authentication failures and real faults. They use RFC 7807
`ProblemDetails` with a `traceId` carrying the correlation ID. They never carry a stack trace.

### Request validation

A malformed request gets a **400**, never a 500. Four things are rejected:

| Condition | Response |
|---|---|
| No `items` property at all | 400 — *Missing basket* |
| `items` present but empty | 400 — *Empty basket* |
| A quantity below 1 or above `Basket.MaxQuantityPerLine` (50) | 400 — *Invalid basket* |
| More than `Basket.MaxLines` (50) lines | 400 — *Invalid basket* |

The bounds live in `Basket.FromLinesAsync`, next to the existing quantity check. So they hold for
anything that builds a basket, not only for what arrives over HTTP. The null and empty checks sit
at the endpoints, in `BasketRequestValidation`, because binding is an HTTP concern.

Two of these came from probing the deployed API, not from reading the code. Both are now covered
by scenarios:

- `{"couponCode":"PIZZA10"}` with no `items` returned **500**. A non-nullable reference type in a
  record is only a compile-time annotation. `System.Text.Json` does not enforce it, so the
  property bound as null and the endpoint threw. The DTOs now declare `Items` as nullable, so the
  compiler requires the check.
- A quantity of 2,000,000,000 returned **200** and a subtotal of €20,000,000,000. Rule 1 held: no
  price came from the client. But the server had priced a basket nobody could fulfil.

The line cap is not only about arithmetic. `IMenu.GetUnitPriceAsync` costs one database round trip
per line. For an order, the whole loop runs inside the redemption transaction against a 5 DTU
database. So an uncapped line count is a cheap way to hold that transaction open.

**Preview is a hint. Submission is the truth.** Both run the same calculation on the same input,
and they can legitimately disagree. If a coupon expires or runs out in between, the order is
created at full price and the response says why. That case is logged as a warning.

### Health checks

`/health` checks nothing external. If liveness checked the database, a brief database interruption
would restart a perfectly healthy app. `/health/ready` checks the DbContext.

Neither is exposed through the gateway, and the App Service has no `healthCheckPath`. Platform
probes go through Easy Auth, so pointing one at `/health` would mark every instance unhealthy.
Avoiding that means adding `/health` to `excludedPaths`, which publishes it anonymously on the
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

**One request, one ID.** API Management stamps `x-correlation-id` on the way in, keeping the
caller's if they sent one, and returns it on the way out, including on the error path.
`CorrelationIdMiddleware` pushes it onto the Serilog log context, so every line for that request
carries it. It also mirrors the ID onto `Activity.Current` as a **tag**. `ProblemDetails` reports
it as `traceId`. So the ID a caller quotes from a failed response is the ID in the logs.

The Activity mirroring is a tag on purpose, and it does not replace the trace ID. Overwriting the
Activity's identifiers would break the W3C trace context that joins the gateway's telemetry to the
application's, and that link is what the correlation ID exists for. APIM's diagnostic sets
`httpCorrelationProtocol: W3C` for the same reason.

**Logged:** coupon results with their reason, order totals, redemption limits reached, and any
coupon that passed preview and then failed at submission. That last one is logged as a warning.

**Never logged:** tokens, subscription keys, connection strings, or personal data. The correlation
ID is safe to return because it identifies a request, not a person.

Sampling is 100% here. In production it would be 5–10%. Application Insights bills on data volume,
and that is the most commonly underestimated cost in an Azure estate. Bicep declares one alert
rule on the 5xx rate.

---

## 8. Testing

**Reqnroll, not SpecFlow.** SpecFlow reached end of life on 31 December 2024 and never supported
.NET 8. Reqnroll is the maintained continuation by the original author, with the same Gherkin
syntax.

**Fifteen scenarios, at two levels.** Eight drive `PricingService` and `CouponEvaluator` directly.
The rules are pure, and a rule is best tested with no HTTP round trip in front of it. The other
seven run against the API through `WebApplicationFactory<Program>`. They exercise real routing,
model binding, status codes and the database: everything an endpoint does that a direct call would
skip.

What a scenario claims decides which level it belongs at. "A discount never exceeds the subtotal"
is arithmetic and needs no server. "An order redeems exactly one use of a coupon" is a claim about
a transaction, and only the endpoint can make it.

**The database is swapped for SQLite in-memory.** SQLite specifically, not the EF Core in-memory
provider. The EF provider supports neither `ExecuteUpdateAsync` nor transactions, and redemption
depends on exactly those two mechanisms. Under it, every order carrying a coupon returned a 500
*and the suite still passed*, because no scenario exercised that path. The most important
concurrency invariant in the design had no coverage and could not have had any. The test host
keeps one `SqliteConnection` open for the lifetime of the factory, because a SQLite in-memory
database exists only while a connection to it is open. The test host creates the schema with
`EnsureCreated()`.

**The pricing and coupon logic is never mocked.** A test that mocks the thing it is testing proves
nothing. Only the database is substituted.

The scenarios that matter most:

- A discount never makes the total negative.
- An order is priced from the server's own data, ignoring whatever the client claims.
- Previewing a coupon three times does not consume a redemption.
- Each rejection reason produces the right outcome.
- **An order carrying a valid coupon is discounted, increments `UsageCount` by exactly one,
  and writes one `CouponRedemptions` row against that order.** This is the brief's functional
  goal at the level the brief states it: the coupon affecting the final order price. It is also
  the only scenario that exercises rule 4's atomic `UPDATE` at all.
- An order against a coupon at its redemption limit is still created, at full price, with
  `couponApplied: false` and `RedemptionLimitReached`, and consumes nothing.
- A request with no `items` property is a 400 and **not a server error**. The scenario asserts
  that separately from the status code, because "not 5xx" is the contract these documents make
  and a status equality check alone would not say so.
- A quantity above the maximum is refused. The scenario asserts against
  `Basket.MaxQuantityPerLine` rather than a literal, so raising the cap cannot silently turn it
  into a test of nothing.

**What the redemption scenarios do and do not establish.** They establish that the redemption
runs, that it moves the counter by one, that the audit row is written, and that an exhausted
coupon is refused without consuming anything. Both were mutation-checked rather than trusted.
Making `TryRedeemAsync` claim a redemption without performing the `UPDATE` fails the first.
Dropping the audit-row write fails it too. So neither would pass against a broken implementation.

They do not establish the *concurrent* case: two callers racing for the last redemption. That is
deliberate. A multi-threaded timing assertion inside a deployment pipeline is flaky, and a flaky
test is worse than no test. The implementation is safe anyway, because the update is a single
statement with the limit in its `WHERE` clause. The limitation is the coverage, not the behaviour,
and [assumptions.md](assumptions.md) §3 lists it as one.

Scenarios are written in business language. The mapping from a readable step to a rejection reason
lives in the step definition, so changing the wording of a message does not break a test.

---

## 9. Infrastructure choices

| Resource | Choice | Reason |
|---|---|---|
| API Management | **Consumption** | Provisions in ~3 minutes. Other tiers take 30–45, which makes a from-scratch pipeline impractical. |
| Backend | App Service, **Linux B1** | The free tier has a daily CPU quota that stops the app when exceeded. |
| Database | Azure SQL, **Basic** | Basic has no auto-pause, so the 30–60 second serverless wake-up that would look like a broken app cannot happen. Auto-pause is a *serverless* feature. There is no property to switch off, and none is missing. |
| Frontend | Storage account **static website** | Deploys cleanly and adds no second login system. Static Web Apps' built-in auth would sit alongside the gateway's, and two competing sign-in systems are confusing. |
| Telemetry | App Insights + Log Analytics | Connected to both the gateway and the backend. |

The storage account name is **deterministic rather than random**, so the frontend URL stays stable
across a teardown and rebuild and a registered redirect URI keeps working. The name comes from
`uniqueString(subscription().id, resourceGroup().name)`. Neither input changes when the group is
deleted and recreated in the same subscription, so the name is reproduced rather than held.
(`main.bicep` takes a `storageAccountName` parameter that would pin it literally. Nothing passes
one.) The URL is still not knowable *before* the first deployment either way, because the `zNN`
segment of `https://<account>.zNN.web.core.windows.net` is a DNS zone assigned at account
creation.
`errorDocument404Path` is `index.html`, so refreshing on a client-side route does not 404.

Both managed identities are **user-assigned, not system-assigned**. That is an authentication
decision, and [authentication.md](authentication.md) §6 covers it.

Roughly $20/month at rest: App Service B1 ~$13, SQL Basic ~$5, APIM Consumption effectively free
at this volume, storage and Application Insights pennies. One resource group, so
`az group delete` removes all of it.
