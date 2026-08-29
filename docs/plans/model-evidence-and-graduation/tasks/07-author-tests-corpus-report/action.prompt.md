## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-author-tests-corpus-report`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-author-tests-corpus-report": { "someKey": "someValue" } }`.
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

Author the FAILING tests, plus the minimal stub they compile against, for the **stratified corpus
report** — the surface the whole plan exists to produce, and the one where a rosier-than-justified
number would be invisible.

**Write only to these two files:**
- `tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs`
- `src/Guardrails.Core/Telemetry/TelemetryReport.cs` (stub)

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs` and
`src/Guardrails.Core/Telemetry/TelemetryReport.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including changes to other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The test class MUST be named `TelemetryReportTests`** in namespace `Guardrails.Core.Tests.Telemetry`,
with `[Trait("Category", "ModelEvidence")]` on the class and every method. The guardrails filter on
exactly `Category=ModelEvidence&FullyQualifiedName~TelemetryReportTests`.

**Pin these six behaviours to these exact test method names:**

| behaviour | test method name |
|---|---|
| stratified by model, tier and fingerprint bucket | `Report_StratifiesByModelAndTierAndBucket` |
| below minimum n renders insufficient evidence | `Report_BelowMinimumSample_RendersInsufficientEvidence` |
| attempts-to-green never without abandonment rate | `Report_AttemptsToGreen_AlwaysAccompaniedByAbandonmentRate` |
| every row carries its sample size | `Report_EveryRowCarriesItsSampleSize` |
| a costless provider reports no money, not zero | `Report_CostlessProvider_ReportsNoMoney_NotZero` |
| different model fingerprints are never pooled | `Report_DifferentModelFingerprints_AreNeverPooled` |

**These tests ARE the honesty rules of charter §5 — encode them as constraints the type cannot violate,
not as assertions about one sample.** The failure they prevent is a report that reads fine and misleads:

- **Stratification is structural, not a convention.** Rows group by (model fingerprint × tier ×
  fingerprint bucket). Test that a corpus mixing an easy-tier weak model with a hard-tier strong model
  does NOT produce a single cross-tier per-model figure — that comparison is the selection confounding
  the charter names as the big one.
- **Below minimum n, the row renders "insufficient evidence" and NO verdict.** Not a blank, not a number
  with a caveat. Assert the absence of the number.
- **Attempts-to-green never renders alone.** Test that a summary carrying attempts-to-green also carries
  abandonment rate over the same denominator. Averaging over successes only flatters exactly the model
  that gives up.
- **Every row carries `n`.**
- **A costless provider reports time and volume, not `$0`.** Assert no money value is produced where
  none was reported.
- **Two model fingerprints never pool**, even under the same model string — a changed digest or
  quantization is a different model and starts a new sample.

**The tests MUST COMPILE and FAIL** against a `NotImplementedException` stub. Do NOT implement the
report. Rendering is a plain data structure plus a formatter; the CLI task presents it.
