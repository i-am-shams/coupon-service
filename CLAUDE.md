# CLAUDE.md

**Read `AGENTS.md` in the repository root first. It is the single source of truth for this
project's rules, structure, conventions, and commands.** Everything below is Claude-specific
guidance layered on top; it does not repeat or override those rules.

---

## Your role on this project

Copilot CLI handles the tight implementation loop — domain classes, endpoints, components,
tests. You handle the work where a mistake is expensive to discover:

**Infrastructure.** Bicep templates and `azure-pipelines.yml`. These are multi-file, and every
mistake costs a five-minute deploy cycle to find. Slow down here.

**Review.** Check what was implemented against the non-negotiable rules in `AGENTS.md`,
especially rules 1, 2, 4, 7 and 8. Those are security and correctness invariants, not style.

**Azure.** Use the Azure MCP tools for resource queries, deployment inspection, logs, and
diagnostics rather than guessing at CLI syntax.

**Pipeline diagnosis.** Use the Azure DevOps MCP tools to inspect failed builds directly. Fetch
the failing step's log before proposing a fix.

---

## How to review

When asked to review code, produce findings, not a rewrite.

Order your findings: **correctness and security first, then design, then style.** A missing
tenant filter matters; a naming preference does not.

For each finding, say what is wrong, why it matters, and the smallest change that fixes it.

If the code is fine, say so plainly rather than inventing observations.

---

## Debugging discipline

This project has several layers, and a failure in one looks like a failure in another. Identify
the layer before proposing a fix.

For an HTTP failure, read the response headers first:

| What you see | Who rejected the request |
|---|---|
| `"missing subscription key"` | APIM — key check |
| The policy's own error message string | APIM — `validate-jwt` |
| `WWW-Authenticate: Bearer realm="<app>.azurewebsites.net"` | The backend — the gateway's own token failed |
| `403` with an empty body | The backend — gateway known, not on the allowed client list |
| `404` from APIM with no backend headers | Path matched no operation — **no policy ran at all** |

Routing happens before authentication in APIM. A 404 means the path did not resolve, so the key
and token checks never ran.

For a failing deployment, check what the deployed resource actually is before re-reading the
template.

---

## Things that have already cost time on this stack

Volunteer these if you see the situation arising.

**Tokens last one hour.** A call that worked and now returns 401 from the gateway is almost
certainly an expired token. Re-issue before debugging anything else.

**v1 vs v2 issuers.** Calling the v2 token endpoint can still return a v1 token
(`iss` = `https://sts.windows.net/{tenant}/`, note the trailing slash) if the API's manifest does
not set `accessTokenAcceptedVersion: 2`. `validate-jwt` compares the issuer as an exact string.
Decode the token and copy what is actually in it.

**SQL contained users take the application ID, not the object ID.** This is the opposite of the
rule for RBAC assignments. Users and groups use the object ID; a service principal — which a
managed identity is — uses the application ID. Using the wrong one creates a user that then
fails to authenticate.

**APIM Consumption tier does not support `rate-limit-by-key` or `quota-by-key`.** Only the
subscription-scoped versions, applied at product scope. Check the "applies to" line on a policy
reference page before writing it.

**PowerShell:** brace variables followed by a dot — `"${APIM}.azure-api.net"`. Use `curl.exe`,
not `curl`, which is an alias for `Invoke-WebRequest`.

---

## When to stop and ask

- A change would contradict `docs/approach.md`
- You need a credential
- The fix requires a manual step in a pipeline that must have none
- You are about to introduce a new Azure resource

Ask in one short message with the options and your recommendation, not a list of questions.
