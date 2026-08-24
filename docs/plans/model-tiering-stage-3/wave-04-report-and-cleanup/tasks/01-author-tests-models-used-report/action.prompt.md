## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-04-report-and-cleanup/01-author-tests-models-used-report": { "someKey": "someValue" } }`.
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

Write the TDD **red** for #349's fifth and last surface: a **models-used summary line** on the run report.
You author the tests and the minimal throwing stub they compile against. You do **not** implement the
aggregation and you do **not** print anything from `RunCommand` — `02-implement-models-used-report` does
both, over the stubs you leave.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/JournalModelsUsed.cs` (the stub),
`tests/Guardrails.Core.Tests/ModelTiering/ModelsUsedSummaryTests.cs`, and
`tests/Guardrails.Integration.Tests/ModelTiering/ModelsUsedReportTests.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these three
paths — including `RunCommand.cs`, any other production file, neighbouring test files, or a `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

Your tests MUST **compile and FAIL**. Failing is the point; *not compiling* is a mistake to fix. Every
test method name below is PINNED — a guardrail reads the runner's own TRX result file and requires each
one, by name, to have been executed and to have `Failed`.

### 1. The stub: `src/Guardrails.Core/Journal/JournalModelsUsed.cs`

A new file in `namespace Guardrails.Core.Journal`, shaped as the **sibling** of `JournalTierSpend` — read
that file first; the brief is explicit that this line "is the same kind of read over the same records, so
follow the sibling rather than inventing a path". Declare exactly this surface, with real XML doc comments,
and every method body `throw new NotImplementedException(...)`:

```csharp
public static class JournalModelsUsed
{
    public static IReadOnlyList<ModelUsage>? Summarize(JournalDocument document);
    public static string? Render(JournalDocument document);
}

