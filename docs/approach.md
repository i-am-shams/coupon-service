# Coupon Service — Proposed Approach

**Khalid Shams** · Technical assignment for Bangladesh Software Solution

---

## 1. Summary

A .NET 8 backend with three endpoints behind Azure API Management, a React frontend, and an Azure DevOps pipeline that sets up and deploys everything using Bicep.

The coupon logic is a separate project with its own interface, so it can be split into its own service later without breaking anything. For now, it ships inside one deployable — the reason is in §3.

One design rule: **the server decides the price.** The browser sends a basket. It never sends money.

---

## 2. Coupon domain

**Two coupon types**

| Type | Example | Effect |
|---|---|---|
| Percentage | `PIZZA10` | 10% off the subtotal |
| Fixed amount | `FIVEOFF` | €5 off the subtotal |

**Three conditions:** expiry date, minimum order value, and a total redemption limit.

**No free-item coupons.** A percentage or fixed discount is just arithmetic. A free item changes the basket, so the coupon would need to know product IDs and prices — tying coupons to the product catalog for no real benefit.

**The interface**

```csharp
public interface ICouponEvaluator
{
    CouponEvaluation Evaluate(string code, CouponBasket basket, DateTimeOffset asOf);
}

public sealed record CouponBasket(decimal Subtotal);

public sealed record CouponEvaluation(
    bool IsValid,
    decimal DiscountAmount,
    string? Description,
    CouponRejectionReason? Reason);
```

Three things matter here.

It **never returns just true or false.** A rejection carries a reason — `Expired`, `MinimumSpendNotMet`, `NotFound`, `RedemptionLimitReached` — which drives the message the customer sees, the log entry, and the test assertion from one place.

It **takes the current time as a parameter** instead of reading the clock inside. Otherwise expiry rules cannot be tested properly.

It **takes a subtotal, not a basket.** Every condition in the locked set — expiry, minimum order value, redemption limit — needs only the order's value. Passing the full basket would hand the coupon project pizza IDs and prices, and a boundary that depends on the coupon code politely not looking is not a boundary. With `CouponBasket` it genuinely cannot see them. The cost is that any item-scoped coupon — "10% off pizzas only", buy one get one — would require reopening this signature, which is a deliberate design change rather than a small edit.

**Rules enforced:** a discount can never be more than the subtotal, so the total never goes below zero. One coupon per order. Rounding happens once, on the final discount.

---

## 3. Architecture

### One deployable, two separate domains

```
src/
  PizzaShop.Api/            HTTP endpoints
  PizzaShop.Ordering/       basket, pricing, orders
  PizzaShop.Coupons/        coupon rules and evaluation
  PizzaShop.Infrastructure/ EF Core, database
tests/
  PizzaShop.Bdd/            Reqnroll scenarios
```

Ordering depends on `ICouponEvaluator`. It doesn't know how a discount is calculated. Coupons don't know about pizza prices or delivery.

**Why not two separate services.** Two App Services means two pipelines, two health checks, and extra authentication between them. That's more work than the assignment needs. What matters is the interface, and that boundary is already real — I can move coupons behind an HTTP call later without touching the ordering code.

I'd rather focus on the gateway and pipeline, which is where the assignment puts the emphasis.

**If you want a separate coupon service**, the change is small and I've kept it that way on purpose. `PizzaShop.Coupons` moves to its own App Service, the interface implementation is swapped for an HTTP client, and it becomes a second API in APIM. The ordering code doesn't change. Tell me and I'll build it that way.

### Who calculates what

Ordering calculates the subtotal from its own prices. Then it asks the coupon side for a discount. Finally, it calculates the total itself.

The other options were worse. If ordering applied the coupon rules itself, the coupon logic would exist in two places. If the coupon service returned the final total, it would need to know pizza prices and delivery — that would turn it into another ordering service.

---

## 4. API design

