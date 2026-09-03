# 35 — What actually goes through the event seam (#595)

**Issue:** #595 (the event stream ships one kind, and the row omits every field that decides a response).
**Status:** reviewed and settled — ready for breakdown. No open questions.
**Design of record:** `docs/plans/595-event-vocabulary-contract.md`. This plan states WHAT to build and
in WHAT ORDER; that document states WHY, and carries the rejected alternatives. Where the two disagree,
this plan is wrong.
**Binds to:** `IRunObserver`, `Journal.AttemptRecord` and `TelemetryRow` as they stand on
`fix/595-events-lifecycle-kinds` at `e7ba57d`. This plan defines **no vocabulary of its own**.

---

## 1. What this plan is

Plan 34 (`c10a13a`) built the seam. This plan fixes what goes through it.

Three things ship together because each is useless without the others:

1. **`events.jsonl` gains a run-termination event** — `run-finished`, so a consumer that attaches and
   sees a quiet stream can tell "still running" from "finished while I was disconnected".
2. **The `attempt-finished` row gains the fields that decide a response** — cost, turns, model, tier,
   runner, timing — by handing observers the journal's own `AttemptRecord` instead of three loose
   primitives.
3. **The `attempt-finished` event starts firing on the default execution path at all**, which it does
   not today (§2, Bug A). Without this, item 2 delivers cost and turns to the minority of runs.

## 2. The two measured defects

### 2.1 The row omits everything that decides a response

`EventRow` (`src/Guardrails.Core/Execution/RunEventStream.cs`) carries `kind · at · runId · taskId ·
attempt · outcome` plus the additive fields landed on `e7ba57d`. Plan 34 §6 put `events.jsonl` in scope
as *"field-aligned with `TelemetryRow`"*. `TelemetryRow` has 30 members; the alignment holds for five
identity fields and stops. A consumer that wants cost or model still reads the attempt directory — the
filesystem read #585 was filed to remove.

### 2.2 Bug A — in the DEFAULT mode, a succeeded attempt emits no `attempt-finished` at all

**Structurally confirmed; behaviorally UNPROVEN — proving it is a deliverable of this plan (§6.2).**

`AttemptJournaler.ValidateFragmentForSettle` (`src/Guardrails.Core/Execution/AttemptJournaler.cs`) is
the worktree-mode success path. It builds a `PendingAttempt`, sets `DeferredSettle = true`, and returns
**without raising `AttemptFinished`** — every other journaller method raises it (9 sites), and so do the
two revalidate settles in `TaskExecutor`. The deferred settle that finally journals the record,
`Scheduler.RecordSucceededSettle`, does not raise it either: **`Scheduler.cs` raises `AttemptFinished`
zero times.**

Worktree is the default mode. So today `attempt-finished` fires for failures and halts only.

This is the same asymmetry the SSOT already documents at §15.2a for record *fields* — *"a member hung
directly off `AttemptRecord` lands in serial mode and silently vanishes in worktree mode unless
`PendingAttempt` grows a carrier of its own"* — one seam over, with nothing watching the event variant.

**What this plan does NOT fix, deliberately.** Worktree settles that end `needs-human` (a failed union
re-verify, an unresolvable AI-merge, a non-FF integration failure) call `RecordSettle(..., NeedsHuman,
null)` and build no `AttemptRecord` at all, so they still raise nothing. That gap is in the **journal**,
and the event is a projection of the journal; manufacturing a record for the event alone would put a
fact in `events.jsonl` that is not in `run.json`. It gets its own issue.

## 3. What to build

### 3.1 `IRunObserver` — one member added, one member's payload changed

```csharp
// ADDED — default { } body, like every optional member on this interface
void RunFinished(int? exitCode, string? faultKind) { }

// CHANGED — was (TaskNode task, int attempt, Journal.AttemptOutcome outcome)
void AttemptFinished(TaskNode task, Journal.AttemptRecord record) { }
```

