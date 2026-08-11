## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "01-author-tests-stream-model-capture": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Write xUnit tests proving `ClaudeStreamParser` captures the model the Claude Code CLI echoes in its own
`stream-json` output, plus the minimal stub the tests compile against.

**Files (create/edit ONLY these):**
- `tests/Guardrails.Core.Tests/ClaudeStreamParserModelTests.cs` — the test file.
- `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` — add ONLY a minimal stub: a nullable
  `public string? Model {{ get; init; }}` property on the `ClaudeResult` record (grep for
  `record ClaudeResult`). Do NOT change `ClaudeStreamParser.Feed` — leave the model UNpopulated so the
  tests fail (TDD red). Filling `Feed` is the next task's job. (If you implement `Feed` here, the
  `tests-fail-on-stubs` guardrail will FAIL — it detects tests passing against the stub.)

**Tests to write (they MUST compile and FAIL against the stub):**
1. A canned stream whose first line is a system/init message carrying model `claude-sonnet-5`, then a
   terminal result line → the parsed `ClaudeResult.Model` equals `claude-sonnet-5`.
2. A stream whose terminal result line carries model `claude-opus-4-8` and no init line → the parsed
   `ClaudeResult.Model` equals `claude-opus-4-8` (result-line fallback).
3. A stream with NO model on any line → `ClaudeResult.Model` is null and parsing does NOT throw.

Mirror the existing `ClaudeStreamParserTests.cs` in the same project for construction/feeding idioms and
xUnit usage.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/ClaudeStreamParserModelTests.cs` and `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` (the stub property only). After this task completes the harness runs
a `git diff` check and rejects any edit outside these paths — including other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write {"needsHuman": "<what is missing>"} to the state-out path and stop.