# Coupon Service — Execution Plan

**Project:** Pizza coupon service · technical assignment for Bangladesh Software Solution
**Source of truth:** `docs/approach.md` (the approved approach document)

---

## 0. The honest framing, before any tooling

Read this first, because it shapes everything below.

**The risk in this project is not writing code.** The domain is small: two coupon types, three
conditions, three endpoints, a React form. You have written harder things before lunch.

The risk is **Azure deployment plumbing**, and no AI agent removes it. From the APIM lab last
week: four hours on gateway basics, an hour lost to a Function App deploy that couldn't be fixed
from the client side, an hour on Entra and JWT audience mismatches. Every one of those was a
platform problem, not a coding problem.

So the tooling strategy has one job: **make the code fast so you have time left for the
plumbing.** Do not spend half a day perfecting agent configuration. Ninety minutes of setup,
then build.

**Corollary:** the most valuable MCP server here is not the one that writes code. It is the
Azure DevOps one, because it lets you diagnose failed pipeline runs without leaving the terminal.

---

## 1. Phase 0 — De-risking spikes (do these first, timeboxed)

Before writing a line of the real project, prove the three things that can block you. Each is a
throwaway. Each has a decided fallback.

| # | Spike | Timebox | If it fails |
|---|---|---|---|
| 1 | **Passwordless SQL via SID.** Create a SQL server, an App Service with a managed identity, and run `CREATE USER ... WITH SID` from a script. Connect and read a row. | 90 min | Revert to SQL auth + Key Vault. One paragraph changes in §6 of the approach doc. |
| 2 | **Azure DevOps → Azure deployment.** A service connection and a trivial pipeline that deploys one Bicep resource group. Nothing else. | 60 min | Nothing to fall back to — this one *must* work. If it doesn't, escalate to Asheq immediately. |
| 3 | **PKCE token in a browser.** A bare React page with MSAL, signing in, printing the token. Decode it and check `aud`, `iss`, `scp`. | 60 min | Fall back to a device-code flow or document a simplified auth story. Decide before day 3. |

**Why this order matters.** Spike 1 is the one you have never done. Spike 2 is the one with no
alternative. Spike 3 is the one that silently eats an evening if the audience is wrong.

Write down what you learn in `docs/decisions.md` as you go. Those notes become the assumptions
section of the final documentation.

---

## 2. Build sequence — vertical slice first

**The single most important sequencing rule: get one endpoint working end to end through the
deployed gateway before building the other two.**

`GET /menu` is the right first slice. No auth beyond a subscription key, no coupon logic, no
database writes. Once a request travels browser → APIM → App Service → SQL → back, everything
after it is filling in.

The failure mode to avoid is building all three endpoints locally, then discovering on day 3
that the gateway, the token audience, and CORS all need work at once.

| Phase | Work | Depends on |
|---|---|---|
| **A** | Domain model, `ICouponEvaluator`, pricing rules, BDD scenarios. No Azure, no database. | — |
| **B** | EF Core, migrations, `GET /menu` endpoint, structured logging, health checks. Local only. | A |
| **C** | Bicep: resource group, App Service, SQL, APIM, storage, App Insights. Pipeline stages 1–4, with the smoke test polling rather than calling once — see below. **Deploy `/menu` through the gateway.** | B, spikes 1–2 |
| **D** | Coupon validate and order endpoints. `validate-jwt` on orders. Entra wiring. | C, spike 3 |
| **E** | React frontend, MSAL, CORS policy, deploy to storage. | D |
| **F** | Smoke tests, README, documentation, test account, hardening. | E |

**Phase C is the one that overruns.** Budget generously and start it earlier than feels
comfortable.

### The smoke test polls; it does not call once

The deploy stage returns `RuntimeSuccessful` while the container is still starting.
Measured on spike 1: an App Service cold start after a zip deploy took **~33 seconds**,
and a request made before the startup probe passed got the stock welcome page and a
`404` from a perfectly healthy app.

So the smoke-test stage retries against an overall timeout — poll every few seconds up
to roughly 120s, fail only when the budget is exhausted:

```bash
deadline=$((SECONDS + 120))
until curl -fsS "$GATEWAY/api/v1/menu" -H "Ocp-Apim-Subscription-Key: $KEY" | grep -q Margherita; do
  if [ $SECONDS -ge $deadline ]; then echo "smoke test timed out after 120s"; exit 1; fi
  sleep 5
done
```

