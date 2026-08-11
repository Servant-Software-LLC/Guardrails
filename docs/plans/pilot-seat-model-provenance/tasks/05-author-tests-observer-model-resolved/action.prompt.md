## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "05-author-tests-observer-model-resolved": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Write tests proving the two on-the-fly `IRunObserver` decorators forward a new `AttemptModelResolved`
event to their inner observer, plus the minimal interface stub.

**Files (create/edit ONLY these):**
- `tests/Guardrails.Integration.Tests/ObserverModelResolvedTests.cs` — the test file.
- `src/Guardrails.Core/Execution/IRunObserver.cs` — add ONLY a stub: a new default-method event
  `void AttemptModelResolved(TaskNode task, int attempt, string resolvedModel) {{ }}` (a no-op default,
  mirroring the existing `AttemptStarting` default-method shape). Do NOT make the decorators forward it.

**Tests (must compile and FAIL against the stub):**
1. `OnTheFlyLogSiteObserver` forwards `AttemptModelResolved` to its inner observer — construct it wrapping
   a recording `IRunObserver` double (mirror the constructor args existing observer tests use), call the
   event, assert the recording inner received it with the same `(task, attempt, resolvedModel)`.
2. Same for `OnTheFlyDiagramObserver`.

Because the decorators inherit the no-op default, the recording inner receives nothing and the assertions
FAIL — the intended TDD red.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/ObserverModelResolvedTests.cs` and `src/Guardrails.Core/Execution/IRunObserver.cs` (the stub method only). After this task completes the harness runs
a `git diff` check and rejects any edit outside these paths — including other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write {"needsHuman": "<what is missing>"} to the state-out path and stop.