- **`exitCode`** is the `Guardrails.Cli.ExitCodes` vocabulary (SSOT §7): `0` green · `1` harness error ·
  `2` needs-human/gate failure · `3` cancelled · `4` escalations pending · `5` proceeded unreviewed. It
  is **null** when the run is unwinding on an unhandled fault and no exit code was ever determined.
  Null is honest; a fabricated code would claim a verdict the run never reached.
- **`faultKind`** is the unhandled exception's **type name**, null on every non-fault path, and **never
  the exception message**. #585 layer 3 (`--on-event <url>`) will POST these rows to an
  operator-supplied URL, and the message is the one value on the row that can carry an absolute path, a
  token, or a fragment of source. Same posture as `WaveBreakdownFinished`'s `failureKind`.
- **No `runId` on the member.** `RunCommand.BuildObserverChain` already holds it — it reaches the writer
  as a `RunEventStream` constructor parameter, replacing the current `Path.GetFileName(directory)`
  derivation.
- **`AttemptRecord` is already the per-attempt SSOT** the telemetry ETL maps to `TelemetryRow`. Every
  raise site already has the record in a local named `record` (or `failedRecord`) and already reads
  `record.Outcome` off it, so each edit is `_observer.AttemptFinished(task, record)`.

**There is no `run-started`.** It was designed and rejected — see the design of record §1a. Do not add
it, and do not add a field to another kind to compensate.

### 3.2 `RunEventStream` — the `events.jsonl` writer

- New `runId` constructor parameter (replaces the path derivation).
- New `run-finished` row: `exitCode`, `faultKind`. **The only kind with no `taskId`** —
  `EventRow.TaskId` becomes `required string?`. Keep `required`: dropping it lets a future kind omit
  `taskId` silently, which `JsonIgnoreCondition.WhenWritingNull` makes indistinguishable from a
  legitimately run-scoped row.
- New `seq` field on **every** row: a monotonic, 1-based, per-process counter assigned **inside the
  append lock**, and the `at` timestamp moved inside that lock too. Today `At` is built outside
  `lock (_gate)`, so under parallel workers `at` order can disagree with file order — and on Windows
  its ~15.6 ms tick resolution means concurrent rows share an `at` outright. **`seq`, not `at`, is the
  ordering key**, and #585 layer 3 will key retry and ordering on it.
- Widened `attempt-finished` row, each field naming its `TelemetryRow` twin verbatim:

  | Row field | `TelemetryRow` | From the record |
  |---|---|---|
  | `costUsd` | `CostUsd` | `record.CostUsd` |
  | `turns` | `Turns` | `record.Turns` |
  | `model` | `Model` | `record.Provenance?.Model` |
  | `tier` | `Tier` | `record.Provenance?.Tier` |
  | `runner` | `Runner` | `record.Provenance?.Runner` |
  | `startedAt` | `StartedAt` | `record.StartedAt` |
  | `endedAt` | `EndedAt` | `record.EndedAt` |
  | `needsHumanKind` | *(none — journal-owned)* | `record.NeedsHumanKind` |

  **Do not add `elapsedSeconds`.** It appears in #585's illustrative row and has no `TelemetryRow`
  counterpart; shipping it is the forked vocabulary #585 forbids two paragraphs later. `endedAt` minus
  `startedAt` is the same fact in the corpus's own terms.

  **Do not add `attemptsMax`.** The retry budget is not in scope at this seam — it reaches observers
  only via `AttemptStarting(task, attempt, budget)`, and is already on the shipped `attempt-started`
  row as `budget`. A consumer correlates on `(taskId, attempt)`.

### 3.3 `ObserverProjection` — the `observer.jsonl` writer

- Declares and records `RunFinished`. Not because `guardrails attach` needs it, but because this class's
  documented contract is "record every observed call, in order" — a decorator that drops a member makes
  its own doc false.
- Flattens the `AttemptRecord` onto the `AttemptFinished` line, including **all five `required`
  members** (`attempt`, `startedAt`, `endedAt`, `outcome`, `logDir`) plus the optionals it holds. See
  §6.3 — omitting any of the five is silent.

### 3.4 The decorators and renderers

- `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver`: explicit `RunFinished` forwards, plus the
  `AttemptFinished` signature update.
