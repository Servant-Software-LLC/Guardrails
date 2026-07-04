# Harden the flaky `WorktreeProviderSeamTests` barrier test

## Context

`tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs` contains
`Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier` — a real,
currently-passing unit test that proves the scheduler genuinely runs independent tasks
concurrently (not serially) by making 3 tasks rendezvous at a barrier before any can finish. If
the scheduler regressed to serial execution, the barrier would never open and the test's own
30-second `CancellationToken` would fire, turning the regression into a test timeout rather than a
silent false-green.

This test is currently **flaky under CI load** (see issue #214). Evidence: on PR #213's CI, one of
two parallel `macos-latest` runs on the identical commit failed with `Assert.Equal() Failure:
Expected: 3, Actual: 2` (asserting `executor.AssignedWorktreePaths.Count`), while the OTHER
parallel run on the same commit passed cleanly, and a re-run of the failed job then passed too.
The test is not touched by unrelated PRs when this happens — it fails and passes on the identical
code, which is the signature of a genuine timing/scheduling race, not a logic bug in the code under
test.

## The ask

Harden this test so it does not intermittently fail on a loaded CI runner, **without weakening
what it actually proves** — it must still fail (ideally via its own timeout, not a silent pass) if
the scheduler regresses to serial task execution. Investigate the actual root cause of the
flakiness (don't just guess) — plausible angles worth checking: thread-pool starvation under a
loaded runner delaying when a task's `ExecuteAsync` call actually gets a thread to run on, the
interaction between the per-task barrier-arrival signal and the `ConcurrentBag` write ordering, or
something else the investigation turns up. Whatever the root cause turns out to be, the fix should
address it directly (e.g. a more generous timeout, a more robust rendezvous mechanism, forcing
thread-pool minimum threads for the test, or another fix that matches the actual diagnosis) rather
than papering over the symptom (e.g. do not simply retry-until-pass, and do not weaken the
assertion to tolerate fewer than 3 arrivals — that would defeat the test's whole purpose).

## Acceptance

- The test still asserts all 3 tasks got distinct `WorktreeHandle`s, `Integrate` was called once
  per task, and the overall run succeeded — the assertions themselves should not be weakened.
- The test should be demonstrably more robust against the kind of CI-load-induced flakiness
  observed in #214 (state what changed and why it addresses the actual root cause, not just "added
  a retry").
- The rest of `Guardrails.Core.Tests` stays green — this is a single-file, test-only change with
  no production-code impact.

## Stack

.NET 8, xUnit v3. This is a fast, in-process unit test (`Guardrails.Core.Tests`) — no external
process, no I/O beyond in-memory fakes (`FakeWorktreeProvider`, `FakeJournal`). Verification is
`dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj`.
