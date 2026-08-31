# Assumptions and known limitations

What this build takes as given, what it deliberately does not do, and where it is weakest. The
last section says what would be done next and in what order.

---

## 1. Assumptions

1. **This is a new build.** There is no existing ordering codebase to integrate with. If there
   were one, `ICouponEvaluator` is the integration point — see
   [architecture.md](architecture.md) §2.

2. **Menu items are seeded data.** Eight pizzas and four coupons are inserted at startup, and the
   insert is idempotent. Managing the product catalogue — an admin UI, price changes,
   availability — is out of scope. There is no endpoint that writes to `Pizzas` or `Coupons`.

3. **One coupon per order.** Stacking is a pricing decision rather than a technical limit; the
   request carries a single `couponCode`.

4. **One currency, no tax, no delivery fee.** Prices are euros. Nothing in the pricing path
   handles VAT, regional rates, or a delivery charge, and adding any of them changes what
   "subtotal" means to the coupon evaluator.

5. **Orders are stored but not fulfilled.** There is no payment step, no kitchen, no dispatch, and
   no order status beyond "created".

6. **The reviewer uses a pre-created test account**, documented in [../README.md](../README.md).
   It is a **member** of the tenant rather than a guest, so no first-sign-in consent prompt can
   appear in front of them, and Entra **security defaults are disabled** on this tenant so that no
   MFA registration or challenge can either.

   Both halves of that matter, and the second is a property of the *tenant* rather than of the
   account — so it does not survive a rebuild. Security defaults are on by default; a tenant
   recreated from scratch would enforce MFA again and the reviewer would meet a wall this
   documentation says is not there. It is recorded as a Day 0 item in
   [deployment.md](deployment.md) §2 for that reason.

   Disabling it tenant-wide is the only lever available here: Conditional Access, which would
   scope the exemption to this one account, needs an Entra ID P1 licence and this tenant has no
   licensed SKUs. The trade is acceptable because the tenant is disposable and holds one lab
   application, no production identity and no real data. In an environment with any of those, the
   answer is a Conditional Access exclusion and not this.

7. **The coupon set is locked at two types and three conditions.** Percentage and fixed amount;
   expiry, minimum order value, total redemption limit. This is a scope boundary, and anything
   outside it is in §4 rather than in the code.

8. **A reviewer has a browser and no Azure access.** The system is exercised from
   the deployed frontend, not from a subscription. Nothing in the review path requires `az login`.

---

## 2. Deliberate exclusions

These are things a reviewer might expect to find and will not. Each was decided rather than
missed.

### No free-item coupons

A percentage or fixed discount is arithmetic. A free item changes the *basket*, so the coupon
would need product IDs and prices — tying the coupon domain to the product catalogue for no real
benefit. `CouponBasket` carries a subtotal precisely so that it cannot see them.

### No item-scoped coupons

"10% off pizzas only", buy-one-get-one, a category restriction, a minimum item count. All of them
require reopening `ICouponEvaluator.EvaluateAsync` to pass item data across the boundary. That is a
deliberate design change with a visible cost, which is exactly what the signature was chosen to
make it.

### No personal data, anywhere

**This system holds no personal data at all.** `OrderEntity` has a timestamp, amounts, coupon
fields and lines. It has no user column, and the order endpoint reads no caller identity from the
token it just validated.

That is a real property and it was worth keeping. Recording the caller's `oid` without enforcing
anything with it would collect personal data for no requirement. Authentication on `POST /orders`
**authorises the action rather than personalising it**: the token proves the caller may place an
order. The brief asks for no order history, no per-user limits, and no "my orders".

*Where this is weakest, stated rather than defended:* `CouponRedemptions` exists as an audit trail
— it records which order consumed which redemption and when, which `UsageCount` alone cannot tell
you. An audit trail with no actor answers "was this coupon redeemed" but not "by whom", and "by
whom" is the question anyone investigating coupon abuse actually asks. That is a genuine gap, not
a scope decision dressed up as one.

If it were closed, the narrowest change is one nullable `RedeemedByOid` column on the
**redemption**, not on the order, keeping the order itself anonymous. `oid` rather than
`preferred_username` — see §3.

*What would reopen it:* per-user coupon limits entering scope, or any requirement for order
history. Neither is in the brief.

### No admin surface

No endpoint creates or edits a coupon. In production that is the obvious next API, and it is the
one that would need a `roles`-based policy rather than the delegated `scp` check the order
endpoint uses.

