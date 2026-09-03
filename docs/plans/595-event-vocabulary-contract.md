# Architecture: the `events.jsonl` event vocabulary — run bracketing and the attempt row (#595)

Design of record for the two `IRunObserver` contract changes issue #595 asks for, on top of the
additive kinds already landed on `fix/595-events-lifecycle-kinds` (`e7ba57d`).

Status: **design of record, pending inline human review** (#106). Implementation milestones do not
start until the draft PR's comments are addressed.

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

1. **Run-level bracketing** — `run-started` / `run-finished`. No member of `IRunObserver` is
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
| `RunStarting` / `RunFinished` on `IRunObserver` | **harness** — `Guardrails.Core.Execution` + one CLI raise site each |
| `AttemptFinished` payload widening | **harness** — `Guardrails.Core.Execution` |
| The worktree-mode `attempt-finished` coverage hole | **harness** (bug, found while designing — see below) |
| `GET /events` losing the terminal row to its poll interval on shutdown | **harness** (bug, load-bearing for decision 1) |
| `GET /events` returning an empty 200 for a run with no rows yet | **NOT a bug — leave it.** Deliberate, test-pinned, and the window closes on its own once `run-started` exists |
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
   are still running. Once `run-started` is written at the top of `ExecuteAsync`, the file-missing
   window shrinks to the handful of statements between `:461` and `:525`. `run-started` closes it.

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
   tempting cheap answer (have `RunEventStream`'s constructor author a `run-started` row, or have
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

**Where the design strains an invariant.** #5, at one seam: `events.jsonl` begins at `run-started`,
so six pre-DAG halts produce no file at all. I do not fix that (see rejected alternatives) — I
*declare* it, in the SSOT, with the list of the six and the pointer to the mechanism that does cover
them (the process exit code, and the `halt` section #432 added to `run.json`). A declared blind spot
is honest; an undeclared one is the #595 defect again.

---

## Decision 1 — `RunStarting` / `RunFinished` on `IRunObserver`

### 1a. Does `run-started` still earn its keep?

**Yes — but on the header argument, not the liveness argument, and the difference matters.**

The already-landed `task-started` kind mostly closes #595's cases (a) and (b): a stream with
`task-started` rows and no `task-settled` is a healthy run in flight. So the liveness case for
`run-started` is weaker than #595 implies, and weaker still because the observer chain does not exist
during the pre-DAG phases (below) — `run-started` is **not** a reliable "the process launched" proof
and must not be documented as one.

What it uniquely buys, and nothing else in the stream does:

1. **The denominator.** `taskCount`. Five `task-settled` rows mean nothing without it. A `/events`
   consumer has no other source — it would have to read the plan folder, which is the filesystem read
   #585 exists to remove.
2. **The resume correction.** `alreadyCompleteCount`. A resume of a 12-task plan with 10 already green
   emits two `task-started` rows and a green `run-finished`. Without this field that stream reads as a
   run that skipped ten tasks. Resume is the *normal* case for long unattended runs (#361), so this is
   not an edge.
3. **`runId` as a stated fact rather than a derived one** — see 1c.
4. **Coverage of the JIT-breakdown window.** A waved plan's between-wave breakdown can run 30 minutes
   with no `task-started` (the checkpoint fires inside the Scheduler, after `ExecuteAsync`). That
   window *is* covered by `run-started`.

`run-finished` needs no defense: #595's case (c) — finished while disconnected — has no other answer,
and it is the terminal signal an unattended supervisor and layer 3 both branch on.

### 1b. Exact signatures

Added to `src/Guardrails.Core/Execution/IRunObserver.cs`, both with default `{ }` bodies:

```csharp
/// <summary>
/// The run's DAG is about to execute (issue #595) — the FIRST call any observer receives, raised once
/// per PROCESS from <c>RunCommand.ExecuteAsync</c> before the Scheduler is even constructed.
/// <paramref name="taskCount"/> is the plan's task count (the DENOMINATOR a stream consumer has no
/// other source for); <paramref name="alreadyCompleteCount"/> is how many of those were ALREADY
/// terminal in the journal before this process started, so a resume's short stream is
/// self-explanatory rather than looking like a run that skipped work.
///
/// <para><b>Both counts are AS RECORDED AT RUN START, and both can be wrong later.</b>
/// <paramref name="taskCount"/> is <c>PlanDefinition.Tasks</c> — the flattened union of the waves
/// AUTHORED so far — so a JIT-waved plan (SSOT §14.4) authors more tasks after this fires and the
/// stated count undercounts. <paramref name="alreadyCompleteCount"/> is read before the §7.2
/// definition-drift rewind, which can push already-succeeded tasks back to pending, so more tasks may
/// run than the arithmetic predicts. This is a HEADER for orientation, not an accounting record; a
/// consumer that needs exact progress counts the rows.</para>
///
///
/// <para><b>What this event does NOT claim.</b> It is not "the process launched": the observer chain
/// is built after plan validation, the Windows MAX_PATH preflight, the plan-level Full Flight Checks
/// and the interactive drift/wave/breakdown confirms, each of which can halt the run before any
/// observer exists (SSOT §8.1 lists all six). It claims exactly "the observer chain is live and the
/// DAG is about to run".</para>
///
/// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
/// forward it EXPLICITLY or the run's own identity never reaches the projections behind it, in every
/// mode (the <see cref="WaveGateFinished"/> / <see cref="VerifierAdvisoryFound"/> lesson).</para>
/// </summary>
void RunStarting(int taskCount, int alreadyCompleteCount) { }

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

**Should the new member carry `runId` explicitly? No — and the first draft was wrong to say yes.**
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
root already holds the value.

`_runId` stops being `readonly`-and-derived and becomes `readonly`-and-passed. `runId` still appears on
every row; only the seam is spared.

**Fields deliberately NOT on `RunStarting`:**

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

### 1c. Where each is raised — the real question

**`RunStarting`: the first statement of `RunCommand.ExecuteAsync`**
(`src/Guardrails.Cli/Commands/RunCommand.cs:2403`), which gains two parameters (`string runId`,
`int alreadyCompleteCount`).

One site, not two. Both the `live` and `--no-ui` branches build the chain and then call
`ExecuteAsync`, so raising it there covers both and cannot drift between them. Placed **before**
`SchedulerFactory.Create` so it genuinely precedes every other observer call — including
`ParallelismClampedNoProvider` (raised from the Scheduler ctor) and `VerifierAdvisoryFound` (raised at
`Scheduler.cs:334`).

Precisely: in the `live` branch `BuildObserverChain` and `ExecuteAsync` are adjacent statements; in
the `--no-ui` branch four calls sit between them (`WriteInitialIndex`, `PrintStaticIndexLink`,
`WriteInitialDiagram`, `PrintDiagramLink`, `RunCommand.cs:527-531`). A throw from any of those leaves
the chain constructed and `RunStarting` unraised — but that run never executed a task either, so the
stream correctly holds nothing, exactly as for the six pre-DAG halts. The window is real and its
behavior is already the documented one.

`alreadyCompleteCount` is computed by the caller from the journal already loaded at
`RunCommand.cs:~322` (`RunJournal.LoadOrCreate`) — the count of `Tasks` entries whose status is
terminal-green. **Not** re-read from disk.

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
   all 20 current members today, so this test is **green on arrival** and fails only on the two new
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
  a bracket*, and `(run-started … run-finished)` brackets are what a consumer segments on. Making it
  durable across processes would mean reading the file back to find the high-water mark — a reader in
  the writer, for an ordering a bracket already gives.

`seq` and `at` and `kind` are the three fields that exist because a live stream needs them and a
settled telemetry row does not. That is stated in §8.1, so nobody looks for a `TelemetryRow` twin.

---

## Seams and contracts touched

| Seam | Change |
|---|---|
| `IRunObserver` | + `RunStarting(string, int, int)`, + `RunFinished(string, int?, string?)`, `AttemptFinished` payload → `Journal.AttemptRecord` |
| `RunEventStream` | + `runId` ctor parameter; 2 new emitting members; `EventRow.TaskId` → **`required string?`** (a run-scoped row has no task); `seq` + `at` assigned inside `_gate`; new `attempt-finished` fields |
| `ObserverProjection` | 2 new recording members; `AttemptFinished` line flattens the record's fields |
| `OnTheFlyDiagramObserver`, `OnTheFlyLogSiteObserver` | 2 new explicit forwards each (the swallow hazard) |
| `LiveRunObserver`, `ConsoleRunObserver` | `AttemptFinished` signature only. **They must NOT declare the two new members** — see the disposal hazard below. |
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
> **On every TASK-scoped row** (that is, every kind except `run-started` and `run-finished`):
>
> | Field | Meaning |
> |---|---|
> | `taskId` | the task's folder name |
>
> **The kinds.**
>
> | `kind` | Raised from | Additional fields |
> |---|---|---|
> | `run-started` | `IRunObserver.RunStarting` | `taskCount`, `alreadyCompleteCount` (no `taskId`) |
> | `task-started` | `TaskStarting` | — |
> | `attempt-started` | `AttemptStarting` | `attempt`, `budget` |
> | `guardrail-finished` | `GuardrailFinished` | `guardrail`, `passed`, and on failure `detail` |
> | `attempt-finished` | `AttemptFinished` | `attempt`, `outcome`, `costUsd`, `turns`, `model`, `tier`, `runner`, `startedAt`, `endedAt`, `needsHumanKind` |
> | `task-settled` | `TaskFinished` | `outcome`, `detail` |
> | `run-finished` | `IRunObserver.RunFinished` | `exitCode`, `faultKind` (no `taskId`) |
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
> **Where the stream begins, and what its absence means.** The first row is `run-started`, raised
> when the observer chain is live and the DAG is about to execute. Six halts return BEFORE the chain
> exists and therefore write no `events.jsonl` at all: plan validation errors; an unparseable
> `--autonomy` value; the Windows MAX_PATH worktree preflight (§3.2); the plan-level Full Flight
> Checks failing (§7 `planPreflights`); and a declined interactive definition-drift (§7.2) or
> wave-drift (§14.6) confirm. This is structural, not incidental: the interactive confirms and the
> Full Flight Checks phase both write plain console lines and so must precede the live region the
> chain is built around (§12.1). **A consumer must read "no `events.jsonl`" as "the run has not
> reached the DAG", never as "no run"** — and for those halts the covering record is the process exit
> code plus `run.json`'s `halt` section (§7). (All six were traced to their return statements in
> `RunCommand.RunAsync` when this section was written; an exhaustive list ages, so treat it as the
> halts known at that time rather than a closed set.)
>
> **A third state, and what it means.** A file whose FIRST row is not `run-started` means the
> `RunStarting` event was swallowed on its way down the decorator chain — a defect, not a run state.
> It is distinguishable and should be reported, not tolerated.
>
> **A runId spans processes, so the file can hold more than one bracket.** A resume reuses the run id
> and appends to the SAME `events.jsonl`. A `run-started` following a `run-finished` is therefore
> normally the resume signal — though it is equally the signature of a second concurrent process
> (above), so a consumer treats it as "a new bracket began", not as proof of a resume. Take the LAST
> `run-finished` as current. A resume of an already-complete run re-fires the terminal gate with no
> attempt burned (§7) and emits its own bracket around zero `task-started` rows — which
> `alreadyCompleteCount` explains.
>
> **`run-finished` is a durable FILE event first.** A `/events` subscriber can miss the terminal row:
> the run appends it and tears the log server down microseconds later. A client whose connection
> closes must RE-READ the file rather than assume it saw the end of the stream.
>
> **`taskCount` and `alreadyCompleteCount` are run-start snapshots, not guarantees.** `taskCount` is
> the tasks AUTHORED at run start, so a JIT-waved plan (§14.4) authors more afterward and the stated
> count undercounts. `alreadyCompleteCount` is read before the §7.2 definition-drift rewind, which can
> return already-succeeded tasks to pending. The pair orients a late subscriber; it does not balance.
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
plan; `alreadyCompleteCount` is read before the §7.2 drift rewind. §1a's case for the event rests on
"the denominator" and "it covers the 30-minute JIT-breakdown window" — and the JIT plan is precisely
the one whose denominator is wrong. I caveat both rather than move the raise site, because raising
`RunStarting` after the Scheduler's drift resolution and wave authoring would put it after
`ParallelismClampedNoProvider` and `VerifierAdvisoryFound`, so it would no longer be the first call
and would no longer bracket anything. **A header for orientation is worth having; an accounting record
is a different feature.** If review disagrees, the fallback below applies with more force than the
first draft admitted.

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

**The strongest counter-argument: `run-started` does not do what its name promises, and shipping it
teaches consumers a false invariant.** A consumer reads the SSOT's kind table, sees `run-started`, and
writes "if no `run-started` after N seconds, alert" — and gets a false alarm on every run that spends
20 minutes in Full Flight Checks, plus a *missed* alarm on the six halts that never write a row.
The event's name asserts a bracket it does not actually bracket. That is the #595 defect shape (a
signal that looks like it certifies something it does not) reintroduced by the fix for it.

**Response, and it is a concession as much as a rebuttal.** The name is load-bearing, so it is
constrained rather than defended: the XML doc has an explicit "what this event does NOT claim"
paragraph and a "both counts are run-start snapshots" one; §8.1 has a named "where the stream begins"
subsection listing all six halts and the covering mechanism, plus the third-state rule for a file
whose first row is not `run-started`. A consumer who reads the contract cannot form the false
invariant. A consumer who does not read it can — and I accept that residue, because the alternative (no
run-level bracketing at all) leaves #595's case (c) with no answer whatsoever, and because
`taskCount`/`alreadyCompleteCount` are unavailable from any other source in the stream. **The honest
framing, and the one that should appear in the PR: `run-started` is a HEADER row that happens to also
prove liveness from the DAG onward — not a process-launch event.**

**The fallback, now more live than the first draft allowed** (finding A above): ship **`run-finished`
alone**. It answers #595's case (c), it is what an unattended supervisor and layer 3 branch on, its
placement is now correct on every fault path, and it carries none of `run-started`'s exposure — no
name that overpromises, no two approximate counters. The cost is that a late subscriber has no
denominator, which matters less than it did once you notice the denominator would have been wrong on
JIT-waved plans anyway. **I still recommend shipping both**, because a header row that is explicitly
labeled a snapshot is more useful than no header at all — but this is the decision I would most like
review to actually weigh rather than wave through.

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
empty-200 is deliberate, test-pinned, and the window it covered closes on its own once `run-started`
exists. What replaced it (Bug B′, a three-line final drain) is genuinely load-bearing: without it the
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

One task per row. `filesTouched` cells are backticked and segment-resolvable against the real tree.

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-harness-developer` | `src/Guardrails.Core/Journal/JournalModel.cs` | Nothing to change — **read-only precondition check** that `AttemptRecord` and every nested record (`AttemptUsage`, `AttemptSegments`, `AttemptProvenance`) are `public`. Fold into task 2 if you prefer; listed so the assumption is verified, not assumed. |
| 2 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/IRunObserver.cs` | Add `RunStarting(int, int)` / `RunFinished(int?, string?)` with the XML docs above (including the "does NOT claim", "both counts are run-start snapshots" and "never the message" paragraphs); change `AttemptFinished` to `(TaskNode, Journal.AttemptRecord)`. **No `runId` on either member** — it is a `RunEventStream` ctor parameter (task 4). Leave `NullObserver` alone — default-bodied members need no entry. |
| 3 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/` | The exhaustive two-assembly decorator meta-test (assertion 1) **plus** behavioral forward tests for both new members (assertion 2) — written RED against task 2's interface, before task 4 declares anything. Retire or subsume `WaveGateForwardingTests` and `AttemptModelForwardingTests`'s reflection halves rather than leaving three overlapping sweeps. |
| 4 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/RunEventStream.cs` | A `runId` ctor parameter replacing the `Path.GetFileName` derivation; the two new emitting members; `EventRow.TaskId` → **`required string?`** (keep `required`: dropping it lets a future kind omit `taskId` silently, which `JsonIgnoreCondition.WhenWritingNull` makes indistinguishable from a run-scoped row); the `seq` counter and the `At` stamp both assigned **inside `_gate`**; the new `attempt-finished` fields. Update the class doc's "Emitted kinds" list and delete its now-false "Run-level bracketing is NOT here" paragraph. |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/ObserverProjection.cs` | The two new recording members; flatten the `AttemptRecord` onto the `AttemptFinished` line — **all five `required` members** (`attempt`, `startedAt`, `endedAt`, `outcome`, `logDir`) plus the optionals it holds. Omitting any of the five makes `AttachCommand`'s replay throw `FormatException`, which it **silently swallows and skips** (`AttachCommand.cs:189-196`) — a `guardrails attach` where no attempt ever finishes, with no error anywhere. Task 13's round-trip test is what catches this; do not land task 5 without it. |
| 6 | `guardrails-harness-developer` | `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`, `src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs` | Explicit forwards for both new members on the two decorators; `AttemptFinished` signature update on all four. The two renderers keep the interface default for the run members — comment the **reason**, not the choice: `RunFinished` is the first `IRunObserver` call ever made on the chain after `LiveRunObserver.DisposeAsync` has torn down the Spectre live loop, so declaring it there is a use-after-dispose. A style rationale would be accepted on its merits by the next reader who thinks a completion line would look nice. |
| 7 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/AttemptJournaler.cs`, `src/Guardrails.Core/Execution/TaskExecutor.cs` | The 11 mechanical raise-site edits (`AttemptJournaler` 162, 382, 455, 534, 611, 668, 734, 802, 852; `TaskExecutor` 587, 622). |
| 8 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/Scheduler.cs` | **Bug A** — the new raise in `RecordSucceededSettle`, after `RecordSettleWithAttempt`. Comment it with the §15.2a serial-versus-worktree trap. The comment must say **"the worktree SUCCESS path's only route to this event"** — NOT "the default mode's only route", which is false: the union-reverify / AI-merge / non-FF `needs-human` settles still raise nothing (they build no record). |
| 9 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/RunCommand.cs` | `RunStarting` at the top of `ExecuteAsync` (+ two parameters, bound BY NAME at both call sites); `alreadyCompleteCount` from the journal loaded at `:307`; `runId` threaded into `BuildObserverChain`'s `RunEventStream` construction (`:2357`). Then the **new outer bracket**: hoist `diagramObserver` to `OnTheFlyDiagramObserver?` initialized null, open the try **before** the `if (live)` at `:509`, add the `catch (Exception ex) { faultKind = ex.GetType().Name; throw; }` and `finally { diagramObserver?.RunFinished(…); }`. **Leave the `finalSitesSettled` block at `:556` exactly as it is**, nested inside — widening it would newly fire `TrySettleFinalSitesAfterFault` on a mid-DAG fault, which is a separate behavior change. |
| 10 | `guardrails-harness-developer` | `src/Guardrails.Cli/Commands/AttachCommand.cs` | Rebuild an `AttemptRecord` in the `AttemptFinished` replay case from task 5's flattened line — its five `required` members (`Attempt`, `StartedAt`, `EndedAt`, `Outcome`, `LogDir`) must all be on the line, which needs one new `RequireDateTimeOffset` helper beside the existing `RequireString`/`RequireInt`/`RequireBool`. Add no `case` for the run members — comment that `default:` (`AttachCommand.cs:311-315`) intentionally ignores them. |
| 11 | `guardrails-harness-developer` | `src/Guardrails.Cli/Ui/LogServer.cs` | **Bug B′ only** — `WriteEventsStream`'s poll loop does ONE final read-and-flush when `_shutdown` signals, before returning, so the terminal `run-finished` row is not lost to the 150 ms wait. **Do NOT change the empty-200 for a missing file**: it is deliberate (`LogServer.cs:763-767`), pinned by `EventsEndpointTests.cs:127-142`, and the change would hang that test rather than fail it. |
| 12 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/` | Row-shape tests: the two new kinds; the `attempt-finished` field set; absent-not-null for every kind; `taskId` present on every task-scoped kind and absent on both run-scoped ones; `seq` strictly increasing and unique across concurrent writers; `run-started` with **asymmetric** `taskCount`/`alreadyCompleteCount` (never 1 and 1) so a positional slip fails; the `faultKind`-carries-no-message negative assertion; and the §8.1 exit-code gloss pinned by reflection over `ExitCodes`. |
| 13 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/` | The composition-root wiring test through `RunCommand.BuildObserverChain` (assertion 3); the `run-finished`-on-every-exit-path matrix **including a throw out of `ExecuteAsync`** (assertion 4); a **real-settle** worktree test for Bug A that does NOT take the fake-provider `PendingAttempt is null` branch; and — the one the first draft missed — an **`ObserverProjection` → `AttachCommand` round-trip** for the widened `AttemptFinished`, asserting the replayed record's fields, because a short line is skipped silently rather than failing. |
| 14 | `guardrails-architect` | `docs/plans/02-schemas-and-contracts.md` | Edits 1–3 above, verbatim. **Lands in the same change as task 2** (invariant #4) — not after. |
| 15 | `guardrails-skill-author` | `.claude/skills/guardrails-domain-knowledge/SKILL.md` | The contract quick-reference gains `events.jsonl`'s kinds and the "absence means the DAG was not reached" rule. **Verified: the skill mentions `events.jsonl`, `observer.jsonl` and `attach` nowhere** — plan 34 shipped past its own self-updating clause as well as past the SSOT. |

**Sequencing.** 1 → 2 → 3 (RED) → 4, 5, 6, 7 in parallel → 8 → 9 → 10, 11 in parallel → 12, 13 →
14 (with 2, not after) → 15. Build stays broken between 2 and 7 by construction: that is the point of
the signature change — the compiler enumerates the sites.

**Ordering note for task 14.** Invariant #4 says the SSOT edit lands in the *same change*, and this
plan spans several commits on one branch. "Same change" means the same PR/branch, and the SSOT edit
must not be the last commit before merge as an afterthought — author it alongside task 2, adjust if
implementation reveals a divergence.

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
6. **A third, lower priority: no plan-folder lock.** Two concurrent `guardrails run` invocations
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
   on `RunStarting`; it is the argument *against*.)
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
