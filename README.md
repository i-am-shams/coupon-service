# Pizza Shop — coupon service

A pizza ordering service with coupon support. .NET 8 minimal API behind Azure API Management, a
React + MSAL frontend, Azure SQL reached without a password. Infrastructure is Bicep; deployment
is a six-stage pipeline with no manual step in it.

**A technical assignment for a software consultancy · [Khalid Shams](https://khalid-shams.vercel.app)**

---

## The claim

**An empty resource group becomes the running system in 17m45s.** The resource group itself,
every Azure resource, the database schema, the database access grant, the backend, the frontend,
and a smoke test that checks the security policies actually fire — unattended, with no stored
secret anywhere.

Measured, not designed for.
**[Open the run log](https://github.com/i-am-shams/coupon-service/actions/runs/34342033409)** —
9 September 2026, six green jobs, starting from a resource group that did not exist.

| | |
|---|---|
| Empty resource group → green smoke test | **17m45s** |
| Pipeline jobs, no manual step | **6** |
| Stored secrets | **0** |
| BDD scenarios | **15** |
| Smoke assertions, through the gateway | **6** |

### Two pipelines, and why

Built for **Azure DevOps**, because the assignment required it. Ported to **GitHub Actions** so
the pipeline is public and a stranger can open the log rather than take my word for it.

`azure-pipelines.yml` is retained **unchanged** as the original deliverable.
`.github/workflows/deploy.yml` is its sibling — same six stages in the same order, same Bicep
templates, same scripts, deploying into a separate resource group so neither can take the SQL
administrator away from the other.

The Azure DevOps cold start measured **16m58s** on its own agent pool. Different runner, different
number; it is kept as a labelled comparison in
[docs/verification-run.md §7](docs/verification-run.md) rather than blended into the figure above.

---

## Live demo

> ### Live until ~22 September 2026
>
> The Azure subscription is a free trial and expires around then. After that these URLs stop
> resolving — which is exactly why the claim above rests on a reproducible pipeline and a written
> record rather than on a link.
>
> **Frontend** — <https://stcouponsvclabgh.z29.web.core.windows.net>
> **Gateway** — `https://apim-couponsvc-lab-vxziaw.azure-api.net`
>
> Browsing the menu and checking a coupon are anonymous. Placing an order requires a sign-in;
> **evaluation access is available on request.**

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
crosses. The preview is a read-only hint that consumes no redemption. The server recalculates from
scratch when you submit, and the two are allowed to disagree.

---

## Architecture

```
Browser ──► API Management ──► App Service ──► Azure SQL
  MSAL         subscription key    Easy Auth       Entra-only auth
  PKCE         validate-jwt        allowedApps     contained user
```

Four hops, three trust relationships, and no password anywhere in the running system. API
Management reaches the App Service as a managed identity; the App Service reaches Azure SQL as
one; the SQL server has Entra-only authentication, so a password does not merely go unused — one
cannot be created. There is no Key Vault, because there is nothing to store.

| Endpoint | Auth |
|---|---|
| `GET /api/v1/menu` | APIM subscription key |
| `POST /api/v1/coupons/validate` | APIM subscription key |
| `POST /api/v1/orders` | subscription key **+** an Entra access token with `Orders.Write` |

**What the domain does**

- **Two coupon types** — percentage and fixed amount.
- **Three conditions** — expiry, minimum order value, total redemption limit.
- **The server decides the price.** No endpoint accepts a price, subtotal or total from the
  client. The browser sends `pizzaId` and `quantity`; the server resolves every price itself. The
  code enforces this — you cannot build a priced basket from outside the `Ordering` assembly.
- **Preview never mutates.** `POST /coupons/validate` consumes no redemption. Only `POST /orders`
  redeems, as a single atomic `UPDATE … WHERE UsageCount < RedemptionLimit`.
- **A rejection carries a reason**, never a bare boolean. One enum drives the customer message,
  the log entry and the test assertion.

```
src/PizzaShop.Api/              HTTP endpoints, composition root
src/PizzaShop.Ordering/         basket, pricing, orders    -> depends on ICouponEvaluator only
src/PizzaShop.Coupons/          coupon rules               -> knows nothing about pizzas
src/PizzaShop.Infrastructure/   EF Core, persistence, adapters
tests/PizzaShop.Bdd/            Reqnroll scenarios
infra/                          Bicep, APIM policies, OpenAPI, deployment scripts
web/                            React + MSAL frontend
```

---

## How the claim is verified

Every pipeline run ends with a smoke test that calls the deployed system **through the gateway**,
never around it. Six assertions, and each one asserts *which* component rejected the call rather
than only the status code — a 401 from the subscription-key check and a 401 from `validate-jwt`
are indistinguishable by status alone.

| | Assertion |
|---|---|
| 1 | `GET /menu` with no key → 401, body naming the missing key |
| 2 | `GET /menu` with a key → 200, body contains a pizza read from Azure SQL |
| 3 | `POST /orders` with a key, no token → 401 carrying the `validate-jwt` message |
| 4 | `POST /orders` with a key and a malformed token → 401 |
| 5 | The static site serves the built app, not just *a* 200 |
| 6 | The dev-only token claims panel is absent from the deployed bundle |

**If you run it yourself, assertion 2 will look stuck.** On a cold deployment it took 25 attempts
over **165 seconds** — the App Service's first boot runs EF Core migrations and seeds the database
before it serves anything. The smoke test polls for exactly this reason; a single call would
return 404 and send you to the API Management routing configuration, where the problem is not. On
a warm deployment the same assertion passes on attempt 1 in about 10 seconds.

Alongside it, **15 Reqnroll BDD scenarios** run before any Azure resource is created, so nothing
is ever provisioned for code that does not compile.

Timings, per-job durations, the full smoke output, and three assumptions that this run turned into
observations: **[docs/verification-run.md](docs/verification-run.md)**.

---

## Run it yourself

**Locally** — no Docker, no Azure account:

```bash
dotnet test                                  # 15 Reqnroll BDD scenarios
dotnet run --project src/PizzaShop.Api       # API on LocalDB

cd web
cp .env.example .env.local                   # fill in the five values
npm install && npm run dev                   # http://localhost:5173
```

`http://localhost:5173` is a registered redirect URI and an allowed CORS origin, so a local
frontend talks to the deployed gateway unchanged. `npm run dev` also enables a token claims panel
that decodes the live access token; every production build strips it, and the pipeline fails if it
ever reaches `dist/`.

**Deploying your own** — everything below the Day 0 line is automated. Deleting the resource group
and re-running the pipeline produces a working system.

**Day 0 — by hand, once. Five items.** A pipeline cannot create the credential it logs in with,
and it cannot create itself.

1. A GitHub repository with `.github/workflows/deploy.yml` on `main`.
2. An Entra app registration with a **federated credential** for GitHub OIDC, and **Contributor at
   subscription scope**. No client secret exists.
3. Repository **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` — on the
   Variables tab, not Secrets. All three are public identifiers.
4. Two Entra app registrations, `coupon-api` and `coupon-spa`. The SPA's redirect URIs go on the
   **SPA** platform — not `publicClient`, not `web`. This one completes in **two sittings**: the
   deployed frontend origin cannot be registered until the storage account exists, so the first
   pipeline run comes before it.
5. The `env:` block at the top of the workflow, set for your subscription and tenant.

Items 1 and 5 are dull and easy to leave out of a list like this, which is why they are in it.
Contributor is sufficient and deliberately not more: the deployment contains no Azure RBAC role
assignment at all, so the power to create one is never needed.
[docs/deployment.md](docs/deployment.md) §2 has the detail, including the two `az` commands for
the redirect URIs that look right and are not, and the Azure DevOps equivalents of items 1–3.

**Day 1 — the pipeline.** Push to `main`, or dispatch the workflow manually.

```
1  Build and test          dotnet build, BDD suite, publish artifacts
2  Provision               Bicep: resource group, APIM, App Service, SQL, storage, telemetry
3  Grant database access   create the contained user for the App Service's managed identity
4  Deploy backend          push the API; EF migrations run at startup
5  Build and deploy UI     npm build with the gateway URL injected; upload to $web
6  Smoke test              six assertions through the gateway and against the live site
```

**Teardown:** `az group delete --name rg-coupon-service-gh --yes`. Then re-run the pipeline.
API Management stays name-reserved for 48 hours after deletion; the pipeline purges it on the next
run, which is the one step the "delete it and re-run" claim actually rests on.

---

## No passwords

**The running system holds no credential.** Not in configuration, not in an environment variable,
not in a Key Vault — there is no Key Vault. Service-to-service authentication is managed identity
throughout, and the SQL server accepts nothing else.

The **pipeline** uses two credentials at deploy time, and this is the honest boundary: an API
Management subscription key and a storage account key, both fetched from ARM at the moment they
are needed, used inside a single step, and never written to a file or published as a variable. The
precise claim — and why the storage account key is a *smaller* grant than the role assignment that
would have replaced it — is in [docs/authentication.md](docs/authentication.md) §9.

Authentication to Azure itself is workload identity federation over OIDC in both pipelines. There
is no client secret to store or rotate.

---

## Documentation

| | |
|---|---|
| [docs/verification-run.md](docs/verification-run.md) | The cold-start run in full: timings, smoke output, what it settled |
| [docs/architecture.md](docs/architecture.md) | The shape, the coupon/ordering split, why one deployable, data model, testing |
| [docs/deployment.md](docs/deployment.md) | Day 0 and Day 1, the six stages, what has already cost a deploy cycle |
| [docs/authentication.md](docs/authentication.md) | The two credentials, PKCE, gateway-to-backend, passwordless SQL, what is proven and what is not |
| [docs/assumptions.md](docs/assumptions.md) | Assumptions, known limitations, what would come next |

Also here: [docs/approach.md](docs/approach.md), the design document written before the build, and
[docs/decisions.md](docs/decisions.md), a running log of every non-obvious choice made while
building — including the ones that turned out to be wrong.
