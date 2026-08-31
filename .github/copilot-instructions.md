# GitHub Copilot instructions

**Read `AGENTS.md` in the repository root first. It is the single source of truth for this
project's rules, structure, conventions, and commands.** Everything below is Copilot-specific;
it does not repeat or override those rules.

---

## Your role on this project

You handle the implementation loop: domain classes, EF Core configuration, endpoint handlers,
React components, and tests. Claude Code handles Bicep, the pipeline, and review.

Work in small increments. Build and test after each one.

---

## Before multi-file changes

Use `/plan` first. This project has a deliberate dependency boundary — `Ordering` may depend on
`ICouponEvaluator` and nothing else from `Coupons` — and a plan surfaces a violation before you
have written it.

---

## The rules most easily broken while moving fast

Full list is in `AGENTS.md`. These four are the ones that get violated by autocomplete momentum:

**Never add a price to a request DTO.** Requests carry `pizzaId` and `quantity`. The server
looks up prices. If a `price`, `subtotal`, or `total` field appears in an inbound model, that is
a security bug.

**Never use string interpolation in a log call.** `LogInformation("Coupon {Code} rejected",
code)` — not `LogInformation($"Coupon {code} rejected")`. The first gives queryable fields.

**Never mock the pricing or coupon logic in a test.** The in-memory database is fine. Mocking
the calculation makes the test meaningless.

**Never call `DateTime.UtcNow` inside coupon rules.** Time comes in as a parameter.

---

## Testing

Reqnroll, not SpecFlow. SpecFlow reached end of life in December 2024 and does not support
.NET 8. If you generate a `SpecFlow` package reference, that is wrong.

Scenarios go through `WebApplicationFactory` so they exercise real routing and model binding.
Write them in business language — a step reads like something a product person would say, and
the mapping to a `CouponRejectionReason` enum lives in the step definition.

---

## Commands

Build: `dotnet build`
Test: `dotnet test`
Run: `dotnet run --project src/PizzaShop.Api`
Frontend: `cd web && npm run dev`

Use the Task agent to run these rather than describing what should be run.

---

## After each change

- Build passes
- Tests pass
- No secret in the diff
- If you made a non-obvious choice, add one line to `docs/decisions.md`