- `LiveRunObserver` and `ConsoleRunObserver`: `AttemptFinished` signature update only. **They must NOT
  declare `RunFinished`** — `await using var liveObserver` is scoped to the `if (live)` block, so
  `RunFinished` is the first `IRunObserver` call ever made on the chain *after* the Spectre live loop
  has been torn down. Declaring it there is a use-after-dispose. Comment the reason, not the choice: a
  style rationale ("a renderer that doesn't render it") would be accepted on its merits by the next
  reader who thinks a completion line would look nice.

### 3.5 `Scheduler` — Bug A's fix

In `RecordSucceededSettle`, after `RecordSettleWithAttempt`:

```csharp
_observer.AttemptFinished(task, record);
```

Ordering is already correct — `OnSettledAsync` reaches this before it raises `TaskFinished`, so the
stream reads `attempt-finished` then `task-settled`. The `PendingAttempt is null` early-return branch
(the fake-provider path) raises nothing: there is no record, and inventing one would be a fabricated
fact.

The comment must say **"the worktree SUCCESS path's only route to this event"** — not "the default
mode's only route", which is false while §2.2's residual stands.

### 3.6 `RunCommand` — where `RunFinished` is raised

The current `finally` that calls `TrySettleFinalSitesAfterFault` is **not** wide enough: `ExecuteAsync`
is invoked above the try that holds it, with no `catch` in between, so an unhandled throw out of the
Scheduler — the largest fault surface in the process — would unwind straight past it.

Hoist the chain variable and open a new bracket around both the DAG and the terminal phase:

```csharp
OnTheFlyDiagramObserver? diagramObserver = null;   // was assigned in both branches
int? resolvedExitCode = null;
string? faultKind = null;
try
{
    if (live) { … } else { … }        // both branches call ExecuteAsync
    bool finalSitesSettled = false;   // the existing #333 block, UNCHANGED, nested inside
    try { … } finally { … }
}
catch (Exception ex) { faultKind = ex.GetType().Name; throw; }   // TYPE only; bare rethrow
finally { diagramObserver?.RunFinished(resolvedExitCode, faultKind); }
```

- `resolvedExitCode` is set immediately above each of the two `return` statements in the inner block —
  **never read from `Finish`'s return value**, because the terminal-gate-failure branch overrides it.
- `diagramObserver?.` — a throw before the chain is built raises nothing, correctly.
- **Leave the `finalSitesSettled` block exactly as it is.** Widening *it* would newly fire
  `TrySettleFinalSitesAfterFault` on a mid-DAG fault: arguably an improvement, definitely a separate
  change.

### 3.7 `AttachCommand` — the replay

Rebuild an `AttemptRecord` in the `AttemptFinished` case from §3.3's flattened line; needs one new
`RequireDateTimeOffset` helper beside the existing `RequireString`/`RequireInt`/`RequireBool`. Add **no
`case` for `RunFinished`** — the `default:` branch ignores unknown members by design; comment that it
is deliberate.

### 3.8 `LogServer` — deliver the terminal row

`WriteEventsStream`'s tail loop returns on the shutdown signal **without a final read**, and
`run-finished` is appended microseconds before the server is disposed. Add one final read-and-flush on
the shutdown signal.

**Do NOT change the empty-200 for a missing `events.jsonl`.** It is deliberate
(`LogServer.cs` documents it), pinned by
`tests/Guardrails.Integration.Tests/RunEvents/EventsEndpointTests.cs`
(`EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError`), and a change would **hang** that
test rather than fail it, because it reads the whole body. The window it appears to cover does not
exist: the log server does not start until after every pre-DAG phase.

## 4. What is deliberately NOT built

| Not built | Where it lives |
|---|---|
| `run-started` | Designed and rejected — design of record §1a. Do not re-propose without reading it. |
| `needs-human` / `task-blocked` / `wave-gate` / `merge` / preflight kinds | #595 defers them: "as they earn their keep". |
| `--on-event <url>` webhooks | #585 layer 3, its own plan. This plan settles the vocabulary it will build on. |
| Moving the observer chain earlier so pre-DAG halts reach the stream | Structural: the interactive confirms and Full Flight Checks write plain console lines and must precede the live region. Related to #572. |
| A record for the worktree `needs-human`-by-integration-gate settles | §2.2 — a journal-completeness issue of its own. |
| A plan-folder lock so two runs cannot share one `events.jsonl` | Pre-existing; the SSOT wording is scoped honestly instead. |
| Rewiring `guardrails attach` onto `run-finished` | Its journal heuristic returns before the terminal gate runs. Separate follow-up. |

