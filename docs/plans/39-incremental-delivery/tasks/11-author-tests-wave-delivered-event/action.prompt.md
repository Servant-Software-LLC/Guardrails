## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `11-author-tests-wave-delivered-event`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "11-author-tests-wave-delivered-event": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests for the `WaveDelivered` announcement, plus the interface member they compile
against — design 39 §5 ("Wiring and halts").

**Test file 1:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredEventTests.cs`
**Test file 2:** `tests/Guardrails.Integration.Tests/WaveDelivery/WaveDeliveredCliForwardingTests.cs`

**The CLI class lives in Guardrails.Integration.Tests, and it MUST (review, 2026-09-11).**
`Guardrails.Core.Tests` references `Guardrails.Core` and nothing else — it has no
`using Guardrails.Cli` anywhere, while Integration.Tests uses it throughout — and
`tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs:21` states the constraint in
its own header. The CLI observers are in `src/Guardrails.Cli`, no task in this plan may
edit a `.csproj`, and the first draft put this class in Core.Tests — where the honest test
cannot compile and the compiling test proves nothing. The repo's own
`ObserverForwardingSweepTests` lives in Integration.Tests for exactly this reason.

**Test classes:** `WaveDeliveredEventTests` (the CORE decorators) and
`WaveDeliveredCliForwardingTests` (the CLI decorators and renderers). Two classes because two tasks implement them:
splitting the forwarding by assembly keeps each retry bounded, which a single six-file wiring task does
not — GR2042 flagged exactly that fingerprint on plan 40's first draft.

**Interface change:** add this member to `IRunObserver.cs`, beside `WaveStarting` and `WaveFinished`,
with a **no-op default implementation** so every existing implementer still compiles:

```csharp
void WaveDelivered(Model.WaveNode wave, Journal.WaveDeliveredRecord delivery) { }
```

Document it as design 39 §5 says: the Scheduler raises it only for a record whose status is `delivered`,
and only after that record is persisted, so an observer never sees a result the journal does not hold.
`WaveDeliveredRecord` is on your base as task 09's stub: construct one with an object initializer and never
read its members here. Its getters throw until task 10 lands, and the same-instance assertion below needs
none of them.

**Decorators and renderers are different, and only decorators forward (review, 2026-09-13).** Every
`IRunObserver` implementer is one of two things:
- A **DECORATOR** wraps an inner observer and passes every event on to it. There are four:
  `RunEventStream` and `ObserverProjection` in Core, and `OnTheFlyDiagramObserver` and
  `OnTheFlyLogSiteObserver` in the Cli (each Cli one holds an `_inner`). These are exactly the types
  `ObserverForwardingSweepTests` hand-lists in its `decoratorNames` array.
- A **RENDERER** wraps nothing: `ConsoleRunObserver` and `LiveRunObserver` have no `_inner`. They
  implement the new member by rendering the event; there is nothing for them to forward to.

A forwarding test that includes a renderer demands something that cannot happen, and stays red for
task 13 forever. Hand-list the decorators; never derive the set from `: IRunObserver`.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The `ObserverForwardingSweepTests` contract is named in §5 for a reason.** An event that exists but
is swallowed by a decorator is silence wearing a passing test, and this repo has shipped exactly that:
a projection quietly dropped two new events and everything stayed green.

**Pin these behaviours to these EXACT method names:**

- `EveryCoreDecorator_ForwardsTheEvent` — `RunEventStream` and `ObserverProjection`. **Model this on
  `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`**, which already
  does this job and has been through the failure modes. Read it first. In particular it
  DELIBERATELY hand-lists its decorators rather than reflecting over every `IRunObserver`
  implementer, and it is right to: reflection over the Core assembly also finds the nested
  `private sealed class NullObserver` (in `IRunObserver.cs`), whose contract is to SWALLOW. A
  "derive the set by reflection" test would demand forwarding from the one type designed not to,
  and would be permanently red for a task that cannot edit it. Copy its declared exemption and
  its non-vacuity floor too. Pass one `WaveDeliveredRecord` instance and assert that SAME instance
  reaches the inner observer — a decorator that rebuilds or re-reads the record is not forwarding it.
- `EveryCliDecorator_ForwardsTheEvent` — `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver`,
  and ONLY those two, with the same same-instance assertion.
- `ADecoratorThatDropsTheEvent_IsCaught` — the negative control. Without it, a sweep that enumerates
  zero decorators passes and proves nothing.
- `ObserverProjection_AppendsTheDeliveryToObserverJsonl` (in `WaveDeliveredEventTests`) — call
  `WaveDelivered` on an `ObserverProjection` over a temp directory, typed as `IRunObserver` (a default
  interface member resolves only through the interface), then read `observer.jsonl`. Exactly one line,
  carrying `"member": "WaveDelivered"`, the wave's `waveDir`, the record's `commit`, and its `covers` as a
  JSON array in order — the shape `ObserverProjection`'s `SuppliedResourcesCommitted` line already uses.
  `observer.jsonl` is the verbatim record of every observer call, and `guardrails attach` tails it; a
  decorator that forwards without appending drops the delivery from it.
- `RunEventStream_AppendsNoEventsRowForADelivery` (in `WaveDeliveredEventTests`) — call `WaveDelivered` on a
  `RunEventStream` over a temp directory: no `events.jsonl` row is written. DECIDED: `events.jsonl` gains
  no `wave-delivered` kind. `RunEventStream` writes no row for any wave-level event today (`WaveStarting`,
  `WaveFinished` and `WaveGateFinished` only forward), and a delivery's durable, machine-readable record is
  `run.json`'s `waves.<dir>.delivered`, persisted before the event is raised.
- `ConsoleRunObserver_PrintsTheDeliveredWaveAndCommit` (in `WaveDeliveredCliForwardingTests`) — the
  `--no-ui` renderer: `IRunObserver observer = new ConsoleRunObserver(writer)` over a `StringWriter`; call
  `WaveDelivered`, and assert the output names the wave's directory and the commit.
- `LiveRunObserver_PrintsTheDeliveredWaveAndCommit` (in `WaveDeliveredCliForwardingTests`) — the live
  table: a Spectre `TestConsole` (`.Interactive()`), a `LiveRunObserver` over it, `WaveDelivered` called
  through `IRunObserver`, and after disposal `console.Output` names the wave's directory and the commit.
  A renderer that implements the member with an empty body passes every forwarding row; these two rows
  are what fail it.

**Model the last four on plan 40's precedent:**
`tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs` and
`tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs` test
`SuppliedResourcesCommitted` the same way. Build every record with an object initializer and assert against
the literal values you put in it. Never read a `WaveDeliveredRecord` getter in a test: they throw until
task 10 lands.

**No payload-shape test of the RAISED record here (review, 2026-09-13).** An earlier draft pinned
`Event_CarriesTheWaveTheCommitAndWhatItCovered`. A shape test of a record handed to a no-op default passes
on the stub, and which record the Scheduler raises, and when, cannot be seen from this task's files.
Task 28 pins that against the real Scheduler. The projection and renderer rows above assert something
different: what an observer WRITES when it is handed a record, which the interface's no-op default never
writes.

Two rows are exempt from the red census, and both must still exist:
- `ADecoratorThatDropsTheEvent_IsCaught` drives the sweep's detection against a test-local decorator that
  swallows the event and never touches production forwarding, so a correct test is green on arrival.
- `RunEventStream_AppendsNoEventsRowForADelivery` asserts an absence, and on this task's base
  `RunEventStream` inherits the no-op default, which writes nothing.

Task 12's forward census requires both Passed. Do NOT couple either to the missing forwarding to force a
red.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The tests MUST COMPILE, and the other five MUST FAIL: `EveryCoreDecorator_ForwardsTheEvent`,
`ObserverProjection_AppendsTheDeliveryToObserverJsonl`, `EveryCliDecorator_ForwardsTheEvent`,
`ConsoleRunObserver_PrintsTheDeliveredWaveAndCommit` and `LiveRunObserver_PrintsTheDeliveredWaveAndCommit`.
Do NOT implement the forwarding, the projection or the rendering.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredEventTests.cs`, `tests/Guardrails.Integration.Tests/WaveDelivery/WaveDeliveredCliForwardingTests.cs`, and `src/Guardrails.Core/Execution/IRunObserver.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
