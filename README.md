# Pizza Shop — coupon service

A pizza ordering service with coupon support. .NET 8 API behind Azure API Management, React
frontend, Azure SQL, deployed end to end from an Azure DevOps pipeline using Bicep.

**Technical assignment for Bangladesh Software Solution · Khalid Shams**

---

## Try it

### **<https://stcouponsvclabdtjori.z29.web.core.windows.net>**

Sign-in is only needed to place an order. Browsing the menu and checking a coupon are anonymous.

**Test account**

```
username:  <PENDING — reviewer test account>
password:  <PENDING>
```

> The account is a member of the tenant and has been signed in to once, so there is no consent
> screen and no MFA registration prompt.

**Coupons to try**

| Code | What happens |
|---|---|
| `PIZZA10` | 10% off — applies |
| `FIVEOFF` | €5 off — applies |
| `SPEND50` | 15% off, but only over €50 — rejected below that, `MinimumSpendNotMet` |
| `OLDCODE` | expired in 2020 — rejected, `Expired` |
| anything else | rejected, `NotFound` |

A rejected coupon never fails the order. The order is created at full price and the response says
which reason applied.

**Worth clicking:** add pizzas, press **Check** with `SPEND50` below €50, then add more until it
crosses. The preview is a read-only hint — it consumes no redemption. The server recalculates
from scratch when you submit, and the two are allowed to disagree.

---

## What it does

- **Two coupon types** — percentage and fixed amount.
- **Three conditions** — expiry, minimum order value, total redemption limit.
- **The server decides the price.** No endpoint accepts a price, subtotal or total from the
  client. The browser sends `pizzaId` and `quantity`; every price is resolved server-side. This is
  enforced by construction — a priced basket cannot be built from outside the Ordering assembly.
- **Preview never mutates.** `POST /coupons/validate` consumes no redemption. Only `POST /orders`
  redeems, as a single atomic `UPDATE`.
- **A rejection carries a reason**, never a bare boolean — one enum drives the customer message,
  the log entry and the test assertion.

| Endpoint | Auth |
|---|---|
| `GET /api/v1/menu` | APIM subscription key |
| `POST /api/v1/coupons/validate` | APIM subscription key |
| `POST /api/v1/orders` | subscription key **+** an Entra access token with `Orders.Write` |

Gateway: `https://apim-couponsvc-lab-dtjori.azure-api.net`

---

## Deploy it

Everything below the Day 0 line is automated. Deleting the resource group and re-running the
pipeline produces a working system.

**Day 0 — by hand, once.** A pipeline cannot create the credential it uses to log in.

1. An Azure DevOps service connection (workload identity federation, Contributor on the
   subscription).
2. Two Entra app registrations — `coupon-api` and `coupon-spa`. The SPA's redirect URIs go on the
   **SPA** platform, not `publicClient` and not `web`.
3. A reviewer test account — a member, signed into once.

**Day 1 — the pipeline.** Push to `master`, or run `azure-pipelines.yml` manually.

```
1  Build and test          dotnet build, BDD suite, publish artifacts
2  Provision               Bicep: resource group, APIM, App Service, SQL, storage, telemetry
3  Grant database access   create the contained user for the App Service's managed identity
4  Deploy backend          push the API; EF migrations run at startup
5  Build and deploy UI     npm build with the gateway URL injected; upload to $web
6  Smoke test              six assertions through the gateway and against the live site
```

**Teardown:** `az group delete --name rg-coupon-service --yes`. Then re-run the pipeline.

Full detail, including the four failures that each cost a deploy cycle:
[docs/deployment.md](docs/deployment.md).

---

## Run it locally

```bash
dotnet test                                  # Reqnroll BDD suite
dotnet run --project src/PizzaShop.Api       # API on LocalDB — no Docker needed

cd web
cp .env.example .env.local                   # fill in the five values
npm install && npm run dev                   # http://localhost:5173
```

`http://localhost:5173` is a registered redirect URI and an allowed CORS origin, so a local
frontend talks to the deployed gateway unchanged. `npm run dev` also enables a token claims panel
that decodes the live access token — it is stripped from every production build, and the pipeline
fails if it ever reaches `dist/`.

---

## No passwords

**The running system holds no credential.** API Management reaches the App Service as a managed
identity; the App Service reaches Azure SQL as one. The SQL server has Entra-only authentication,
so a password does not merely go unused — one cannot be created. No Key Vault, because there is
nothing to store.

The pipeline uses two credentials at deploy time, both fetched from ARM at the moment they are
needed and never stored. The precise claim, and why the storage account key is a *smaller* grant
than the role assignment that would have replaced it, is in
[docs/authentication.md](docs/authentication.md) §9.

---

## Documentation

| | |
|---|---|
| [docs/architecture.md](docs/architecture.md) | The shape, the coupon/ordering split, why one deployable, data model, testing |
| [docs/deployment.md](docs/deployment.md) | Day 0 and Day 1, the six stages, how to run it, what has already cost a deploy cycle |
| [docs/authentication.md](docs/authentication.md) | The two credentials, PKCE, gateway-to-backend, passwordless SQL, what is proven and what is not |
| [docs/assumptions.md](docs/assumptions.md) | Assumptions, known limitations, what would come next |

Also in the repository: [docs/approach.md](docs/approach.md), the approved design document
written before the build, and [docs/decisions.md](docs/decisions.md), the running log of every
non-obvious choice made while building — including the ones that turned out to be wrong.

```
src/PizzaShop.Api/              HTTP endpoints, composition root
src/PizzaShop.Ordering/         basket, pricing, orders    -> depends on ICouponEvaluator only
src/PizzaShop.Coupons/          coupon rules               -> knows nothing about pizzas
src/PizzaShop.Infrastructure/   EF Core, persistence, adapters
tests/PizzaShop.Bdd/            Reqnroll scenarios
infra/                          Bicep, APIM policies, OpenAPI, deployment scripts
web/                            React + MSAL frontend
```
