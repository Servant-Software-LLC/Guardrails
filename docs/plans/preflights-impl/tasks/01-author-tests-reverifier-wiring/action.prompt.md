## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `01-author-tests-reverifier-wiring`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-reverifier-wiring": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author a **composition-root wiring test** in `tests/Guardrails.Integration.Tests/ReVerifierWiringTests.cs`
proving that the production `SchedulerFactory` wires the attempt-decoupled `IReVerifier` seam in BOTH
serial and worktree mode (Deliverable 1 / test #12). This test must be RED against current code and go
green once Deliverable 2 (the implementation task) wires `IReVerifier` unconditionally.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ReVerifierWiringTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including changes to production files
(`SchedulerFactory.cs`), other test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Requirements for the test:
- Tag the test class `[Trait("Category", "Preflights")]` (the convention every new test in this plan
  carries, so the baseline root can exclude them).
- Drive the **REAL** `SchedulerFactory.Create(plan, processRunner, probe, observer)` — NEVER
  `new Scheduler(...)` with a manually-injected re-verifier (that would pass even with an unwired
  factory and is FORBIDDEN). Use reflection to read the private `IReVerifier? _reVerifier` field on the
  returned `Scheduler` (field at `Scheduler.cs` line ~27).
- **Serial-mode assertion (the discriminating one):** build a `PlanDefinition` with
  `MaxParallelism = 1`, call the real factory, assert the reflected `_reVerifier` is **NOT null**. This
  FAILS on current code (today the factory constructs the re-verifier only inside the
  `MaxParallelism > 1 && IsGitRepository(...)` guard at `SchedulerFactory.cs` line ~92) — that failure IS
  the intended TDD red.
- **Worktree-mode regression assertion:** build a plan with `MaxParallelism = 2` over a **real git
  repository workspace** and assert `_reVerifier` is non-null there too (this already passes today; it
  guards against a regression when the wiring is made unconditional). REUSE the existing git-repo /
  worktree test fixtures and plan builders in `tests/Guardrails.Integration.Tests/` (e.g. the
  `GitWorktreeLifecycle` helpers, `ScriptPlanBuilder` / `FakeClaudePlanBuilder`, `PathExecutableProbe` /
  a fake probe, `IRunObserver.Null`) — do NOT hand-roll a git repo or a new plan builder.
- The test MUST **compile** (all referenced types already exist) and **fail** on the serial-mode
  assertion. Compiling is required; failing is intentional. Do NOT modify `SchedulerFactory` to make it
  pass — that is the implementation task's job.
- The repo builds with `TreatWarningsAsErrors=true` — the test file must be warning-clean (no unused
  usings/variables, xUnit analyzer clean).

Publish nothing to state.