## 5. Sequencing — and the RED-BUILD WINDOW (read this before breaking down)

**This is the single most likely way the breakdown goes wrong.**

Changing `AttemptFinished`'s signature breaks 11 raise sites, 6 declarations, 1 replay dispatcher and 5
test doubles **simultaneously**. Between the interface edit and the last call-site edit the solution
**does not compile — by construction**. That is the point of the change: the compiler enumerates the
sites, which is why this is safer than an additive overload.

A task-level guardrail asserting "the solution builds" will therefore **fail legitimately** mid-DAG, and
the harness will read a correct implementation as a failure and burn its retry budget on it.

### 5.1 The required shape: the signature change is ONE task

Put the interface edit and **every** edit needed to restore compilation in a **single task**:

- `src/Guardrails.Core/Execution/IRunObserver.cs` — add `RunFinished`, change `AttemptFinished`
- `src/Guardrails.Core/Execution/AttemptJournaler.cs` — 9 raise sites
- `src/Guardrails.Core/Execution/TaskExecutor.cs` — 2 raise sites
- `src/Guardrails.Core/Execution/RunEventStream.cs`, `ObserverProjection.cs` — signature only
- `src/Guardrails.Cli/Ui/LiveRunObserver.cs`, `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`,
  `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs`, `src/Guardrails.Cli/ConsoleRunObserver.cs` —
  signature only
- `src/Guardrails.Cli/Commands/AttachCommand.cs` — the replay decode
- the 5 test doubles that override it (`grep -rl "void AttemptFinished" tests/`)

That task's guardrails are the normal ones: solution builds, both suites pass. **Every other task in
this plan starts and ends green**, so every other task can carry a build guardrail.

Do not decompose this by file. It is large but purely mechanical, and its size is the price of
atomicity.

### 5.2 Turn budget

That task edits ~25 sites across two files over 900 and 4,600 lines. This is #584's archetype — a
precise multi-site edit inside very large files — so give it a **generous `maxTurns`** and expect the
`max-turns` auto-escalation to be exercised. A stingy budget on this task is the second most likely
failure.

### 5.3 If it must be split anyway

It should not be. If breakdown insists, the split is by **compilation-unit closure**, and every
intermediate task must carry a guardrail that is **not** "the solution builds" — a targeted
grep/regex assertion that the specific edits landed — with the build guardrail restored at the closing
task. State plainly that this is worse: it leaves a window where nothing deterministic gates the work,
which is the posture this product exists to refuse.

### 5.4 Task order

1. **The atomic signature change** (§5.1) — build green at its end.
2. **RED: the decorator-forwarding tests** — the exhaustive two-assembly meta-test plus a behavioral
   forward test for `RunFinished`. Compiles (the member exists) and **fails** (no decorator declares
   it). Guardrail asserts the tests fail, and fail for that reason — the `tests-fail-on-stubs` pattern
   plan 34 used.
3. **The decorator forwards** (§3.4) — turns 2 green.
4. `RunEventStream` (§3.2), `ObserverProjection` (§3.3) — independent of each other.
5. **RED: the Bug A real-settle worktree test** (§6.2). Fails because nothing raises the event.
6. **Bug A's fix** (§3.5) — turns 5 green.
7. `RunCommand`'s bracket (§3.6), `LogServer`'s final read (§3.8) — independent of each other.
8. The remaining tests (§6).
9. Docs: the SSOT (§7.1) and the knowledge skill (§7.2).

**The SSOT edit is authored WITH step 1, not after it** (§7.1).

## 6. The tests that are load-bearing

Ordinary coverage aside, four assertions carry this plan. Each exists because the defect it catches is
**invisible** — green suite, no error, wrong behavior.

