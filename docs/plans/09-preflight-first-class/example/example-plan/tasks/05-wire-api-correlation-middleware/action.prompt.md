# Action: wire correlation middleware in Acme.Payments.Api

<!-- Harness-contract header (see 03's note). SIMULATION: illustrative sample, not runnable. -->

Add middleware to `Acme.Payments.Api` that:

- reads an inbound `X-Request-Id` header (or generates one when absent),
- passes that id into the `Acme.Payments.Core` charge call (the threading added in `04`), and
- leaves `GET /health` still returning `200`.

## Constraints

- Edit only `Acme.Payments.Api/`. Your `writeScope` is exactly that folder.
- The plan-level positive preflight (`<plan>/preflights/01-all-repo-tests-green.ps1`) already
  proved `/health` was up BEFORE this plan started, so this task's "/health still 200" guardrail
  is provably testing the effect of YOUR middleware, not a pre-existing break.
- This task's own `preflights/` folder additionally verifies, in THIS task's worktree at
  `taskBase`, that `04-implement-correlation` actually delivered the `RequestId` threading this
  task builds against — the JIT dependency-delivery precondition keyed to the `04 -> 05`
  `dependsOn` edge.
