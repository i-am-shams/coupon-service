# AGENTS.md

Instructions for AI coding agents working in this repository. This file is the single source of
truth. `CLAUDE.md` and `.github/copilot-instructions.md` point here — do not duplicate rules
into them.

---

## Current objective: portfolio publication and GitHub Actions port

This repository is being copied to a **public GitHub repository** for portfolio use,
and its Azure DevOps pipeline is being ported to GitHub Actions. Read this section
before making any change.

### The claim this project exists to support

The pipeline builds the entire system from an **empty Azure resource group in roughly
17 minutes** — resource group, every Bicep-defined resource, database schema, database
access grant, backend, frontend, and a smoke test that verifies the security policies
actually fire. This was validated by deleting everything and re-running, not assumed.

This claim is the point of the repository. Every other decision is subordinate to it.

### Invariants — do not break these

1. **No stored secrets.** The pipeline authenticates to Azure via workload identity
   federation. The GitHub Actions port must use OIDC federated credentials
   (`azure/login@v2`, `permissions: id-token: write`). A stored client secret is a
   regression, not a shortcut. If a task appears to require one, stop and report.
2. **No manual steps.** Every stage must run unattended from a clean trigger. If a
   step cannot be automated, stop and report rather than documenting a workaround.
3. **Full reproducibility from empty.** The workflow must still provision the resource
   group itself. Do not assume pre-existing infrastructure.
4. **The smoke test must still verify security policy enforcement.** A smoke test
   reduced to a liveness check does not support the claim.

If a proposed change would weaken any of these four, say so before making it.

### Hard prohibitions

- **Never push to, force-push to, or rewrite the `dev.azure.com` remote.** That
  repository is a contractual deliverable for the company that set this assignment.
  It is frozen. This work is a copy, not a move, and the copy flows one way.
- **Never push to a public GitHub remote before the credential scrub is complete and
  verified.** GitHub keeps force-push-orphaned commits reachable by SHA on public
  repositories and across the fork network, effectively permanently. A rewrite that
  follows a bad push does not undo it. Rewrite first, push once.
- **Never set up mirroring or sync between the two remotes.** A sync job is how
  redacted history becomes un-redacted later.
- **Never commit a credential, connection string, key, or token**, including in
  example config, test fixtures, docs, or this file.

### Known state

- A live reviewer test account (username and password, plain text) is committed in the
  README and present in git history. Treat any encounter with it as a finding to
  report, not a value to reuse.
- The Azure subscription is a free trial expiring around **22 September 2026**. Live
  demo URLs die then. Evidence for the claim must not depend on a live link.
- Branch is `master`, 37 commits. It becomes `main` on GitHub.

### Definition of done

- Public GitHub repo, history preserved, no credential in any commit
- GitHub Actions workflow reaching green from an empty resource group, with no stored
  secret and no manual step
- `azure-pipelines.yml` retained in the GitHub copy as the original, unmodified
- Azure DevOps repository byte-identical to its current state
- README addressed to a stranger, not to one evaluator
- `docs/verification-run.md` containing durable evidence of a full cold-start run,
  since Actions logs expire at 90 days

---

## What this project is

A pizza ordering service with coupon support, built as a technical assignment. A .NET 8 backend
behind Azure API Management, a React frontend, deployed to Azure entirely from a pipeline using
Bicep — no manual step at any point.

There are two pipelines, and they are equivalent: `azure-pipelines.yml` for Azure DevOps and
`.github/workflows/deploy.yml` for GitHub Actions. Same six stages in the same order, same Bicep
templates, same scripts under `infra/scripts/`. They deploy into separate resource groups so
neither can take the SQL Entra administrator away from the other. A change to the deployment is
not finished until both carry it.

**Read `docs/approach.md` before making design decisions.** It contains the agreed architecture
and the reasoning behind every choice. If a change would contradict it, stop and ask rather than
proceeding.

---

## Non-negotiable rules

These are correctness and security invariants. Violating one is a bug, not a style preference.

### 1. The server decides the price

**No endpoint accepts a price, subtotal, or total from the client.** Requests carry basket
contents — `pizzaId` and `quantity`. The server resolves prices from its own data on every
request.

If you find yourself adding a `price` or `total` field to a request DTO, stop.

### 2. Preview never mutates

`POST /coupons/validate` is a read-only preview. It must not consume a redemption, write to the
database, or have any side effect. Only `POST /orders` records a redemption.

### 3. Coupon rejections carry a reason, never a bare boolean

`CouponEvaluation` returns validity, amount, description, and a `CouponRejectionReason` enum.
The enum drives the customer-facing message, the log entry, and the test assertion from one
place.

### 4. Redemption is a single atomic update

```sql
UPDATE Coupons SET UsageCount = UsageCount + 1
WHERE Id = @Id AND UsageCount < RedemptionLimit
```

