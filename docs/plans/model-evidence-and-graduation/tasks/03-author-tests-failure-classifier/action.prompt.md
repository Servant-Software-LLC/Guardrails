## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-author-tests-failure-classifier`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-author-tests-failure-classifier": { "someKey": "someValue" } }`.
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

Author the FAILING tests, plus the minimal stubs they compile against, for the **guardrail-failed
classifier** — the piece that recovers what `run.json` cannot say on its own.

**The problem it solves** (charter §6, the warn block): three different failures are all journaled as
`AttemptOutcome.GuardrailFailed` and are indistinguishable in `run.json` —

| site | failure |
|---|---|
| `src/Guardrails.Core/Execution/TaskExecutor.cs:975` | staging move failed |
| `src/Guardrails.Core/Execution/TaskExecutor.cs:1040` | harness-write out of scope |
| `src/Guardrails.Core/Execution/TaskExecutor.cs:1093` | write-scope violation |

Each sets a distinguishing `TaskResult.Summary`, but `AttemptJournaler.FailedAttempt` persists only
`ActionExitCode`, `Outcome`, `FailedGuardrails`, `CostUsd`, `Usage` and `LogDir` — **the summary is
dropped**, and `GuardrailFailureFingerprint` never leaves memory. Read
`src/Guardrails.Core/Execution/AttemptJournaler.cs` and confirm that for yourself before writing the
tests; the whole classifier exists because of it.

What IS on disk: `logDir` is journaled, and `FailedAttempt` writes `feedback.md` into it from
`RetryPolicy.ForWriteScopeViolation` / `ForHarnessWriteOutOfScope`. So the classifier reads that file.

**Write only to these two files:**
- `tests/Guardrails.Core.Tests/Telemetry/TelemetryFailureClassifierTests.cs`
- `src/Guardrails.Core/Telemetry/TelemetryFailureClassifier.cs` (stub)

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/TelemetryFailureClassifierTests.cs` and
`src/Guardrails.Core/Telemetry/TelemetryFailureClassifier.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside these paths — including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

**The test class MUST be named `TelemetryFailureClassifierTests`** in namespace
`Guardrails.Core.Tests.Telemetry`, with `[Trait("Category", "ModelEvidence")]` on the class and every
test method. The guardrails filter on exactly
`Category=ModelEvidence&FullyQualifiedName~TelemetryFailureClassifierTests`.

**Pin these six behaviours to these exact test method names:**

| behaviour | test method name |
|---|---|
| write-scope violation, from the feedback text | `Classify_WriteScopeViolation_FromFeedbackText` |
| staging-move failure is NOT write-scope | `Classify_StagingMoveFailure_IsNotWriteScope` |
| harness-write out-of-scope is its own kind | `Classify_HarnessWriteOutOfScope_IsItsOwnKind` |
| a real guardrail failure stays guardrail-failed | `Classify_RealGuardrailFailure_StaysGuardrailFailed` |
| missing log site is undifferentiated, not guessed | `Classify_MissingLogSite_IsUndifferentiated_NeverGuessed` |
| unrecognized wording is undifferentiated | `Classify_UnrecognizedFeedbackWording_IsUndifferentiated` |

**Design constraints the tests must encode:**
- The classifier takes an attempt's **log directory path** and its journaled `failedGuardrails` list.
  It reads `feedback.md` from that directory. Tests write their own temp log sites — no fixture may
  point at a real run's logs, which may be pruned.
- A **non-empty `failedGuardrails`** list means a genuine guardrail failed; the classifier must leave
  it as `guardrail-failed` without reading anything.
- **A log site that no longer exists yields `undifferentiated` — never a guess.** Assert the exact
  value, not merely "not write-scope". This is the honesty rule of the whole plan expressed as one
  test: an attempt we cannot classify must say so rather than be quietly counted as something.
- **Unrecognized wording also yields `undifferentiated`.** The feedback text has changed across harness
  releases, so a classifier that assumes today's wording silently mis-buckets older runs.

**The tests MUST COMPILE and FAIL.** Write the stub so the test project builds — members that throw
`NotImplementedException`. Failing is the point; NOT compiling is a mistake to fix. Do NOT implement the
classification logic — that is the next task, and it is the task that gets to go and read real
historical `feedback.md` files.
