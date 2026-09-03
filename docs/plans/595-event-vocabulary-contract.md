# Architecture: the `events.jsonl` event vocabulary — run termination and the attempt row (#595)

Design of record for the two `IRunObserver` contract changes issue #595 asks for, on top of the
additive kinds already landed on `fix/595-events-lifecycle-kinds` (`e7ba57d`).

Status: **REVIEWED AND SETTLED.** The implementation plan is `docs/plans/35-event-vocabulary.md`.

## Maintainer ruling (settled — do not re-open)

> **Decision 1 — `run-finished` ships ALONE. `run-started` is DROPPED.**
> Carried by this document's own finding A: both of `run-started`'s counters are approximations,
> `taskCount` undercounts exactly the JIT-waved plan the argument leaned on, and the name asserts a
> bracket it does not deliver — false alarms across a long Full Flight Checks phase, missed alarms on
> the six halts that write no row. That is the #595 defect shape reintroduced by the fix for it.
> `task-started`, already shipped on `e7ba57d`, covers DAG-onward liveness, which was the gap that
> actually motivated `run-started`.
>
> **Decision 2 — `AttemptFinished` → `(TaskNode, Journal.AttemptRecord)` is APPROVED as designed.**
> Projection-side accumulation was pushed back on and the §2b rejection held: cost and turns are
> announced to no observer ever, so accumulation cannot solve the stated problem, and it would make
> the projection a second owner of a fact `run.json` owns.

Sections below are kept as authored where they remain true, and marked where the ruling supersedes
them. `RunStarting`'s rejection is preserved in §1a rather than deleted, so the case for it — and the
reason it lost — survives for whoever proposes it again.

**#563 applied.** Every load-bearing claim #595 makes was re-verified against the code rather than
taken from the issue body. One came back incomplete and changed the design: `attempt-finished` is not
merely field-poor, it is **absent entirely on the default execution path** (Bug A). That is not in
#595, and #595's suggested scope would have shipped over it.

**Revised after an independent adversarial pass** (a non-authoring agent, per the standing rule that
the adversarial pass must not be run by the author). It falsified five things in the first draft, and
all five corrections made the design smaller or more honest:

- `RunFinished` in the `finally` at `RunCommand.cs:556` **would not have fired on a mid-DAG fault** —
  `ExecuteAsync` is called at `:525`/`:534`, *outside* that try. The headline claim of decision 1 was
  wrong. Fixed by a new outer bracket (§1c).
- **`runId` does not belong on the seam.** `BuildObserverChain` already has it and already constructs
  `RunEventStream`; a constructor parameter fixes the silent path-derivation with one line, no
  forwarding hazard, and no member to swallow. All three of the first draft's reasons collapsed.
- **The first draft's "Bug B" was not a bug.** The empty-200 is deliberate, documented, and pinned by
  `EventsEndpointTests.cs:127-142` — and the fix as written would have *hung* that test rather than
  failed it. Dropped, replaced by a much smaller transport fix that is actually load-bearing (Bug B′).
- **`at` is neither unique nor monotonic** under parallel workers, and the first draft told layer 3 to
  key on it. Fixed by a `seq` assigned inside the append lock (§2c).
- Two figures in a section labeled "measured compile surface" were **not measured**. Corrected, and
  the section now cites how each was counted.

Findings that survived as accepted limitations rather than fixes are recorded in the self-critique.

---

## What's being asked

Plan 34 (`c10a13a`) shipped one emission seam (`IRunObserver`) with two projections off it:
`RunEventStream` → `events.jsonl` (semantic, low-frequency, agent-facing) and `ObserverProjection` →
`observer.jsonl` (render-fidelity, drives `guardrails attach`). #595 measured that `events.jsonl`
shipped with one kind and a six-field row, and asks for two things:

1. **Run-level bracketing** — proposed as `run-started` / `run-finished`; **shipped as
   `run-finished` alone** (§1a). No member of `IRunObserver` is
   run-scoped, so this needs a **new seam**, not a new projection.
2. **Cost and turns on the attempt row** — plan 34 §6 claimed `events.jsonl` was "field-aligned with
   `TelemetryRow`"; the alignment holds for five identity fields and stops there.

**Ambiguity named, and how I narrowed it.** #595 says "widen `EventRow` to the `TelemetryRow` fields
it claims alignment with" and lists six. `TelemetryRow` has 30 members. "Field-aligned" is doing two
different jobs in that sentence: *the seam must be able to carry any of them* (a contract property) and
*the row must print some of them* (an editorial choice). I split those deliberately — the **seam
carries the whole attempt record; the row prints a curated subset** — and every rejected field below
says which of the two reasons excluded it. If you disagree with the split, the row is the cheap half to
change later and the seam is the expensive half; that asymmetry is why the seam gets the general answer.

Second ambiguity: **#595's example row is a sketch, not a spelling.** It writes `attempt_failed`,
`ts`, `elapsedSeconds`, `attemptsMax`; the shipped stream writes `attempt-finished`, `at`, and
`budget`, and has no elapsed field. Three of those divergences are already shipped and correct. This
design pins the real names in the SSOT rather than retrofitting the sketch — see
"Names that deliberately diverge from #585's example row" below.

---

## Placement

| Item | Placement |
|------|-----------|
| `RunFinished` on `IRunObserver` | **harness** — `Guardrails.Core.Execution` + one CLI raise site |
| `RunStarting` on `IRunObserver` | **REJECTED** — §1a records the case and why it lost |
| `AttemptFinished` payload widening | **harness** — `Guardrails.Core.Execution` |
| The worktree-mode `attempt-finished` coverage hole | **harness** (bug, found while designing — see below) |
| `GET /events` losing the terminal row to its poll interval on shutdown | **harness** (bug, load-bearing for decision 1) |
| `GET /events` returning an empty 200 for a run with no rows yet | **NOT a bug — leave it.** Deliberate, test-pinned, and the server does not start until after every pre-DAG phase |
| The worktree `needs-human`-by-integration-gate settles raising no attempt event | **separate issue** — the gap is in the journal, not the seam |
| A plan-folder lock against two concurrent runs sharing one `events.jsonl` | **out of scope** — pre-existing; §8.1 scopes its single-writer claim honestly instead |
| `logs/<runId>/events.jsonl` + `observer.jsonl` wire contract | **schema** — `02-schemas-and-contracts.md` §8.1/§8.2 (**they are absent from the SSOT entirely**) |
| `needs_human` / `task_blocked` / `wave_gate` / `merge` / preflight kinds | **not this change** — #595 itself defers them ("as they earn their keep") |
| Moving the observer chain earlier so pre-DAG halts reach the stream | **out of scope**, with reason (below) |
| `--on-event <url>` webhooks | **v2 / next plan** — #585 layer 3; three dependencies flagged below |

### Bugs found while designing this — one in scope, one retracted

**Bug A — the worktree-mode success emits no `attempt-finished` at all.**
`AttemptJournaler.ValidateFragmentForSettle` (`src/Guardrails.Core/Execution/AttemptJournaler.cs:193`)
is the **default (worktree) mode** success path. It builds a `PendingAttempt`, sets
`DeferredSettle = true`, and **never calls `AttemptFinished`** — every other journaller method does
(9 sites) and so do the two revalidate settles in `TaskExecutor`. The deferred settle that finally
journals the record, `Scheduler.RecordSucceededSettle`
(`src/Guardrails.Core/Execution/Scheduler.cs:4471`), does not raise it either. `Scheduler.cs` raises
`AttemptFinished` nowhere.

So today, in the mode most runs use, `events.jsonl` carries `attempt-finished` for **failures and
halts only**. A supervising agent filtering for `outcome: "succeeded"` on that kind sees nothing, and
"nothing" is what #585 exists to stop meaning two things.

This is the same defect the SSOT already documents one seam over, in §15.2a: *"a member hung directly
off `AttemptRecord` lands in serial mode and silently vanishes in worktree mode unless `PendingAttempt`
grows a carrier of its own."* `PendingAttempt`'s own doc comment says it about `Usage`, `Turns` and
`Segments` in three consecutive paragraphs. The **event** variant of that trap had nobody watching it.
Widening the seam without fixing this would deliver cost and turns to the minority path.

**Bug A has a residual the fix does NOT close, and the fix's comment must not claim otherwise.**
Worktree settles that end `needs-human` — a failed union re-verify (`Scheduler.cs:~4289`), an
unresolvable AI-merge (`:~4333`), a non-FF integration failure — call
`_journal.RecordSettle(task.Id, NeedsHuman, null)` and build **no `AttemptRecord` at all**. So even
after task 8, those paths raise nothing. That is the highest-value row for an unattended supervisor:
a task that passed its own guardrails and then failed the integration gate.

**Deliberately not fixed here, and the reason is DRY, not scope.** The gap is in the *journal* — those
settles record no attempt — and the event is a projection of the journal. Manufacturing an
`AttemptRecord` for the event alone would put a fact in `events.jsonl` that is not in `run.json`,
which is the second-owner problem this whole design rejects elsewhere. **File it as a journal-
completeness issue**: once those settles journal a real record, this event follows for free. Task 8's
comment must say "the worktree SUCCESS path's only route to this event", not "the default mode's only
route", or it ships false on day one.

**The bug I thought was Bug B, and why it is NOT in scope.** The first draft argued that
`LogServer.WriteEventsStream`'s empty-200 for a missing `events.jsonl`
(`src/Guardrails.Cli/Ui/LogServer.cs:789-793`) was #585's defect at the transport layer, and proposed
holding the connection open. **That was wrong, three times over, and it is worth recording so nobody
re-proposes it:**

