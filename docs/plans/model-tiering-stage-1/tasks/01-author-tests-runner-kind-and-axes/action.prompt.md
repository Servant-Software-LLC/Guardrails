## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `01-author-tests-runner-kind-and-axes`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-runner-kind-and-axes": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author xUnit tests -- and ONLY the minimal stubs they need to COMPILE -- for the Stage 1 provider-registry
schema described in `docs/plans/model-tiering-stage-1.charter.md` section **A - #224**.

Write the tests to `tests/Guardrails.Core.Tests/ModelTiering/PromptRunnerSchemaTests.cs`, with
`[Trait("Category","ModelTieringStage1")]` on the class. That trait is load-bearing: the plan's baseline
preflight filters it OUT so your intentionally-red tests never break the pre-DAG baseline.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/ModelTiering/` and `src/Guardrails.Core/Model/PromptRunnerConfig.cs` (the stub file). After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths -- including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file --
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Encode these behaviours as tests -- each is a settled requirement of the plan, not a suggestion:

1. **`kind` defaults to `claude`.** A `promptRunners` block with NO `kind` parses and yields `kind ==
   claude`. This is what keeps the change additive: every existing config must validate unchanged.
2. **`kind` accepts `claude|codex|openrouter|local`** and REJECTS an unrecognized value with a validation
   error naming the bad value.
3. **The three axes are optional and top-level on the block**: `costly` (bool), `strength` (int >= 1),
   `specialization` (`coding|planning-reasoning|general|unspecified`). A block carrying them round-trips.
4. **Each malformed axis form fails validation**: a non-bool `costly`, `strength: 0`, an out-of-enum
   `specialization`.
5. **Per-model `routing` guidance exists, validates, and round-trips.** Its first consumer is Stage 2 --
   this stage only proves it survives a parse/serialise cycle.
6. **A config still carrying `routing.rank` raises a RETIRED-FIELD WARNING**, not an error. `rank` is NOT
   implemented: ordering is ascending `strength` (the weakest model that can serve the tier goes first), and
   the warning is what stops a migrated config's ordering changing silently.

Then write the MINIMAL stubs in `src/Guardrails.Core/Model/PromptRunnerConfig.cs` that let the test project
COMPILE -- members declared, bodies throwing `NotImplementedException` or returning `default`. Do NOT
implement the behaviour: the tests MUST compile and FAIL. Failing is the point; NOT compiling is a mistake
to fix.
