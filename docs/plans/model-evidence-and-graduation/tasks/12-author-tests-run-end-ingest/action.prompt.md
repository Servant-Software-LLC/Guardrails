## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12-author-tests-run-end-ingest`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-author-tests-run-end-ingest": { "someKey": "someValue" } }`.
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

Author the FAILING wiring test for **run-end telemetry ingest**: a completed run records its own
attempts into the corpus, without anyone remembering to type `guardrails telemetry ingest`.

**Why this is a task and not a line inside another one.** Everything before it built a corpus the
operator has to *choose* to fill. A measurement nobody remembers to collect is a measurement that
does not exist — and the corpus this plan exists to build is only worth having if it fills by itself.
That makes this the same shape as task 11: a component that works, reachable from nothing.

**Write only to `tests/Guardrails.Integration.Tests/RunEndTelemetryIngestTests.cs`.**

**Scope boundary (harness-enforced):** Write only to that path. **Do NOT edit
`src/Guardrails.Cli/Commands/RunCommand.cs`** — the wiring is task 13's entire deliverable, and doing
it here would make this test green before the task that exists to earn it. After this task completes,
the harness runs a `git diff` check and rejects any edit outside your path; an out-of-scope edit fails
the task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out
path and stop.

**The test class MUST be named `RunEndTelemetryIngestTests`** in namespace
`Guardrails.Integration.Tests`, with `[Trait("Category", "ModelEvidence")]` on the class and every
method. The guardrails filter on exactly
`Category=ModelEvidence&FullyQualifiedName~RunEndTelemetryIngestTests`.

**Pin these four behaviours to these exact test method names:**

| behaviour | test method name |
|---|---|
| a completed run ingests its own journal, no manual verb | `Run_IngestsItsOwnJournal_WithoutAManualVerb` |
| a run that ended needs-human still ingests its attempts | `Run_ThatEndedNeedsHuman_StillIngestsItsAttempts` |
| a failed telemetry write is REPORTED, and the exit code is unchanged | `Run_TelemetryWriteFailure_IsReported_AndExitCodeUnchanged` |
| opt-out suppresses an ingest that otherwise happens | `Run_CollectionDisabled_SuppressesIngestThatOtherwiseHappens` |

**Both of the last two assert a CONTRAST, and that is not a style preference — it is what makes them
falsifiable before the wiring exists.** On the unwired tree nothing ingests, so *"the exit code is
unchanged"* and *"nothing was written"* are both **already true**. A test asserting only those passes
against unwired code, the red census can never bind it, and this task burns its whole retry budget on
tests that are correct. Each must therefore assert something that is FALSE until task 13 lands.

**Drive a REAL run.** `tests/Guardrails.Integration.Tests/FakeClaudeRunTests.cs` and
`FakeClaudePlanBuilder.cs` are the existing end-to-end harness — a real plan folder, a fake `claude`,
a real `guardrails run`. `WorktreeContainmentHookWiringTests.cs` is the closest precedent for a
*wiring* assertion driven that way. Do not assert on a hand-built `RunReport`: the claim is that the
production run path ingests, and only the real path can prove it.

**Design constraints the tests must encode:**
- **Every run outcome ingests, not just green.** A run that ends `needs-human` is exactly the run whose
  attempts the corpus most needs — a model that fails is the evidence a model comparison is made of.
  Assert this with a plan that genuinely ends unresolved.
- **Telemetry can never change the run's outcome — and its failure is REPORTED, not swallowed.** Point
  the corpus at a root that cannot be written (a path taken by a file, a read-only location — whatever is
  portable on this repo's three OSes) and assert **all three**: the run's exit code is unchanged, the
  summary still prints, **and the console carries a line saying ingest did not happen**. That third
  assertion is what makes the test red today; the first two are already true of unwired code. The
  precedent is in the seam: `WriteDurableFinalSite` is called from `RunCommand.Finish` under *"Best-effort:
  a render hiccup must never change the run's exit code."* This test is that promise for telemetry, plus
  the half that keeps a silent failure from being indistinguishable from success.
- **Opt-out is honoured at run end too**, not only in the verb — and the test proves it by CONTRAST. Run
  the same plan twice against the same corpus root: once with collection enabled (assert rows appear) and
  once opted out (assert nothing is written at all). The enabled half is what fails before the wiring; an
  opted-out run alone proves nothing, because an unwired harness also writes nothing.
- Every test points the corpus at a **temp directory** it deletes afterwards. No test may write to the
  real `~/.guardrails/telemetry/`.

**The test MUST COMPILE and FAIL.** No stub file is yours to write — the types it drives all exist by
now — so the red comes from `RunCommand` not yet ingesting. If it does not compile, fix the test, not
the production code.