| Endpoint | Method | Auth | Purpose |
|---|---|---|---|
| `/api/v1/menu` | GET | Subscription key | Pizzas and prices |
| `/api/v1/coupons/validate` | POST | Subscription key | Preview a discount. Changes nothing. |
| `/api/v1/orders` | POST | Subscription key + token | Place an order |
| `/health` | GET | Not exposed via APIM | Liveness |
| `/health/ready` | GET | Not exposed via APIM | Readiness |

### Requests carry baskets, not prices

```json
POST /api/v1/coupons/validate
{
  "couponCode": "PIZZA10",
  "items": [ { "pizzaId": 1, "quantity": 2 }, { "pizzaId": 3, "quantity": 1 } ]
}
```

```json
{
  "couponCode": "PIZZA10",
  "isValid": true,
  "rejectionReason": null,
  "subtotal": 31.50,
  "discountAmount": 3.15,
  "total": 28.35,
  "description": "10% off your order"
}
```

The order request uses the same shape. **No endpoint accepts a price or total from the client.**

This is the most important decision in the design. If the validate endpoint accepted `"subtotal": 150.00` from the browser, anyone could claim a huge basket and get a discount on money they never spent.

### Preview is a hint. Submission is the truth.

Both endpoints run the same calculation on the same input. The preview lets the customer see what they'd save. It changes nothing and uses up no redemption.

Submission recalculates from scratch. The two can disagree — if a coupon expires in between, the order is created at full price and the response says why. That case is logged as a warning.

### Status codes

`POST /coupons/validate` returns **200** even when the coupon is rejected. Its job is to answer "can I use this code?" — and "no, it expired" is a valid answer.

`POST /orders` returns **201** even if the coupon failed, with `couponApplied: false` and a reason. The order worked; the discount didn't.

4xx and 5xx are for bad requests, authentication failures, and real faults. Faults use RFC 7807 `ProblemDetails` with a `traceId` matching the correlation ID, and never return a stack trace.

---

## 5. Authentication

### Two things, two jobs

Every call needs an **APIM subscription key**. Placing an order also needs an **Entra ID token**.

They do not do the same job. The key shows which client and product are making the call, and it's how the gateway applies rate limits. It does not prove who you are — a key never expires and says nothing about what the holder can do. The token proves who you are and what you are allowed to do.

Simply put: **the key gives access to the API. The token lets you place an order.**

### Anonymous browsing, sign-in at checkout

- `GET /menu` and `POST /coupons/validate` — key only
- `POST /orders` — key plus a valid token

Nobody should have to sign in to look at a menu. It also means the app works the moment it loads.

In APIM, `validate-jwt` is scoped to the order endpoint rather than the whole API — a better demonstration of policy scoping than one blanket rule.

### The flow

Authorization code with PKCE, using `@azure/msal-react`.

A browser can't keep a secret, so the client-credentials flow isn't available to a React app. PKCE proves the app finishing the sign-in is the one that started it.

The gateway validates the token with `validate-jwt`, checking three things:

- **audience** — the token was issued for this API, not another app
- **issuer** — it came from this tenant
- **scope** — the caller has `Orders.Write`

Audience is the one most often left out of copied examples. Without it, any valid token from the tenant is accepted, including one issued for a completely unrelated app.

### Gateway to backend

The App Service accepts requests only from API Management. APIM authenticates with a user-assigned managed identity, and the App Service allows that one client and nothing else.

User-assigned rather than system-assigned, for a reason that only appears when you build it. A system-assigned identity exposes no client ID to ARM — and the client ID is what both the App Service's allowed-client list and the SQL contained user are built from. Recovering one from a principal ID means a Microsoft Graph call the deployment principal has no permission to make. A user-assigned identity also outlives its App Service: recreate the app with a system-assigned identity and it returns with a new client ID, leaving a database user that exists under the right name and silently no longer authenticates.

Two separate trust relationships: the caller trusts the gateway, and the backend trusts the gateway. The caller and the backend never trust each other, and the backend can't be reached directly.

---

## 6. Infrastructure

