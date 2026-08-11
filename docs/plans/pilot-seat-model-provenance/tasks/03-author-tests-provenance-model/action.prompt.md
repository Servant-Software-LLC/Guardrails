## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "03-author-tests-provenance-model": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Write xUnit tests for the model-provenance reconciliation, plus the minimal stubs they compile against.

**Files (create/edit ONLY these):**
- `tests/Guardrails.Core.Tests/AttemptProvenanceModelTests.cs` — the test file.
- `src/Guardrails.Core/Journal/JournalModel.cs` — add THREE nullable stub properties to the
  `AttemptProvenance` record (grep for `record AttemptProvenance`): `ResolvedModel`, `RequestedModel`,
  `Effort` (all `public string? ... {{ get; init; }}`). Leave `Model` as-is.
- `src/Guardrails.Core/Execution/PromptExecutionSupport.cs` — add TWO stub helpers next to the existing
  `ResolveModelForDisplay` (grep for it): `public static string ResolveActualModel(string? resolvedModel,
  string? requestedModel)` and `public static bool IsModelMismatch(string? resolvedModel, string?
  requestedModel)` — each body `throw new NotImplementedException();` so the file COMPILES but the tests
  FAIL (TDD red).

**Tests (must compile and FAIL against the stubs):**
1. `ResolveActualModel("claude-opus-4-8", "claude-sonnet-5")` -> `"claude-opus-4-8"` (resolved wins).
2. `ResolveActualModel(null, "claude-sonnet-5")` -> `"claude-sonnet-5"` (falls back to requested).
3. `ResolveActualModel(null, null)` -> `"(cli default)"` (the SAME sentinel `ResolveModelForDisplay` uses).
4. `IsModelMismatch("claude-opus-4-8", "claude-sonnet-5")` -> true; equal -> false; `IsModelMismatch(null,
   "claude-sonnet-5")` -> false (unknown resolved is not a mismatch).
5. An `AttemptProvenance` round-trips `ResolvedModel`/`RequestedModel`/`Effort` (construct set, read back).

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/AttemptProvenanceModelTests.cs`, `src/Guardrails.Core/Journal/JournalModel.cs`, and `src/Guardrails.Core/Execution/PromptExecutionSupport.cs` (the stubs only). After this task completes the harness runs
a `git diff` check and rejects any edit outside these paths — including other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write {"needsHuman": "<what is missing>"} to the state-out path and stop.