A single call here fails intermittently, and it fails in the way most likely to be
misread — a `404` reads as a routing or policy fault, sending you to the APIM
configuration when the only problem was timing. The same retry applies to each of the
three policy assertions in §7 of the approach document.

---

## 3. Tool division of labour

Not "Copilot writes, Claude reviews." Divide by **what each is actually better at.**

### GitHub Copilot CLI — the implementation loop

`npm install -g @github/copilot`

Use it for: domain model, EF Core entities and configuration, endpoint handlers, React
components, test scaffolding, and the tight edit-run-test cycle.

Why: it is fast, terminal-native, and its Task agent runs builds and tests with a short summary
on success and full output on failure — which is exactly the loop you want on Phase A and B.

Use `/plan` before anything multi-file. Use `/fleet` when you want the same task attempted
several ways and one result to pick from.

### Claude Code — infrastructure, review, and Azure

Use it for:

- **Bicep and the pipeline YAML.** High stakes, multi-file, and a mistake costs a five-minute deploy cycle to discover. Worth the more deliberate tool.
- **Reviewing what Copilot produced** against the rules in `AGENTS.md`, particularly the security invariants.
- **Anything touching Azure**, via the Azure plugin.
- **Diagnosing failed pipeline runs**, via the Azure DevOps MCP server.

### You, in chat — decisions and debugging strategy

Neither agent should decide architecture. When something is genuinely stuck, the useful move is
to reason about *which layer* is failing before asking either tool to fix it. That is what the
401-vs-403 table in the approach document is for.

---

## 4. MCP servers and plugins to install

**Priority order.** Install the first two before you start. The rest only if you have time.

### 1. Azure plugin for Claude Code (highest value)

Packages the Azure MCP Server plus Azure agents and skills in one install, from Anthropic's
plugin marketplace. Covers 40+ Azure services — resource queries, deployments, logs,
diagnostics.

Authenticate with `az login` first; the MCP server uses your existing CLI credentials.

### 2. Azure DevOps MCP Server (highest value for this project specifically)

Runs locally. Gives access to work items, pull requests, **builds and pipeline runs**, test
plans, and wikis.

The reason this matters: your repo must live in Azure DevOps, and your pipeline will fail
repeatedly during Phase C. Being able to ask "show me the last failed build and the log for the
step that broke" from the terminal, instead of clicking through the ADO web UI, is worth an hour
across the project.

### 3. GitHub MCP Server

Included with Copilot CLI by default. Nothing to do.

### 4. Optional, only if time allows

- A Playwright or browser MCP for verifying the deployed frontend
- A filesystem MCP if you want agents reading outside the repo

**Do not install more than four.** Every MCP server adds tool descriptions to the context window
and increases the chance an agent picks the wrong tool.

---

## 5. Repository layout

```
coupon-service/
├── AGENTS.md                        ← single source of truth for both agents
├── CLAUDE.md                        ← thin pointer to AGENTS.md
├── README.md                        ← what a reviewer reads first
├── .github/
│   ├── copilot-instructions.md      ← thin pointer to AGENTS.md
│   └── agents/
│       └── bicep-reviewer.agent.md  ← optional custom agent
├── azure-pipelines.yml
├── docs/
│   ├── approach.md                  ← the approved approach document
│   ├── architecture.md              ← deliverable
│   ├── deployment.md                ← deliverable
│   ├── authentication.md            ← deliverable
│   ├── assumptions.md               ← deliverable
│   └── decisions.md                 ← running log, written as you go
├── infra/
│   ├── main.bicep
│   ├── modules/
│   └── scripts/
│       └── grant-db-access.ps1      ← the SID script from spike 1
├── src/
│   ├── PizzaShop.Api/
│   ├── PizzaShop.Ordering/
│   ├── PizzaShop.Coupons/
│   └── PizzaShop.Infrastructure/
├── tests/
│   └── PizzaShop.Bdd/
└── web/                             ← React frontend
```

**`docs/approach.md` goes in on the first commit.** It is the context both agents need, and it
is also evidence of how the project was planned.

The four deliverable documents are extracted from it at the end, not written from scratch.

---

