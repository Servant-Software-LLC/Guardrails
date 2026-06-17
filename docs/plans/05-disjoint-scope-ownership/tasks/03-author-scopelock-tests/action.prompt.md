## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author failing xUnit.v3 tests for M2 (§4.2, §4.3, §10 of
`docs/plans/05-disjoint-scope-ownership.md`). Mirror the existing test conventions in
`tests/Guardrails.Core.Tests/` — in particular the patterns already in
`SchedulerTests.cs` and any fake-runner / `FakeExecutableProbe` helpers — including
package versions and `[Fact]`/`[Theory]` style. Two files:

1. `tests/Guardrails.Core.Tests/ScopeLockTests.cs` — for a new `ScopeLock` type that
   generalizes `WorkspaceLock` (the binary shared/exclusive lock becomes the special
   case `**` = exclusive). Assert:
   - A waiter with scope S is admitted iff S does not intersect the union of held
     scopes (use `WriteScope.Overlaps`).
   - Strict FIFO fairness: no skip-ahead — if an earlier FIFO waiter is blocked, a
     later non-overlapping waiter still waits (starvation-free, deterministic).
   - Two universal `["**"]` holders are mutually exclusive (the WorkspaceLock special
     case still holds).
   - An empty-scope `[]` waiter is admitted against any held set (disjoint from all).

2. `tests/Guardrails.Core.Tests/ScopeSchedulerTests.cs` — for the rewired `Scheduler`.
   Using a fake/instrumented runner that records each task's execution window:
   - Three independent (no DAG edge), narrow, mutually-disjoint-scope tasks have
     OVERLAPPING execution windows (they ran concurrently).
   - Two universal-scope tasks (or two overlapping-scope tasks) have NON-overlapping
     windows (they serialized).
   - `maxParallelism` still caps the worker count independently.

The tests MUST fail (or fail to compile, because `ScopeLock` and the scope-aware
Scheduler API do not exist yet) against current code — that is intentional. Do NOT
implement `ScopeLock`, do NOT rewire the Scheduler, and do NOT remove `exclusive` here.

You do NOT need to hash anything or write to state — `captureHashes` handles it.
Publish nothing to state.
