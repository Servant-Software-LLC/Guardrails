## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `05-author-tests-action-tier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-author-tests-action-tier": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author xUnit tests -- and ONLY the minimal stubs they need to COMPILE -- for `action.tier`, per section
**C - #225** items 1 and 4 of `docs/plans/model-tiering-stage-1.charter.md`.

Write them to `tests/Guardrails.Core.Tests/ModelTiering/ActionTierTests.cs` with
`[Trait("Category","ModelTieringStage1")]` on the class (the baseline preflight filters that trait out).

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/ModelTiering/ActionTierTests.cs`, `src/Guardrails.Core/Loading/RawManifests.cs` (holds `internal sealed class RawAction`) and `src/Guardrails.Core/Model/ActionDefinition.cs` (the resolved model) — the two stub sites. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths -- including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file --
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Encode these behaviours -- mirror `action.model` / `action.maxTurns`, which already exist and are the
pattern to follow:

1. **`action.tier` parses onto `RawAction` and reaches the resolved model**, accepting `easy|medium|hard`.
2. **`guardrails validate` REJECTS an unrecognized tier** with an error naming the bad value.
3. **`tier` is OPTIONAL**: a task with no tier parses and validates exactly as today. This is the additive
   guarantee -- a single-model user's plan must be unaffected.
4. **A plan-wide default tier covers anything left untagged**, including a task hand-added after breakdown.

Then write MINIMAL stubs so the test project COMPILES: a `Tier` property on `RawAction` in
`src/Guardrails.Core/Loading/RawManifests.cs` and on `ActionDefinition` in
`src/Guardrails.Core/Model/ActionDefinition.cs`. Both types ALREADY EXIST — add the member, do not create a
new file or a second `RawAction`. `Guardrails.Core` grants `InternalsVisibleTo Guardrails.Core.Tests`, so
the internal `RawAction` is directly visible from the test project. Do NOT implement the behaviour (no
parse wiring, no validation): the tests MUST compile and FAIL.
