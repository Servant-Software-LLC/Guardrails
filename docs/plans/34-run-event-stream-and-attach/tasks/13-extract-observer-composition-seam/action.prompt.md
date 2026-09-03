## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `13-extract-observer-composition-seam`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "13-extract-observer-composition-seam": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

A pure structural change that opens an injection point - **no behaviour change, no new decorator**.

`RunCommand.cs` builds the observer decorator chain inline, in TWO branches (the live-UI branch and the
`--no-ui` branch). Find them by grepping for `new OnTheFlyDiagramObserver` - there are two construction
sites, and a wiring that lands in one branch and not the other is exactly the silent half-fix this plan
exists to prevent. Do not cite line numbers; they move.

Extract that construction into ONE named **public** method - `BuildObserverChain` - that both branches
call and that a test can call directly. **Public, not internal:** task 14's tests live in
`Guardrails.Integration.Tests`, a DIFFERENT assembly, and `Guardrails.Cli` ships no
`InternalsVisibleTo` - so an internal or private seam is uncompilable from the very tests that must
drive it, and task 14 would dead-end. This matches the house pattern: the observer types those tests
already touch (`LiveRunObserver`, `OnTheFlyLogSiteObserver`) are public for the same reason. It takes what the inline code takes today (the inner renderer plus the
logs root, run id, plan and whatever else those sites already use) and returns the composed
`IRunObserver`.

This task adds NO new observers to the chain and changes NO behaviour: the chain it returns must be the
same chain, in the same order, for both branches. Task 15 is what inserts the projections into it.

**TDD-exempt:** a pure extraction with no behaviour of its own has no meaningful unit test - the guardrail
is that the seam EXISTS and that both call sites use it, plus the whole suite still passing at the
terminal gate.
