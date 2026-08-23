## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-02-capture-and-persist/04-implement-provenance-model-persist": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
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

Fill real logic over the two stub declarations `03-author-tests-provenance-model-persist` left, so the
`Category=ObservedModelProvenance` tests go green — **without disturbing a single one of the ~2,100 lines
of conformance assertions in the same file**, which run alongside them.

### 1. `src/Guardrails.Core/Execution/ActionRunner.cs` — carry it

`ActionRun.FromPrompt` is the ONE point where a runner-shaped `PromptResult` is restated in the shape the
attempt loop consumes. Set `ObservedModel = result.ObservedModel` there. Copy the discipline of the
`CostUsd` line two members up: a straight carry, no recomputation, no defaulting — **absent stays absent**.
`FromScript` sets nothing (a script attempt runs no model).

### 2. `src/Guardrails.Core/Execution/TaskExecutor.cs` — fold it onto the provenance

**This is the whole design, and the reason it is only one edit.** `AttemptProvenance` is built at attempt
LAUNCH — before anything ran — so the observed model cannot be part of `BuildProvenance`. It is folded onto
that same object once the action returns.

There is already a precedent for exactly this in the same method, and you should read it before writing
anything: the **D32 judge fold**. Grep for `provenance with { Judge` — the comment block above it explains
why folding onto the provenance object is *mechanical rather than cosmetic*, and that reasoning is what
makes this task one edit instead of six:

> `AttemptProvenance` is the one member that already rides `PendingAttempt`, so a value folded here reaches
> BOTH record construction paths with no further edit — the serial `AttemptJournaler` AND
> `Scheduler.RecordSucceededSettle` (`Provenance = pending.Provenance`).

So: **do not** edit `AttemptJournaler.cs`, `Scheduler.cs`, `RunReport.cs` or `JournalModel.cs`. They already
carry whatever the provenance object holds. Your `writeScope` excludes them deliberately; if you find
yourself needing one, that is a finding worth a `needsHuman`, not a workaround.

**Where to fold.** After the `ActionRun` comes back from the action runner and its logs are written, and
BEFORE any journal call reads the provenance. Grep for `ActionRun action = await _actionRunner.RunAsync`
to find the call; the fold goes below it. *(This describes the tree at plan-authoring time — verify the
surrounding shape is still what this says before assuming it.)* The local is REASSIGNED because records
are immutable: a `with` whose result is discarded changes nothing — the same note the judge fold carries.

**What to fold** — the settled contract, and nothing more:

- `provenance.Model` becomes **best-known-actual**: the observed model when the runner reported one, else
  what `BuildProvenance` already put there (the resolved route, else the `"(cli default)"` sentinel).
- `provenance.RequestedModel` is set to the previous `Model` value **only when the two differ**. When they
  match, leave it absent — its *presence* is the mismatch signal, and a key written on every attempt
  destroys that signal completely.
- A runner that reported **no** observed model changes nothing at all. This is not a courtesy: every
  pre-existing conformance test in `Stage2ConformanceTests` runs against a fake runner that reports none,
  and `ProvenanceModel_StaysTheResolvedRoute_WhenTheRunnerReportedNoModel` pins it.
- A **script** attempt has no provenance object to fold onto and no model to fold. Skip it, exactly as the
  judge fold skips a null provenance rather than manufacturing an object of nulls.

**Re-mirror the artifact.** The judge fold calls `AttemptArtifacts.WriteProvenance(logDir, provenance)`
again after reassigning, because on the guardrail-FAILED path that file is the only surface that records
the folded value at all. Do the same, for the same reason.

**Do NOT add a `resolvedModel` key** anywhere — not on the record, not in a serializer. The settled
contract is one field per fact plus a second field for the disagreement; grep `JournalModel.cs` for
*"two fields claiming the same fact is how they drift"* for the reasoning in place.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/ActionRunner.cs` and
`src/Guardrails.Core/Execution/TaskExecutor.cs`. The harness runs a `git diff` check after this task and
rejects any edit outside those two paths — an out-of-scope edit fails the task immediately and consumes a
retry. In particular: **do NOT edit the authored tests.** Make them pass by fixing the implementation.

### If a PRE-EXISTING conformance assertion breaks

Your guardrail runs the whole `Stage2ConformanceTests` class, not just the new methods — that is
deliberate, because `TaskExecutor.cs` is central enough that a narrow filter would let real collateral
damage through to a wave gate no task can fix. The analysis says nothing should break: the fold is a no-op
when no observed model was reported, which is every pre-existing test. If one breaks anyway, **do not edit
it** — its file is outside your scope, and a shipped conformance assertion disagreeing with this contract
change is a decision above this task. Write
`{"needsHuman": {"question": "<which assertion, and why the contract change contradicts it>", "kind": "blocked-work"}}`
to the state-out path and stop.