| Resource | Choice | Reason |
|---|---|---|
| API Management | Consumption tier | Deploys in ~3 minutes. Other tiers take 30–45, which makes a from-scratch pipeline impractical. |
| Backend | App Service, Linux, B1 | The free tier has a daily CPU limit that stops the app when exceeded. |
| Database | Azure SQL, Basic tier | Basic has no auto-pause to switch off. The 30–60 second serverless wake-up that would look like a broken app cannot occur. |
| Frontend | Storage account, static site | Deploys cleanly and adds no second login system. |
| Telemetry | Application Insights + Log Analytics | Connected to both the gateway and the backend. |

### No stored secrets

**The running system holds no credential.** APIM talks to the App Service using a managed identity, and the App Service talks to Azure SQL the same way. The SQL server has Entra-only authentication, so a password does not merely go unused — one cannot be created. No Key Vault is needed, because there is nothing to store.

**The pipeline is a narrower claim, and worth stating precisely rather than rounding up.** Two credentials are used at deploy time: the API Management subscription keys, and the storage account key that uploads the frontend. Both are fetched from ARM at the moment they are needed, used inside a single task, and never written to a file or published as a pipeline variable.

The storage key is there because the alternative is worse. Uploading blob content is a data action that Contributor does not include, and the pipeline cannot grant itself the role that would cover it — `Microsoft.Authorization/*/Write` is in Contributor's `notActions`. Making it work would mean giving the service connection User Access Administrator: the standing power to assign itself any role, on anything. Contributor already includes `listKeys`, so the account key confers nothing the pipeline could not already obtain, where that role grant would confer a great deal.

To be straight about what changed here: an earlier draft of this section said there was no password anywhere in the solution, full stop. That was broader than the truth, and the subscription keys made it loose before the storage key ever existed — §5 requires them. The claim that holds is about the running system, which was always the substance of it.

The tricky part is the database, because the usual approach is blocked.

Normally, to give a managed identity access to Azure SQL, you'd run:

```sql
CREATE USER [my-app] FROM EXTERNAL PROVIDER;
```

That makes SQL look up the identity in Entra ID, which means the SQL Server needs the **Directory Readers** role. That requires a tenant administrator, so it can't be done from an app pipeline — and on someone else's tenant it may not be possible at all.

There's a documented alternative that skips the lookup. If you give SQL the ID directly as a SID, it never has to ask Entra anything:

```sql
CREATE USER [my-app] WITH SID = 0x<client-id-as-bytes>, TYPE = E;
ALTER ROLE db_datareader ADD MEMBER [my-app];
ALTER ROLE db_datawriter ADD MEMBER [my-app];
```

The SID is the managed identity's **client ID** (its application ID) converted to bytes. The pipeline gets this from the Bicep output.

This detail matters because it's the opposite of the rule everywhere else in Azure. For role assignments you use the object ID. For a SQL contained user, it's different: users and groups use the object ID, but a service principal (which a managed identity is) uses the application ID. If you use the object ID here, the user is created but fails to authenticate later.

To run that command, the pipeline has to be an Entra admin on the SQL Server. That's just a setting in Bicep, along with a firewall rule that lets Azure services connect.

The result is a passwordless database with no tenant admin needed and no manual step. It adds about twenty lines in the pipeline and one Bicep property — a fair trade for a database that has no credential to leak, rotate, or store.

### Frontend hosting

Azure Static Web Apps would be the production choice for CDN and custom domains. I'm not using it here because its built-in login would sit alongside the login at the gateway, and having two competing sign-in systems is confusing.

The storage account name is pinned rather than generated, so the frontend URL is **stable across a teardown and rebuild** and a registered redirect URI keeps working.

It is not knowable *before* the first deployment. A static-site hostname is `https://<account>.zNN.web.core.windows.net`, and the `zNN` segment is a DNS zone assigned when the account is created — `z29` for this deployment, and not predictable from the account name. So the redirect URI is registered after the first provision, and the pipeline fails the frontend stage if the deployed origin ever stops matching what was registered.

`errorDocument404Path` is set to `index.html` so a page refresh on a client-side route doesn't 404.

---

## 7. Deployment

### Where "from scratch" starts

A pipeline can't create the credential it uses to log in. Being clear about where that line falls is better than pretending it isn't there.

