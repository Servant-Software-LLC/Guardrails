## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `03-author-tests-registry-kind-dispatch`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-registry-kind-dispatch": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author xUnit tests -- and ONLY the minimal stubs they need to COMPILE -- for
`PromptRunnerRegistry.FromConfig` dispatching on `kind`, per section **A - #224** item 2 of
`docs/plans/model-tiering-stage-1.charter.md`.

Write them to `tests/Guardrails.Core.Tests/ModelTiering/RegistryKindDispatchTests.cs` with
`[Trait("Category","ModelTieringStage1")]` on the class (the baseline preflight filters that trait out, so
your intentionally-red tests never break the pre-DAG baseline).

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/ModelTiering/RegistryKindDispatchTests.cs` and `src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs` (the stub file). After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths -- including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file --
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Encode these behaviours:

1. **`kind: claude` builds a real `ClaudePromptRunner`.** Assert the CONCRETE TYPE, not merely that some
   runner came back -- a type assertion is what catches an inverted pairing when more kinds become real
   in #223.
2. **An unimplemented kind FAILS registry construction with an actionable message naming the kind.** Only
   `claude` is real in this stage; `codex`/`openrouter`/`local` are #223.
3. **It NEVER silently falls back to Claude.** Assert the failure, and assert the returned runner is not a
   `ClaudePromptRunner` standing in for the requested kind. A silent fallback would spend a run against a
   model the config did not ask for -- this is the behaviour the plan calls out by name.
4. **A config with no `kind` still builds Claude**, unchanged -- the additive guarantee.

Then write MINIMAL stubs in `src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs` so the test project
COMPILES -- members declared, bodies throwing `NotImplementedException`. Do NOT implement the dispatch: the
tests MUST compile and FAIL. Failing is the point; NOT compiling is a mistake to fix.
