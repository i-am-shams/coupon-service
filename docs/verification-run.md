# Verification run — GitHub Actions, from an empty resource group

Durable evidence for the claim this repository exists to support: **the pipeline builds the
entire system from nothing, unattended, with no stored secret.** GitHub retains Actions logs
for 90 days, so the numbers and outputs are recorded here rather than linked to.

Everything below is measured from two runs of this workflow: a cold start from an empty resource
group (§2), and an incremental run against the group it created (§3a). Nothing is carried over
from the Azure DevOps pipeline's own cold-start run, which used a different agent pool and
produced a different number; it appears only in the labelled comparison in §7.

Both recorded runs are on commit `8b5aa42` / `5042e5c`, before the `vite` 6 upgrade that
followed. The bundle sizes quoted are that build's.

---

## 1. What was run

| | |
|---|---|
| Repository | `i-am-shams/coupon-service` |
| Workflow | `.github/workflows/deploy.yml` |
| Run | [34342033409](https://github.com/i-am-shams/coupon-service/actions/runs/34342033409) |
| Commit | `8b5aa428785215cecf345caae4eaf15b5362fca6` |
| Trigger | `workflow_dispatch` on `main` |
| Date | 2026-09-09 |
| Resource group | `rg-coupon-service-gh`, `centralindia` |
| Runner | `ubuntu-latest`, GitHub-hosted |

**The resource group did not exist when the run started.** The preceding run
(`34333162547`) failed at `Azure login` in job 2, which is the step *before*
`Deploy infra/main.bicep`, so `az group create` never executed. This run created the group and
everything in it.

Authentication was workload identity federation over OIDC throughout. No client secret exists
for this pipeline, and none of the six jobs holds one.

---

## 2. Result

**Green. Six jobs, 17m45s wall clock**, 10:46:39Z to 11:04:24Z.

| Job | Start (UTC) | End (UTC) | Duration |
|---|---|---|---|
| 1 · Build and test | 10:46:44 | 10:47:30 | **46s** |
| 2 · Provision | 10:47:32 | 10:55:31 | **7m59s** |
| 3 · Grant database access | 10:55:34 | 10:56:09 | **35s** |
| 4 · Deploy backend | 10:56:11 | 11:00:34 | **4m23s** |
| 5 · Build and deploy frontend | 11:00:37 | 11:01:07 | **30s** |
| 6 · Smoke test | 11:01:10 | 11:04:23 | **3m13s** |

Job time totals 17m26s; the remaining 19 seconds is runner scheduling between jobs.

Inside job 2, from the log:

```
10:47:45  ==> Resolving the API Management name for this resource group
10:48:23      apim-couponsvc-lab-vxziaw
10:48:26      APIM soft-delete: none found
10:48:29      stcouponsvclabgh is free — this deployment will create it
10:48:31      Log Analytics soft-delete in rg-coupon-service-gh: none found
10:48:32      object ID: b8503512-47ec-4a52-b50c-931e086d0ca8
10:55:29      outputs read
```

ARM reports the Bicep deployment itself at **PT6M32.5S**. The 38 seconds before it is the
resource-less template that resolves the APIM name, and the three pre-flight checks.

### What was deployed

```
Gateway  : https://apim-couponsvc-lab-vxziaw.azure-api.net
Backend  : app-couponsvc-lab-vxziaw
Database : sql-couponsvc-lab-vxziaw.database.windows.net/sqldb-couponsvc-lab
Frontend : https://stcouponsvclabgh.z29.web.core.windows.net
```

The Azure subscription is a free trial expiring around 22 September 2026. **Those URLs will
stop resolving.** That is why this document records outputs rather than pointing at them.

---

## 3. The six smoke assertions

Run against the deployed system through the gateway, never around it. Verbatim:

```
  PASS  gateway rejects a call with no subscription key (401) (attempt 1, 2s)
        body mentions the subscription key, so this is APIM's key check and not
        something else returning 401

  PASS  backend serves the menu containing 'Margherita' (200) (attempt 25, 165s)
        8 items returned, read from Azure SQL through the managed identity

  PASS  gateway rejects an order with no access token (401) (attempt 1, 1s)
        body carries the validate-jwt message, so this is the token policy and not
        the key check

  PASS  gateway rejects an order with a malformed access token (401)

  PASS  static website serves the application shell (200) (attempt 1, 1s)
        title and hashed bundle reference both present, so this is the built app

  PASS  deployed bundle carries no token claims panel (410 kB checked)

Smoke test: 6 assertions passed.
```

**Assertion 2 took 25 attempts over 165 seconds.** That is the App Service cold start — EF Core
migrations and the seed running at first boot against a Basic database — and it is the exact
failure the polling exists to absorb. A single call would have returned 404 and sent someone to
the APIM routing configuration. The Azure DevOps cold-start run measured 5 attempts over 45s for
the same assertion, so this is variance in first-boot time, not a difference between the
pipelines.

**What these six do and do not prove.** Five of the six are negatives: they prove the gateway
*rejects* — no key, no token, a malformed token. The one positive hop, assertion 2, authenticates
as the gateway's managed identity, not as a user. **Nothing here exercises interactive sign-in.**
No assertion needs a user credential, which is why the reviewer test account is not a
prerequisite for this pipeline; it is also why a green run is not evidence that `validate-jwt`
*accepts* a real token.

### The sign-in path, closed by hand — recorded 2026-09-10

The gap above is real and stays described. It now has a hand-run result beside it.

Against the deployment from this run, the deployed origin
`https://stcouponsvclabgh.z29.web.core.windows.net` was added to `coupon-spa`'s redirect URIs on
the **SPA** platform — the second sitting of Day 0 item 4, which cannot happen before a first
provision because the origin is not knowable until the storage account exists. Then, in a browser:
sign in through the MSAL redirect flow, add pizzas, apply a coupon, submit.

| | |
|---|---|
| Result | **Order #1**, coupon applied |
| Sign-in | No perceptible delay — about a second, by observation rather than measurement |

**Order #1 is the part worth recording.** No smoke assertion places an order, so the `Orders`
table was untouched by the pipeline. An identity counter starting at 1 means the database was
created and seeded from empty by this deployment, with nothing carried over — the same proof the
Azure DevOps rebuild used in `decisions.md`, phase G. It also exercises the one path the six
assertions cannot: a real user token minted by Entra, accepted by `validate-jwt` at the gateway,
with the redemption written through the App Service's managed identity.

**This does not close the automation gap, and is not recorded as if it did.** It is one manual
observation, on one date, by one person. Nothing in the pipeline will catch a regression on this
path; a green run still proves only that `validate-jwt` *rejects*. Automating it would mean a
browser-driver run holding a real user credential — `assumptions.md` §8 explains why that trade
was declined. What changed is that the sign-in path has now been shown to work at least once, and
when it was checked is written down.

### Unit and BDD scenarios

```
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 1 s - PizzaShop.Bdd.dll (net8.0)
```

All 15 Reqnroll scenarios, rendered as a check run by `dorny/test-reporter` and retained as a
`.trx` artifact.

---

## 3a. The second run — incremental, and why it matters

One green run shows the pipeline can work. It does not show it works twice. Run
[34352183376](https://github.com/i-am-shams/coupon-service/actions/runs/34352183376), commit
`5042e5c`, triggered by a push to `main` roughly 90 minutes later, ran the same six jobs against
the resource group the cold-start run had already created.

**Green. 8m12s**, 12:38:19Z to 12:46:31Z.

| Job | Cold (from empty) | Incremental |
|---|---|---|
| 1 · Build and test | 46s | 45s |
| 2 · Provision | 7m59s | **3m51s** |
| 3 · Grant database access | 35s | 42s |
| 4 · Deploy backend | 4m23s | **51s** |
| 5 · Build and deploy frontend | 30s | 42s |
| 6 · Smoke test | 3m13s | **28s** |
| **Total** | **17m45s** | **8m12s** |

Six assertions passed again. The three jobs that collapsed all collapsed for one reason, and the
smoke log isolates it to a single line:

```
cold         PASS  backend serves the menu containing 'Margherita' (200) (attempt 25, 165s)
incremental  PASS  backend serves the menu containing 'Margherita' (200) (attempt 1,   10s)
```

Same commit's code, same gateway, same assertion, same expected string. The only difference is
that the App Service had already completed its first boot — EF Core migrations and seeding — so
there was nothing to wait for. **That is the evidence that the cold run's 165 seconds was
latency and not a defect**, which a single run could not have established. Job 4 falls for the
same reason: `azure/webapps-deploy` returns once the platform accepts the package, and the wait
that follows belongs to the container, not the deploy.

Job 2's 7m59s → 3m51s is ARM finding every resource already present and converging rather than
creating. Job 3 re-running is a non-event by design: `grant-db-access.sql` is idempotent, so it
succeeded against a contained user that already existed instead of failing on a duplicate.

The redirect-URI guard matched again, and the claims-panel check found the deployed bundle clean
at 410 kB. Neither is a repeat of a cached result — both are re-read from the live deployment on
every run.

---

## 4. Three things this run settled that were previously assumptions

### The `zNN` DNS zone segment was predicted correctly

`main.bicep`'s `storageAccountName` is pinned here rather than derived, because the derived name
is a function of the resource group name and this pipeline uses a different group from the Azure
DevOps one. Pinning the account name does not pin the URL: the endpoint is
`https://<account>.zNN.web.core.windows.net`, and `zNN` names the DNS zone of the storage stamp
the account lands on, assigned at creation and not derivable from the name.

`expectedFrontendOrigin` was set to `z29` by reading the zone off the Azure DevOps deployment's
account, in the same region, and predicting that a new account would land in the same zone. The
guard confirms the prediction held:

```
==> Redirect URI guard
    deployed  : https://stcouponsvclabgh.z29.web.core.windows.net
    registered: https://stcouponsvclabgh.z29.web.core.windows.net
    match
```

**This is one observation, not a rule.** A region has many stamps. The value of the guard is that
a wrong prediction fails job 5 with both strings side by side, before anything is uploaded,
rather than surfacing in a browser as `AADSTS50011` after a green run.

### A GitHub-hosted runner satisfies the SQL server's `0.0.0.0` firewall rule

`sql.bicep` carries one firewall rule, `0.0.0.0-0.0.0.0` — the "allow Azure services" form. Its
comment claimed coverage of "the Microsoft-hosted pipeline agent". Whether a GitHub-hosted runner
also qualified was untested: the rule is an ARM concept, not an IP range, so "GitHub runners are
Azure VMs" is not by itself an argument that it applies to them.

**It does.** Job 3 created the contained user in 35 seconds with no firewall change. Had it not,
the failure would have been unmistakable — `Cannot open server … Client with IP address 'a.b.c.d'
is not allowed to access the server`, the only error in this pipeline that names a client IP.

### The exact-name APIM purge filter matched only this deployment

The Azure DevOps pipeline matches soft-deleted services with
`starts_with(name, 'apim-couponsvc-lab-')` over a subscription-wide list. This workflow resolves
the exact name first and matches on equality. This run resolved `apim-couponsvc-lab-vxziaw`
against the Azure DevOps deployment's `apim-couponsvc-lab-dtjori`, confirming the two names are
genuinely distinct and that the prefix form would have selected both. See `decisions.md`,
phase H.

---

## 5. The first attempt failed, and why

Run `34333162547`, on the same commit, failed in **1m15s at job 2, step `Azure login`**:

```
AADSTS700213: No matching federated identity record found for presented assertion subject
'repo:i-am-shams@8597430/coupon-service@1361729297:ref:refs/heads/main'
```

The federated credential had been registered with the subject documented by both Microsoft and
GitHub, `repo:i-am-shams/coupon-service:ref:refs/heads/main`. That is not what this repository
presents. GitHub's API confirms the default now embeds numeric owner and repository IDs:

```json
GET repos/i-am-shams/coupon-service/actions/oidc/customization/sub
{ "use_default": true,
  "use_immutable_subject": false,
  "sub_claim_prefix": "repo:i-am-shams@8597430/coupon-service@1361729297" }
```

`owner_id 8597430` and `repo_id 1361729297` match the presented assertion exactly. The fix was
one field on the existing credential — issuer, audience, application, service principal and role
assignment all unchanged — and no workflow edit, since `deploy.yml` never references the subject.

**The ID-qualified subject is the better credential.** It survives a repository or account
rename; the documented name-based form breaks silently on one. Anyone reproducing Day 0 should
read the subject off a failed assertion rather than copying it from documentation.

Job 1 passed in the failed run too, so the 15 scenarios have now been observed green on two
independent runs.

---

## 6. Day 0 for this pipeline

Five items, not the three in `approach.md` §7 and not the five in `deployment.md` §2 — the Azure
DevOps items are replaced by their GitHub equivalents, and the reviewer test account drops out
because, as section 3 shows, no assertion signs in.

| | Item | Why the pipeline cannot do it |
|---|---|---|
| 1 | GitHub repository with `deploy.yml` on `main` | The workflow cannot create itself |
| 2 | Entra app registration `github-actions-coupon-service`, a federated credential, and **Contributor at subscription scope** | A pipeline cannot create the identity it logs in with |
| 3 | Repository **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | They name the registration from item 2 |
| 4 | The `coupon-api` and `coupon-spa` registrations — **reused**, not duplicated | Microsoft Graph, not ARM |
| 5 | The `env:` block in `deploy.yml` set for the target subscription and tenant | Depends on items 2 and 4 |

Item 3 goes on the **Variables** tab, not Secrets. All three are public identifiers, and a value
placed under Secrets is unreachable as `vars.*` — `azure/login` then fails on a blank client ID
rather than a wrong one.

**Contributor is sufficient, and it is not an oversight.** The deployment contains no Azure RBAC
role assignment at all: `grep -rn "Microsoft.Authorization" infra/` returns only the comments in
`storage.bicep` explaining why there is none. The two managed identities are created and attached
to resources — `Microsoft.Web/sites/write`, not `Microsoft.Authorization/roleAssignments/write` —
and reach what they need through a SQL contained user and Easy Auth `allowedApplications`, neither
of which is RBAC. `authentication.md` §9 records the experiment that settled this.

**Item 4 completes in two sittings**, and the second is deferred until after a first provision
because the origin is not knowable before then. `coupon-spa` needs
`https://stcouponsvclabgh.z29.web.core.windows.net` added on the **SPA** platform, *alongside*
`http://localhost:5173` and the Azure DevOps origin — not replacing either.

---

## 7. Comparison with the Azure DevOps cold-start run

Two runners, two numbers. Recorded side by side because the comparison is informative, not
because either is the other's benchmark.

| Stage | Azure DevOps | GitHub Actions |
|---|---|---|
| 1 Build and test | 1m30s | 46s |
| 2 Provision | 8m46s | 7m59s |
| 3 Grant DB access | 50s | 35s |
| 4 Deploy backend | 3m16s | 4m23s |
| 5 Build and deploy UI | 1m10s | 30s |
| 6 Smoke test | 1m26s | 3m13s |
| **Total** | **16m58s** | **17m45s** |

The two large differences are both first-boot variance rather than pipeline differences: job 4
and job 6 together carry the App Service cold start, which took 165s of polling here against 45s
there. Provisioning, which is almost entirely ARM waiting on Azure, came out within 47 seconds
across the two.