**Day 0 — set up once, by hand, and documented**

1. Azure subscription and Azure DevOps service connection
2. Two Entra ID app registrations (the API and the React app). The React app's redirect URIs go on the **SPA** platform. Not `publicClient` — Entra accepts a browser origin there and then fails sign-in, because CORS on the token endpoint is enabled only for SPA-platform URIs. Not `web` — that platform expects a client secret to redeem the code, which a browser cannot hold. This item completes in two sittings: `http://localhost:5173` at the start, and the deployed static-site origin once the storage account exists, added **alongside** the localhost one rather than replacing it.
3. A test user for review

Nothing else. No database credential and no tenant-level role grant needed — see §6.

**Day 1 — every pipeline run, fully automated**

Resource group, all Azure resources from Bicep, database schema, backend deploy, APIM API and policies, frontend build and upload, smoke test.

App registrations live in Microsoft Graph, not in ARM, so Bicep can't create them. Doing it from a pipeline needs `Application.ReadWrite.All`, which most tenants block and which would let the deployment give itself more permissions. Creating identity objects belongs in identity governance, not in an app pipeline. Their IDs are passed into Bicep as parameters.

### Stages

```
1  Build and test      dotnet build, unit and BDD tests, publish artifact
2  Provision           Bicep deploy → outputs: gateway URL, app name, storage account, identity
3  Grant DB access     derive the SID, create the contained user for the App Service identity
4  Deploy backend      push artifact; EF Core migrations run at startup
5  Build and deploy UI npm build with gateway URL and client ID injected; upload to $web
6  Smoke test          call the gateway and check both the key and the token policies
```

Build and test come first, so no Azure resource is created for code that doesn't compile. The frontend build comes after provisioning because the gateway URL is built into the JavaScript — that's a real dependency.

### Two things worth noting

**Migrations run when the app starts**, not from a pipeline step. This avoids opening the SQL firewall to build agents. The trade-off: with more than one instance they can race, and a failure shows up as a failed start rather than a failed deploy. Migration bundles would be the cleaner production answer.

**The smoke test checks the response body, not just the status code.** A status-only check can pass against a deployment that isn't serving the app at all. I've shipped that bug before — a single-page app whose fallback returned 200 for every path.

**It also checks that the gateway's security policies actually fire.** Otherwise nothing tests them — a deployment could go green with `validate-jwt` misconfigured and nobody would know. Six checks. Each asserts *which* component rejected the call, not only the status, because a 401 from the key check and a 401 from the token policy are indistinguishable by status alone:

| Call | Expected |
|---|---|
| `GET /menu` with no subscription key | 401, body naming the missing key — so it is the key check and not something else returning 401 |
| `GET /menu` with a key | 200, body contains a known pizza name |
| `POST /orders` with a key but no token | 401, body carrying the `validate-jwt` message |
| `POST /orders` with a key and a malformed token | 401 from the gateway |
| `GET` the static site root | 200, serving the built application rather than a fallback |
| the deployed JavaScript bundle | contains no development-only token claims panel |

---

## 8. Testing

**Reqnroll, not SpecFlow.** SpecFlow reached end of life on 31 December 2024 and never supported .NET 8. Reqnroll is the maintained continuation by the original author, with the same Gherkin syntax.

Scenarios run against the API through `WebApplicationFactory`, so they exercise real routing, model binding, and status codes — not just calling objects. The database is swapped for **SQLite in-memory** — SQLite specifically, not the EF Core in-memory provider. The EF provider supports neither `ExecuteUpdateAsync` nor transactions, which are exactly the two mechanisms the atomic redemption below depends on. Under it every order carrying a coupon returns a 500, and the suite still passes, because no scenario would have exercised the path. **The pricing and coupon logic is never mocked** — a test that mocks the thing it's testing proves nothing.

Scenarios are written in business language. The mapping from a readable step to a rejection reason lives in the step definition, so changing the wording of a message doesn't break tests.

The scenarios that matter most:

- A discount never makes the total negative
- An order is priced from the server's own data, ignoring whatever the client claims
- Previewing a coupon three times doesn't use up a redemption
- Each rejection reason produces the right outcome

