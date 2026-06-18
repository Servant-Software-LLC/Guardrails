## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Retire the test-protection triad cleanly (M7 — §6, §8, §10 M7 of
`docs/plans/05-disjoint-scope-ownership.md`). No back-compat, no deprecation window.
This is a removal task verified by absence + the suite staying green; there is no
test-author split. Do ALL of:

- Remove `captureHashes`, `restoreOnRetry`, and the `tests-untouched` retry-feedback
  path from the harness source:
  - `src/Guardrails.Core/Model/TaskNode.cs` (the `CaptureHashes` / `RestoreOnRetry`
    members),
  - `src/Guardrails.Core/Loading/PlanLoader.cs`, `RawManifests.cs`, and
    `PlanValidator.cs` (parsing + GR2013/GR2014 if they pertain ONLY to the triad —
    follow the design; keep any code reused by the enforcer),
  - `src/Guardrails.Core/Execution/FileHashCapture.cs` (the capture-into-fragment
    path — KEEP the SHA-256 primitive the enforcer reuses),
  - `src/Guardrails.Core/Execution/RetryPolicy.cs` (the `IsTestsUntouched` /
    "Do NOT edit the test file(s)" feedback),
  - `src/Guardrails.Core/Execution/CapturedFileStore.cs` (remove, or retain/rename only
    the byte-store the enforcer's untracked-baseline path reuses — your call, per §7 M7).
- Delete the SSOT §3.1 and §3.1.1 sections in
  `docs/plans/02-schemas-and-contracts.md` (the captureHashes / restoreOnRetry
  contract). Keep §3.2 (writeScope) and the enforcement sections added earlier.
- Regenerate the dogfood plan folder `docs/plans/04-dogfood-cost-cap/**` to the new
  writeScope pattern (explicit writeScope per task, no triad) and ensure it validates
  clean. This regeneration is REQUIRED and is checked: at least one
  `04-dogfood-cost-cap/**/task.json` must declare `writeScope`, none may keep
  `captureHashes`/`restoreOnRetry`, and the regenerated impl/guardrail files must not
  reference a `tests-untouched` filename.

Update/remove the EXISTING consumers this removal breaks. `01-build` builds only
`src/Guardrails.Core` and `02-triad-removed-from-source` greps only `src/`, so the
test assemblies can fail to compile while every src-scoped check stays green. You MUST
also update:
- `tests/Guardrails.Core.Tests/PlanLoaderTests.cs` and
  `tests/Guardrails.Core.Tests/PlanValidatorTests.cs` — they assert
  `DiagnosticCodes.CaptureHashes` / `RestoreOnRetryWithoutCaptureHashes` (GR2013/GR2014).
- The **integration** project, which is a SEPARATE assembly no Core-only guardrail
  catches: `tests/Guardrails.Integration.Tests/StateFlowTests.cs`,
  `tests/Guardrails.Integration.Tests/StatePlanBuilder.cs`, and
  `tests/Guardrails.Integration.Tests/ParallelRunTests.cs` use
  `captureHashes:`/`restoreOnRetry:`/`exclusive:`. Update or remove every such reference.

HARD CONSTRAINT — #48 single-writer-per-key STAYS untouched: do NOT change
`src/Guardrails.Core/State/JsonMerger.cs` or `StateManager.cs` single-writer
enforcement; it is a separate state-model invariant, NOT part of the triad.

Exit criterion (M7): no `captureHashes`/`restoreOnRetry`/`tests-untouched` references
remain in the harness source; the solution builds; the regenerated 04 dogfood plan
validates clean; #48 single-writer is intact. The full-suite-green check is the
terminal task downstream — here, get Guardrails.Core building and the triad gone.
Publish nothing to state.
