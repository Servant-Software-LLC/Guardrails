## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-02-capture-and-persist/03-author-tests-provenance-model-persist": { "someKey": "someValue" } }`.
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

Author the **failing tests** — and only the minimal stub declarations they need to COMPILE — for the
second half of #349: persisting the observed model as the journal's per-attempt provenance.

### The settled contract — do NOT re-litigate it

The charter review resolved `s3-provenance-shape`, and it amends DoR §9.3. Encode exactly this:

- **`provenance.model` becomes best-known-actual** — `observed ?? route ?? sentinel`. Existing readers
  improve with no change on their side.
- **`requestedModel` is written ONLY when it differs** from the observed value. Its *presence* is the
  mismatch signal; there is no separate flag.
- **There is NO `resolvedModel` key.** DoR §9.3 asked for one; Stage 2 refused it in the shipped
  contract — grep `JournalModel.cs` for the phrase *"two fields claiming the same fact is how they
  drift"* to read the reasoning in place. One field per fact, and a second field only for the
  disagreement.

`AttemptProvenance.Effort` already shipped with Stage 2. That half of §9.3 is done — **do not re-add it.**

### Files to write — and nothing else

1. **The tests go INSIDE the existing
   `tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs`.** They belong there and
   nowhere else because the both-paths machinery they need — `Stage2DeferredSettleRun`, `SegmentProvider`,
   `LedgerRunner`, `Ledger`, `CopyPlanTemplate` — is **private and nested inside that class**. Grep for
   `class Stage2DeferredSettleRun` to find it. Reproducing it in a new file would be a large copy of
   private machinery; adding four methods to the class that owns it is the smaller, honest change.

   Append your tests as a clearly-marked new region at the END of the test methods, next to
   `Judge_ProvenanceReachesRunJson_BothPaths` (grep for that name — it is the direct precedent and the
   shape to follow). **Change nothing else in that file.** It is ~2,100 lines of shipped conformance
   assertions; every one of them must still pass.

   Tag **each new test method** with `[Trait("Category", "ObservedModelProvenance")]`. This is
   load-bearing: both this task's census and task 04's `tests-pass` select on
   `--filter "Category=ObservedModelProvenance"`, which is the only selector narrow enough to name your
   tests without dragging in the whole pre-existing conformance suite. The class-level
   `[Trait("Category", "TierResolution")]` stays as it is — traits accumulate.

2. **Stub declaration A — `src/Guardrails.Core/Journal/JournalModel.cs`:** add
   `public string? RequestedModel { get; init; }` to the **`AttemptProvenance`** record, carrying
   `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` exactly like every sibling member, plus
   an XML doc comment stating it is written only when it differs from `Model`. Declare it and nothing
   more.

3. **Stub declaration B — `src/Guardrails.Core/Execution/ActionRunner.cs`:** add
   `public string? ObservedModel { get; init; }` to the **`ActionRun`** record, with an XML doc comment.
   Declare it and nothing more — do **not** touch `FromPrompt` or `FromScript`. Task 04 populates it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs`,
`src/Guardrails.Core/Journal/JournalModel.cs` and `src/Guardrails.Core/Execution/ActionRunner.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside these paths —
including changes to `Stage2PlanHarness.cs`, other production files, or the `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a missing
symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### The behaviours, and the EXACT test-method name each must carry

The census guardrail reads a TRX result file and requires each of these three names to be present and
`Failed`. Name them exactly:

| test method | behaviour |
|---|---|
| `ObservedModel_BecomesProvenanceModel_OnBothRecordPaths` | a run whose fake runner reports an observed model **different** from the route's writes that OBSERVED value as `provenance.model` in `run.json` — asserted on the SERIAL record path **and** on the deferred worktree-settle path |
| `RequestedModel_IsWritten_WhenTheObservedDiffersFromTheRoute` | that same run also writes `provenance.requestedModel` equal to the ROUTE's model — the fact `provenance.model` no longer carries |
| `RequestedModel_IsAbsent_WhenTheObservedMatchesTheRequest` | a run whose runner echoes exactly the model the route asked for writes **no `requestedModel` key at all** — present *is* the mismatch signal, so an always-written key destroys it |

Author **two more**, deliberately GREEN from the start — the regression half, NOT in the census:

- `ProvenanceModel_StaysTheResolvedRoute_WhenTheRunnerReportedNoModel` — a runner reporting no model
  leaves today's behaviour exactly as it is (route, else the `"(cli default)"` sentinel). This is the
  clause that stops task 04 buying the new fact at the cost of the old one.
- `NoResolvedModelKeyIsEverWritten` — parse `run.json` and assert no attempt's provenance carries a
  `resolvedModel` key. The settled contract, pinned so a later well-meaning edit cannot quietly add it.

### How to drive it — the two things that make this feasible

- **Scripting an observed model needs NO harness change.** `Stage2TaskSpec.Results` takes an explicit
  per-call script of `PromptResult`s, so
  `Results = [Stage2PlanHarness.Success() with { ObservedModel = "claude-observed-alpha" }]` is the whole
  setup. `PromptResult.ObservedModel` is declared by
  `01-author-tests-observed-model-capture`, which this task depends on.
- **Assert on `run.json` parsed back off disk, never on the returned in-memory result.** A value computed,
  assigned to something in memory and never serialized satisfies any assertion made against the harness's
  own return value. That is not hypothetical here: this repo shipped `AttemptRecord.Usage` declared, read
  by the per-tier aggregation, and assigned by none of the record-construction sites — structurally dead,
  every guardrail green (#475). `Judge_ProvenanceReachesRunJson_BothPaths` reads the journal off disk for
  exactly that reason; do the same.

### Why BOTH paths is non-negotiable

A succeeded attempt's record is built in **two** places: the serial
`AttemptJournaler.CompleteSucceededOrInvalidFragment`, and the Scheduler's deferred settle
(`RecordSucceededSettle`) — which is the mode a real worktree run takes. A field threaded through only the
first is not half-delivered, it is **invisible to nearly every user**. Assert BOTH receipts before any
content assertion, so a failure on the serial run cannot mask a second host that never took the path it
claims and "both paths" quietly becomes one path asserted twice.

### The red must COMPILE

Failing is the point; **not compiling is a mistake to fix**. With the two stub declarations above the
integration test project compiles and the three behavioural tests fail against members nothing populates.
Do NOT implement the carry or the fold — that is task 04's whole deliverable.