public sealed record ModelUsage
{
    public required string Model { get; init; }
    public required int Attempts { get; init; }
    public IReadOnlyList<string> RequestedModels { get; init; } = [];
}
```

Nothing else. No aggregation logic, no partial implementation — a stub that half-works makes the red below
ambiguous.

### 2. The contract your tests pin

This is what `02-implement-models-used-report` will be held to, so encode it precisely.

**What is counted.** Every ATTEMPT of every task whose `provenance.model` is non-null, counted
independently — retries included, exactly as `JournalTierSpend` counts them and for the same reason (a
retry ran a model again). Attempts with no `provenance` at all (a script attempt) or a null
`provenance.model` are **excluded outright** and are **not** collected into a bucket of their own; that is
the Invariant-7 discipline `JournalTierSpend` already spells out at length.

**Suppression.** When NO attempt recorded a model, `Summarize` returns `null` — not an empty list — and
`Render` returns `null`, not an empty string. A deterministic-only plan must print exactly today's summary
and not one character more.

**The mismatch.** `provenance.requestedModel` is written **only** when the runner served something other
than the route asked for, so it is absent on an ordinary attempt. An aggregation that assumes both keys
always exist is the specific wrong answer the brief names. A `ModelUsage` collects the distinct requested
ids seen against that served model — empty on the overwhelmingly common agreeing case.

**The rendered line.** `Render` returns the segments only; the caller owns the label, exactly as
`JournalTierSpend.Render` does. One segment per `ModelUsage`, joined with `" · "`:

- `"<model> ×<attempts>"` — e.g. `claude-sonnet-5-20260101 ×7`
- with ` (substituted for <a>, <b>)` appended when `RequestedModels` is non-empty, the ids ordinal-ascending
  and de-duplicated — e.g. `claude-sonnet-5-20260101 ×7 (substituted for claude-opus-5)`
- segments ordered by **descending attempt count, then ordinal-ascending model name**, so the line does not
  shuffle between runs of the same plan.

`Attempts` is always strictly positive on a returned row: a row that counted nothing is never produced.

### 3. `tests/Guardrails.Core.Tests/ModelTiering/ModelsUsedSummaryTests.cs`

Class `ModelsUsedSummaryTests`, `[Trait("Category", "ModelTieringStage3")]`. Build `JournalDocument`
fixtures the way `tests/Guardrails.Core.Tests/ModelTiering/PerTierSpendTests.cs` builds them — read it
first and mirror its private helpers rather than inventing a second fixture idiom. Six PINNED methods:

- **`Attempts_AcrossTasksAndRetriesCountPerModel`** — several tasks, several models, and two attempts of one
  task on the same model; each model's count is the number of attempts that recorded it.
- **`AttemptsWithoutAModel_AreExcluded_WithNoBucketOfTheirOwn`** — a script attempt (no `provenance`) and an
  attempt whose `provenance.model` is null contribute nothing, and no row appears for them. Assert on the
  rendered STRING as well as the structure: a `?? "(none)"`-style bucket is invisible to a structural
  assertion about the real models and is exactly the regression `JournalTierSpend`'s Invariant 7 forbids.
- **`RunWithNoRecordedModel_SummarizesAndRendersNull`** — a journal of script attempts only: `Summarize`
  returns `null` and `Render` returns `null`. Assert `Assert.Null(...)` on both — an empty list or an empty
  string is a different, wrong answer.
- **`RenderedLine_NamesEveryRecordedModel_WithAStrictlyPositiveCount`** — the brief's central requirement.
  For a multi-model fixture, every model the journal recorded appears in the rendered line, each with its
  own strictly positive count, and the line contains no zero count. A line that named a model with `×0`, or
  that dropped a recorded model, must fail this.
- **`RequestedModel_PresentOnlyOnMismatch_IsCarriedIntoTheSegment`** — two-sided in ONE test: a served model
  whose attempts carry a differing `requestedModel` renders the substitution clause naming that requested
  id, and a served model whose attempts carry no `requestedModel` renders **no** substitution clause. The
  presence of the requested id IS the mismatch signal (there is no flag beside it), so a renderer that
  always printed one form or always printed the other must fail here.
- **`SegmentOrder_IsDeterministic_AndDoesNotShuffle`** — pin the documented order (descending count, then
  ordinal name) on a fixture where dictionary order and first-appearance order would both give a different
  answer, so the test actually discriminates.

### 4. `tests/Guardrails.Integration.Tests/ModelTiering/ModelsUsedReportTests.cs`

Class `ModelsUsedReportTests`, `[Trait("Category", "ModelTieringStage3")]`. This is the **real-seam** half:
it drives the actual `guardrails run` command end to end and reads what an operator would see. Copy the
shape of `tests/Guardrails.Integration.Tests/DryRunCliTests.cs` — its private
`InvokeCapturingAsync` helper over a `StringConsoleIo`, and specifically its two shipped tests
`Run_PromptPlan_PrintsTotalCostLine` and `Run_DeterministicPlan_OmitsTotalCostLine`, which are the
precedent for both methods below. Define your own private `InvokeCapturingAsync` in this file; do NOT edit
`DryRunCliTests.cs` or any shared fixture — they are outside your scope.

- **`Run_PromptPlan_PrintsModelsUsedLine_NamingTheModelTheJournalRecorded`** — build a
  `FakeClaudePlanBuilder` plan with one prompt task, run it with `--no-ui`, then read the model the run
  actually journalled out of `state/run.json` (`RunJsonPath`, via `JournalReader`) and assert the printed
  models-used line names **that** value with a positive count. Read it from the journal; do not hardcode
  the string — the whole point of this line is that it reports what the run recorded.

  > **Isolate the `Models used:` line before asserting anything about it.** Wave 3 already prints the
  > attempt's model elsewhere in `--no-ui` output (`ConsoleRunObserver.AttemptModelResolved` writes a
  > `[model] <task> attempt N: ...` line), so a bare `Assert.Contains(model, output)` is **green on this
  > wave's entry tree** and proves nothing at all. Find the single output line that begins `Models used:`
  > and make every assertion against that line.

- **`Run_DeterministicPlan_OmitsModelsUsedLine`** — a script-only `StatePlanBuilder` plan records no model,
  so the summary must carry no models-used line at all: `Assert.DoesNotContain("Models used", output)`,
  mirroring the shipped `Run_DeterministicPlan_OmitsTotalCostLine` verbatim in form.

  > **This one test is GREEN on your tree, and that is expected.** It asserts an ABSENCE that is trivially
  > true before the feature exists, so it is deliberately excluded from the red census — see the guardrail's
  > own note. Write it anyway: it is the regression guard that stops `02-implement-models-used-report` from
  > printing an empty `Models used:` line on every deterministic run. Do not contort it into something that
  > fails today.

### Do not re-litigate the settled shape

- `provenance.model` is best-known-actual; `requestedModel` appears **only** on disagreement; there is
  **no `resolvedModel` key** (Stage 2 refused it and the charter settled it). Do not add one and do not
  describe it as deferred.
- Do not force a `--model` anywhere, and do not re-parse a stream. Everything you count is already in the
  journal, folded there once by `TaskExecutor`. A second owner of that rule would drift from the `run.json`
  it is reporting.
- `StatusCommand` prints the cost line but deliberately not the per-tier line, and the models-used line
  follows the per-tier sibling: `RunCommand` only. It is out of scope here and for task 02.
