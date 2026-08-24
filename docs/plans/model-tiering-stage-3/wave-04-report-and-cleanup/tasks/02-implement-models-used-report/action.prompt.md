## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-04-report-and-cleanup/02-implement-models-used-report": { "someKey": "someValue" } }`.
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

Fill real logic over the `JournalModelsUsed` stub `01-author-tests-models-used-report` left, and print the
result from the run summary, so `ModelsUsedSummaryTests` and `ModelsUsedReportTests` both go green. This is
the last of #349's five operator surfaces.

**Read the two authored test files first.** They are the contract — the summary below describes them, but
where the two differ, the tests are right and this prompt is a summary of them.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/JournalModelsUsed.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`. The
harness runs a `git diff` check after this task and rejects any edit outside those two paths — an
out-of-scope edit fails the task immediately and consumes a retry. In particular: **do NOT edit the
authored tests.** If a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path and stop; do not change it to make it pass.

### 1. `src/Guardrails.Core/Journal/JournalModelsUsed.cs`

Replace the throwing bodies. `JournalTierSpend` is the sibling to follow — same file layout, same accumulator
shape, same discipline about what a null means. Read it before you write this.

**Aggregate** every ATTEMPT of every task whose `provenance.model` is non-null, counted independently —
retries included, for the same reason `JournalTierSpend` counts them: resolution and execution happen per
attempt, so an attempt that ran a model again is another use of it. Collect, per served model, the distinct
`provenance.requestedModel` values seen against it.

**Exclude, do not bucket.** An attempt with no `provenance` (a script attempt, a serial-mode attempt) or a
null `provenance.model` contributes nothing and gets **no bucket of its own**. `JournalTierSpend`'s
Invariant-7 comment is the precedent and says why at length: a `?? "untiered"` fallback passes every
structural assertion while appending a new section to every existing user's run report.

**Return `null`, never empty.** When no attempt recorded a model, `Summarize` returns `null` and `Render`
returns `null` — that is what lets the caller spell suppression as `is { }`, exactly as the two lines above
it already do, instead of testing a rendered string for emptiness.

**`Render` returns the segments only.** No prefix, no header — the caller owns the label, as
`JournalTierSpend.Render`'s own doc comment states. Order and format are pinned by the tests:
`"<model> ×<attempts>"`, joined with `" · "`, with ` (substituted for <a>, <b>)` appended when a served
model has requested ids recorded against it, ordered by descending attempt count then ordinal model name.

`requestedModel` is present **only** on a disagreement, so most attempts have no such key at all. An
aggregation that assumes both keys always exist is the specific wrong answer the brief names.

### 2. `src/Guardrails.Cli/Commands/RunCommand.cs`

One addition, inside `PrintTotalCost` — the method `PrintSummary` already calls to close the run report.
It has already read the journal into a `document` local and already renders two lines from it. Add the
third, in exactly the shape of the one above it:

```csharp
if (JournalModelsUsed.Render(document) is { } models)
{
    output.WriteLine($"Models used: {models}");
}
```

Nothing else changes. Do not re-read the journal, do not add a parameter, do not move the existing lines,
and do not touch `StatusCommand` — it prints the cost line but deliberately not the per-tier line, and this
line follows the per-tier sibling. `RunCommand` only.

Keep the label spelled exactly `Models used: ` — the SSOT and the domain-knowledge skill record that
literal in the next task, and a third spelling of one label is how a surface stops being greppable.

### Why the end-to-end test is the one that matters

`Run_PromptPlan_PrintsModelsUsedLine_NamingTheModelTheJournalRecorded` drives the real `run` command over a
fake-claude plan and reads the model out of the run's own `state/run.json`. It is the only check in this
wave that proves the aggregation is actually REACHED from an operator's terminal rather than merely
existing and being unit-tested — the #475 shape, where a journal field shipped declared, read, and assigned
by no construction site at all with every guardrail green. If it fails while the unit tests pass, the
aggregation is right and the call site is wrong; look at `PrintTotalCost` before you look at
`JournalModelsUsed`.

### Do not re-litigate the settled shape

- `provenance.model` is best-known-actual; `requestedModel` appears **only** on disagreement; there is
  **no `resolvedModel` key**. Do not add one.
- Do not force a `--model` and do not re-parse a runner stream. Everything counted here is already in the
  journal, folded there once by `TaskExecutor`; a second owner of that rule would drift from the `run.json`
  it is reporting.