### 6.1 The decorator-forwarding sweep must cover BOTH assemblies

`IRunObserver`'s optional members have default `{ }` bodies, so a decorator that omits one compiles,
satisfies the interface, and silently swallows the event in every mode. The chain is
`OnTheFlyDiagramObserver → OnTheFlyLogSiteObserver → ObserverProjection → RunEventStream → renderer`,
so a missing forward at the **outermost** decorator means `events.jsonl` never hears the event at all.

The two existing reflection guards (`WaveGateForwardingTests`, `AttemptModelForwardingTests`) sweep
**only the CLI assembly** — so `RunEventStream` and `ObserverProjection`, in Core, are unguarded today.
Replace both reflection halves with **one exhaustive sweep over `typeof(IRunObserver).GetMethods()`
across both assemblies**, with a non-vacuity floor (assert each of the four known decorator types is in
the discovered set, and that the member list is non-empty). Verified: all four decorators declare all 20
current members, so the sweep is green on arrival and fails only on `RunFinished`.

### 6.2 Bug A's test must take the REAL settle path

**Bug A has no behavioral proof yet.** This test is what produces it, and it must be RED before §3.5
lands.

Two traps:

- **The fake-provider branch.** A test using the fake worktree provider takes
  `RecordSucceededSettle`'s `PendingAttempt is null` early return and never exercises the new raise. It
  must go through `ValidateFragmentForSettle` and the real deferred settle.
- **Silent demotion to serial (§9.1).** A test that merely sets `maxParallelism > 1` may run serial
  anyway and pass for the wrong reason — exercising `CompleteSucceededOrInvalidFragment`, which already
  raises the event. **The test must assert it actually took the deferred-settle path** (the settle
  journalled a `mergeSequence`, or the provider was used), not merely that a row appeared.

### 6.3 The `ObserverProjection` → `AttachCommand` round-trip

`AttemptRecord` has five `required` members. If §3.3's line omits any of them, §3.7's replay throws
`FormatException` — which `AttachCommand` **catches and skips**, by design, to stay forward-compatible
with unknown members. The result is `guardrails attach` replaying a run in which no attempt ever
finishes: no exception, no log line, no failing test.

Every other assertion in this plan still passes in that state. **This round-trip is the only thing that
catches it.** Write the line, replay it, assert the reconstructed record's fields.

### 6.4 `run-finished` fires on every exit path

