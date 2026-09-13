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
against — design 39 §5.

**Test file 1:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredEventTests.cs`
**Test file 2:** `tests/Guardrails.Integration.Tests/WaveDelivery/WaveDeliveredCliForwardingTests.cs`

**The CLI class lives in Guardrails.Integration.Tests, and it MUST (review, 2026-09-11).**
`Guardrails.Core.Tests` references `Guardrails.Core` and nothing else — measured: zero
`using Guardrails.Cli` across its 264 files against 156 in Integration.Tests, and
`tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs:21` states the constraint in
its own header. The four CLI decorators are in `src/Guardrails.Cli`, no task in this plan may
edit a `.csproj`, and the first draft put this class in Core.Tests — where the honest test
cannot compile and the compiling test proves nothing. The repo's own
`ObserverForwardingSweepTests` lives in Integration.Tests for exactly this reason.

**Test classes:** `WaveDeliveredEventTests` (the event shape + the CORE decorators) and
`WaveDeliveredCliForwardingTests` (the CLI decorators). Two classes because two tasks implement them:
splitting the forwarding by assembly keeps each retry bounded, which a single six-file wiring task does
not — GR2042 flagged exactly that fingerprint on plan 40's first draft.
**Interface change:** add `WaveDelivered` to `IRunObserver.cs` with a **no-op default implementation**,
so every existing implementer still compiles.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The `ObserverForwardingSweepTests` contract is named in §5 for a reason.** An event that exists but
is swallowed by a decorator is silence wearing a passing test, and this repo has shipped exactly that:
a projection quietly dropped two new events and everything stayed green.

**Pin these behaviours to these EXACT method names:**

- `Event_CarriesTheWaveTheCommitAndWhatItCovered`
- `EveryCoreDecorator_ForwardsTheEvent` — **model this on
  `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`**, which already
  does this job and has been through the failure modes. Read it first. In particular it
  DELIBERATELY hand-lists its decorators rather than reflecting over every `IRunObserver`
  implementer, and it is right to: reflection over the Core assembly also finds the nested
  `private sealed class NullObserver` (in `IRunObserver.cs`), whose contract is to SWALLOW. A
  "derive the set by reflection" test would demand forwarding from the one type designed not to,
  and would be permanently red for a task that cannot edit it. Copy its declared exemption and
  its non-vacuity floor too.
- `EveryCliDecorator_ForwardsTheEvent`
- `ADecoratorThatDropsTheEvent_IsCaught` — the negative control. Without it, a sweep that enumerates
  zero decorators passes and proves nothing.

The tests MUST COMPILE and FAIL. Do NOT implement the forwarding.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredEventTests.cs`, `tests/Guardrails.Integration.Tests/WaveDelivery/WaveDeliveredCliForwardingTests.cs`, and `src/Guardrails.Core/Execution/IRunObserver.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

