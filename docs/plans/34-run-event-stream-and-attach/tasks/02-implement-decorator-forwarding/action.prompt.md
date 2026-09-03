## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `02-implement-decorator-forwarding`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-decorator-forwarding": { "someKey": "someValue" } }`.
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

Task 01 added `AttemptFinished(TaskNode task, int attempt, AttemptOutcome outcome)` to `IRunObserver` as a
default-implemented member and authored `AttemptCompletionForwardingTests`, which currently FAIL.

Make them pass by giving **each of the four implementations** an explicit `AttemptFinished`:

- `src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` - forward to `_inner`, matching the file's existing
  one-line forwarding style
- `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` - same
- `src/Guardrails.Cli/ConsoleRunObserver.cs` - a RENDERER, not a decorator: it has no inner observer, so
  implement the member as this renderer's own output in its existing house style
- `src/Guardrails.Cli/Ui/LiveRunObserver.cs` - likewise a renderer

**Why explicitly, when the interface already carries a default body:** a default-implemented member a
decorator does not declare is inherited as the EMPTY body, and the event is swallowed in every mode. That
trap is documented four separate times in `IRunObserver.cs` (`VerifierAdvisoryFound`,
`AttemptModelResolved`, `WaveGateFinished`, `WaveBreakdownStarting`), each time as a defect that already
shipped. This is the fifth member, and the tests assert on the DECORATORS themselves for that reason.

Do NOT edit the authored tests. Make them pass by fixing the implementations; if a test is genuinely wrong
or incompatible, emit `{"needsHuman": "<why>"}` rather than changing it.