Never read-then-write. Zero rows affected means exhausted; the order is priced without the
coupon.

### 5. Time is injected, never read from the clock

`ICouponEvaluator.Evaluate` takes `DateTimeOffset asOf`. Do not call `DateTime.UtcNow` inside
coupon rules — it makes expiry untestable.

### 6. A discount never exceeds the subtotal

The total floors at zero. There is a test for this. Do not remove it.

### 7. No secrets in code, config, or logs

There are no passwords in this solution by design. Service-to-service authentication uses
managed identity. Never write a connection string, key, token, or password into a file, an
`appsettings.json`, or a log line. If you think you need one, stop and ask.

### 8. Never mock the code under test

BDD scenarios exercise real pricing and coupon logic. The database may be swapped for an
in-memory provider. The discount calculation may not be mocked — a test that mocks the thing it
is testing proves nothing.

### 9. Structured logging only

```csharp
// correct
_logger.LogInformation("Coupon {CouponCode} rejected: {Reason}", code, reason);

// wrong — produces text you can only search, not fields you can query
_logger.LogInformation($"Coupon {code} rejected: {reason}");
```

Never use string interpolation in a log call.

### 10. APIM policy placeholders never use `{{ }}`

Double braces are APIM's own named-value syntax. A policy containing `{{TOKEN}}` will be
rejected at save time because APIM tries to resolve it as a named value. Use `__TOKEN__` for
deploy-time substitution.

---

## Project structure

```
src/PizzaShop.Api/              HTTP endpoints, composition root
src/PizzaShop.Ordering/         basket, pricing, orders
src/PizzaShop.Coupons/          coupon rules and evaluation
src/PizzaShop.Infrastructure/   EF Core, persistence
tests/PizzaShop.Bdd/            Reqnroll scenarios
infra/                          Bicep and deployment scripts
web/                            React frontend
docs/                           approach, decisions, deliverable docs
```

**Dependency rule:** `Ordering` depends on `ICouponEvaluator` only. It must not reference the
`Coupons` project's internals. `Coupons` must not know about pizza prices, delivery, or orders.

The boundary is real because the coupon project may be extracted into its own deployable later
without changing the ordering code. Keep it that way.

---

## Commands

```bash
# build and test
dotnet build
dotnet test

# run locally
dotnet run --project src/PizzaShop.Api

# migrations
dotnet ef migrations add <Name> --project src/PizzaShop.Infrastructure --startup-project src/PizzaShop.Api

# frontend
cd web && npm install && npm run dev && npm run build

# infrastructure
az bicep build --file infra/main.bicep

# Parameters are passed inline, not from a file. There is no infra/params.json - the
# pipeline builds this argument list in the Provision stage, and two of the values
# (deployingPrincipalObjectId, sqlAdminLogin) are only knowable at deploy time.
az deployment group validate -g <rg> -f infra/main.bicep --parameters     apiClientId=<coupon-api client id>     deployingPrincipalObjectId=<oid of the principal running this>     sqlAdminLogin=<display name for the SQL Entra admin>     apimPublisherEmail=<address>
```

---

## Conventions

**C#** — .NET 8, nullable enabled, records for DTOs and value objects, `sealed` by default.
File-scoped namespaces. Async all the way; no `.Result` or `.Wait()`.

**Testing** — Reqnroll for BDD (**not SpecFlow** — it reached end of life in December 2024 and
never supported .NET 8). xUnit underneath. Scenarios in business language; the mapping to
rejection reason enums lives in step definitions, so message wording changes do not break tests.

**API** — `POST /coupons/validate` returns 200 even when a coupon is rejected; the endpoint
succeeded at answering the question. `POST /orders` returns 201 even when the coupon failed,
with `couponApplied: false` and a reason. 4xx and 5xx are for malformed requests, auth failures,
and real faults, using RFC 7807 `ProblemDetails` with a `traceId`.

**React** — TypeScript, functional components, MSAL for auth. The gateway URL and client ID are
injected at build time from pipeline variables; never hardcoded.

**Bicep** — parameters for anything environment-specific. `loadTextContent` for policy XML and
OpenAPI so they stay reviewable as real files. No `dependsOn` unless the implicit graph genuinely
misses something.

---

## Before you act

**Ask rather than assume when:**

- A change would contradict `docs/approach.md`
- You would add a field carrying money to a request DTO
- You would introduce a new Azure resource or an NuGet package
- You would change the auth model
- The task requires a credential

**Log your reasoning.** When you make a non-obvious choice, add a line to `docs/decisions.md`
with what you chose and why. That file becomes part of the final documentation.

---

## What "done" means for a task

- It compiles
- Tests pass, and there is a test for the behaviour you added
- No secret appears anywhere in the diff
- No rule above is violated
- If it changed a decision, `docs/decisions.md` says so