---

## 3. Known limitations

### 1. Startup migrations can race

EF Core migrations run when the application starts, which avoids opening the SQL firewall to build
agents. With more than one instance they can race, and a failure surfaces as a failed *start*
rather than a failed *deploy*. Fine at one instance. Migration bundles run as a pipeline stage,
with a short firewall window, are the production answer.

### 2. Redemption concurrency is correct but only sequentially tested

The single atomic `UPDATE ... WHERE UsageCount < RedemptionLimit` is safe under concurrency with
no locking.

**Sequentially it is covered.** Two scenarios place real orders through the endpoint: one
redeems a coupon and asserts that `UsageCount` moved by exactly one and that a
`CouponRedemptions` row was written against that order; the other orders against a coupon at
its limit and asserts the order is still created at full price, with
`RedemptionLimitReached`, consuming nothing. Both were mutation-checked — breaking the `UPDATE`
fails the first, dropping the audit row fails it too.

**This was previously claimed and not true**, which is worth recording rather than quietly
correcting. Until 2026-08-30 the suite contained no scenario that redeemed a coupon at all: the
pricing scenarios never reached the order endpoint, and the two that did carried no coupon. So
the most important invariant in the design had no test, while this document and
[architecture.md](architecture.md) §8 both stated it had one. The claim was written from the
intent rather than from the feature file, and nothing made the two meet — the same shape as the
`nvarchar(50)` that lived in the schema and nowhere else.

**The parallel case is still not tested**, because a multi-threaded timing assertion inside a
deployment pipeline is flaky — and a flaky test is worse than no test. The implementation is
safe regardless; that *coverage* is the limitation, not the behaviour.

### 3. Rate limiting is per subscription, not per user

The APIM Consumption tier does not support `rate-limit-by-key` or `quota-by-key`: per-key limits
need shared counter state that a scale-to-zero gateway does not keep. Only the subscription-scoped
versions are available, applied at product scope. Per-user throttling needs Standard v2 or above.

A consequence worth naming: one account cannot be prevented from draining a coupon's redemption
limit by rate limiting alone. That needs per-user limits enforced inside the evaluator, which
reopens the locked coupon set.

### 4. No response caching

Consumption has no built-in cache. `GET /menu` is the obvious candidate — it changes rarely and is
called on every page load — and would need an external Redis.

### 5. No developer portal

Same tier reason. The OpenAPI contract is in the repository at
`infra/openapi/pizzashop.openapi.yaml` and is what APIM imports.

### 6. Single region, no failover

Multi-region gateway deployment needs Premium. The App Service, database and storage account are
single-instance in `centralindia`.

### 7. The positive order path is not covered by the pipeline

The smoke test's four gateway assertions are negatives, plus one positive that proves the
gateway-to-backend hop. **No automated assertion covers a valid caller token producing a 201**,
because that needs an interactive PKCE sign-in, which cannot be done from a pipeline agent without
storing a credential.

This matters more than it looks: if `validate-jwt` were reading the `Authorization` header instead
of the stashed caller token, it would be validating the gateway's own app-only token, which has no
`scp` claim — so it would *also* return 401, every assertion would still pass, and the endpoint
would be permanently broken for every real user.

**The behaviour is verified; the automation is what is missing.** A manual PKCE sign-in on
2026-08-28 produced a 201 with the token's claims checked first —
[authentication.md](authentication.md) §8 records the observed values and the order it created.
Between runs the gap is covered by the Application Insights evidence in
[authentication.md](authentication.md) §5. Neither is a pipeline assertion, which is why this is
still listed as a limitation.

### 8. The frontend's subscription key is readable by anyone

It is compiled into a public bundle and a browser cannot hide it. This is understood and accepted:
an APIM subscription key is a client identifier and a rate-limit handle, not a credential, and the
only mutating endpoint is protected by an Entra token instead. What it costs is that `GET /menu`
and `POST /coupons/validate` can be called by anyone who extracts it — both read-only, and one is
a deliberately anonymous endpoint. Full reasoning in [authentication.md](authentication.md) §1.

### 9. The redirect URI guard cannot read Entra

The pipeline fails if the deployed frontend origin stops matching `expectedFrontendOrigin`. That
variable is a record of what a human registered, not a reading of the registration — the
pipeline's principal has no Microsoft Graph permission. A green run means "deployment still
produces the origin someone registered". It does not mean "Entra is correct". Nothing in the
pipeline can check that.