A matrix, not a single case: green; needs-human; terminal-gate failure (the early return whose exit
code differs from `Finish`'s); and **a throw out of `ExecuteAsync`** — asserting `exitCode` is null,
`faultKind` is the type name, and the exception still propagates unchanged.

Plus a **negative** assertion: construct a fault whose message contains a recognizable secret-shaped
string and assert the row does not contain it. With layer 3 coming, this is a security property.

### 6.5 Also required

- Row shape: absent-not-null per kind; `taskId` present on every task-scoped kind and absent on
  `run-finished`; the `attempt-finished` field set.
- `seq` strictly increasing and unique under concurrent writers.
- The composition-root wiring test through `RunCommand.BuildObserverChain` — a unit test against
  `RunEventStream` in isolation passes while the composed chain swallows the event.
- The SSOT exit-code gloss pinned by reflection over `ExitCodes`, so the hand-copied table cannot drift.

## 7. Documentation that ships with the code

### 7.1 The SSOT — and it lands with the interface change, not after

**`events.jsonl` and `observer.jsonl` appear nowhere in `docs/plans/02-schemas-and-contracts.md`.**
Plan 34 shipped a public wire format — the most contract-shaped artifact in the repo, the one an
external consumer parses — with no SSOT entry at all. The invariant ("a contract change lands in the
SSOT in the same change that motivates it") was already strained before this plan; this is where it is
repaid.

Three edits, spelled out verbatim in the design of record §"Schema changes":

1. Retitle §8 to `## 8. Per-attempt log layout, and the run's own streams`.
2. Insert **§8.1 The run event stream (`logs/<runId>/events.jsonl`)** and **§8.2 The observer projection
   (`logs/<runId>/observer.jsonl`)** at the end of §8.
3. Two sentences in §12.2 about `GET /events` — these land **with §3.8**, since they describe behavior
   §3.8 introduces.

### 7.2 The knowledge skill

`.claude/skills/guardrails-domain-knowledge/SKILL.md` mentions `events.jsonl`, `observer.jsonl` and
`attach` **nowhere** — plan 34 shipped past its own self-updating clause as well as past the SSOT. Add
the kinds and the "absence means the DAG was not reached" rule to the contract quick-reference.

## 8. Decisions already made

No open questions. Recorded so the breakdown does not re-open them:

| Decision | Ruling |
|---|---|
| Run bracketing | **`run-finished` alone.** `run-started` rejected — its counters were approximations and its name implied a bracket it did not deliver. |
| How cost/turns reach the row | **The seam carries `Journal.AttemptRecord`.** Projection-side accumulation rejected: cost and turns are announced to no observer ever, and accumulating model/tier would make the projection a second owner of a fact `run.json` owns. |
| Reading the journal from the projection | Rejected — inverts the layering, races the deferred settle, and reintroduces the filesystem read #585 removes. |
| A purpose-built event payload record | Rejected — a third shape beside `AttemptRecord` and `TelemetryRow`. |
| Run-outcome vocabulary | The existing `ExitCodes` integers, not a new token set that must stay 1:1 with them forever. |
| `runId` on the seam | No — a `RunEventStream` constructor parameter from the composition root. |

## 9. Risks

### 9.1 A plan can apparently demote to serial silently — and it endangers §6.2

**Filed as #596.** Real enough to file; not fixed here. `SchedulerFactory` spells the worktree-mode
predicate — `plan.Config.MaxParallelism > 1 && IsGitRepository(plan.Workspace)` — **twice**: inline in
`Create`'s provider wiring, and again as the public `WouldUseWorktreeMode`, which four more CLI sites
call. `IsGitRepository` shells out to `git rev-parse --is-inside-work-tree` each time and swallows every
failure as `false`. Nothing records the answer.

So the decision has two owners and a non-deterministic evaluator, and the two evaluations can disagree
within one run — which is exactly what produces the observed combination of a journalled
`maxParallelism: 1` with **no** `ParallelismClampedNoProvider` row (the clamp notice only fires when the
provider is null, so a run whose first evaluation said "not git" and whose second said "git" journals
serial and warns about nothing).

**Consequence for this plan:** §6.2's assertion that the test took the deferred-settle path is not
belt-and-braces, it is load-bearing. A worktree test that only sets `maxParallelism > 1` may prove
nothing.

### 9.2 The red-build window

§5. Mitigated by making the signature change one task; the residual risk is that task's turn budget.

### 9.3 `run-finished` delivery is best-effort over the wire

`_listener.Stop()` runs first in `LogServer.DisposeAsync`, so even with §3.8 a live subscriber can miss
the terminal row. The SSOT tells consumers the row is durable in the file and to re-read on close.
Guaranteeing delivery would mean the run waiting on its own HTTP clients.

## 10. Acceptance

- A worktree-mode run's `events.jsonl` contains an `attempt-finished` row for a **succeeded** attempt,
  carrying `costUsd`, `turns`, `model`, `tier` and `runner`, and the test proving it took the real
  deferred-settle path.
- Every run that reaches the DAG ends its `events.jsonl` with exactly one `run-finished` per process,
  including a run killed by an unhandled fault (`exitCode` absent, `faultKind` present).
- `guardrails attach` still replays a run's attempts after the payload change — proved by round-trip,
  not by the absence of an error.
- Every `IRunObserver` member is declared by every transparent decorator in both assemblies, proved by
  one reflection sweep.
- `02-schemas-and-contracts.md` §8.1/§8.2 describe the shipped row shapes, and no field in §8.1 lacks
  either a `TelemetryRow` twin or a stated reason for having none.
- Both suites green; the solution builds. (`Guardrails.Core.Tests` and `Guardrails.Integration.Tests`
  stood at 2423 / 1113 on `e7ba57d`.)