1. **It is deliberate and pinned.** `LogServer.cs:763-767` argues for it explicitly, and
   `tests/Guardrails.Integration.Tests/RunEvents/EventsEndpointTests.cs:127-142`
   (`EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError`) asserts
   `Assert.Equal(string.Empty, body)`.
2. **The proposed fix would have hung the suite, not failed it.** That test reads the whole body
   without `HttpCompletionOption.ResponseHeadersRead`, so a held connection never returns and the test
   times out — the worst kind of change to hand an implementer.
3. **The window it was meant to cover barely exists.** `LogServer.TryStart` is at `RunCommand.cs:461`
   — *after* the Full Flight Checks (`:372`), the MAX_PATH preflight, and every interactive confirm.
   So there is no interval in which the server is up, a consumer is attached, and the pre-DAG phases
   are still running. The first row now lands as soon as the first task starts, so the file-missing
   window is the handful of statements between `:461` and the first `TaskStarting`.

**Bug B′ — the last row can miss a live subscriber, and that one IS load-bearing.**
`WriteEventsStream`'s tail loop reads, and on a zero-byte read does
`if (_shutdown.Token.WaitHandle.WaitOne(EventsPollInterval)) return;` — it returns on shutdown
**without a final read**. `RunFinished` is appended in the inner `finally` (`RunCommand.cs:~700`) and
the log server is disposed in the outer one (`:~714`) microseconds later, so the streaming loop is
almost certainly parked in that 150 ms wait and returns having never read the terminal row. The
`run-finished` event — decision 1's entire payoff — lands in the file and never reaches the attached
supervisor.

**The fix is three lines**: on the shutdown signal, do one final read-and-flush before returning.
**And the honest limitation that stays**, because `_listener.Stop()` runs first in `DisposeAsync` and
can abort an in-flight response: **`run-finished` is a durable FILE event first.** A live subscriber
whose connection closes must re-read the file rather than assume it saw everything. §8.1 says so.
Making delivery *guaranteed* would mean the run waiting on its own HTTP clients, which is a worse
trade than a documented re-read.

---

## Invariants in play

1. **#4 — the SSOT is the schema SSOT, and a contract change lands there in the same change.**
   `events.jsonl` and `observer.jsonl` appear **nowhere** in `02-schemas-and-contracts.md`. Plan 34
   shipped a public wire format — the most contract-shaped artifact in the repo, one an external
   consumer parses — with no SSOT entry. This design's largest deliverable is the missing §8.1/§8.2,
   not the two members. **The invariant was already strained before this change; this change is where
   it gets repaid.**
2. **#2 — the harness is the single writer of merged state.** Directly constrains decision 1: the
   tempting cheap answer (have `RunEventStream`'s constructor author a run-scoped row, or have
   `RunCommand` write run rows itself from the moment `runId` is known) gives `events.jsonl` two
   writers. Rejected below.
3. **#5 — honest halts; nothing is marked done unverified.** Constrains `run-finished` hard: it must
   fire on the fault and cancellation paths, it must **not** fabricate an exit code the process never
   returned, and it must not claim a verdict for the six pre-DAG halts it cannot see. All three
   are honored by making `exitCode` nullable and naming the blind spot in the SSOT rather than
   papering over it.
4. **#6 — plain files, light setup.** Both changes are appends to one JSONL file. No new artifact,
   no index, no state.
5. **#1 — deterministic over prompt-judges** — not strained; nothing here consults a model.