### 10. Two deploy-time credentials exist

The running system holds none. The pipeline uses the APIM subscription keys and the storage
account key, both fetched from ARM at the moment they are needed and never stored. The precise
claim — *no secret is stored anywhere, and no credential is used that the deploying principal
could not already obtain* — and why the storage key is a **smaller** grant than the alternative
are in [authentication.md](authentication.md) §9.

### 11. Token lifetime is not the round hour people assume

Observed at **82 minutes**. Entra randomises access token lifetime between roughly 60 and 90
minutes so that fleets of clients do not re-authenticate in lockstep. The debugging heuristic — a
call that worked and now 401s is probably an expired token — holds; the exact number does not.
### 12. The SQL firewall allows all of Azure, not just this deployment

`infra/modules/sql.bicep` carries one firewall rule, `AllowAllWindowsAzureIps`, which is the
`0.0.0.0`–`0.0.0.0` special case meaning "allow Azure services". That is not a rule about
*this* subscription: it permits connection attempts from any Azure resource in any tenant.

What stands between that and the data is authentication, and it is strong — the server is
Entra-only, so no SQL login exists to guess, and the single contained user is bound to a
specific managed identity's SID. An arbitrary Azure resource can open a TCP connection and gets
no further. But the network boundary is doing no work, and defence in depth is the point of
having one.

**The honest fix costs a bigger plan.** Closing it means VNet integration plus a private
endpoint on the SQL server and `publicNetworkAccess: 'Disabled'`. VNet integration needs a
Standard App Service plan or above; B1 does not support it. That is roughly four times the
compute cost of this deployment, for a lab whose stated budget guardrail is $20/month.

So it is recorded rather than fixed. In an environment holding real orders it would be
non-negotiable, and it is the first thing to change if this became one.

---

## 4. With more time

Ordered by what would be done first, not by size.

1. **Prove the positive order path automatically.** Limitation 7 is the one gap that could hide a
   real defect. A Playwright run against the deployed frontend, signing in with the test account
   from a browser context, would close it — the credential problem is real but a pipeline secret
   for a throwaway test account in a lab tenant is a defensible trade, unlike storing one for a
   production identity.

2. **Pin `azp` in `validate-jwt` to the SPA's client ID — deliberately not done here.**
   Today the policy checks audience, issuer and `scp`. It does not check *which application*
   requested the token, so any app registration in this tenant that obtained consent for
   `Orders.Write` could call the order endpoint. Adding one `required-claims` entry on `azp`
   closes that.

   It is listed here rather than implemented because of when it arrived, not because it is hard.
   `validate-jwt` on `POST /orders` is the single policy the whole submission rests on; API
   Management validates policy XML only at save time, so the change is untestable without a
   provision cycle, and two of this project's deploy-cycle losses were already policy-schema
   errors of exactly that kind. Set against that, the tightening closes an attack that requires
   an attacker to already be able to create app registrations in the tenant.

   **I chose not to touch a working authentication policy days before submission.** That is the
   whole reasoning, and it is a better answer than a hardening nobody asked for.

3. **Split `PizzaShop.Coupons` into its own deployable.** The interface already allows it; the
   work that remains is redemption across a service boundary, which becomes a compensating action
   or a two-phase reservation. [architecture.md](architecture.md) §2 has the mechanics.

4. **Migration bundles as a pipeline stage**, with a short firewall window, replacing startup
   migrations.

5. **APIM named values for the tenant and application IDs** in the policy files, rather than
   `__PLACEHOLDER__` substitution at deploy time. Named values are the idiomatic mechanism; the
   substitution exists because `{{ }}` is APIM's own syntax and a policy containing an
   unresolvable named value is rejected at save time.

6. **Per-user rate limiting**, which means Standard v2 and `rate-limit-by-key`. This is what
   limitation 3 actually needs.

7. **Response caching on `GET /menu`**, with an external Redis.

8. **Azure Static Web Apps for the frontend**, with a custom domain and CDN — accepting the second
   sign-in system that ruled it out here, or configuring it away.

9. **Load testing**, to find out whether B1 and a Basic database are the right sizes. Both were
   chosen for behaviour under a demo rather than under load: B1 because the free tier's daily CPU
   quota stops the app, Basic because it has no auto-pause wake-up that would look like a broken
   application.

10. **A `RedeemedByOid` column on the redemption**, if coupon abuse investigation ever becomes a
   requirement — the narrowest way to close the gap in §2.