Redemption is **implemented** as a single atomic update, not a read-then-write:

```sql
UPDATE Coupons SET UsageCount = UsageCount + 1
WHERE Id = @Id AND UsageCount < RedemptionLimit
```

Zero rows changed means the coupon was exhausted between the check and the redemption, and the order is priced without it. This is correct under concurrency without any explicit locking.

The BDD scenarios cover sequential exhaustion. The parallel case isn't tested because a multi-threaded timing assertion in a deployment pipeline is flaky — and a flaky test is worse than no test. The implementation is safe regardless.

---

## 9. Logging and monitoring

Serilog behind `ILogger<T>`, writing to Application Insights and the console. Application code depends on `ILogger`, not on Serilog.

**Structured properties, never interpolated strings.** `LogInformation("Coupon {CouponCode} rejected: {Reason}", code, reason)` gives fields you can query. String interpolation gives text you can only search.

**One request, one ID.** API Management stamps a correlation ID — keeping the caller's if they sent one — and returns it in the response. The backend attaches it to the log scope so every line for that request carries it, and mirrors it onto the current `Activity` so Application Insights joins the gateway and the app into one view.

**Logged:** coupon results with their reason, order totals, redemption limits reached, and — as a warning — any coupon that passed preview but failed at submission.

**Never logged:** tokens, subscription keys, connection strings, or personal data. The correlation ID is safe to return because it identifies a request, not a person.

Sampling is 100% here. In production I'd drop it to 5–10% — Application Insights bills on data volume and it's the most commonly underestimated cost in an Azure estate. One alert rule on 5xx rate is declared in Bicep.

Health checks use `Microsoft.Extensions.Diagnostics.HealthChecks`. Liveness checks nothing external — if it checked the database, a short database interruption would restart a perfectly healthy app. Readiness checks the database. Neither is exposed through the gateway.

---

## 10. Assumptions and known limitations

**Assumptions**

1. This is a new build — there's no existing ordering codebase to integrate with. If there is one, `ICouponEvaluator` is the integration point.
2. Menu items are seeded data. Managing the product catalog is out of scope.
3. One coupon per order. Stacking is a pricing decision, not a technical limit.
4. One currency, no tax or delivery fee.
5. Orders are stored but not fulfilled. There's no payment step.
6. The reviewer will use a pre-created test account, documented in the README. It is a **member** of the tenant rather than a guest, so no first-sign-in consent prompt can appear in front of them.

**Known limitations**

1. **Startup migrations** can race across instances. Fine at one instance; migration bundles are the production answer.
2. **Redemption concurrency** is handled by the atomic update in §8, but only the sequential case is covered by tests.
3. **Rate limiting is per subscription, not per user.** The APIM Consumption tier doesn't support `rate-limit-by-key` because per-key limits need shared counter state that a scale-to-zero gateway doesn't keep. Per-user throttling needs Standard v2 or above.
4. **No response caching.** Consumption has no built-in cache. The menu endpoint would be an obvious candidate with an external Redis.
5. **No developer portal**, for the same tier reason.
6. **Single region, no failover.** Multi-region needs Premium.

---

## 11. With more time

- Split `PizzaShop.Coupons` into its own deployable, which the current interface already allows
- Use APIM **named values** for tenant and application IDs in policy files, rather than injecting them at deploy time
- Migration bundles as a pipeline stage with a short firewall window
- Azure Static Web Apps for the frontend, with a custom domain
- Load testing, to check whether B1 and a Basic database are the right size

---

## 12. Order of work

| Step | Work |
|---|---|
| 1 | Domain model, coupon evaluation, BDD scenarios |
| 2 | API endpoints, EF Core, persistence, logging |
| 3 | Bicep, pipeline, first end-to-end deployment |
| 4 | APIM configuration, policies, Entra integration |
| 5 | React frontend and MSAL |
| 6 | Smoke tests, documentation, hardening |

The gateway and the pipeline come early rather than last, because they carry the highest risk of an unexpected blocker and the brief puts its emphasis there.
