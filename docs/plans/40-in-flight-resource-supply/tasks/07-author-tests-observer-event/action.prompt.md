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

**Test file:** `tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs`
**Test classes:** `SuppliedObserverEventTests` (the event shape + the CORE decorators) and
`SuppliedObserverCliForwardingTests` (the CLI decorators + the live-table / --no-ui rendering).
Two classes because two tasks implement them: splitting the forwarding by assembly keeps each
retry bounded, which a single six-file wiring task does not — GR2042 flagged exactly that
fingerprint on the first draft of this plan.
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
- `EveryDecorator_ForwardsTheEvent` — enumerate the decorator types in the composition and
  assert each forwards it. Do NOT hand-list them in a way that silently goes stale: derive the
  set (reflection over `IRunObserver` implementers, or the real composition) so a decorator
  added later is covered the day it lands.
- `ADecoratorThatDropsTheEvent_IsCaught` — the negative control. Construct a deliberately
  non-forwarding decorator and assert the sweep above FAILS on it. Without this, a sweep that
  enumerates zero decorators passes and proves nothing.

The tests MUST COMPILE and FAIL. Do NOT implement the forwarding.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

