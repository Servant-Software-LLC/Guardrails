## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "09-author-tests-report-models-used": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Write tests for a models-used run summary derived from attempt provenance, plus the minimal stub.

**Files (create/edit ONLY these):**
- `tests/Guardrails.Core.Tests/ReportModelsUsedTests.cs` — the test file.
- `src/Guardrails.Core/Execution/RunReport.cs` — add ONLY a stub: a method
  `public IReadOnlyCollection<string> ModelsUsed()` whose body is `throw new NotImplementedException();`
  (so the file COMPILES and the tests FAIL). Use the existing `AttemptProvenance` shape (it now carries
  `ResolvedModel`/`Model` from the provenance task).

**Tests (must compile and FAIL against the stub):**
1. Given attempts recording models `claude-sonnet-5` and `claude-opus-4-8`, `ModelsUsed()` returns both.
2. Attempts recording the same model twice → `ModelsUsed()` returns it once (deduped).
3. A run with no resolved models (all script attempts) → `ModelsUsed()` is empty, not null.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/ReportModelsUsedTests.cs` and `src/Guardrails.Core/Execution/RunReport.cs` (the stub only). After this task completes the harness runs
a `git diff` check and rejects any edit outside these paths — including other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write {"needsHuman": "<what is missing>"} to the state-out path and stop.