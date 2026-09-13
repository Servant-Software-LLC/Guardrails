## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-author-tests-observer-event`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-author-tests-observer-event": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Author failing tests for the §2 step-3 announcement, plus the interface member they compile
against.

**Test file 1:** `tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs`
  — class `SuppliedObserverEventTests` (the event shape + the CORE decorators).
**Test file 2:** `tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs`
  — class `SuppliedObserverCliForwardingTests` (the CLI decorators + the live-table / --no-ui
  rendering).

Two classes because two tasks implement them: splitting the forwarding by assembly keeps each
retry bounded, which a single six-file wiring task does not — GR2042 flagged exactly that
fingerprint on the first draft of this plan.

**The CLI class lives in Guardrails.Integration.Tests, and it MUST (review, 2026-09-11).**
`Guardrails.Core.Tests` references `Guardrails.Core` and nothing else — measured: zero
`using Guardrails.Cli` across its 264 files, against 156 in Integration.Tests, and
`tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs` states the constraint in its
own header. `ConsoleRunObserver`, `LiveRunObserver`, `OnTheFlyDiagramObserver` and
`OnTheFlyLogSiteObserver` all live in `src/Guardrails.Cli`, no task in this plan may edit a
`.csproj`, and the first draft put this class in Core.Tests — where the honest test cannot
compile and the compiling test proves nothing.
**Interface change:** add `SuppliedResourcesCommitted` to
`src/Guardrails.Core/Execution/IRunObserver.cs` with a **no-op default implementation**, so
every existing implementer still compiles and this task's `writeScope` stays to two files.

Every test carries `[Trait("Category", "Supply")]`.

**Why this matters more than it looks.** A run whose base changed underneath it must SAY so —
a silent base change is indistinguishable from a harness bug when a later task behaves
unexpectedly (§2). An event that exists but is swallowed by a decorator is the same silence
wearing a passing test; this repo has shipped exactly that before, where a projection quietly
dropped two new events.

**Pin these behaviours to these EXACT method names:**

- `Event_CarriesThePathsAndTheCommit` — the announcement names what landed and the commit it
  landed in, so an operator reading the log can tie it to the §4 provenance record.
- `EveryDecorator_ForwardsTheEvent` — enumerate the CORE transparent decorators and assert
  each forwards it. **Model this on `tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs`**,
  which already does exactly this job and has been through the failure modes. Read it first.
  In particular it DELIBERATELY hand-lists its decorators rather than reflecting over every
  `IRunObserver` implementer, and it is right to: reflection over the Core assembly also finds
  the nested `private sealed class NullObserver` (in `IRunObserver.cs`), which by design must
  NOT forward. A "derive the set by reflection" test would demand forwarding from the one type
  whose contract is to swallow, and would be permanently red for a task that cannot edit it.
  It also carries a declared exemption and a non-vacuity floor — copy both.
- `ADecoratorThatDropsTheEvent_IsCaught` — the negative control. Construct a deliberately
  non-forwarding decorator and assert the sweep above FAILS on it. Without this, a sweep that
  enumerates zero decorators passes and proves nothing. (This one is DECLARED-EXEMPT from the
  red census: it is self-contained, so it holds on the stub tree and after. See the header of
  `guardrails/02-tests-fail-on-stubs.ps1`.)

**And pin these FOUR to the CLI class**, which the first draft left pinned by nothing at all —
an empty file passed every guardrail on this task:

- `ConsoleRunObserver_ForwardsTheEvent`
- `LiveRunObserver_RendersTheEventInTheLiveTable`
- `NoUi_PrintsTheSuppliedResourcesLine`
- `EveryCliDecorator_DeclaresAndForwardsTheEvent` — the CLI half of the sweep, over
  `OnTheFlyDiagramObserver` and `OnTheFlyLogSiteObserver` (the two `ObserverForwardingSweepTests`
  names in that assembly).

The tests MUST COMPILE and FAIL. Do NOT implement the forwarding.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

