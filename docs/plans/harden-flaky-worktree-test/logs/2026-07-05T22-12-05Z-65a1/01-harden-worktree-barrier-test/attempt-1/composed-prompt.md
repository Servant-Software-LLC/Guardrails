## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`01-harden-worktree-barrier-test`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "01-harden-worktree-barrier-test": { "someKey": "someValue" } }`. This task does
  not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside this path — including
changes to production code (`src/Guardrails.Core/**`), other test files, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you
believe the fix genuinely requires a production-code change, do NOT make it — write
`{"needsHuman": "<why a production change is required>"}` to the state-out path and
stop instead.

### Background

`tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs` contains
`Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier`. It
proves the `Scheduler` genuinely runs 3 independent tasks **concurrently** (not
serially) by making all 3 rendezvous at a `TaskCompletionSource` barrier
(`BarrierExecutor` in the same file) before any of them can return — if the scheduler
regressed to serial execution, the barrier would never open and the test's own
30-second `CancellationTokenSource.CancelAfter` would fire, turning a scheduler
regression into an honest test timeout rather than a silent false-green.

This test is currently flaky **under load** (issue #214). CI evidence: on PR #213, one
of two identical parallel `macos-latest` jobs on the SAME commit failed with
`Assert.Equal() Failure: Expected: 3, Actual: 2` (on
`executor.AssignedWorktreePaths.Count`), the other parallel job on the same commit
passed, and a re-run of the failed job then passed. This reproduces locally too: 5
consecutive local runs of just this test produced 3 failures and 2 passes, with the
same `Expected: 3, Actual: <1 or 2>` shape — so this is a genuine, currently-live race,
not a one-off CI fluke.

### Your job

1. **Diagnose the actual root cause before changing anything.** Read
   `Scheduler.cs` (`src/Guardrails.Core/Execution/Scheduler.cs`) — specifically how
   worker tasks are started (`Task.Run(() => WorkerLoopAsync(...))`, one per worker, up
   to `maxParallelism`) and how each worker pulls from the channel and calls
   `_executor.ExecuteAsync(...)`. Plausible angles worth checking (confirm or rule out
   with evidence, don't just guess):
   - **Thread-pool starvation.** `Task.Run` schedules onto the shared .NET
     `ThreadPool`. If the pool's current thread count is below what's needed and the
     pool's growth algorithm doesn't inject a new thread fast enough (the CLR only
     grows the pool slowly past `ThreadPool.GetMinThreads()`, especially under
     concurrent CI load from other processes/tests), one or more of the 3 worker
     `Task.Run` continuations can sit queued for longer than expected before actually
     getting a thread — delaying when a task's `ExecuteAsync` runs and therefore when
     it can signal barrier arrival within the test's fixed window.
   - **The interaction between the per-task barrier-arrival signal and the
     `ConcurrentBag` write ordering** in `BarrierExecutor.ExecuteAsync` — check whether
     `AssignedWorktreePaths.Add(...)` and the `Interlocked.Increment` /
     `TaskCompletionSource.TrySetResult` sequencing could under-count arrivals, or
     whether the count assertions run against a bag that hasn't finished draining.
   - Anything else your investigation turns up. State in a code comment (near the
     fix) which cause you found evidence for and how you confirmed it — e.g. by
     instrumenting a temporary diagnostic run locally (do not leave temporary
     diagnostics in the committed test).

2. **Fix the actual root cause directly — do not paper over the symptom.** Explicitly
   forbidden: simply wrapping the test body in a retry-until-pass loop, and weakening
   any assertion to tolerate fewer than 3 arrivals (e.g. changing `Assert.Equal(3, …)`
   to `Assert.True(… >= 2)`). Either would defeat the test's entire purpose — proving
   genuine 3-way concurrency. Plausible legitimate fixes, depending on what your
   diagnosis finds:
   - If thread-pool starvation is the cause: force the pool to have enough minimum
     threads available BEFORE the test starts driving the scheduler (e.g.
     `ThreadPool.SetMinThreads` raised for the duration of the test, restored
     afterward, or a `Task.Factory.StartNew` with `TaskCreationOptions.LongRunning`
     equivalent applied consistently) — whatever addresses the actual delay you
     diagnosed.
   - If the timing budget itself is just too tight under load: widen the test's own
     margins (e.g. the 30-second `CancellationToken` deadline, or an internal
     rendezvous wait) — but only as PART of addressing the diagnosed cause, not as a
     substitute for it.
   - A more robust rendezvous mechanism in `BarrierExecutor` if the diagnosis points
     there instead.

3. **Do not weaken what the test proves.** After your change, the test must still:
   - assert all 3 tasks received distinct `WorktreeHandle`s
     (`AssignedWorktreePaths.Count == 3` and `.Distinct().Count() == 3`);
   - assert `Integrate` was called once per task (`IntegrateCallCount == 3`);
   - assert the overall run succeeded (`report.AllSucceeded`);
   - still fail (ideally via its own timeout, not a silent pass) if the scheduler ever
     regresses to serial execution — do not change `BarrierExecutor` in a way that lets
     it succeed without all 3 tasks genuinely overlapping.

4. **Stay inside your `writeScope`.** Only
   `tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs` may change. Do not touch
   `Scheduler.cs` or any other production file — if your diagnosis genuinely requires a
   production-code change to fix correctly, stop and write
   `{"needsHuman": "<why>"}` instead of editing it.

### Verification you can run yourself before finishing

`dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~WorktreeProviderSeamTests" --nologo` — run it several times in a row
locally; it should pass consistently. This task's own guardrails will additionally run
it in a tight repeated loop and then the whole `Guardrails.Core.Tests` project once.

## Shared state

Your input state (a snapshot, read-only) is:

```json
{}
```

## Output contract

Write your new/changed state as a single JSON object fragment to this absolute path:

`C:\Dev AI\Guardrails\docs\plans\harden-flaky-worktree-test\logs\2026-07-05T22-12-05Z-65a1\01-harden-worktree-barrier-test\attempt-1\action-out-fragment.json`

Write ONLY your own keys (conventionally namespaced under your task id). Do NOT modify state.json directly — the harness is the single writer and merges your fragment after guardrails pass. If you have nothing to contribute, write nothing.

If you cannot proceed without a human decision, write exactly `{ "needsHuman": "<your question>" }` to that same path and stop — the harness will escalate to a human without burning further retries.
