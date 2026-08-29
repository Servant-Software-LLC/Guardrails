## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "06-implement-barrier-wait": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Two halves, both required:

**A. Implement `BarrierWait`** in `src/Guardrails.Core/Providers/BarrierWait.cs` so that every test in
`tests/Guardrails.Core.Tests/Providers/BarrierWaitTests.cs` — written by task 05, and OUT of your write
scope — passes.

**B. Wire it into the wave barrier** in `src/Guardrails.Core/Execution/Scheduler.cs`, so a provider
quota limit at a barrier **pauses and re-probes** instead of ending the run.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Providers/BarrierWait.cs` and `src/Guardrails.Core/Execution/Scheduler.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside these two paths.
That exclusion is not incidental — see "the files you must not touch" below. An out-of-scope edit fails
the task immediately and consumes a retry.

`BarrierWaitTests.cs` is **not** in your write scope. Implement to those tests; do not reshape them, and
do not reshape `BarrierWait`'s surface in a way they no longer type-check against. If a test looks
wrong, that is a `needsHuman` (`"kind": "blocked-work"`), not an edit.

## The problem (issue #511), stated so the fix is not guessed

A provider quota limit **inside a task** is already ridden out. `ActionRunner` classifies it
`PromptFailureKind.Transient`; `TaskExecutor` (around line 218) raises
`IRunObserver.PromptPaused(task, reason, delay, pauseCount)`, waits on `TransientBackoff`, and re-runs
the SAME attempt without consuming the retry budget (issue #115).

The **same signal at a wave barrier** ends the run. The barrier is `Scheduler.RunBreakdownSegmentsAsync`:
it invokes the JIT breakdown for the next wave, and the returned `WaveBreakdownOutcome` already carries
the runner's classification —

```csharp
// src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs
public PromptFailureKind FailureKind { get; init; }
public bool TerminatedCleanly => ProcessCompleted && Error is null && FailureKind == PromptFailureKind.None;
public string? FailureKindToken => FailureKind switch { … PromptFailureKind.Transient => BreakdownFailureTokens.Transient, … };
```

A `Transient` outcome is therefore already **named** end-to-end (`BreakdownFailureTokens.Transient`
exists, and #385 milestone 1 deliberately stopped discarding `FailureKind`). What is missing is that
nothing **acts** on it: `TerminatedCleanly` is false, the segment falls through to the incomplete /
quarantine / fail branches, and the run halts. Same signal, same provider, two outcomes — and the
barrier is where a long unattended run has the most invested.

## What to build

### A. `BarrierWait` — the policy

```
nextProbe = min(resetInstant, now + probeInterval)      // probeInterval defaults to 30 minutes
```

Wait and re-probe rather than terminate. The `min` is the interesting half: a reset hint three hours out
does not buy a three-hour sleep — we re-probe in 30 minutes, because the hint is advisory. Bounded by a
cumulative ceiling; when the ceiling is spent, waiting is refused and the barrier settles. Read
`src/Guardrails.Core/Execution/TransientBackoff.cs` — the shipped in-task sibling — and keep the two
readable side by side.

The tests are the specification. Do not add behaviour they do not pin, and do not remove the
constructor-settable ceiling and probe interval they rely on.

### B. The wiring — where, and what "acts on it" means

In `Scheduler.RunBreakdownSegmentsAsync`, before a `Transient` outcome is allowed to settle the wave:

1. If the barrier's `BarrierWait` still `CanWaitAgain()`, **raise the pause on the observer**, wait, and
   **re-probe** — re-invoke the breakdown for the same wave. The operator must see a PAUSE, not a
   failure.
2. If the ceiling is spent, settle as today — but with the rate-limit cause named, so the halt says
   "the provider limit never cleared within the barrier's N-minute bound", not "the breakdown did not
   complete cleanly".
3. A re-probe after a transient pause **must not silently eat the segment budget**. `MaxBreakdownSegments`
   bounds how many *authoring* attempts a wave gets; a wait for a rate limit is not an authoring attempt,
   and spending one on it would quietly shrink the wave's real budget. Decide this deliberately and say
   in a comment which you chose and why.

Honour `cancellationToken` throughout: a Ctrl+C during a 30-minute barrier wait must return promptly,
the way `TaskExecutor`'s pause loop checks `cancellationToken.IsCancellationRequested` around its own
wait.

### The observer hook: REUSE, do not add

```csharp
// src/Guardrails.Core/Execution/IRunObserver.cs:84
void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) { }
```

This hook **already exists** and is exactly the signal #511 wants: "a HEALTHY task waiting out a rate
limit, not a failing one." **Raise it.** Do NOT add a new observer method.

**The files you must not touch, and why it is not merely a scope rule:** `IRunObserver.cs`,
`ConsoleRunObserver.cs`, `LiveRunObserver.cs` and `OnTheFlyDiagramObserver.cs` are all outside your write
scope, and **another task cluster in this same plan is editing those files on a parallel branch**. Two
branches appending a member to an observer file merge with **no conflict marker and two copies** — the
CS0101 that red-halted plan-0009 (#175). A new observer method here would buy nothing the `reason`
string does not already carry, and would cost the plan a merge hazard. Everything the operator needs
travels in `reason`.

### The one thing that will fail SILENTLY if you get it wrong

`PromptPaused` takes a `TaskNode`. **At a wave barrier there is no task** — a JIT stub wave has zero
authored tasks, which is precisely why the barrier is running. You will pass a synthetic `TaskNode`
standing for the barrier phase, and its **`Id` is the whole question**, because:

- `LiveRunObserver.PromptPaused` calls `Update(task.Id, …)`, and `Update` **tolerates an unknown id as a
  no-op** (`if (!_rowByKey.TryGetValue(taskId, out int row)) { return; }`, the #379 collapsed-wave
  tolerance). So a wrong id does not throw. It renders **nowhere**, and the live table shows an
  unexplained 30-minute silence — the failure mode looking exactly like success, which is the defect
  class this repo keeps re-finding.
- The live table **already has a row for this phase**. `WavePhaseLiveRow` (in `Guardrails.Cli/Ui/`) keys
  the JIT-breakdown phase row as `KeyFor(waveDir, BreakdownPhase)` → **`"<waveDir>/(breakdown)"`**, and
  `LiveRunObserver` indexes it in `_rowByKey`. The parenthesised segment is documented there as
  un-collidable with a task id.

So: **the pause must land on the wave's existing breakdown phase row.** `Guardrails.Core` cannot
reference `Guardrails.Cli`, so you cannot call `WavePhaseLiveRow.KeyFor` — you are reproducing a key
convention across an assembly boundary, which is a name-convention seam (#96) that drifts silently.
Construct the id from `wave.Dir`, and leave a comment naming `WavePhaseLiveRow.KeyFor` as the Cli-side
owner of the spelling, so the next reader of either side finds the other.

Verify it by reading `LiveRunObserver.RebuildRows` / `_rowByKey` rather than by assuming.

### The reset instant: null is the honest answer today

`ClaudeSignalClassifier.ExtractResetHint(text)` exists and returns an **advisory string** ("11:20am") —
it is explicitly *"never parsed into a"* instant. Turning that string into a `DateTimeOffset` is **not**
this task's job and is not required by any test. Pass `null` when you have no instant; the policy then
uses the 30-minute default, which is the shipped behaviour #511 asks for. If you can derive a genuine
instant cheaply and correctly, pass it — but do not invent a parser, and never guess an instant from a
string you did not fully parse. A wrong instant is worse than none: it makes `min` return a fictional
time the operator is then shown.

### The reason string — the operator's whole view of this

It is the payload. It must carry the provider cause **and the next-probe time**, and read as a healthy
wait. `ConsoleRunObserver` renders it as:

```
[paused] <id>: transient — <reason>; backing off <N>s (pause <n>); does NOT count against retries
```

so write `reason` to complete that sentence. `BarrierWaitTests` pins that the policy's reason names the
next-probe time; use the policy's reason at the call site rather than composing a second, divergent one.

## Read before you write

- `src/Guardrails.Core/Execution/Scheduler.cs` — `RunBreakdownSegmentsAsync` and the settlement helpers
  it returns through (`FailBreakdown`, `IncompleteBreakdown`, `CompleteBreakdown`). This is a large file
  and the barrier logic is dense; understand the existing settlement branches before adding one.
- `src/Guardrails.Core/Execution/TaskExecutor.cs` around lines 200–220 — the shipped in-task pause loop.
  Your barrier loop is its sibling: same observer call, same "does not consume the budget" posture,
  different bound.
- `src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs` — `WaveBreakdownOutcome`, `FailureKind`,
  `TerminatedCleanly`, `CutOffCause`, and `BreakdownFailureTokens.Transient`.
- `tests/Guardrails.Core.Tests/Providers/BarrierWaitTests.cs` — the specification for half A.

## Verify before you finish

Run `dotnet build Guardrails.sln` and
`dotnet test tests/Guardrails.Core.Tests --filter "Category=BacklogSlate&FullyQualifiedName~BarrierWaitTests"`.
The solution build is the gate, not the test project alone: `Scheduler.cs` is consumed by
`Guardrails.Cli`, so a change to its surface can leave `tests/Guardrails.Core.Tests` green and the CLI
broken (the #176 transitive-compile-dependency trap).