**Where the design strains an invariant.** #5, at one seam: `events.jsonl` begins at the first
`task-started`,
so six pre-DAG halts produce no file at all. I do not fix that (see rejected alternatives) — I
*declare* it, in the SSOT, with the list of the six and the pointer to the mechanism that does cover
them (the process exit code, and the `halt` section #432 added to `run.json`). A declared blind spot
is honest; an undeclared one is the #595 defect again.

---

## Decision 1 — `RunFinished` on `IRunObserver` (`RunStarting` rejected)

### 1a. `run-started` — proposed, and REJECTED

Preserved rather than deleted, because the case for it is genuinely tempting and someone will make it
again. **The ruling is settled; this section exists so the next proposal starts from the counter-case.**

**What was proposed, and what it uniquely bought.** A `run-started` header row carrying `taskCount`
(the denominator — five `task-settled` rows mean nothing without it, and a `/events` consumer has no
other source short of reading the plan folder, which is the filesystem read #585 exists to remove) and
`alreadyCompleteCount` (so a resume of a 12-task plan with 10 already green does not read as a run
that skipped ten tasks). Plus coverage of the JIT-breakdown window, where a waved plan can spend 30
minutes with no `task-started` row at all.

**Why it lost, and every reason came out of this document's own analysis:**

1. **Both counters are approximations.** `taskCount` is `PlanDefinition.Tasks` — the flattened union
   of the waves AUTHORED so far — so a JIT-waved plan authors more afterward and the stated count
   undercounts. `alreadyCompleteCount` is read before the §7.2 definition-drift rewind, which can
   return already-succeeded tasks to pending. **The denominator would have been wrong on exactly the
   plan shape argument 4 leaned on**, which is not a caveat, it is the argument eating itself.
2. **The name asserts a bracket it does not deliver.** The observer chain is built after plan
   validation, the MAX_PATH preflight, Full Flight Checks and every interactive confirm, so
   `run-started` is not a process-launch event. A consumer would write "alert if no `run-started`
   within N seconds" and get a false alarm on every long Full Flight Checks phase and a *missed*
   alarm on the six halts that write no row at all. **That is the #595 defect shape — a signal that
   looks like it certifies something it does not — reintroduced by the fix for it.**
3. **The gap it was meant to close is already closed.** `task-started`, shipped on `e7ba57d`, covers
   liveness from the DAG onward, which was the motivating case.
4. **Its remaining unique argument was `runId` on the seam, and that argument was itself wrong** —
   see §1b: the composition root already holds `runId` and hands it to the writer as a constructor
   parameter.

**What is lost, stated plainly:** a late-attaching consumer gets no denominator from the stream. That
is a real cost, and it is smaller than it looks precisely because the denominator this design could
have supplied would have been unreliable on the runs that most need it. If exact progress is ever
wanted, it is a `run-progress` kind emitted where the counts are actually known — not a header row
guessing at run start.

`run-finished` needs no such defense: #595's case (c) — finished while disconnected — has no other
answer, and it is the terminal signal an unattended supervisor and layer 3 both branch on.

### 1b. Exact signature

**ONE** member added to `src/Guardrails.Core/Execution/IRunObserver.cs`, with a default `{ }` body:

```csharp
/// <summary>
/// This process is DONE with the run (issue #595) — raised once, from a <c>finally</c> that brackets
/// BOTH the DAG and the terminal plan-guardrail phase in <c>RunCommand.RunAsync</c>, so it fires on
/// the normal return, on the terminal-gate-failure early return, on a cancellation, and on an
/// unhandled fault from ANYWHERE in the run body alike. A terminal event that does not fire on the
/// paths an unattended supervisor most needs it would be worse than none.
///
/// <para><paramref name="exitCode"/> is the process exit code this run resolved to — the
/// <c>ExitCodes</c> vocabulary of SSOT §7, which is what a CI wrapper and a supervising agent already
/// branch on — or <b>null</b> when the run is unwinding on an unhandled fault and no exit code was
/// ever determined. Null is the honest answer there; a fabricated code would be the harness claiming
/// a verdict it never reached.</para>
///
/// <para><paramref name="faultKind"/> is the unhandled exception's TYPE NAME (e.g.
/// <c>OperationCanceledException</c>) and null on every non-fault path. <b>Never its message.</b>
/// #585 layer 3 will POST these rows to an operator-supplied URL, and an exception message is the one
/// value on this row that can carry an absolute path, a token, or a fragment of source. Same posture
/// as <see cref="WaveBreakdownFinished"/>'s <c>failureKind</c>: a token, not prose.</para>
///
/// <para>Default no-op; a transparent DECORATOR must forward it explicitly.</para>
/// </summary>
void RunFinished(int? exitCode, string? faultKind) { }
```

**Should the member carry `runId` explicitly? No — and the first draft was wrong to say yes.**
The concern is real: `RunEventStream` derives it as `Path.GetFileName(directory)`
(`RunEventStream.cs:74`), an implicit contract ("the directory's name IS the run id") that breaks
**silently** — pass a differently shaped directory and every row's `runId` is wrong with nothing
failing. But the fix is one line and no seam change:

```csharp
// RunCommand.BuildObserverChain already takes runId (RunCommand.cs:2352) and already constructs this:
var eventsProjection = new RunEventStream(inner, logsRoot, runId);
```

A constructor parameter fixes the derivation with no interface member, no forwarding hazard, and
nothing for a decorator to swallow. The first draft's three reasons all collapse against it, including
the one it weighted highest: a layer-3 webhook projection is *also* constructed by
`BuildObserverChain`, so it takes `runId` the same way. **Making an unbuilt v2 bet the justification
for the most expensive part of a v1 contract change was speculative abstraction**, and the composition
root already holds the value. This was also `run-started`'s last surviving unique argument (§1a).

`_runId` stops being `readonly`-and-derived and becomes `readonly`-and-passed. `runId` still appears on
every row; only the seam is spared.

**Fields that were proposed for `RunStarting` and are moot now it is rejected** — kept because two of
them would be proposed again for any future run-scoped event:

| Rejected | Why |
|---|---|
| `waveCount` | No wave kind is emitted yet (#595 defers `wave_gate`). A header field pointing at rows that do not exist is a promise, not data. Add it with the wave kinds. |
| `maxParallelism`, `harnessVersion`, `host`, `os`, `cpuCount`, `totalMemoryBytes` | All seven `RunEnvironment` members are already journaled at run start and reach the corpus. None is something a supervisor *acts on* mid-run. YAGNI. |
| `resumed` (bool) | `alreadyCompleteCount` is strictly more useful and needs no definition of "resumed". |
| The `PlanDefinition` itself | Hands an observer the whole plan to answer two integer questions. The interface's own convention (`AttemptModelResolved`, `AttemptRouteResolved`, `VerifierAdvisoryFound`) is primitives beside `TaskNode`. |
| A purpose-built `RunStartInfo` record | Three parameters at one call site. A new public type to avoid one named-argument call is speculative abstraction. **Mitigation for the two adjacent `int`s** — the SSOT's own "NAMED, never positional" rule (§ the `RecordSettleWithAttempt` note) applies: the single call site binds by name, and the test fixture must use **asymmetric** counts (e.g. 12 and 3, never 1 and 1) so a slip is visible rather than green. |

**Fields deliberately NOT on `RunFinished`:**

| Rejected | Why |
|---|---|
| `RunReport` | Cannot be constructed on the fault path — `report` is unassigned if `ExecuteAsync` threw — so a `RunFinished(RunReport)` cannot fire where it is needed most. It also does not contain the terminal-gate verdict, which lives in a `bool?` local. |
| A run-outcome token set (`"green"`, `"needs-human"`, …) | Would be a second vocabulary that must stay 1:1 with `ExitCodes` forever and lies the moment it drifts. `ExitCodes` *is* the harness's terminal vocabulary (SSOT §7). The gloss problem a bare integer creates is solved by restating the code table in §8.1, where the stream's reader is reading. |
| `succeeded` / `passed` bool | `exitCode == 0`. |
| Task counts, cost totals | Derivable from the rows already in the file; the run's total cost is in `run.json`. |
| The exception message | Layer 3 will POST it. See `faultKind` above. |

### 1c. Where `RunFinished` is raised — the real question

**`RunFinished` is the only member raised, and `runId` reaches the writer through the composition
root** (`BuildObserverChain` → `new RunEventStream(inner, logsRoot, runId)`), not through the seam.

**`RunFinished`: a NEW `finally` that brackets the DAG *and* the terminal phase.**

The first draft said "reuse the existing `finally` that calls `TrySettleFinalSitesAfterFault`", and
that was **wrong**: `ExecuteAsync` is invoked at `RunCommand.cs:525` and `:534`, while the try holding
`finalSitesSettled` does not open until `:556`. There is no `catch` between them and the outer
log-server `try` at `:464`. **An unhandled throw out of the Scheduler — the largest fault surface in
the process — would have unwound straight past it and `RunFinished` would never have fired**, on
exactly the path the member exists for.

The correction, and it is small. Hoist the chain variable to nullable and open the bracket *before*
the `if (live)` branch:

```csharp
OnTheFlyDiagramObserver? diagramObserver = null;   // was: assigned in both branches
int? resolvedExitCode = null;
string? faultKind = null;
try
{
    if (live) { … diagramObserver = BuildObserverChain(…); (report, scheduler) = await ExecuteAsync(…); }
    else      { … }

    bool finalSitesSettled = false;                 // the #333 block below is UNCHANGED
    try { … } finally { … }
}
catch (Exception ex)
{
    faultKind = ex.GetType().Name;   // TYPE ONLY — never the message (layer 3 posts this)
    throw;                           // bare rethrow: stack, verdict and exit code untouched
}
finally
{
    diagramObserver?.RunFinished(resolvedExitCode, faultKind);
}
```

Three properties this shape buys, each deliberate:

- **`diagramObserver?.`** — if the chain was never built (a throw in `BuildObserverChain` or
  `new LiveRunObserver`), nothing is raised. Correct: there is no observer, and no `events.jsonl` for
  the row to land in.
- **The #333 block is left exactly as it is**, nested inside. Widening *it* would newly fire
  `TrySettleFinalSitesAfterFault` on a mid-DAG fault — a real behavior change, arguably an improvement,
  but not this change's to make.
- **`await using var liveObserver` keeps its scope** inside the `if (live)` block, so the Spectre live
  region is still disposed at that brace and `RunFinished` still fires after it. No disposal ordering
  moves. (That the inner renderer is disposed by then is a hazard in its own right — see below.)

With that bracket, `RunFinished` fires on:

| Exit path | What fires | `exitCode` | `faultKind` |
|---|---|---|---|
| Normal green return | `finally` | `0`, or `5` (`ProceededUnreviewed`) | null |
| needs-human halt (a halt MESSAGE, not a process exit) | `finally` | `2`, or `4` (`EscalationsPending`) | null |
| Wave halt / definition-drift halt / merge-halt | `finally` | `2` | null |
| Aborted report (#150 infra fault, converted not thrown) | `finally` | `1` | null |
| Cancellation converted by the Scheduler (`RunReport.Cancelled`) | `finally` | `3` | null |
| **Terminal-gate FAILURE early return** | `finally` | `2` | null |
| Cancellation *during* the terminal gate (`OperationCanceledException` out of `PlanGuardrailPhase.EvaluateAsync`) | `finally` | **null** | `"OperationCanceledException"` |
| **An unhandled throw out of `ExecuteAsync` — the Scheduler, the DAG, the whole run body** | `finally` | **null** | the type name |
| A throw from `BuildObserverChain` or `new LiveRunObserver` | nothing | — | — (no observer exists) |
| Any other unhandled throw after the run body | `finally` | **null** | the type name |
| **Resume of an already-complete run** | `finally` | `0` | null — the terminal gate re-fires with no attempt burned; this is the normal path and needs no special case |

Note the terminal-gate-failure row: `Finish` returns `Success` for an `AllSucceeded` run and the
early return then overrides it to `TaskFailed`. So **`RunFinished` must not read `Finish`'s return
value** — it must read a local set at each decision point. Concretely:

```csharp
        // inside the #333 block, unchanged except for the two assignments:
        if (report.AllSucceeded && planGuardrailsPassed is false)
        {
            PrintTerminalGateFailure(probe.Plan.PlanDirectory, io);
            resolvedExitCode = ExitCodes.TaskFailed;   // <- NOT Finish's return value
            return ExitCodes.TaskFailed;
        }
        // …
        resolvedExitCode = exitCode;
        return exitCode;
```

Verified: those are the **only two `return` statements** in that block (`RunCommand.cs:556-715`), so
the enumeration is complete. Every `resolvedExitCode` assignment sits immediately above its own
`return`, so the two cannot drift. The `catch`/`throw;` exists only to name the fault; a bare `throw;`
changes nothing about propagation.

`Finish` itself is broadly guarded (`WriteDurableFinalSite` and `HasUnresolvedEscalation` swallow IO
faults, `IngestRunTelemetry` is catch-all) but not *provably* total — its console writes could throw.
If it does, the bracket reports `exitCode: null` plus the fault kind, which is the honest answer.

**Why not before the terminal plan-guardrail phase?** Because the terminal gate is part of the run's
verdict — a run whose DAG drained green and whose `<plan>/guardrails/` gate then failed exits `2`,
and delivery happens *after* the gate. A `run-finished` raised before it would announce a verdict the
run had not reached, which is invariant #5 in the smallest possible form.

**Why not a second `run-finished` for the DAG?** Because that is what the DAG's own last
`task-settled` is, and a second run-scoped terminal event would make "which one means done?" a
question a consumer has to answer.

### 1d. The pre-observer window — declared, not fixed

Six halts return before the observer chain exists, so they write no `events.jsonl` at all:

1. `PlanProbe` validation errors → `1`
2. an unparseable `--autonomy` value → `1`
3. the Windows MAX_PATH worktree preflight → `1`
4. **the plan-level Full Flight Checks failing** → `2`
5. a declined interactive definition-drift confirm → `2`
6. a declined interactive wave-drift confirm → `2`

**Not fixed here, and the reason is structural, not laziness.** The chain cannot move earlier: the
interactive confirms cannot run inside the Spectre live region (`#145` Bug 1 — a console write into an
active `Live` region corrupts the table), and the Full Flight Checks phase writes plain console lines
for the same reason. The observer chain is built where it is *because* those two must precede it.
Constructing a partial chain early (just `RunEventStream` over `IRunObserver.Null`) would give
`events.jsonl` two writers — invariant #2.

So it is declared, in the SSOT, with the covering mechanism named: the process exit code, and the
`halt` section `run.json` grew for #432. **A consumer must read "no `events.jsonl`" as "the run has
not reached the DAG", never as "no run".** That sentence goes in §8.1 verbatim.

Note what this does *not* require: the transport needs no "not yet" state, because
`LogServer.TryStart` runs at `RunCommand.cs:461` — after every one of the six. There is no interval in
which a subscriber is attached and the run is still pre-DAG, which is why the first draft's proposed
`/events` change was solving a window that does not exist.

**This is the same structural gap as #572,** one surface over: "the web log site shows only `pending`
during the pre-DAG phase, though `run.json` already carries the state." Both are consequences of the
observer chain being built after the pre-DAG phases. If #572 is ever fixed by moving or splitting the
chain, this window closes for free — which is an argument for solving it there rather than here.

### 1e. The decorator-swallow hazard, and the assertions required

Chain order, outermost first (`RunCommand.BuildObserverChain`, `RunCommand.cs:2349`):

```
OnTheFlyDiagramObserver → OnTheFlyLogSiteObserver → ObserverProjection → RunEventStream → inner (live table | console)
```

Both new members have default `{ }` bodies, so adding them compiles everywhere and **17 of the 22
test doubles never notice**. But if `OnTheFlyDiagramObserver` or `OnTheFlyLogSiteObserver` fails to
declare them, the call resolves to the interface's empty body **at the outermost decorator** and never
reaches `RunEventStream` at all. `events.jsonl` gets nothing, in every mode, and nothing in the build
can see it. That is plan 34 §3's whole subject.

**The existing guard does not cover the projections.** `WaveGateForwardingTests` and
`AttemptModelForwardingTests` both sweep only `typeof(LiveRunObserver).Assembly` / `typeof(ConsoleRunObserver).Assembly`
— the **CLI** assembly. `RunEventStream` and `ObserverProjection` live in **Core** and are covered by
neither. The reflection guard that exists protects the two decorators that merely re-render, and not
the two that are the point.

**Required assertions, in the order they retire risk:**

1. **One exhaustive, two-assembly meta-test replaces the per-member pattern.** Enumerate
   `typeof(IRunObserver).GetMethods()` and assert that **every** transparent decorator in **both**
   `Guardrails.Core` and `Guardrails.Cli` declares **every** member — reusing
   `AttemptModelForwardingTests.Declares` (which correctly treats an inherited default as *not*
   declared, and catches explicit interface implementations). Verified: all four decorators declare
   all 20 current members today, so this test is **green on arrival** and fails only on the new
   ones — which is exactly the signal wanted. Non-vacuity floor, copying the existing precedent:
   `Assert.Contains` each of the four known decorator types, and assert the member list is non-empty.
   This is the durable fix; it retires the class rather than patching instance #6.
2. **A behavioral forward test per new member**, on the outermost decorator, into a recording inner —
   the shape `AttemptCompletionForwardingTests` already uses. Reflection proves a method *exists*;
   only a call proves the arguments arrive unmangled. Assert the whole payload, not a count.
3. **A wiring test through the real composition root** — call `RunCommand.BuildObserverChain`, raise
   both members on the returned chain, and assert the rows land in `events.jsonl` and
   `observer.jsonl`. The chain is public precisely so a test can do this
   (`RunCommandObserverWiringTests` already does it for the shipped kinds). **This is the assertion
   that matters most**: a unit test against `RunEventStream` in isolation proves the projection works
   while the composed chain swallows the event — the #382 fake-masked-green shape.
4. **A `run-finished`-fires-on-every-exit-path test matrix** — at minimum: green, needs-human,
   terminal-gate-failure, and a throw from the terminal-gate phase (assert `exitCode: null` and the
   `faultKind` token, and that the exception still propagates).
5. **A negative assertion that `faultKind` never carries a message** — construct a fault whose message
   contains a recognizable secret-shaped string and assert the row does not contain it. Layer 3 makes
   this a security property, not a style preference.

### 1f. Should `ObserverProjection` record them? Does `attach` need them?

**`ObserverProjection`: yes, record both. `guardrails attach`: no change.**

`attach` does not *need* them — it decides whether a run is still going by reading the journal
(every task settled to a terminal status), never from an event, and `AttachCommand.Dispatch`'s
`default:` case (`AttachCommand.cs:311-315`) ignores an unknown `member` by design, so adding rows
breaks no replay and needs no new `case`.

`ObserverProjection` records them anyway because its documented contract is *"record every observed
call… reading the file back reproduces the exact call sequence, in order"*. A decorator that silently
drops a member makes its own doc false from the day it ships — which is plan 34 §3's rule, stated by
`ObserverProjection`'s own class comment. One extra line per run is not a cost worth reasoning about.

**A follow-up, deliberately not taken here:** `attach`'s journal-derived termination check returns as
soon as every task is terminal — i.e. *before* the terminal `<plan>/guardrails/` gate runs, which has
been measured at 10m44s. `run-finished` is a strictly better termination signal. Rewiring `attach`
onto it is a separate change with its own test surface (and the terminal gate is not on the
`IRunObserver` seam at all — `RunCommand` calls `diagramObserver.PlanGuardrailsStarting()` on the
*concrete* type). **Do not fold it in.** File it.

---

## Decision 2 — the attempt row: pass the journal's `AttemptRecord` on the seam

### 2a. The mechanism

**Change `AttemptFinished` to carry `Journal.AttemptRecord`:**

```csharp
// before
void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome) { }

// after
void AttemptFinished(TaskNode task, Journal.AttemptRecord record) { }
```

**Why this is the right shape, and cheaper than it looks.** Every one of the 11 existing raise sites
already reads `record.Outcome` off a local `AttemptRecord` it just built — the call is literally
`_observer.AttemptFinished(task, attemptNumber, record.Outcome)`. The edit at each site is
`_observer.AttemptFinished(task, record)`. The payload is not something new to assemble; it is the
object already in hand, already being partially destructured.

And `AttemptRecord` (`src/Guardrails.Core/Journal/JournalModel.cs:433`) is already the per-attempt
SSOT that the telemetry ETL reads:

| `TelemetryRow` property | `AttemptRecord` source | ETL line (`TelemetryIngest.cs`) |
|---|---|---|
| `Outcome` | `Outcome` (via `AttemptOutcomeToken`) | 107 |
| `StartedAt` / `EndedAt` | `StartedAt` / `EndedAt` | 105 / 106 |
| `CostUsd` | `CostUsd` | 114 |
| `Turns` | `Turns` | 128 |
| `InputTokens` / `OutputTokens` | `Usage?.InputTokens` / `.OutputTokens` | 115 / 116 |
| `ActionMs` / `GuardrailMs` | `Segments?.ActionMs` / `.GuardrailMs` | 129 / 130 |
| `Model` | `Provenance?.Model` | 108 |
| `Runner` | `Provenance?.Runner` | 109 |
| `Kind` | `Provenance?.Kind` | 110 |
| `Tier` | `Provenance?.Tier` | 111 |
| `TierSource` | `Provenance?.TierSource` | 112 |
| `Effort` | `Provenance?.Effort` | 113 |
| `ModelDigest` | `Provenance?.ModelDigest` | 126 |
| `RouteWarm` | `Provenance?.RouteWarm` | 127 |

That table is the whole argument. Handing observers the record makes the event **literally "the
journal's attempt record, emitted live"** — which is #585's own requirement in its own words: *"an
event and its eventual telemetry row should agree field-for-field on everything they share, and the
telemetry writer should be able to consume the event stream rather than re-deriving the same facts."*
The alignment stops being a claim someone has to maintain and becomes a property of the type. **Every
future `TelemetryRow` field sourced from an attempt arrives at the stream with no further contract
change** — which is the property #585 layer 3 depends on ("build them once the vocabulary is settled").

Dropping the `int attempt` parameter is safe: `record.Attempt` replaces it, and it is the same value
the journal writes, so a record whose `Attempt` were wrong would already be a journal bug.

**Measured compile surface** — every figure below was counted, and the two the first draft got wrong
are corrected. It is far smaller than the "~20 test doubles" fear, because `AttemptFinished` is a
default-bodied member that most doubles never override:

| Count | What | How it was counted |
|---|---|---|
| 1 | interface declaration | `IRunObserver.cs:86` |
| 11 | raise sites | `grep -n "\.AttemptFinished(" src/` → `AttemptJournaler.cs` 162, 382, 455, 534, 611, 668, 734, 802, 852; `TaskExecutor.cs` 587, 622 (both `RevalidateAsync`) |
| +1 | NEW raise site (Bug A) | `Scheduler.RecordSucceededSettle` |
| 6 | declarations in `src/` | `RunEventStream`, `ObserverProjection`, `LiveRunObserver`, `ConsoleRunObserver`, `OnTheFlyLogSiteObserver`, `OnTheFlyDiagramObserver` |
| 1 | replay dispatcher | `AttachCommand.cs:267` |
| 5 | test doubles that override it | `grep -rl "void AttemptFinished" tests/` — of ~36 `: IRunObserver` declarations under `tests/`; the rest inherit the default and are untouched |
| 0 | stale `samples/` fixtures | **Corrected.** The first draft claimed five, under three plan folders. The repo has four `samples/` directories, all under `model-tiering-stage-3`, and none mentions `AttemptFinished`, `IRunObserver` or `attempt-finished`. Two of the three folders cited have no `samples/` at all. No action. |

### 2b. Rejected alternatives

| Alternative | Rejected because |
|---|---|
| **Accumulate in the projection** (`RunEventStream` remembers `AttemptModelResolved` / `AttemptRouteResolved` per (task, attempt)) | Cannot carry cost or turns at all — neither is announced to any observer, ever, so it does not solve the stated problem. And for the fields it *could* carry it is actively wrong: it makes the projection stateful (a dictionary that must be thread-safe across parallel workers and pruned, or it grows for the life of a waved run), and it makes the row's model a value the *projection derived*, while `run.json`'s is the value the harness folded once. That is a second owner of the same fact — the D22a discipline `IRunObserver` spells out on `AttemptModelResolved`: *"A surface that recomputed it would be a second owner of the rule and would drift from the `run.json` it is supposed to be showing."* |
| **Read the journal from the projection** | Inverts the layering (a decorator reaching into the journal); contends with the single writer (invariant #2); and is **racy by construction** — in worktree mode the record is not written until the deferred settle, so the read would find nothing exactly on the default path. It also reintroduces the filesystem read #585 was filed to remove, one directory over. |
| **A purpose-built `AttemptCompletion` record** (a curated subset) | A **third** shape beside `AttemptRecord` and `TelemetryRow`, so every future field needs adding in three places and can disagree in two. #585's "do not invent a second vocabulary" applies with full force. It also buys nothing the record does not: an observer that ignores `FailedGuardrails` is not harmed by its presence. |
| **Add a parallel member** (`AttemptCompleted(task, record)`, leaving `AttemptFinished` alone) | Two members for one event is a fork with a compatibility story attached. There are no external `IRunObserver` implementors to protect — the interface is consumed inside this repo only. |
| **Widen the existing signature additively** (`AttemptFinished(task, attempt, outcome, AttemptRecord? record)`) | Two spellings of the same three facts on one call, with a nullable that would be non-null at every site. Strictly worse than passing the record. |

### 2c. Which fields go on the row

The seam carries the record; the **row** prints what a supervising agent decides with. Each field
names its `TelemetryRow` counterpart verbatim (`camelCase` on the wire, per `LineOptions`):

| Row field | `TelemetryRow` property | Source on the record | The decision it serves |
|---|---|---|---|
| `outcome` | `Outcome` | `Outcome` (via `JournalJson.OutcomeToken`) | already shipped — wait out `max-turns` vs stop and fix a guardrail |
| `costUsd` | `CostUsd` | `CostUsd` | "am I burning money in a retry loop?" — the cost-cap intervention |
| `turns` | `Turns` | `Turns` | with `max-turns`, whether the auto-escalated budget can plausibly help |
| `model` | `Model` | `Provenance?.Model` | "is the tier wrong?" → escalate or pin |
| `tier` | `Tier` | `Provenance?.Tier` | the same decision one rung up |
| `runner` | `Runner` | `Provenance?.Runner` | which provider is misbehaving (a live concern once local inference lands, #570) |
| `startedAt` | `StartedAt` | `StartedAt` | required on the record, so never absent |
| `endedAt` | `EndedAt` | `EndedAt` | elapsed by subtraction — see the `elapsedSeconds` rejection |
| `needsHumanKind` | *(none — journal-owned)* | `NeedsHumanKind` | **answerable vs not** (#361 answer-injection, #387 picks) — #585's own test: two opposite responses turning on one field. No telemetry twin because telemetry does not need it; a field the journal owns and the corpus declines is not a fork. **But see the caveat immediately below — it is weaker than the first draft claimed.** |

**An attempt-level `needs-human` is NOT terminal for the task, and the row must not be read as if it
were.** `Scheduler.OnSettledAsync` (`Scheduler.cs:3990-4021`) classifies a needs-human settle and,
when `RerunForBestGuessInjectionIfPendingAsync` comes back green, **adopts** the re-driven attempt
(#550). So a perfectly ordinary stream reads:

```
attempt-finished  outcome=needs-human  needsHumanKind=…
attempt-finished  outcome=succeeded
task-settled      outcome=succeeded
```

A supervisor that acts on the first row races the harness's own re-drive — and #361's answer-injection
*is* the mechanism that just superseded it. The first draft sold this field as "the single most
consequential branch"; it is not, unqualified. It is the right field to *read*, and `task-settled` is
the row to *act* on. §8.1 says so explicitly, because a consumer cannot infer it.

**Rejected from the row** (all still reachable at the seam, at zero contract cost, which is the point):

| Rejected | Why |
|---|---|
| **`elapsedSeconds`** | Appears in #585's example row but has **no `TelemetryRow` counterpart**. Shipping it would be the forked vocabulary #585 forbids two paragraphs later. `endedAt - startedAt` is the same information in the corpus's own terms. |
| **`attemptsMax`** | Not in scope at the seam — the retry budget exists only as a local in `TaskExecutor` (`1 + (task.Retries ?? config.DefaultRetries)`) and reaches observers **only** via `AttemptStarting(task, attempt, budget)`. It is already on the shipped `attempt-started` row as `budget`. Threading it into 11 journaller methods to repeat a value the consumer received one row earlier is not worth a contract change; a consumer correlates on `(taskId, attempt)`. |
| `actionMs` / `guardrailMs` | Answer "is the *agent* slow or are the *guardrails* slow?" — a real question, but a post-mortem one, already in `run.json` and the corpus. No live intervention turns on it that `endedAt - startedAt` does not. Free to add later. |
| `inputTokens` / `outputTokens` | A supervisor acts on cost, not raw volume. They become interesting when a **costless local provider** reports volume and no money (#570 / the Mac Studio arc) — add them when that runner ships and there is a decision attached. |
| `requestedModel`, `tierSource`, `effort`, `modelDigest`, `routeWarm`, `kind` | Corpus/post-mortem dimensions. Nothing mid-run branches on them. |
| `actionExitCode` | `outcome` already says how the attempt ended. |
| `failedGuardrails` | The already-shipped `guardrail-finished` kind carries each failure with its `detail` as it happens — earlier and per-guardrail, which is strictly better. Repeating the set on the settle row would be two sources for one fact. |

**Honest consequence to expect.** Four of `FailedAttempt`'s ten call sites — all inside
`ValidateFragmentForSettle` — pass no `provenance`, `turns` or `segments`, so on those paths the
record itself has nulls and the row will show `model`/`tier`/`runner`/`turns` absent. That is the
journal's existing gap becoming visible in the stream. **Do not paper over it in the projection** —
that would be the projection deriving facts again. Either fix it on the record (a separate change) or
leave it; the stream reporting exactly what the journal holds is the property worth keeping.

### 2d. Model/tier via accumulation even if cost comes via the seam?

**No, and the split would be incoherent.** Two fields of one row would arrive by two different
provenance paths with two different silent-failure modes: a missed decorator forward nulls the model,
a missing record field nulls the cost. A `null` in the row would then mean different things depending
on which field it was, and no consumer could tell "the harness never knew" from "an event was
swallowed on the way here". One source per row. The record.

---

### 2e. `seq` — the ordering field the first draft was missing

`RunEventStream` builds `At = DateTimeOffset.UtcNow` **outside** `lock (_gate)` at every call site
(e.g. `RunEventStream.cs:83-87`); only the append is serialized. Under M4 parallel workers two rows
can therefore carry an `at` order that disagrees with file order — and on Windows
`DateTimeOffset.UtcNow` resolves to the ~15.6 ms system timer tick, so many concurrent rows carry an
**identical** `at`. The first draft then told layer 3 to key retry and ordering on `(runId, at)`: a
key that is neither unique nor monotonic.

**Fix, and it belongs in this change** because #585 says layer 3 must not be built until the
vocabulary is settled:

- Add `seq` — a monotonic, 1-based, per-**process** counter assigned **inside** `_gate`, immediately
  before the append.
- Move the `At` timestamp inside the lock too, so `at` and `seq` and file order all agree.
- `seq` restarts at 1 for a resume (a new process appending to the same file). It orders rows *within
  a bracket*, and `run-finished` is what closes one. Making it
  durable across processes would mean reading the file back to find the high-water mark — a reader in
  the writer, for an ordering a bracket already gives.

`seq` and `at` and `kind` are the three fields that exist because a live stream needs them and a
settled telemetry row does not. That is stated in §8.1, so nobody looks for a `TelemetryRow` twin.

---

## Seams and contracts touched

| Seam | Change |
|---|---|
| `IRunObserver` | + `RunFinished(int?, string?)`, `AttemptFinished` payload → `Journal.AttemptRecord` |
| `RunEventStream` | + `runId` ctor parameter; 2 new emitting members; `EventRow.TaskId` → **`required string?`** (a run-scoped row has no task); `seq` + `at` assigned inside `_gate`; new `attempt-finished` fields |
| `ObserverProjection` | 2 new recording members; `AttemptFinished` line flattens the record's fields |
| `OnTheFlyDiagramObserver`, `OnTheFlyLogSiteObserver` | 2 new explicit forwards each (the swallow hazard) |
| `LiveRunObserver`, `ConsoleRunObserver` | `AttemptFinished` signature only. **They must NOT declare `RunFinished`** — see the disposal hazard below. |
| `AttemptJournaler`, `TaskExecutor` | 11 mechanical raise-site edits |
| `Scheduler.RecordSucceededSettle` | **+1 new raise site** — Bug A |
| `AttachCommand.Dispatch` | `AttemptFinished` decode rebuilds an `AttemptRecord` from the flattened line. No new `case` for the run members (`default:` ignores them). |
| `LogServer.WriteEventsStream` | Bug B′ — one final read on shutdown, so the terminal row is not lost to the poll interval. The empty-200 for a missing file is UNCHANGED. |
| `02-schemas-and-contracts.md` | **New §8.1 and §8.2** (currently absent), §8 retitled |

### The renderer disposal hazard — why `LiveRunObserver` must NOT implement `RunFinished`

`await using var liveObserver = new LiveRunObserver(…)` is scoped to the `if (live)` block
(`RunCommand.cs:519`), so the Spectre live region is **already disposed** by the time control reaches
the terminal-gate phase — let alone the `finally` where `RunFinished` fires. The chain
(`diagramObserver`) still holds a reference to that disposed renderer, and the call is forwarded all
the way down to it.

Today this is harmless precisely because `LiveRunObserver` inherits the interface's empty default.
That makes "the renderers keep the default" a **constraint, not a style preference**: a later change
that implements `RunFinished` on `LiveRunObserver` would write into a disposed `AnsiConsole.Live`
region, in the live mode every attended operator uses. The one-line comment task 6 adds must say *why*,
not just *that*. (The precedent is already in the same `finally`: `TrySettleFinalSitesAfterFault`
calls into `diagramObserver` after disposal too — safe only because the diagram and log-site observers
are file writers.)

`ConsoleRunObserver` has no such hazard, but it also has nothing to add: `RunCommand` already prints
the run's own start and end lines. It keeps the default for the ordinary reason.

### Bug A's exact call site

In `Scheduler.RecordSucceededSettle` (`src/Guardrails.Core/Execution/Scheduler.cs:4471`), immediately
after the `record` is built and journaled:

```csharp
_journal.RecordSettleWithAttempt(
    task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, definitionHash, bucket: pending.Bucket);
_observer.AttemptFinished(task, record);   // #595: the DEFAULT (worktree) mode's only route to this event
```

Ordering is already correct: `OnSettledAsync` → `SettleGreenIfWorktreeAsync` → `SettleAsync` →
`RecordSucceededSettle` runs *before* `_observer.TaskFinished` at `Scheduler.cs:4091`, so the stream
reads `attempt-finished` then `task-settled`.

The `PendingAttempt is null` early-return branch (the fake-provider path) raises **nothing** — there
is no attempt record to report, and inventing one would be a fabricated fact. **This is a test-design
trap worth naming:** an integration test that uses the fake worktree provider takes that branch and
will *not* exercise the new event, so a test proving Bug A is fixed must go through
`ValidateFragmentForSettle` and the real settle. This is the #382 fake-masked-green shape exactly.

**Semantic note to document, because it looks like a bug and is not:** in worktree mode the event
now fires at *settle* time (under the integration lock), later than the attempt physically ended. That
is correct — the outcome genuinely is not known until the integration commit lands, since a non-FF
union rollback can still turn a green-looking attempt into needs-human. `at` is when it was observed;
`startedAt`/`endedAt` come from the record. The consumer gets both truths.

---

## Schema changes — exact `02-schemas-and-contracts.md` edits

### Edit 1 — retitle §8 (line 3574)

```diff
-## 8. Per-attempt log layout
+## 8. Per-attempt log layout, and the run's own streams
```

Reason: §8 already documents run-scoped content (the gate captures); §8.1/§8.2 make that explicit
rather than filing a run-scoped stream under a per-attempt heading. The number does not change, so
every existing "SSOT §8" cross-reference still resolves.

### Edit 2 — insert §8.1 and §8.2 at the end of §8, immediately before `## 9. Prompt runners` (line 3775)

> ### 8.1 The run event stream (`logs/<runId>/events.jsonl`) — issues #585 / #595
>
> One JSON object per line, appended as it happens, UTF-8 without BOM, `\n`-terminated, flushed per
> row. Written by exactly one component **per process** — `RunEventStream`, a decorator on the
> `IRunObserver` seam (plan 34 §5) — and served live over `GET /events` (§12.2). **Semantic and
> low-frequency: it is the stream a supervising AGENT filters on FIELDS.** Its render-fidelity sibling
> is §8.2.
>
> **"Per process" is not a hedge.** A resume reuses the run id (§7) and appends to the SAME file, and
> nothing locks a plan folder against two concurrent `guardrails run` invocations — both would resolve
> the same run id and both would append here. Single-writer therefore holds *within* a process and not
> across them.
>
> **A consumer filters on fields, never on a `kind` allowlist.** An unrecognized `kind` must remain a
> visible row: that property is the whole reason this file exists (#585 measured three hand-written
> stdout greps, each of which failed by producing silence, which is also what a healthy quiet run
> produces).
>
> **Fields absent versus null.** A row carries only the fields its `kind` defines; inapplicable fields
> are OMITTED, never written as `null`. So `field in row` is a straight answer, and a `null` never
> appears. A field the harness genuinely did not know (an unreported cost) is likewise omitted.
>
> **On every row, without exception.**
>
> | Field | Meaning |
> |---|---|
> | `kind` | the event discriminator, kebab-case (table below) |
> | `seq` | a monotonic, 1-based counter within this PROCESS's bracket, assigned under the writer's append lock. **`seq`, not `at`, is the ordering key.** It restarts at 1 for a resume, which appends a fresh bracket to the same file. |
> | `at` | when the row was WRITTEN (ISO-8601 UTC), stamped under the same lock. Not a domain timestamp — `startedAt`/`endedAt` are those. Its resolution is the platform clock tick (~15.6 ms on Windows), so concurrent rows can share an `at`; that is why `seq` exists. |
> | `runId` | the run's id, passed to the writer by the composition root. |
>
> **On every TASK-scoped row** (that is, every kind except `run-finished`):
>
> | Field | Meaning |
> |---|---|
> | `taskId` | the task's folder name |
>
> **The kinds.**
>
> | `kind` | Raised from | Additional fields |
> |---|---|---|
> | `task-started` | `TaskStarting` | — |
> | `attempt-started` | `AttemptStarting` | `attempt`, `budget` |
> | `guardrail-finished` | `GuardrailFinished` | `guardrail`, `passed`, and on failure `detail` |
> | `attempt-finished` | `AttemptFinished` | `attempt`, `outcome`, `costUsd`, `turns`, `model`, `tier`, `runner`, `startedAt`, `endedAt`, `needsHumanKind` |
> | `task-settled` | `TaskFinished` | `outcome`, `detail` |
> | `run-finished` | `IRunObserver.RunFinished` | `exitCode`, `faultKind` — **the only kind with no `taskId`** |
>
> **One vocabulary, not two (#585).** `outcome` on `attempt-finished` is the wire token of
> `Journal.AttemptOutcome` (`JournalJson.OutcomeToken`) — the same token §7 journals and §15.2's
> `TelemetryRow.Outcome` carries. `outcome` on `task-settled` is the `Execution.TaskOutcome` token,
> spelled to match `OutcomeToken` on the members the two enums share. `needsHumanKind` is the §7
> `NeedsHumanKinds` token. Every field on `attempt-finished` other than `needsHumanKind` names a
> `TelemetryRow` property verbatim: `costUsd`→`CostUsd`, `turns`→`Turns`, `model`→`Model`,
> `tier`→`Tier`, `runner`→`Runner`, `startedAt`→`StartedAt`, `endedAt`→`EndedAt`, `outcome`→`Outcome`.
> `needsHumanKind` is journal-owned and has no telemetry counterpart by design.
>
> **`attempt-finished` is the journal's `AttemptRecord`, emitted live.** `IRunObserver.AttemptFinished`
> carries the whole `Journal.AttemptRecord` (§7), so the row is a projection of the record the journal
> writes and §15.3's ETL reads — not a parallel assembly of the same facts. A field the record does not
> populate on a given path (four of `FailedAttempt`'s call sites pass no provenance) is omitted from
> the row; the stream reports exactly what the journal holds, and never derives a fact of its own.
>
> **`exitCode` is the §7 `ExitCodes` vocabulary,** not a token set of this stream's own:
> `0` green · `1` harness/validation error · `2` a task needs a human (or a gate failed) · `3`
> cancelled · `4` escalations pending · `5` drained green but proceeded through unreviewed wave(s).
> It is **omitted** when the run is unwinding on an unhandled fault and no code was determined — in
> which case `faultKind` carries the exception's TYPE NAME. `faultKind` never carries an exception
> MESSAGE: #585 layer 3 (`--on-event <url>`) posts these rows to an operator-supplied URL, and a
> message is the one value on the row that can carry a path, a token, or a fragment of source.
> (The code table above is restated here for the reader's convenience and is pinned by a test that
> reflects over `ExitCodes` — a hand-copied gloss that nothing checks is the same drift risk this
> design cites when it rejects a parallel token set.)
>
> **Where the stream begins, and what its absence means.** The first row is a `task-started`. There
> is deliberately NO run-opening event: one was designed and rejected (design of record
> `docs/plans/595-event-vocabulary-contract.md` §1a) because its payload could not be stated
> accurately at run start and its name would have implied a bracket it did not deliver. Six halts
> return BEFORE the observer chain exists and therefore write no `events.jsonl` at all: plan
> validation errors; an unparseable `--autonomy` value; the Windows MAX_PATH worktree preflight
> (§3.2); the plan-level Full Flight Checks failing (§7 `planPreflights`); and a declined interactive
> definition-drift (§7.2) or wave-drift (§14.6) confirm. This is structural, not incidental: the
> interactive confirms and the Full Flight Checks phase both write plain console lines and so must
> precede the live region the chain is built around (§12.1). **A consumer must read "no
> `events.jsonl`" as "the run has not reached the DAG", never as "no run"** — and for those halts the
> covering record is the process exit code plus `run.json`'s `halt` section (§7). (All six were traced
> to their return statements in `RunCommand.RunAsync` when this section was written; an exhaustive
> list ages, so treat it as the halts known at that time rather than a closed set.)
>
> **A runId spans processes, so the file can hold more than one `run-finished`.** A resume reuses the
> run id and appends to the SAME `events.jsonl`. **Take the LAST `run-finished` as current**; rows
> after one belong to a later process (a resume, or — see above — a second concurrent run). A resume of an already-complete run re-fires the terminal gate with no
> attempt burned (§7) and emits its own bracket around zero `task-started` rows — which
> no `task-started` rows at all: every task was already green, so the terminal row is the only one.
> **A `run-finished` with no `task-started` before it in that process's tail is a completed resume,
> not a stalled run.**
>
> **`run-finished` is a durable FILE event first.** A `/events` subscriber can miss the terminal row:
> the run appends it and tears the log server down microseconds later. A client whose connection
> closes must RE-READ the file rather than assume it saw the end of the stream.
>
> **An attempt-level `needs-human` is not terminal for its task.** The harness may re-drive the attempt
> with an injected best guess (§7.1, #361/#550) and adopt a green result, so a `needs-human`
> `attempt-finished` can be followed by a `succeeded` one for the same task. Read `needsHumanKind` for
> context; act on `task-settled`.
>
> **What is NOT emitted yet,** deliberately, and will be added when a consumer decision needs it:
> `needs-human`, `task-blocked`, `wave-gate`, `merge`, and the plan/wave preflight phases (#595).
>
> ### 8.2 The observer projection (`logs/<runId>/observer.jsonl`) — issue #560
>
> The SECOND projection off the same seam: one JSON line per `IRunObserver` CALL, naming the member
> and flattening its arguments as camelCase fields, in order. **Render fidelity, not semantics** —
> `guardrails attach` (§12.2) replays it into a real `LiveRunObserver` in a second terminal rather
> than reimplementing the renderer, so it must carry every call including the live-only ones a
> filtered agent stream would starve. It is deliberately NOT the same file as §8.1: one stream
> serving both consumers serves each badly.
>
> Consequences a reader needs:
>
> - Both projections declare **every** member of `IRunObserver` explicitly, because a decorator that
>   leaves one to the interface's default body swallows that event silently in every mode (plan 34 §3).
> - **The replay's skip rule is wider than "unknown member", and that is a hazard, not just
>   forward-compatibility.** An unrecognized `member` is skipped — genuinely forward-compatible. But a
>   **known** member whose line is missing a field the replay requires raises `FormatException`, which
>   the replay also swallows and skips. So a SHAPE change to a member that this file's writer and the
>   replay disagree about produces a silently incomplete replay, not an error. Any change to a
>   projected member's fields must be covered by a writer→replay round-trip test.
> - **`observer.jsonl` and `events.jsonl` spell shared enums differently, on purpose.** This file
>   writes `outcome` as the enum's `ToString()` (`GuardrailFailed`), because the replay parses it back
>   with `Enum.Parse`; §8.1 writes the kebab wire token (`guardrail-failed`), because that is the
>   token §7 and §15.2 use. §8.1's "one vocabulary" rule governs the AGENT-facing stream; this file is
>   an internal round-trip format between two halves of one feature, and its spelling is an
>   implementation detail of that round trip. A reader comparing the two files will see the
>   difference; this paragraph is why.

### Edit 3 — two sentences in §12.2, where `GET /events` is described

Lands **with task 11**, not with task 2 — it describes behavior task 11 introduces, and a commit range
where the SSOT is ahead of the endpoint is the drift this doc exists to prevent.

> `GET /events` streams §8.1 over one connection: a late subscriber first receives every row already
> on disk, then subsequent rows as they are appended. A run that has written no row yet completes with
> an empty body — correct, because the server does not start until after every pre-DAG phase (§8.1),
> so an empty stream there means the file genuinely holds nothing. On shutdown the stream performs one
> final read before closing, so a run's terminal `run-finished` row is delivered rather than lost to
> the poll interval; delivery is still best-effort, and a client whose connection closes re-reads the
> file (§8.1).

---

## Devil's-advocate self-critique

### What the independent pass found that this section had missed

Recorded honestly, because "the author's own critique missed five things" is the most useful fact in
this document. Five were fixed (listed at the top). These are the ones that **survive as accepted
limitations**, and each is a place review may reasonably overrule me:

**A. Both of `run-started`'s payload fields are approximations.** `taskCount` undercounts a JIT-waved
plan; `alreadyCompleteCount` is read before the §7.2 drift rewind. **This finding decided the
feature**: the maintainer struck `run-started` on it (see the ruling at the top, and §1a). Recorded
here because the finding came from the adversarial pass, not from this document's own critique — the
author's case survived his own review and did not survive an independent one.

**B. `run-finished` is best-effort over the wire.** `_listener.Stop()` runs first in `DisposeAsync`, so
even with task 11's final drain a live subscriber can miss the terminal row. §8.1 tells consumers to
re-read the file. Guaranteeing delivery would mean the run waiting on its own HTTP clients.

**C. Nothing locks a plan folder.** Two concurrent `guardrails run` invocations resolve the same run
id and both append here; `File.AppendAllText` opens `FileShare.Read`, so the second can take an
`IOException` up through `RunEventStream.TaskStarting`. This predates the change; the change makes it
a *stated* contract, so §8.1 now scopes single-writer to "per process" rather than implying a
guarantee the writer does not hold. **Not fixed here** — a plan-folder lock is its own design with its
own resume and crash semantics.

**D. Bug A's residual.** The worktree `needs-human`-by-integration-gate settles still raise nothing,
because they journal no attempt record. Fixed at the journal or not at all (above).

### The strongest objections, and my responses

**The strongest counter-argument — SUSTAINED, and it removed half the design.** A consumer reads the
kind table, sees `run-started`, and writes "alert if no `run-started` within N seconds": a false alarm
on every run with a long Full Flight Checks phase, and a *missed* alarm on the six halts that write no
row at all. The name asserts a bracket it does not deliver. **That is the #595 defect shape — a signal
that looks like it certifies something it does not — reintroduced by the fix for it.**

The first draft's response was to constrain the name in three places (an XML "does NOT claim"
paragraph, an SSOT "where the stream begins" subsection, a third-state rule) and accept the residue.
That response was too clever: **a contract that needs three separate warnings not to be misread is
telling you the event is wrong, not that the documentation is thin.** Combined with finding A — the
counters were unreliable on exactly the plan shape the strongest argument leaned on — the honest call
is the one the maintainer made: ship `run-finished` alone. `task-started` already covers the liveness
gap that motivated the proposal, and the denominator this design could have supplied would have been
wrong when it mattered most.

**What this cost.** A late-attaching consumer gets no denominator from the stream, and must read the
plan folder for one. That is a real regression against #585's "remove the filesystem read" goal, and
it is the right trade: a wrong denominator delivered confidently is worse than an absent one.

**Second counter: decision 2 changes a shipped interface member for a benefit the row does not yet
use.** The seam will carry `Kind`, `Effort`, `ModelDigest`, `RouteWarm`, `FailedGuardrails`,
`ActionExitCode`, `Usage` — seven-plus fields the row deliberately drops. That is YAGNI violated at
the seam to satisfy YAGNI at the row.

**Response.** The asymmetry is deliberate and the direction is what matters: the *seam* is the
expensive thing to change (11 raise sites, 6 declarations, 5 doubles, a replay decoder) and the *row*
is a one-line edit in one file. Paying the expensive change once, generally, so that every subsequent
field is a row edit is the opposite of speculative — it is the smallest **total** change across the
work already scheduled, and #585 layer 3 explicitly requires the vocabulary to be settled before
webhooks are built on it. The alternative — a curated payload record — pays the expensive change again
every time the row grows. I would not accept this argument for a *new* type; I accept it for reusing a
type that already exists and is already the SSOT for exactly these facts.

**Third counter: pulling extra bugs in makes this three changes, and the brief said smallest.**

**Response, revised.** The adversarial pass sustained this against "Bug B" and I dropped it — the
empty-200 is deliberate, test-pinned, and the server does not start until after every pre-DAG phase,
so the window it covered does not exist. What replaced it (Bug B′, a three-line final drain) is genuinely load-bearing: without it the
terminal event never reaches the wire, which is the whole payoff.

Bug A stays, and is not optional: without it, decision 2 delivers cost and turns for the *minority*
execution path while the default path emits no `attempt-finished` at all — the change would ship
looking correct and be measurably false, which is precisely the failure class this repo keeps
re-finding. Both remain separately owned tasks so they can be reviewed and reverted independently.

**Fourth counter: `faultKind` requires a `catch`/`throw;` around the whole terminal block for one
diagnostic string.** True. Response: the fault path is thin (most infra faults are already converted
to an aborted report by #150 rather than thrown), but the residue is exactly the case where the
supervisor has *nothing else* — no task rows, no summary, no exit code. A bare `throw;` preserves the
stack, the verdict and the exit code, and the whole cost is three lines. If review objects, dropping
`faultKind` and keeping `exitCode: null` is a clean subset: the row still fires, and "finished with no
exit code" is still strictly better than silence.

**Fifth counter: `needsHumanKind` on the row has no `TelemetryRow` twin, so it is the fork this design
keeps forbidding.** Response: it is not a *new* vocabulary — it is the existing `NeedsHumanKinds`
token set the journal already writes and `observer.jsonl` already flattens. "Do not invent a second
vocabulary" forbids coining new tokens for facts that already have them; it does not require that the
event and the corpus carry identical field *sets*. Telemetry declining a field is not the event forking
one. That said, this is the one row field I hold least firmly — if review would rather keep the row
strictly a `TelemetryRow` subset, drop it and let a consumer read the kind off `task-settled`'s
`detail`. It costs the unattended-supervisor case something real, and I would rather pay elsewhere.

---

## Implementation handoff

**Superseded as the executable artifact** by `docs/plans/35-event-vocabulary.md`, which is the
plan-breakdown-ready form of this table. Kept here as the rationale of record — the plan states WHAT,
this states WHY, and where they disagree the plan is wrong.

One task per row. `filesTouched` cells are backticked and segment-resolvable against the real tree.

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/IRunObserver.cs` | Add `RunFinished(int? exitCode, string? faultKind)` with the XML doc above (including the "never the message" paragraph); change `AttemptFinished` to `(TaskNode, Journal.AttemptRecord)`. **No `runId` on the member** — it is a `RunEventStream` ctor parameter. Leave `NullObserver` alone. |
| 2 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/` | The exhaustive two-assembly decorator meta-test **plus** a behavioral forward test for `RunFinished` — written RED against task 1's interface, before task 3 declares anything. Subsume `WaveGateForwardingTests` and `AttemptModelForwardingTests`'s reflection halves rather than leaving three overlapping sweeps. |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/RunEventStream.cs` | A `runId` ctor parameter replacing the `Path.GetFileName` derivation; the `RunFinished` emitting member; `EventRow.TaskId` → **`required string?`**; the `seq` counter and the `At` stamp both assigned **inside `_gate`**; the new `attempt-finished` fields. Update the class doc's "Emitted kinds" list and delete its now-false "Run-level bracketing is NOT here" paragraph. |
| 4 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/ObserverProjection.cs` | The `RunFinished` recording member; flatten the `AttemptRecord` onto the `AttemptFinished` line — **all five `required` members** plus the optionals it holds. |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`, `src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs` | Explicit `RunFinished` forwards on the two decorators; `AttemptFinished` signature update on all four. The two renderers keep the interface default — comment the **reason** (use-after-dispose), not the choice. |
| 6 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/AttemptJournaler.cs`, `src/Guardrails.Core/Execution/TaskExecutor.cs` | The 11 mechanical raise-site edits. |
| 7 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/` | The **RED** real-settle worktree test for Bug A. Must not take the fake-provider `PendingAttempt is null` branch, and must assert it actually took the deferred-settle path. |
| 8 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/Scheduler.cs` | **Bug A** — the new raise in `RecordSucceededSettle`. Comment says "the worktree SUCCESS path's only route", NOT "the default mode's only route". |
| 9 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/RunCommand.cs` | `runId` threaded into `BuildObserverChain`'s `RunEventStream` construction; the new outer bracket (`OnTheFlyDiagramObserver?` hoisted null, try before the `if (live)`, `catch`/`throw;`, `finally { diagramObserver?.RunFinished(…); }`). **Leave the `finalSitesSettled` block exactly as it is**, nested inside. |
| 10 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/AttachCommand.cs` | Rebuild an `AttemptRecord` in the `AttemptFinished` replay case; one new `RequireDateTimeOffset` helper. No `case` for `RunFinished` — `default:` ignores it by design. |
| 11 | `guardrails-harness-developer` | `src/Guardrails.Cli/Ui/LogServer.cs` | **Bug B′ only** — one final read on shutdown. Do NOT change the empty-200. |
| 12 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/` | Row-shape tests; `seq` monotonicity under concurrent writers; the `faultKind`-carries-no-message negative assertion; the §8.1 exit-code gloss pinned by reflection over `ExitCodes`. |
| 13 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/` | The composition-root wiring test through `RunCommand.BuildObserverChain`; the `run-finished`-on-every-exit-path matrix **including a throw out of `ExecuteAsync`**; the **`ObserverProjection` → `AttachCommand` round-trip**. |
| 14 | `guardrails-architect` | `docs/plans/02-schemas-and-contracts.md` | Edits 1–3 above, verbatim. Lands in the same change as task 1 (invariant #4). |
| 15 | `guardrails-skill-author` | `.claude/skills/guardrails-domain-knowledge/SKILL.md` | The contract quick-reference gains `events.jsonl`'s kinds and the "absence means the DAG was not reached" rule. |

**Sequencing.** 1 → 2 (RED) → 3, 4, 5, 6 → 7 (RED) → 8 → 9 → 10, 11 → 12, 13 → 14 (with 1, not
after) → 15. **The build is RED between task 1 and task 6 by construction** — that is the point of the
signature change; the compiler enumerates the sites. See plan 35 §5 for what that means for
task-level guardrails.

---

## Proposed plan-document edits

1. **`docs/plans/02-schemas-and-contracts.md`** — edits 1–3 above, verbatim (task 14).
2. **`docs/plans/34-run-event-stream-and-attach.md`** — append a "Superseded by #595" note under §6
   recording four things: the §6 claim "field-aligned with `TelemetryRow`" was satisfied for five
   identity fields only; `events.jsonl` and `observer.jsonl` were shipped **without** an SSOT entry
   (invariant #4) *and* without an entry in the self-updating `guardrails-domain-knowledge` skill,
   both repaid here; §3's decorator-swallow discipline was enforced by tests that sweep only the
   **CLI** assembly, leaving both Core projections unguarded; and the one kind it shipped does not
   fire at all on the default execution path (Bug A). Worth writing down as a pattern, not a scolding:
   plan 34's own review passes were positioned to check that the seam was wired, and nothing was
   positioned to check what went through it — the #587 check-B shape #595 already names.
3. **`docs/plans/03-roadmap.md`** — no change. Neither decision is a v2 bet; #585 layer 3 already has
   its own line.
4. **A new issue, not folded in:** rewire `guardrails attach`'s termination check from the
   journal-derived "every task terminal" heuristic onto `run-finished`. Today attach returns before
   the terminal `<plan>/guardrails/` gate runs (measured at 10m44s), so a watcher stops watching
   while the run is still deciding its verdict. Related: the terminal gate is not on the
   `IRunObserver` seam at all — `RunCommand` calls `PlanGuardrailsStarting`/`PlanGuardrailsFinished`
   on the **concrete** `OnTheFlyDiagramObserver`, so no projection can see the phase. Both belong to
   the same follow-up.
5. **A second new issue: the worktree `needs-human` settles journal no attempt record.** A failed
   union re-verify (`Scheduler.cs:~4289`), an unresolvable AI-merge (`:~4333`) and a non-FF
   integration failure all call `RecordSettle(…, NeedsHuman, null)`, so `run.json` carries no
   `AttemptRecord` for an attempt that ran, passed its own guardrails, and cost real money. The
   missing event is a symptom; the missing record is the bug, and fixing it there gives the event for
   free. **This is the same §15.2a serial-versus-worktree asymmetry a third time** — worth saying so
   in the issue, because three instances is a pattern the codebase should be checked against
   systematically rather than one at a time.
6. **Filed while revising this document: #596** — `SchedulerFactory` decides worktree mode twice
   (inline in `Create`'s provider wiring and again as `WouldUseWorktreeMode`, which four more CLI sites
   call), via a git subprocess whose `catch { return false; }` cannot distinguish "not a repository"
   from "git was momentarily unavailable". The two evaluations can disagree within one run, and the F7
   clamp notice does not fire in the direction that journals a run as serial while wiring worktree
   mode. **This is a direct risk to Bug A's regression test** — a worktree test that only sets
   `maxParallelism > 1` may run serial and pass for the wrong reason — and it is a plausible reason
   Bug A hid this long.
7. **A lower priority: no plan-folder lock.** Two concurrent `guardrails run` invocations
   resolve the same run id and both append to one `events.jsonl` (`File.AppendAllText` opens
   `FileShare.Read`, so the loser can take an `IOException` up through `RunEventStream.TaskStarting`).
   Pre-existing and not this change's to fix, but §8.1 now states single-writer *per process*, and a
   contract that has to be hedged is usually pointing at a missing mechanism.

---

## What #585 layer 3 (`--on-event <url>`) depends on from this design

Flagged because layer 3 must not be built until these are settled:

1. **`runId` comes from the composition root, not the seam.** A webhook projection is a sibling
   decorator constructed by `BuildObserverChain`, which already holds `runId` — it takes it the same
   way `RunEventStream` now does. (The first draft made this the headline argument for putting `runId`
   on `RunStarting` — which was also that member's last surviving unique argument, and so helped
   retire it.)
2. **`run-finished` as the delivery-terminal signal.** A webhook consumer needs one unambiguous "stop
   expecting deliveries" event, and its `exitCode` is what a CI wrapper branches on. Two consequences
   layer 3 must design for: a runId can produce more than one `run-finished` (a resume, or a second
   concurrent process), so **key retry and ordering on `(runId, seq)` within a bracket — never on
   `at`, which is neither unique nor monotonic**; and delivery to a live subscriber is best-effort, so
   a webhook layer that must not miss the terminal event reads the file rather than relying on the
   stream alone.
3. **`faultKind` carries a type name, never a message.** This becomes a *security* property the moment
   rows are POSTed to an operator-supplied URL. If layer 3 later wants richer fault detail, it must
   come from a field designed for redaction, not from widening this one.
4. **The row is a projection of `AttemptRecord`, so the vocabulary is now closed against the journal.**
   Layer 3 can document its payload as "§8.1 rows" without a per-field schema of its own, and a new
   attempt fact reaches webhooks by a row edit rather than a contract change.