## 6. Definition of done

Mapped to the nine requirements in the brief. Do not consider the project finished until every
row is checked.

| # | Requirement | Done when |
|---|---|---|
| 1 | Azure DevOps repository | Code pushed, sensible commit history, no secrets committed |
| 2 | Pipeline deploys everything from scratch | Deleted the resource group, ran the pipeline, got a working system |
| 3 | No manual deployment steps | Day 0 prerequisites documented; nothing else touched by hand |
| 4 | Clean interface for pricing and coupon validation | `ICouponEvaluator` with a rich result type; no coupon logic in the ordering project |
| 5 | APIs secured with proper authentication | Subscription key on all; `validate-jwt` on orders, audience and issuer pinned |
| 6 | Exposed and managed through APIM | API imported from OpenAPI, policies in version control, not clicked in the portal |
| 7 | BDD test project | Reqnroll, scenarios in business language, pricing logic never mocked |
| 8 | Structured logging | Queryable properties, correlation ID from gateway to log line |
| 9 | React frontend | Select pizzas, enter coupon, submit, see total |

**Plus, from the brief's deliverables list:** four documentation topics, and a running system a
reviewer can open.

---

## 7. Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Passwordless SQL blocked | Medium | Spike 1 on day 0. Fallback decided in advance. |
| Pipeline can't reach Azure | Low | Spike 2 on day 0. No fallback — escalate immediately. |
| Token audience mismatch | High | Spike 3. Decode every token before writing policy. |
| CORS between storage site and gateway | Medium | Handled by APIM policy. Test in Phase E, not on the last day. |
| Free credit expires mid-project | **Check this** | Confirm the expiry date now. If it lands inside the project window, decide about pay-as-you-go before you start. |
| Reviewer can't sign in | High if unhandled | Pre-created test account, MFA cleared by signing in once yourself, credentials in the README. |
| APIM provisioning slow | Low | Consumption tier, ~3 min. Never use a classic tier here. |
| Scope creep into coupon features | Medium | Two types, three conditions. Locked. Anything else goes in "with more time." |

---

## 8. Cost guardrails

Rough monthly run rate: App Service B1 ~$13, Azure SQL Basic ~$5, APIM Consumption ~free at this
volume, storage and App Insights pennies. Call it **$20/month**, so a few dollars for the
project.

- Set a budget alert at $10 before you start
- One resource group, so `az group delete` removes everything
- Delete the spike resources when the spikes are done
- **Do not** leave a Developer-tier APIM running by accident — that alone is $48/month

---

## 9. Working rules

**Commit after every green test run.** You will want to bisect when the pipeline starts failing.

**Never let an agent write a secret into a file.** The `AGENTS.md` rules cover this, but check
diffs before committing.

**Write `docs/decisions.md` as you go**, one line per non-obvious choice. Retrofitting it at the
end produces a worse document and takes longer.

**When stuck for 15 minutes, change layer.** If the pipeline is failing, stop reading YAML and
go look at what the deployed resource actually is. Most of last week's lost hours were spent
debugging the wrong layer.

**The reviewer's first thirty seconds decide a lot.** The README should open with: what this is,
the live URL, the test account, and how to deploy it. Not with a project structure diagram.

---

## 10. Suggested schedule

Assumes evenings. Compress if you have full days.

| Day | Work | Ends with |
|---|---|---|
| 0 | Three spikes. Repo scaffold, agent configs, MCP installs. | Three answers, and a decision on each fallback |
| 1 | Phase A — domain, coupon evaluation, BDD scenarios | Green test suite, no Azure involved |
| 2 | Phase B — EF Core, `/menu`, logging, health checks | Running locally end to end |
| 3 | Phase C — Bicep, pipeline, **first deployed slice** | `/menu` reachable through the deployed gateway |
| 4 | Phase D — coupon and order endpoints, JWT policy | All three endpoints working through APIM |
| 5 | Phase E — React, MSAL, CORS | A reviewer can place an order in a browser |
| 6 | Phase F — smoke tests, docs, README, test account | Submittable |
| 7 | Buffer | — |

**Day 3 is the milestone that matters.** If the first deployed slice is not working by the end
of day 3, cut scope somewhere else rather than compressing days 5 and 6 — an undocumented,
unpolished submission undersells everything before it.
