# Per-run cost cap (v2 bet 4, first slice)

The harness already journals each prompt attempt's `costUsd` (SSOT §7) and aggregates
it (`JournalCost.Total`). This slice adds a **per-run ceiling**: an optional
`maxCostUsd` field in `guardrails.json`. When the cumulative journaled cost for the run
reaches or exceeds the cap, the harness stops launching new attempts and marks the
remaining (not-yet-terminal) work `needs-human` with the reason **"cost cap reached"** —
instead of spending unattended past the budget.

This is the smallest honest slice of roadmap v2 bet 4 ("Cost caps"). It is per-RUN only
(no per-task budgets), it trips at the granularity of "before launching the next
attempt" (not mid-attempt — an attempt already in flight finishes and is journaled), and
it reuses the existing needs-human machinery so dependents block exactly as they do for
any other needs-human halt.

## Scope

In:
- A new optional `maxCostUsd` (decimal, USD) field on `guardrails.json` run config
  (SSOT §2), default absent = no cap (today's behavior, unchanged).
- A run-level budget check in the scheduler: before a worker launches a task's attempt,
  if `JournalCost.Total(journal) >= maxCostUsd`, the task is not launched — it is marked
  `needs-human` (reason "cost cap reached") and its transitive dependents `blocked`,
  using the existing halt path. The run then drains to quiescence and exits 2.
- Validation: a `maxCostUsd` that is present and `<= 0` is a validation **error** (a
  zero/negative cap would halt the run before any work — a configuration mistake worth
  catching). New diagnostic code GR2010.
- The SSOT (§2) documents the field; `guardrails-domain-knowledge` notes the semantics.

Out (explicitly deferred — keep the slice small):
- Per-task cost budgets.
- Mid-attempt cancellation when a single attempt blows the cap (we stop launching NEW
  attempts; an in-flight attempt completes).
- A CLI flag to override the cap (config-only for now).
- Cost estimation/prediction before an attempt runs.

## Behavior, precisely

1. `maxCostUsd` absent → no cap; behavior is byte-for-byte today's. This is the common
   case (deterministic plans, or prompt plans the human is happy to run uncapped).
2. `maxCostUsd` present → before each attempt launch, compare the journal's cumulative
   cost to the cap. At or over the cap: do not launch; mark the task `needs-human` with
   reason "cost cap reached"; block its transitive dependents. Independent branches that
   were already launched finish normally (their cost is still journaled and may push the
   total higher — that is honest and acceptable for a per-run cap).
3. The cap is checked against `JournalCost.Total` (all recorded attempts), so a resumed
   run accounts for cost already spent in prior runs — the cap is a true cumulative
   ceiling across resumes, not a per-invocation one.
4. Exit code is the existing `2` (needs-human) when the cap trips; no new exit code.

## Files this touches (orientation for the breakdown, not a contract)

- `src/Guardrails.Core/Model/RunConfig.cs` — add `decimal? MaxCostUsd`.
- `src/Guardrails.Core/Loading/PlanJson.cs` / `RawManifests.cs` — parse `maxCostUsd`.
- `src/Guardrails.Core/Loading/PlanValidator.cs` + `DiagnosticCodes.cs` — GR2010 for a
  non-positive cap.
- `src/Guardrails.Core/Execution/Scheduler.cs` — the launch-time budget check using the
  injected journal and `JournalCost.Total`.
- `docs/plans/02-schemas-and-contracts.md` (§2) and the domain-knowledge skill — docs.

## Acceptance

- A plan with `maxCostUsd` set below the cost of its prompt tasks halts: the over-budget
  task ends `needs-human` with reason "cost cap reached", its dependents `blocked`, exit 2.
- A plan with `maxCostUsd` set comfortably above its total cost runs fully green.
- A plan with no `maxCostUsd` is unchanged (regression-safe).
- `maxCostUsd: 0` (and negatives) fail `guardrails validate` with GR2010.
- Full existing suite stays green.
